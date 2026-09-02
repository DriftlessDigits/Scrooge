using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using Scrooge.Windows;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// THE PRICING SPINE, as one pass over one item: read the panel, decide, write or
/// refuse, end the pass. Per-item state lives on PricingItem (sole source of truth).
/// Price cache lives on RunData.
///
/// <para>Four seams were split out of this file on 2026-08-16 (review pricing split),
/// and each of them is a different KIND of thing so that this one can be only one kind:
/// <see cref="RetainerPanelActions"/> is the hands (every unsafe addon touch),
/// <see cref="BoardReadLadder"/> is the ask (the single enqueue point for a board read),
/// <see cref="PricingVoice"/> is what the pass says (pure, and pinned in Scrooge.Tests),
/// and <see cref="PricingPassEnding"/> is the accounting that runs whatever happened.
/// What is left here is the DECIDING: which fork a pass takes, which guard refuses it,
/// and what gets written when nothing refuses it.</para>
///
/// <para>Nothing forwards. Callers reach each seam where it lives:
/// <c>RetainerPanelActions</c> is static, and the ladder hangs off a pipeline as
/// <see cref="Board"/>. One name per thing.</para>
/// </summary>
internal sealed class ItemPricingPipeline : IDisposable
{
  private readonly MarketBoardHandler _mbHandler;

  /// <summary>The ask-and-wait-for-the-board chain. Owns the enqueue, not the decision.</summary>
  internal BoardReadLadder Board { get; }

  // Fallback for PostPinch hotkey path (no CurrentRun/CurrentItem)
  private int? _hotKeyPrice;

  // Per-run cache lives on RunData — accessor for convenience.
  private Dictionary<string, int?> _cachedPrices => Plugin.CurrentRun?.CachedPrices ?? _noRunCacheStub;

  /// <summary>
  /// The price cache for the runless doors (the PostPinch hotkey), and it is REAL
  /// state - not, as the old comment claimed, a sink where "cache writes are silently
  /// discarded" (stability sweep, 2026-08-16).
  ///
  /// <para>The accessor reads it as well as writes it, and nothing ever emptied it:
  /// this dictionary lived for the whole plugin session, so the second hotkey press on
  /// an item answered out of a price bought minutes or hours ago and never went near
  /// the market board. Worse, <see cref="ClearCachedPrices"/> - the "your floor
  /// settings changed, forget what you priced against" door - only reached
  /// <c>Plugin.CurrentRun</c> and could not touch this at all.</para>
  ///
  /// <para>So it is cleared where a run's own cache is: at the start of every run
  /// (<see cref="ClearState"/>) and whenever the guards move
  /// (<see cref="ClearCachedPrices"/>). It caches WITHIN a burst of hotkey presses,
  /// which is what a per-run cache does, and it goes stale on the same two events.</para>
  /// </summary>
  private readonly Dictionary<string, int?> _noRunCacheStub = [];

  /// <summary>Current run mode. Reads from Plugin.CurrentRun.</summary>
  private bool IsPinchRun => Plugin.CurrentRun?.Mode == RunMode.Pinch;

  /// <summary>Current run mode. Reads from Plugin.CurrentRun.</summary>
  private bool IsHawkRun => Plugin.CurrentRun?.Mode == RunMode.Hawk;

  /// <summary>
  /// RECON (Rounds unit 2): the read-bank-cancel pass. It walks the hawk's own
  /// item chain and reaches this pipeline through the same door, so every read
  /// above the decision - the board packet, the proxy completion, the tape, the
  /// receipt, the market-memory diff, the self-heal round - happens identically.
  /// The mode is asked in exactly three places, and each one is a WRITE the pass
  /// must not make: the panel is cancelled instead of confirmed, no listing value
  /// is booked against a listing that does not exist, and the decision is banked
  /// to the cache instead of applied to an asking price.
  /// </summary>
  private bool IsReconRun => Plugin.CurrentRun?.Mode == RunMode.Recon;

  /// <summary>
  /// THE VETO SEAM (Rounds unit 3). Asked once per cached post: "is there fresh
  /// board data for this variant right now?"
  ///
  /// <para>It returns null, on every item, today - and that is the verified state of
  /// the world, not a stub waiting to be filled. Live test 2026-08-10: opening the
  /// RetainerSell panel without pressing Compare Prices produced no market-board
  /// traffic at all through the game hooks, which is precisely what makes the cached
  /// post worth building. The seam costs one null check and keeps the door open for
  /// a future source (a passive packet, a community read) without anyone having to
  /// invent the arbitration under pressure - see <see cref="CachedPostVeto"/>, where
  /// the arbitration already lives and is already tested.</para>
  /// </summary>
  internal Func<uint, bool, FreshBoardRead?> FreshBoardProvider { get; set; }
    = static (_, _) => null;

  /// <summary>
  /// The veto's disagreement tolerance, percent. Inert by construction: the seam
  /// above answers null for every item, so the arbitration returns "post cached"
  /// without ever reaching this number. Its config knob died in the cleanup pass
  /// rather than sit in the UI pretending to move something; the value stays named
  /// here because <see cref="CachedPostVeto"/> still takes the argument, and a bare
  /// literal at the call site would read as a decision somebody made.
  /// </summary>
  private const int VetoDisagreeTolerancePct = 10;

  internal ItemPricingPipeline(TaskManager taskManager, Func<int, int> applyJitter)
  {
    _mbHandler = new MarketBoardHandler();
    _mbHandler.NewPriceReceived += OnNewPriceReceived;
    Board = new BoardReadLadder(taskManager, applyJitter, _mbHandler, () => _cachedPrices);
  }

  public void Dispose()
  {
    _mbHandler.NewPriceReceived -= OnNewPriceReceived;
    _mbHandler.Dispose();
  }

  /// <summary>Clears per-item and per-run state. Called at the start of each run.</summary>
  internal void ClearState()
  {
    _hotKeyPrice = null;
    // The runless door's cache is per-run state too - a new run must not price
    // against a board read the last hotkey press banked (see _noRunCacheStub).
    _noRunCacheStub.Clear();

    // Warm the community-history cache for the standing thin-history items so
    // the fallback can deploy THIS run — the cache is session-scoped, so the
    // old "warm next pinch" only ever paid out on a second pinch per session.
    try { UniversalisHistory.Prefetch(GilStorage.GetOpenLaneHeldItemIds()); }
    catch (Exception ex) { Svc.Log.Debug($"[UniversalisHistory] prefetch skipped: {ex.Message}"); }
  }

  /// <summary>
  /// Clears the cached price lookup table - BOTH of them. Called when price floor
  /// settings change: a cached price bought under the old guards is exactly what the
  /// new ones must not be applied on top of, and the runless door was unreachable from
  /// here until the stub became honest state (see <see cref="_noRunCacheStub"/>).
  /// </summary>
  internal void ClearCachedPrices()
  {
    Plugin.CurrentRun?.CachedPrices.Clear();
    _noRunCacheStub.Clear();
  }

  /// <summary>
  /// The cheap price guard as it stands RIGHT NOW, for one item - the operand
  /// <see cref="CachedPostGate"/> re-checks a banked price against.
  ///
  /// <para>Resolved from exactly the sources <see cref="SetNewPrice"/> resolves it
  /// from (the floor mode over the item's Lumina vendor price, and the configured
  /// minimum), because a re-check that consulted a second definition of "the floor"
  /// would be a second thing to get wrong. It is arithmetic - no server is asked,
  /// which is what makes re-asking it at post time free. The law itself is
  /// <see cref="PriceFloor.Effective"/>, the one place both floor rules are read.</para>
  /// </summary>
  internal static PostGuards GuardsNow(uint itemId)
  {
    var vendorPrice = 0L;
    if (itemId != 0)
    {
      try { vendorPrice = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(itemId).PriceLow; }
      catch { vendorPrice = 0; }
    }

    var floor = PriceFloor.Effective(
      Plugin.Configuration.PriceFloorMode, vendorPrice, Plugin.Configuration.MinimumListingPrice);

    return new PostGuards(floor.Floor);
  }

  /// <summary>
  /// The lane's evidence source: the BANKED TAPE, not the packet in hand.
  ///
  /// <para>The history packet was already banked upstream (MarketBoardHandler
  /// banks it at OnHistoryReceived, the one moment the data exists), so by the
  /// time we price, the ring holds this window PLUS every window we ever saw for
  /// this item. A slow mover's packet reaches back one visit; its ring reaches
  /// back months — which is exactly the evidence a regime question needs.</para>
  ///
  /// <para>Fail-soft to the packet: if storage is unavailable or the ring reads
  /// empty (fresh install, an item banked before V23, a locked DB), the in-memory
  /// packet still prices the item. Storage is an upgrade to the evidence, never a
  /// dependency of the pinch.</para>
  /// </summary>
  private List<LaneSale> ReadLaneEvidence(uint itemId)
  {
    try
    {
      var ring = GilStorage.ReadSaleHistory(itemId);
      if (ring.Count > 0)
        return ring.Select(r => new LaneSale(r.UnitPrice, r.SaleTime, r.IsHq)).ToList();
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, $"[Regime] Tape read failed for item {itemId} — pricing off the live packet instead");
    }

    return _mbHandler.GetLaneSales(itemId);
  }

  /// <summary>
  /// Final step: applies the calculated price to the listing.
  /// Orchestrates addon reading, price evaluation, and confirm/cancel.
  /// Business logic lives in ApplyPriceDecision; the addon touches live in
  /// <see cref="RetainerPanelActions"/> and the accounting in
  /// <see cref="PricingPassEnding"/>.
  /// </summary>
  internal unsafe bool? SetNewPrice()
  {
    var currentItem = Plugin.CurrentRun?.CurrentItem;
    ItemPayload? itemPayload = null;
    var listingQuantity = 1;
    // Hoisted out of the try because the ENDING has to know (review ruling S7): a
    // refused cached post books no gil and heals no flags, and both of those live
    // below in the finally.
    var cachedRefused = false;
    // WHICH PASS IS RUNNING, read ONCE at the door. IsReconRun and IsHawkRun both ask
    // the LIVE run, and since S9 this method has a path that tears the live run down
    // (the exception door aborts a recon before the finally runs). Re-asking afterwards
    // would answer "neither", and the ending's two mode guards would both fall open -
    // booking a recon's would-have-asked price as gil on market on the one path where
    // nothing at all happened. The mode a pass ran under cannot change mid-pass, so a
    // local is not a cache, it is the fact.
    var isReconRun = IsReconRun;
    var isHawkRun = IsHawkRun;

    try
    {
      if (currentItem?.Result == PricingResult.Skipped)
        return true;

      RetainerPanelActions.CloseItemSearchResult();

      if (!RetainerPanelActions.TryGetReadySellPanel(out var retainerSell))
        return false;

      var itemName = RetainerPanelActions.PopulateItemFromAddon(
        retainerSell, currentItem, listingFromBags: !IsPinchRun, out itemPayload, out listingQuantity);

      // Always populate 14-day history stats for triage context
      // Must run after PopulateItemFromAddon which sets ItemId
      if (currentItem != null)
        _mbHandler.PopulateHistoryStats(currentItem);

      // Ban check — item goes through MB lookup but price is never changed
      if (itemPayload != null)
      {
        var banId = itemPayload.IsHQ ? itemPayload.ItemId + 1_000_000u : itemPayload.ItemId;
        if (Plugin.Configuration.BannedItemIds.Contains(banId))
        {
          var cleanName = Communicator.CleanItemName(itemName, out _);
          var mb = currentItem?.MbPrice;
          // "Listed at" is a claim, and a banned item met in a HAWK is sitting in the
          // bags with nothing on the board (SF-P6): the operand is null there, so the
          // line says where the item actually is instead of printing an empty number.
          var where = currentItem?.CurrentListingPrice is int listed && listed > 0
            ? $"Listed at {listed:N0}"
            : "Not listed";
          var detail = mb.HasValue
            ? $"{where}, MB at {mb:N0}"
            : $"{where}, no MB data";
          Plugin.Ledger?.AddEntry(ItemOutcome.Banned, cleanName, detail);
          if (currentItem != null) currentItem.Result = PricingResult.Banned;

          // Confirm without changing (keeps original price). Recon CANCELS instead:
          // unreachable today (the listable scan already drops banned variants), and
          // that is exactly when a wrong default is cheapest to fix - recon has one
          // rule about this panel and it holds on every path out of this method.
          RetainerPanelActions.ConfirmOrCancel(retainerSell, confirm: !isReconRun);
          return true;
        }
      }

      // THE CACHED POST (Rounds unit 3): the act leg spends what the Look leg bought.
      //
      // The fork itself was taken upstream, at enqueue time, where declining it is
      // free - an item without a postable banked decision simply gets the classic
      // chain's tasks and arrives here having read a real board, exactly as it always
      // did. So reaching this branch means the gate already answered three questions
      // (a row exists, it is inside ReconFreshHours, and its price still clears
      // today's floor and minimum) and no board was asked about this item at all.
      //
      // What is left to ask is the VETO, and today it answers "nothing fresh" every
      // time. The decision then applies WHOLE: the banked price becomes the ask and
      // the ordinary hawk listing path writes it, confirms it, feeds the standing
      // book and books the listing - because a cached post is a REAL listing, and the
      // only thing about it that differs from a fresh one is where the number came
      // from. Which the transcript says out loud.
      //
      // The identity check is the one fail-closed door this branch owns. The plan was
      // composed against a BAG SLOT, and the upstream slot guard compares item ids
      // only - HQ is a slot flag it never looks at. A plan meeting a panel it was not
      // built for therefore has to refuse rather than fall through to the spine,
      // because the spine would run with no board packet at all and could still
      // produce a tape-derived price. Refusing costs one unlisted item and the next
      // run picks it up; the alternative is posting an NQ answer on an HQ listing and
      // never knowing.
      // The confirmed-proposal door died with the deep-cut guard (3.1 sweep) -
      // its only producer was the guard's warn row, delisted 08-23 and removed
      // here with its whole species (UndercutTooDeep, ConfirmedPrice).
      var ridingCache = false;
      if (currentItem?.CachedPost is { RidesCache: true } cached
          && !cached.Matches(currentItem.ItemId, currentItem.IsHq))
      {
        cachedRefused = true;
        currentItem.Result = PricingResult.NoData;
        Svc.Log.Warning($"[Bell] Cached post refused: the panel opened on "
          + $"{currentItem.ItemId}{(currentItem.IsHq ? " HQ" : "")}, the banked decision was for "
          + $"{cached.ItemId}{(cached.IsHq ? " HQ" : "")} - nothing posted.");
      }
      else if (currentItem?.CachedPost is { RidesCache: true } banked)
      {
        var verdict = CachedPostVeto.Decide(
          currentItem.ItemId, currentItem.IsHq, banked.Price,
          FreshBoardProvider, VetoDisagreeTolerancePct);

        if (verdict == VetoVerdict.PostCached)
        {
          ridingCache = true;
          currentItem.FinalPrice = (int)Math.Min(banked.Price, int.MaxValue);
          currentItem.Result = PricingResult.Pending;

          // THE TRUE-UP'S OPERAND, adopted from the banked row (unit 4). No receipt
          // is written this pass - no spine ran - so the receipt this listing belongs
          // to is the one recon wrote, and it is still carrying recon's anchor rather
          // than the gil about to be posted. Adopting the id here means the pass's
          // ordinary ending corrects it with no branch of its own. See
          // CachedPostTrueUp for what this touches.
          if (CachedPostTrueUp.Adopt(banked, refused: false) is long adoptedReceipt)
            currentItem.ReceiptId = adoptedReceipt;

          var cachedName = Communicator.CleanItemName(itemName, out _);
          Plugin.Ledger?.AddEntry(ItemOutcome.PostedFromRecon, PricingVoice.VoiceName(currentItem, cachedName),
            CachedPostNote.Line(banked.Price, banked.BankedAt,
              DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }
        // RePriceOnFresh needs no branch of its own: the spine block below is the
        // re-price, and it runs for exactly the items that did not ride the cache.
      }

      // --- Lane decision: the pricing spine ---
      // "Listings are what people want; sales are what people paid." The lane
      // (recency-weighted clearing price from the MB history packet) is the
      // model; the board is positioning only. ONE decision function, both
      // pricing paths — safety references are lane-relative, so the fresh
      // Hawk listing door gets the same protection as the reprice door (the
      // fence was written through the door with different rules).
      // Runs for triage-reprice bypass items too: bypass skips HOLDS and caps,
      // never the anchor choice — a bypassed reprice must not price off a lone
      // crazy or a row over the rail (the old triage-Reprc-off-the-fence disease).
      // Foreign rows only (-1 = the lane block didn't run). Our own listing is
      // the thing being repriced, never the queue: a board holding nothing but
      // our own row IS an empty board, and A10's genuine silence is exactly that
      // plus a tape that cannot speak.
      var laneForeignCount = -1;
      if (currentItem != null
          && currentItem.ItemId != 0
          && !currentItem.FromPriceCache
          // A refused cached post must not fall through to the spine: no board was
          // read for this item, so the spine would price it off a cold tape alone.
          && !cachedRefused
          // A cached post has already got its answer, from a spine that ran against
          // a real board. Running the spine again HERE would run it against no board
          // at all (nothing was asked) and a tape read cold - which would produce an
          // EmptyBoard or a thin-history hold and overwrite the banked answer with a
          // strictly worse one. The point of the cache is that this arithmetic was
          // never the expensive part; the board was.
          && !ridingCache)
      {
        var laneCfg = new LaneConfig
        {
          CeilingMult = Plugin.Configuration.UpwardRepriceMultiplier,
          MinHistorySamples = Plugin.Configuration.LaneMinHistorySamples,
          HalfLifeDays = LaneHalfLife.Resolve(currentItem.ItemId),
          HqPremiumPct = Math.Clamp(Plugin.Configuration.HqPremiumPercent, 0, 100) / 100.0,
          SeatBudget = Plugin.Configuration.SeatBudget,
        };
        var hqPricing = Plugin.Configuration.HQ && currentItem.IsHq;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sales = ReadLaneEvidence(currentItem.ItemId);

        var board = _mbHandler.GetBoard(currentItem.ItemId, hqPricing);
        laneForeignCount = 0;
        foreach (var listing in board)
          if (!listing.IsOwn)
            laneForeignCount++;

        // THE PRICING SPINE (extracted to LaneEvaluation, 08-02 — one
        // composition, two callers; the ledger's relist preview is the other).
        // The community provider fires only when the local lane is too thin,
        // preserving the miss-queues-a-fetch lifecycle; the velocity provider
        // likewise resolves packet-then-almanac only when no local segment can
        // report a pace (A5: one window, two readings — Sarcenet's "9.55/day"
        // was real, at 600-1,500, which is why a fallback rate never outranks
        // the segment's own).
        var item = currentItem;
        var answer = LaneEvaluation.Evaluate(
          sales, board, currentItem.IsHq, hqPricing, laneCfg, now,
          communityProvider: () =>
          {
            var community = UniversalisHistory.TryGet(item.ItemId);
            if (community == null)
              item.CommunityQueued = true; // miss queued a fetch; warm next pinch
            return community;
          },
          fallbackVelocityProvider: () =>
            _mbHandler.GetPacketVelocityPerDay(item.ItemId)
              ?? UniversalisStats.TryGet(item.ItemId, item.IsHq)?.Velocity,
          currentItem.CurrentListingPrice);
        var (segment, lane, velocity, decision) =
          (answer.Segment, answer.Lane, answer.Velocity, answer.Decision);

        // Evidence snapshot for decision memory: the world this hold is judged
        // against. READS ONLY (board packet + settled sales already in hand — no
        // listings-table write). Cheapest foreign board price is the undercut
        // probe; newest quality-matched sale + lane sample count are the "did
        // history grow" probe; the standing listing is the manual-price anchor.
        long cheapestForeign = 0;
        foreach (var listing in board)
          if (!listing.IsOwn && (cheapestForeign == 0 || listing.UnitPrice < cheapestForeign))
            cheapestForeign = listing.UnitPrice;
        long latestSale = 0;
        foreach (var s in sales)
          if (s.IsHq == hqPricing && s.Timestamp > latestSale)
            latestSale = s.Timestamp;
        currentItem.LaneEvidence = new StandingMemory.EvidenceSnapshot(
          currentItem.CurrentListingPrice ?? 0,
          lane?.SampleCount ?? 0,
          latestSale,
          cheapestForeign);

        if (lane != null)
        {
          Svc.Log.Debug($"[Regime] item {currentItem.ItemId}{(hqPricing ? " HQ" : "")}: "
            + $"segment {segment.SegmentCount}/{segment.ElectorateCount} sales, cut {segment.Cut}, "
            + $"band {lane.BandLow:N0}-{lane.BandHigh:N0} around {lane.Median:N0}"
            + (velocity is double v ? $", {v:0.##}/day" : "")
            + $" [{lane.Source}]");
        }

        currentItem.Lane = decision;

        // Decision receipt (M4): one row per pricing decision, coordinates RELATIVE
        // (item-agnostic) and read from the LIVE board + lane at decision time, never
        // from the event log (design Section 7). arm_id carries exactly one value
        // today: 'recon' on a Look's receipt, cleared at adoption (08-23 - see the
        // insert call below); the racing-arms use A10 retired never happened. The
        // outcome join fills later from a GilTrack confirm / evict, never here; A9
        // grades "did position 1 clear by the next pinch" off exactly these rows.
        try
        {
          var foreignDepth = 0;
          foreach (var l in board) if (!l.IsOwn) foreignDepth++;
          // Spread reads the SEGMENT, the same population lane_n counts — a spread
          // measured over demoted evidence would describe a market this receipt
          // did not price into.
          double spread = 0;
          long lo = long.MaxValue, hi = 0; var laneN = 0;
          foreach (var s in segment.Sales)
            if (s.IsHq == hqPricing) { laneN++; if (s.UnitPrice < lo) lo = s.UnitPrice; if (s.UnitPrice > hi) hi = s.UnitPrice; }
          if (laneN > 1 && lane != null && lane.Median > 0)
            spread = (hi - lo) / lane.Median;

          long decidedPrice = decision.Anchor ?? currentItem.CurrentListingPrice ?? 0;

          var coords = DecisionReceipts.Compute(new DecisionReceipts.ReceiptInputs(
            DecidedPrice: decidedPrice,
            LaneMedian: lane?.Median ?? 0,
            LaneSampleCount: lane?.SampleCount ?? 0,
            LaneWeightedAgeDays: lane?.WeightedAgeDays ?? 0,
            LaneSpread: spread,
            BoardDepth: foreignDepth,
            VelocityPerDay: velocity,
            Quantity: currentItem.Quantity,
            LaneStackNorm: null, // lane sale quantity not surfaced yet - column rides for later
            BandLow: lane?.BandLow ?? 0,
            BandHigh: lane?.BandHigh ?? 0,
            SegmentCount: segment.SegmentCount,
            SegmentCut: segment.Cut.ToString(),
            // The A10 coordinate: stepped-over crazies sit ahead of us by
            // construction, so CraziesSkipped IS the insertion index. A held row
            // never lists and has no position - null, not a zero that would read
            // as front-of-queue.
            QueuePosition: decision.Outcome == LaneOutcome.HeldThinHistory
              ? null : decision.CraziesSkipped,
            ClusterSize: decision.ClusterSize,
            // CompetitorPosition retired 08-23 (defaults null in ReceiptInputs):
            // "structurally zero on every listed row" was the fossil claim - the
            // audit read 0 on all 1,623 post-V30 rows including the Caligae
            // write that sat 4th. The true seat rides seat_at_write instead:
            // Census.Seat is SeatOf(queue, the write) since F1, 1-based, every
            // cheaper foreign row counted. Seat 0 = the walk wrote nothing.
            SeatAtWrite: decision.Census.Seat > 0 ? decision.Census.Seat : null,
            // The V47 shadow (ruled 08-23): the 3.1 queue-doctrine candidate's
            // answer on this same board. Write-only food for the 3.1 ruling.
            ShadowPrice: decision.Shadow?.Price,
            ShadowSeat: decision.Shadow?.Seat,
            ShadowDefense: decision.Shadow?.Defense,
            // The x-of-y coordinate: what the game said the whole board held.
            // depth < total on a receipt = that decision ran censored.
            BoardTotal: _mbHandler.BoardTotal,
            // The V35 spans: the queue position's story in gil (what the
            // crashers were, what the real line asked) - readable off the row.
            CrasherFloor: decision.CrasherFloor,
            CrasherCeiling: decision.CrasherCeiling,
            ClusterFloor: decision.ClusterFloor,
            ClusterCeiling: decision.ClusterCeiling,
            // The V37 operand: the crowd that won the outnumbering test. Null
            // rather than zero when no pack was stepped - no test ran, so no
            // crowd won it, and a zero would read as "outnumbered by nobody".
            CrowdBehind: decision.CrowdBehind > 0 ? decision.CrowdBehind : null));

          // A9 grading, BEFORE the new receipt lands: the item coming back through
          // the pipeline IS the trigger, and the evidence is already in hand - the
          // tape we priced off and the board we positioned against, no second read.
          // Order matters: once the new receipt is inserted, "the newest receipt"
          // would be the one we just wrote instead of the one being judged.
          // Its OWN catch, deliberately: grading is a dark readout that feeds
          // nothing, and it must never be able to cost us the receipt - the
          // evidence - by throwing on the way to writing it.
          try
          {
            var settles = new List<ReceiptGrading.Settle>();
            foreach (var s in sales)
              if (s.IsHq == hqPricing) settles.Add(new ReceiptGrading.Settle(s.UnitPrice, s.Timestamp));
            var foreignAsks = new List<long>();
            foreach (var l in board) if (!l.IsOwn) foreignAsks.Add(l.UnitPrice);
            GilStorage.GradeReceiptsOnRecurrence(
              currentItem.ItemId, currentItem.IsHq, currentItem.RetainerName, settles, foreignAsks);
          }
          catch (Exception gex)
          {
            Svc.Log.Debug($"[Receipt] Grading pass skipped for {currentItem.ItemId}: {gex.Message}");
          }

          currentItem.ReceiptId = GilStorage.InsertDecisionReceipt(
            currentItem.ItemId, currentItem.IsHq, currentItem.RetainerName,
            GilTracker.GetItemCategory(currentItem.ItemId),
            // THE LOOK'S MARK (Drift, 08-23: "I haven't listed the sword yet" -
            // a recon receipt wore ask grammar on every reader surface). A recon
            // pass posts NOTHING; its receipt is a Look until the hawk adopts it,
            // and the true-up clears the mark at that adoption. Ask-speaking
            // surfaces and the grader key off it - a decision nobody posted is
            // not an ask and grades nothing.
            isReconRun ? "recon" : null,
            coords, decision.Outcome.ToString(), decision.Evidence,
            decidedPrice: decidedPrice,
            // THE STANCE, banked with the decision (V45). Read HERE rather than at
            // the write site because "the posture" means the knobs as they stood
            // when this price was decided, and the insert happens on the same
            // instant - a config read inside GilStorage would be a read of whatever
            // the knobs say by the time the row lands. Capture only: the 4.0
            // undercut report card is the consumer and nothing reads it back today.
            undercutPosture: UndercutPosture.Compose(
              Plugin.Configuration.UndercutMode,
              Plugin.Configuration.UndercutAmount,
              Plugin.Configuration.UndercutSelf,
              Plugin.Configuration.EnableMaxPriceIncreaseCap,
              Plugin.Configuration.MaxPriceIncreasePercentage,
              Plugin.Configuration.UpwardRepriceMultiplier));
        }
        catch (Exception ex)
        {
          Svc.Log.Warning($"[Receipt] Failed to write decision receipt: {ex.Message}");
        }

        if (decision.Outcome == LaneOutcome.HeldThinHistory)
        {
          if (currentItem.BypassPriceGuards)
          {
            // Drift ordered this reprice; with no lane to consult, his call
            // rides the first-pass anchor. Bypass beats the hold, not the lane.
          }
          else
          {
            // No lane anywhere — never act on a guess wearing numbers. The
            // own-sales fallback below may still price a fully SILENT market;
            // otherwise the item holds and flags for Drift.
            currentItem.FinalPrice = null;
            currentItem.Result = PricingResult.LaneHeld;
          }
        }
        else if (decision.Anchor is long anchor)
        {
          var anchorInt = (int)Math.Min(anchor, int.MaxValue);
          // The lane never classifies our own listings as competition, so a
          // listing anchor is always a foreign price to undercut — an own
          // listing can no longer BE the anchor. UndercutSelf keeps its meaning
          // only in the first-pass offerings path, not lane classification.
          // A cross-quality anchor must be BEATEN, not matched — Gentleman's
          // Match against a strictly better HQ writes the very corpse the rail
          // exists to prevent.
          var price = decision.AnchorIsListing
            ? _mbHandler.ApplyUndercutMode(anchorInt, isOwnListing: false, decision.CrossQualityCapped)
            : anchorInt;

          // THE ONE FLOOR LAW (ruled 2026-08-21). The honest ask - priced off the full
          // lane, NEVER clamped up - either clears max(minimum, mode floor) or no legal
          // listing exists. One calculation point (PriceFloor.Effective), one verdict,
          // shared with the cached-post re-check, the first-pass sentinel and the
          // Ledger's relist preview.
          var floor = PriceFloor.Effective(
            Plugin.Configuration.PriceFloorMode, currentItem.VendorPrice,
            Plugin.Configuration.MinimumListingPrice);
          if (floor.Refuses(price))
          {
            currentItem.FinalPrice = null;
            currentItem.RejectedPrice = price; // the operand that lost - narration renders THIS, not the board read
            currentItem.Result = PricingResult.BelowFloor;
          }
          else
          {
            currentItem.FinalPrice = price;
            currentItem.Result = PricingResult.Pending;
          }
        }
      }

      // Own-sales fallback — when the market is fully silent (no listings AND
      // no usable history), the player's own last sale prices the item.
      // Staleness-gated by StalePriceDays, never discounted (sole seller =
      // premium position), always labeled in chat. Locked spec 2026-07-10.
      // A lane hold with listings PRESENT does not fall back — pricing a
      // thin-history item off one stale own sale is the convicted baseline.
      if (currentItem != null
          && currentItem.ItemId != 0
          // A refused cached post read no board at all, and the own-sales fallback is
          // the last door in this method that can produce a price without one. It
          // exists for a market proven SILENT - a board read that came back empty -
          // and a refusal proves nothing about the market. Fail closed.
          && !cachedRefused
          && currentItem.Result != PricingResult.BelowFloor
          && (currentItem.Result != PricingResult.LaneHeld || laneForeignCount == 0)
          && (currentItem.FinalPrice == null || currentItem.FinalPrice <= 0))
      {
        (int Price, long Timestamp)? ownSale = null;
        try { ownSale = GilStorage.GetLastSalePriceWithTime(currentItem.ItemId, currentItem.IsHq); } catch { /* storage unavailable */ }

        if (ownSale is (int salePrice, long saleTs) && salePrice > 0)
        {
          var ageDays = (int)((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - saleTs) / 86400);
          if (Plugin.Configuration.StalePriceDays <= 0 || ageDays <= Plugin.Configuration.StalePriceDays)
          {
            currentItem.FinalPrice = salePrice;
            currentItem.Result = PricingResult.Pending;
            Communicator.PrintOwnSalesFallback(itemName, salePrice, ageDays);
          }
        }
      }

      // Hotkey path (no CurrentItem) rides _hotKeyPrice. With an item in hand,
      // FinalPrice is authoritative — a lane hold nulls it, and falling back to
      // the first-pass _hotKeyPrice would price the held item anyway.
      var newPrice = currentItem != null ? currentItem.FinalPrice : _hotKeyPrice;

      // RECON'S TAIL (Rounds unit 2). Everything above this line ran exactly as it
      // runs for a hawk item - which is the point: recon's reads are pinch-grade
      // evidence, and a cheaper pass that skipped the spine would bank a guess. What
      // changes is only the last act: the decision goes to the cache and the panel is
      // CANCELLED, always, on every outcome. There is no branch here that can reach
      // ApplyPriceDecision, which is what makes "recon never writes a price" a fact
      // about the code rather than a promise about it.
      if (isReconRun)
      {
        BankReconDecision(currentItem, itemName, newPrice);
        RetainerPanelActions.ConfirmOrCancel(retainerSell, confirm: false);
        return true;
      }

      var confirmed = ApplyPriceDecision(retainerSell, currentItem, itemName, newPrice);

      RetainerPanelActions.ConfirmOrCancel(retainerSell, confirmed);
      return true;
    }
    catch (Exception ex)
    {
      // THE CANCEL IS STRUCTURAL (review ruling S9, 2026-08-12). "Recon always
      // cancels" was branch-complete and exception-incomplete: every RETURN from the
      // block above cancels the panel, and an unguarded throw took none of them. The
      // throw sites are real and ordinary - a Lumina GetRow on an item the sheet does
      // not know, AtkValues[8] on a panel whose layout the game changed - and the
      // ECommons task manager swallows the exception, nulls the current task and runs
      // the NEXT one. So the pass walked on with a sell panel standing open, pre-filled
      // with the game's suggested ask, and the following item's context-menu click
      // landing on top of it. One stray confirm away from listing an item at a price
      // nobody decided.
      //
      // So the teardown moves into the path that always runs. It cancels rather than
      // confirms on every mode, which is the only fail-closed reading of "we do not
      // know what just happened": nothing gets listed off an exception.
      RetainerPanelActions.CancelPanelAfterThrow();

      Svc.Log.Error(ex, "[Pricing] SetNewPrice threw - the sell panel was cancelled");
      if (Plugin.Configuration.ShowErrorsInChat)
        Svc.Chat.PrintError($"[Scrooge] Pricing failed on this item: {ex.Message}");

      if (isReconRun)
      {
        // AND THEN THE ERROR REPORTS. A recon pass whose panel had to be force-cancelled
        // cannot be trusted to walk to the next item, and recon is a round stage marked
        // done at fire time - a silent stop here would leave the round strolling on to
        // the melt as though every board had been read. Abort reports the death in these
        // words, and the null return below tells the task manager to drop the rest of
        // the chain (ReconProcessNext included) rather than march on over a corpse.
        Plugin.PinchHost.AbortReconRun($"the pricing pass threw on an item: {ex.Message}");
        return null;
      }

      // The bell survives one bad item. Its own post-price handler owns the walk to the
      // next one and reads this result; Skipped is the honest word for an item nothing
      // was decided about, and it is already the result that books no gil and heals no
      // flags in the ending below.
      if (currentItem != null) currentItem.Result = PricingResult.Skipped;
      return true;
    }
    finally
    {
      // THE ENDING, whole and in one place (review pricing split, 2026-08-16). Its five
      // jobs and their five guards are unchanged; what changed is that the pass's facts
      // arrive as operands rather than as a second read of the live run - see
      // PricingPassEnding for why that distinction is the load-bearing one here.
      PricingPassEnding.Run(new PricingPassOperands(
        Result: currentItem?.Result ?? PricingResult.Pending,
        Item: currentItem,
        ItemPayload: itemPayload,
        ListingQuantity: listingQuantity,
        IsReconRun: isReconRun,
        IsHawkRun: isHawkRun,
        CachedRefused: cachedRefused,
        MbHandler: _mbHandler));

      // The runless door's carried price is this file's own state, so it is reset here
      // rather than inside the ending. Nothing in the ending reads or writes it.
      _hotKeyPrice = null;
    }
  }

  /// <summary>
  /// Evaluates the price and applies it to the RetainerSell addon.
  /// Handles valid price (undercut/cap checks), error routing (floor/min/nodata),
  /// and hawk-run vendor-sell. All chat messages and log entries happen here.
  /// Returns true = confirm (price applied or kept), false = cancel (error/skip).
  /// </summary>
  /// <remarks>
  /// CapBlocked returns true (confirm) because the addon value was never
  /// changed — confirming just keeps the old price (no-op).
  /// </remarks>
  private unsafe bool ApplyPriceDecision(
    AddonRetainerSell* retainerSell, PricingItem? currentItem,
    string itemName, int? newPrice)
  {
    var cleanName = Communicator.CleanItemName(itemName, out _);

    // --- Valid price path ---
    if (newPrice.HasValue && newPrice > 0)
    {
      if (IsHawkRun)
      {
        Svc.Log.Debug($"Setting new listing price");
        RetainerPanelActions.SetAskingPrice(retainerSell, newPrice.Value);
        // Chat's price-change line needs a price it CHANGED FROM, and a hawk listing has
        // none since SF-P6 (the pre-filled suggestion was never an ask), so this prints
        // nothing on a fresh post rather than narrating "Pinching from 7 to 395" about a
        // move that never happened. The run log still speaks the List.
        Communicator.PrintPriceUpdate(itemName, currentItem?.CurrentListingPrice, newPrice.Value, 0f);
        Plugin.Ledger?.IncrementAdjusted();
        if (currentItem != null) currentItem.Result = PricingResult.Listed;
        LogLaneOutcome(currentItem, cleanName);
        // Warn-and-list died with the lane (2026-07-13): a genuinely silent
        // market HOLDS instead of listing at an unguarded anchor, and no price
        // over the rail can be written — nothing suspicious gets listed about.
      }
      else
      {
        // Slow-mover pressure is GONE (cleanup pass): its ladder took a second cut
        // on top of a price the spine had already chosen, and the eviction question
        // it answered is rethought at 3.1. The spine's number is the number.
        var oldPrice = currentItem?.CurrentListingPrice ?? RetainerPanelActions.ReadAskingPrice(retainerSell);
        var cutPercentage = oldPrice > 0 ? ((float)newPrice.Value - oldPrice) / oldPrice * 100f : 0f;

        // The upward-hold guard died with the lane (2026-07-13): its
        // own-last-sale baseline was one stale data point (~half of live holds
        // were false positives), and it blocked downward deepens too. The lane
        // ceiling carries the 3x discipline with an absolute reference now.

        // The deep-cut guard died in the 3.1 sweep (delisted 08-23, inert at its
        // 100 default - the lane owns crasher defense: company test,
        // nonsense-half, floor). Every proposed cut writes; the cap below still
        // governs raises.
        {
          if (IsPinchRun && Plugin.Configuration.EnableMaxPriceIncreaseCap
              && cutPercentage > Plugin.Configuration.MaxPriceIncreasePercentage
              && currentItem?.BypassPriceGuards != true)
          {
            // CLAMP AND CLIMB (ruled 07-26). The cap used to SKIP here, and a
            // skip never shrinks the gap: a lane crushed by the old race logic
            // (Multifaceted Cotton at 75 against a ~230 band) hit the same cap
            // every pinch and was fossilized at its crushed price forever - a
            // fence with no gate. The guard's intent survives whole in the
            // clamp: no single jump ever exceeds the cap, but the lane heals
            // over successive pinches (75 -> 150 -> 300 -> in-band). The wanted
            // price still persists as the operand so the narration renders the
            // proposal, not just the step.
            var cappedPrice = (int)Math.Max(
              oldPrice + 1,
              Math.Floor(oldPrice * (1.0 + Plugin.Configuration.MaxPriceIncreasePercentage / 100.0)));
            _cachedPrices.TryAdd(itemName, cappedPrice);
            RetainerPanelActions.SetAskingPrice(retainerSell, cappedPrice);
            Communicator.PrintPriceUpdate(itemName, oldPrice, cappedPrice, Plugin.Configuration.MaxPriceIncreasePercentage);
            Plugin.Ledger?.IncrementAdjusted();
            // ITS OWN OUTCOME, NOT Skipped (ruled B7, built 3b-7). A capped climb WROTE
            // a price - the ask moved, the gil is on the board - and Skipped renders
            // alarm-red "rule blocked, no price set". Red means blocked again.
            Plugin.Ledger?.AddEntry(ItemOutcome.RepriceCapped, PricingVoice.VoiceName(currentItem, cleanName),
              RunLogVoice.RepriceCapped(oldPrice, cappedPrice, newPrice.Value,
                currentItem?.Lane?.Census ?? default, currentItem?.Lane?.Evidence));
            if (currentItem != null)
            {
              currentItem.Result = PricingResult.Applied;
              currentItem.PriceChangePercent = Plugin.Configuration.MaxPriceIncreasePercentage;
              currentItem.RejectedPrice = newPrice.Value; // the full proposal the cap deferred
            }
          }
          else
          {
            Svc.Log.Debug($"Setting new price");
            _cachedPrices.TryAdd(itemName, newPrice);
            RetainerPanelActions.SetAskingPrice(retainerSell, newPrice.Value);
            Communicator.PrintPriceUpdate(itemName, oldPrice, newPrice.Value, cutPercentage);
            // A kept ask is a Held, not an adjustment (ruled 08-15) - the counter
            // splits exactly where the run log's verb does.
            if (newPrice.Value == oldPrice)
            {
              Plugin.Ledger?.IncrementHeld();
              // A held FIRST seat gets the reverse gap test (ruled 2026-08-23):
              // if we read as the crasher under the reachable line, say so.
              PersistCrasherSeatFlag(currentItem, oldPrice);
            }
            else Plugin.Ledger?.IncrementAdjusted();
            if (currentItem != null) currentItem.Result = PricingResult.Applied;
            LogLaneOutcome(currentItem, cleanName);
          }
        }
      }

      return true; // confirm — price was applied, or kept unchanged (cap/undercut)
    }

    // --- Error path: no valid price ---
    var result = currentItem?.Result ?? PricingResult.NoData;

    switch (result)
    {
      case PricingResult.LaneHeld:
      {
        // "Held (thin history)" — verdicts unify, execution stays local:
        // keep-price-and-flag at the pinch, don't-auto-price in a Hawk run.
        // A genuine MB no-response (all retry windows elapsed) reads
        // identically to thin history at this point; MbTimedOut distinguishes
        // it so the flag says "didn't respond" instead of "too thin".
        var evidence = PricingVoice.HeldEvidence(currentItem);
        var heldReason = PricingVoice.HeldReason(currentItem);
        Plugin.Ledger?.AddEntry(ItemOutcome.LaneHeld, PricingVoice.VoiceName(currentItem, cleanName),
          RunLogVoice.Skip(currentItem?.CurrentListingPrice, heldReason,
            currentItem?.Lane?.Census ?? default, evidence));
        Communicator.PrintLaneHeld(itemName, heldReason);
        PersistLaneHeldFlag(currentItem, evidence);
        // Pinch: confirm keeps the old price (no-op). Hawk: cancel — never
        // list at an unguarded anchor (warn-and-list's replacement).
        return !IsHawkRun;
      }

      case PricingResult.BelowFloor:
        // THE DOMAN DESTINY (ruled 2026-08-21): under the Enclave floor, an item that
        // fails it is worth 2x at the Enclave and 1x at the counter - so auto-vendor
        // is REFUSED for it, whatever the toggle says. The config window disables the
        // toggle when the mode is chosen, but a settings file that carries both is
        // exactly the case a mode check has to survive.
        if (IsHawkRun && Plugin.Configuration.AutoVendorSellOnPriceCheckFail
            && Plugin.Configuration.PriceFloorMode != PriceFloorMode.DomanEnclave)
        {
          // Capture the verdict BEFORE the switch: the executor needs VendorSell,
          // the narrator needs the reason that produced it. Reading Result after
          // this line only ever yields VendorSell, which made "Below floor" /
          // "Below minimum" unreachable in TrackVendorSale (07-24).
          if (currentItem != null)
          {
            currentItem.VendorFallbackFrom = result;
            currentItem.Result = PricingResult.VendorSell;
          }
          Svc.Log.Debug($"[HawkRun] Price check failed — will vendor-sell");
        }
        else
        {
          // ONE NARRATION, operands named. The honest ask is the operand the floor
          // actually refused (RejectedPrice on the lane path); MbPrice is the board
          // read and can sit far above it, which is the Mossy Stone Daggers lie.
          var floor = PriceFloor.Effective(
            Plugin.Configuration.PriceFloorMode, currentItem?.VendorPrice,
            Plugin.Configuration.MinimumListingPrice);
          var honestAsk = (long)(currentItem?.RejectedPrice ?? currentItem?.MbPrice ?? 0);
          Communicator.PrintBelowPriceFloorError(itemName, floor, honestAsk);
          Plugin.Ledger?.AddEntry(ItemOutcome.Skipped, PricingVoice.VoiceName(currentItem, cleanName),
            RunLogVoice.Skip(currentItem?.CurrentListingPrice,
              RunLogVoice.Reasons.BelowFloor(floor, honestAsk),
              currentItem?.Lane?.Census ?? default, currentItem?.Lane?.Evidence));
        }
        break;

      default:
        Svc.Log.Warning("SetNewPrice: No price to set");
        Communicator.PrintNoPriceToSetError(itemName);
        Plugin.Ledger?.AddEntry(ItemOutcome.NoData, PricingVoice.VoiceName(currentItem, cleanName),
          RunLogVoice.Skip(currentItem?.CurrentListingPrice, RunLogVoice.Reasons.NoBoardData));
        if (currentItem != null) currentItem.Result = PricingResult.NoData;
        break;
    }

    return false; // cancel
  }

  /// <summary>
  /// Files the composed lane-outcome row, if the outcome has anything to say. The
  /// composition itself is <see cref="PricingVoice.LaneOutcomeEntry"/> - this is the
  /// half that needs a ledger.
  /// </summary>
  private static void LogLaneOutcome(PricingItem? item, string cleanName)
  {
    if (PricingVoice.LaneOutcomeEntry(item, cleanName) is not { } row)
      return;

    Plugin.Ledger?.AddEntry(row.Outcome, row.Name, row.Voice);
  }

  /// <summary>
  /// Raises (or re-raises) the lane_held triage flag for a held item.
  ///
  /// <para>THIS IS NOT OPTIONAL FOR RECON, and the reason is structural rather than
  /// cosmetic. <see cref="PricingPassEnding"/> runs the self-heal round for every item
  /// that was really evaluated: any open flag whose rule did NOT re-fire this pass has
  /// resolved, so it closes. Recon evaluates in full, so the self-heal fires - and a
  /// recon that held an item without recording that the rule fired would CLOSE the very
  /// flag it just re-earned, quietly healing every thin-history item in the bags on its
  /// way past. The raise and the heal are two halves of one mechanism; a pass may skip
  /// both or run both, never one.</para>
  ///
  /// <para>Scope tags the container the flag points at, for the zombie round: a
  /// pinch holds an item in the retainer's sell LISTINGS (board); a hawk run - and
  /// recon, which walks the same bags - holds an unlisted item in the sell
  /// INVENTORY. Only a run that walks the matching container may later close it as
  /// item_gone.</para>
  /// </summary>
  private void PersistLaneHeldFlag(PricingItem? currentItem, string evidence)
  {
    if (currentItem == null || currentItem.ItemId == 0) return;

    // The lane_held rule fired — record it so the finally round keeps (rather than
    // heals) this flag, and evidence-key the upsert so a still-thin item that
    // changed nothing stays silent.
    currentItem.RaisedFlagReasons.Add("lane_held");
    try
    {
      GilStorage.UpsertStandingFlag(currentItem.ItemId, currentItem.IsHq,
        currentItem.RetainerName, currentItem.SlotIndex, "lane_held",
        // "not enough sales", not "thin history" (strings-three): the flag
        // detail draws straight into UI rows, and the evidence line already
        // says the numbers ("only 2 sales on record, need 3...").
        currentItem.MbTimedOut
          ? $"Held (board didn't answer) - {evidence}"
          : $"Held (not enough sales) - {evidence}",
        currentItem.CurrentListingPrice ?? 0, 0,
        currentItem.LaneEvidence, Plugin.Configuration.LaneMinHistorySamples,
        scope: IsHawkRun || IsReconRun
          ? StandingMemory.ScopeTag(StandingMemory.FlagScope.Inventory)
          : StandingMemory.ScopeTag(StandingMemory.FlagScope.Board));
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Standing] Failed to persist lane-held flag: {ex.Message}");
    }
  }

  /// <summary>
  /// THE CRASHER SEAT (ruled 2026-08-23, the Grade 2 Gemsap case). Pinch-only,
  /// held asks only: when the ask we just left standing sits crasher-far below
  /// the reachable line behind it, the board is telling us WE are the anomaly -
  /// no queue pressure on the cluster, and a real buyer takes our discount
  /// first. The walk already owns this arithmetic for other people's crashers;
  /// this points it at our own seat and routes the answer to triage instead of
  /// silently calling the hold a solid price.
  ///
  /// <para>Evidence-keyed like lane_held (the snapshot carries the cheapest
  /// foreign row, so a cluster that moves re-asks and an unchanged board stays
  /// silent), and registered in RaisedFlagReasons so the pass-end self-heal
  /// keeps it while the rule keeps firing - and closes it the pass the gap
  /// disappears (we raised, they came down, or the item sold).</para>
  /// </summary>
  private void PersistCrasherSeatFlag(PricingItem? currentItem, long heldAsk)
  {
    if (currentItem == null || currentItem.ItemId == 0) return;
    if (currentItem.Lane is not { } lane) return;
    if (!LanePricing.HeldAskReadsAsCrasher(heldAsk, lane.Census)) return;

    currentItem.RaisedFlagReasons.Add("crasher_seat");
    var floor = lane.Census.CompetitorFloor ?? 0;
    var gapPct = heldAsk > 0 ? (int)Math.Round((floor - (double)heldAsk) / heldAsk * 100) : 0;
    try
    {
      GilStorage.UpsertStandingFlag(currentItem.ItemId, currentItem.IsHq,
        currentItem.RetainerName, currentItem.SlotIndex, "crasher_seat",
        $"You read as the crasher here - your {heldAsk:N0} stands alone, and the "
          + $"{lane.Census.Competitors} real seller{(lane.Census.Competitors == 1 ? "" : "s")} "
          + $"behind you start {gapPct}% higher at {floor:N0}. A lone lowball puts no "
          + "pressure on them, and a buyer takes your discount first. Raise?",
        (int)heldAsk, (int)floor,
        currentItem.LaneEvidence, Plugin.Configuration.LaneMinHistorySamples,
        scope: StandingMemory.ScopeTag(StandingMemory.FlagScope.Board));
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Standing] Failed to persist crasher-seat flag: {ex.Message}");
    }
  }

  /// <summary>
  /// RECON'S ONLY WRITE: the spine's answer, banked to <c>decision_cache</c> for
  /// the act half to spend. No price is applied, no listing posted, no standing
  /// book entry made - recon changed nothing about the world except what we know
  /// about it.
  ///
  /// <para><b>Banked only when there is a decision to bank.</b> A null lane means
  /// the spine never ran for this item - the per-run price cache short-circuited
  /// it, or identity never resolved - and banking "no answer" as a fresh row would
  /// be the worst of both doors: recon would skip the item for the next 24 hours
  /// and unit 3 would find a row with nothing to post from. Writing nothing leaves
  /// the item stale, which is the honest state and the one that self-corrects.</para>
  ///
  /// <para><paramref name="decidedPrice"/> is <see cref="PricingItem.FinalPrice"/>
  /// - the guarded ask, after the undercut step, the floor and the minimum - not
  /// the raw lane anchor. That is what a listing pass would actually have written,
  /// and therefore the only number unit 3 can honestly post from cache. Null is a
  /// real answer: the spine held.</para>
  /// </summary>
  private void BankReconDecision(PricingItem? currentItem, string itemName, int? decidedPrice)
  {
    if (currentItem == null || currentItem.ItemId == 0) return;

    var cleanName = Communicator.CleanItemName(itemName, out _);

    // A hold still raises its flag - see PersistLaneHeldFlag for why the self-heal
    // round makes this mandatory rather than merely tidy.
    if (currentItem.Result == PricingResult.LaneHeld)
      PersistLaneHeldFlag(currentItem, PricingVoice.HeldEvidence(currentItem));

    if (currentItem.Lane is not { } decision)
    {
      // No spine ran (price-cache hit, or unresolved identity). Say so plainly and
      // leave the item stale rather than banking an answer nobody computed.
      Svc.Log.Debug($"[Recon] {cleanName}: no lane decision this pass - nothing banked, stays stale");
      currentItem.Result = PricingResult.Reconned;
      return;
    }

    // A guard rejection (below floor / below minimum) is a real verdict about the
    // price, and the price it rejected is not one we would ever post - so the cache
    // banks a HOLD, with the guard's own words. Unit 3 reading a row like this pays
    // the full chain rather than posting a number the guards already refused.
    var held = decidedPrice is not int price || price <= 0;

    // AND THE GUARD'S OWN WORDS ARE THE GUARD'S (the minors batch, 2026-08-12). The
    // sentence above was true of the null price and false of the label beside it: a
    // below-floor row banked outcome "Undercut" with nothing to undercut with, which
    // is a row that contradicts itself to every reader - the cached-post narration,
    // the receipt trail, anyone reading the table to ask why an item keeps getting
    // re-walked. The lane's outcome still names every row the spine actually priced;
    // a row the GUARDS turned down is named by the guard that turned it down. Nothing
    // branches on this string (the gate reads the null price), so the fix is honesty,
    // not behaviour.
    //
    // The GUARD family only. A LaneHeld row is the SPINE holding, and the lane's own
    // outcome (HeldThinHistory and its siblings) is already that verdict in its own
    // words - overriding it here would trade one honest label for another and lose
    // which evidence ran out.
    var outcome = held && currentItem.Result is PricingResult.BelowFloor
        or PricingResult.CapBlocked
      ? currentItem.Result.ToString()
      : decision.Outcome.ToString();

    try
    {
      GilStorage.UpsertDecisionCache(
        currentItem.ItemId, currentItem.IsHq,
        held ? null : decidedPrice,
        outcome,
        decision.Evidence,
        currentItem.ReceiptId,
        Plugin.Accountant.RoundRunId,
        DateTimeOffset.UtcNow.ToUnixTimeSeconds());
      // The lane median was banked here too, from V40 until the doctrine sweep
      // (2026-08-15) - the only thing it fed was the receipt ratio's recompute at
      // true-up time, and that ratio's writer is retired. The receipt id above is
      // what the surviving true-up (decided_price + retainer) needs.
    }
    catch (Exception ex)
    {
      // The bank is the pass's product, so a failure here is worth a warning rather
      // than a debug line - but it must never take the run down. The item simply
      // stays stale and the next recon walks it again.
      Svc.Log.Warning($"[Recon] Failed to bank the decision for {cleanName}: {ex.Message}");
    }

    // RECON IS WHAT COULD HAPPEN (Movement 1, rule 2). Nothing was posted and no gil
    // moved, so the line speaks a verdict rather than an act - and the seat it names
    // is the seat this ask WOULD take, straight off the census of the board recon
    // just read.
    Plugin.Ledger?.AddEntry(ItemOutcome.Reconned, PricingVoice.VoiceName(currentItem, cleanName),
      RunLogVoice.Recon(held ? null : decidedPrice, decision.Census,
        PricingVoice.ReconHoldReason(currentItem, decision, PriceFloor.Effective(
          Plugin.Configuration.PriceFloorMode, currentItem.VendorPrice,
          Plugin.Configuration.MinimumListingPrice)),
        decision.Evidence));

    currentItem.Result = PricingResult.Reconned;
  }

  private void OnNewPriceReceived(object? sender, NewPriceEventArgs e)
  {
    Svc.Log.Debug($"New price received: {e.NewPrice}");

    // Always capture for hotkey path (no CurrentRun/CurrentItem)
    _hotKeyPrice = e.NewPrice > 0 ? e.NewPrice : null;

    var item = Plugin.CurrentRun?.CurrentItem;
    if (item != null)
    {
      item.MbPrice = _mbHandler.LastCheckedPrice > 0 ? _mbHandler.LastCheckedPrice : null;

      if (e.NewPrice > 0)
      {
        item.FinalPrice = e.NewPrice;
      }
      // ONE SENTINEL FOR ONE VERDICT (2026-08-21). -2 and -3 were the mode floor and
      // the minimum arriving separately; the first-pass handler now applies the one
      // law and reports -2 for either. -3 stays understood on the read side because a
      // packet in flight when the plugin reloaded can still carry it.
      else if (e.NewPrice is -2 or -3)
        item.Result = PricingResult.BelowFloor;
      else
        item.Result = PricingResult.NoData;
    }
  }
}
