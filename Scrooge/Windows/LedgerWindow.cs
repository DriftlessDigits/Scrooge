using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Scrooge.Windows;

/// <summary>
/// The Ledger (M6 session 2): the one worklist surface. Evolved from the routing
/// window - it keeps the piles/reasons/overrides/Go/location-parity seed and widens
/// it into the action-named piles of the unified design (Review / List / Reprice /
/// Pull-and-Vendor / Melt / Churn / Watch, design Section 3). It ABSORBS the old
/// Triage inbox (design Section 7): held flags and this-run's skipped items become
/// the Review / Reprice / Pull-and-Vendor / Watch rows, keyed the same way as the
/// routing WorkItems, with their full action set preserved (Vend / Pull / Reprice /
/// Dismiss). There is no second surface: the TriageWindow is gone.
///
/// Bulk-ability IS the confidence threshold (design Section 4): each actionable pile
/// offers a one-click confirm that acts ONLY on its Unanimous-tier rows; Mixed needs
/// the row click; a Contradicted verdict is demoted to Review and is immune to bulk.
/// Every manual decision writes a teaching signal against its verdict class (V14
/// routing_overrides) - the write side is real even though the v0 read side is a
/// single override-count refinement.
/// </summary>
internal sealed class LedgerWindow : Window
{
  /// <summary>One routed inventory item (bag gear); Pile tracks player moves.</summary>
  internal sealed class RoutedItem
  {
    public uint ItemId { get; init; }
    public string Name { get; init; } = "";
    public bool IsHq { get; init; }
    public int Ilvl { get; init; }
    public int Quantity { get; init; }
    public InventoryType Container { get; init; }
    public int SlotIndex { get; init; }
    public int LastSalePrice { get; init; }
    /// <summary>DC-scope community median (Universalis history), for the Contradicted-row objection. 0 = none.</summary>
    public long CommunityMedian { get; init; }
    /// <summary>DC settled sales backing that median.</summary>
    public int CommunitySampleCount { get; init; }
    /// <summary>Home-world velocity (units/day), for the Contradicted-row objection. Null = no trusted data.</summary>
    public double? MarketVelocity { get; init; }
    /// <summary>The verdict leaned on community fallback (no local sale, community median in play) - tag it while world data warms.</summary>
    public bool CommunityFallback { get; init; }
    /// <summary>The router's original call - immutable; overrides move Pile, not this.</summary>
    public RoutingVerdict Verdict { get; init; }
    /// <summary>Current routing exit. Starts at Verdict.Exit; the player may move it.</summary>
    public RoutingExit Pile { get; set; }
    /// <summary>Still in the Review pile (ambiguous, unresolved).</summary>
    public bool InReview { get; set; }
    public bool OverrideRecorded { get; set; }
    /// <summary>Evidence-refined confidence for this verdict (design Section 4). Bulk gates on Unanimous.</summary>
    public ConfidenceTier Confidence { get; set; } = ConfidenceTier.Mixed;
    /// <summary>
    /// The player ruled on this row - a move click now, or a persisted ruling
    /// re-applied from routing_overrides on refresh. Resolves Review (even a
    /// Contradicted demotion) and makes the row bulk-confirmable. A ruling holds
    /// while the router's verdict is unchanged; a new verdict re-asks.
    /// </summary>
    public bool PlayerResolved { get; set; }

    /// <summary>The Ledger pile this row is drawn in (confidence-demoted Contradicted -> Review).</summary>
    public LedgerPile ActivePile
      => LedgerPiles.Effective(LedgerPiles.ForRoutingExit(Pile, InReview), Confidence, PlayerResolved);
  }

  /// <summary>One inbox row: a live run item, or a held flag wearing a synthetic PricingItem.</summary>
  private sealed record InboxRow(PricingItem Item, TriageFlag? Flag)
  {
    public bool IsFresh => Flag == null;
  }

  // --- Bag-routing state ---
  private List<RoutedItem> _items = [];
  private int? _ventureStock;
  private int? _weeklyBurn;
  private int _uniVersion;
  private int _uniHistVersion;

  // --- Listed pile (session 3): cached own-board snapshot, refreshed with the
  // rest of the ledger so the section never queries the DB per frame. ---
  private List<ListedLine> _listed = [];
  private long _earliestObs;

  // --- Ripeness sensors: cached with the listings refresh, never per frame ---
  private long _lastFullScanAt;
  private int _eventsSinceScan;

  // --- Yields bridge (minimal): fresh melt output still in bags, ready to hand
  // to the Hawk run. The data seam of the future full-sweep workflow. ---
  private List<(uint ItemId, string Name, bool IsHq, int Qty)> _freshYields = [];

  // The one-button sweep's cursor (v0, zero intelligence - see SweepPlan).
  private readonly SweepPlan _sweep = new();

  /// <summary>Last DesynthOrchestrator.AbortEpoch the deck has reconciled - an
  /// unseen bump means the fired melt run DIED and the stage must HALT.</summary>
  private int _seenDesynthAbortEpoch;

  /// <summary>The persisted sweep has been rehydrated (or retired) - done once,
  /// lazily, on the first deck draw when every orchestrator is constructed.</summary>
  private bool _sweepRestored;

  // --- Absorbed triage state (was TriageWindow) ---
  private List<PricingItem> _triageItems = [];
  private List<TriageFlag> _heldFlags = [];
  private readonly Dictionary<long, PricingItem> _flagItems = []; // flag id -> synthetic (stable identity)
  private Dictionary<PricingItem, TriageAction> _actions = [];

  // --- Confidence refinement (design Section 4) ---
  private Dictionary<string, int> _overrideCounts = new(StringComparer.Ordinal);
  private Dictionary<(uint, bool, string), string> _persistedRulings = new();
  private readonly HashSet<(uint, bool, string)> _signalsRecorded = [];

  public LedgerWindow()
    : base("Scrooge - Ledger###LedgerWindow", ImGuiWindowFlags.None)
  {
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new Vector2(560, 320),
      MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };
    Size = new Vector2(720, 560);
    SizeCondition = ImGuiCond.FirstUseEver;
    IsOpen = false;
  }

  public override void OnOpen()
  {
    Refresh();
    RefreshHeldFlags();
    RefreshListings();
  }

  /// <summary>
  /// Loads the own-board snapshot for the Listed pile from captured data (the
  /// listings table). Read-only, cached - called on open and on refresh, never
  /// per frame. Storage failure degrades to an empty section.
  /// </summary>
  private void RefreshListings()
  {
    try
    {
      _listed = GilStorage.GetAllCurrentListings()
        .Select(l => new ListedLine(
          l.RetainerName, l.ItemId, l.ItemName, l.IsHQ, l.UnitPrice, l.Quantity, l.FirstSeenTimestamp))
        .ToList();
      _earliestObs = GilStorage.GetEarliestObservation();
      _lastFullScanAt = GilStorage.GetLastFullScanTime();
      _eventsSinceScan = _lastFullScanAt > 0 ? GilStorage.CountMarketEventsSince(_lastFullScanAt) : 0;
      _freshYields = LoadFreshYields();
    }
    catch { _listed = []; _earliestObs = 0; _lastFullScanAt = 0; _eventsSinceScan = 0; _freshYields = []; }
  }

  /// <summary>
  /// Fresh melt output for the yields bridge: distinct yield items from the
  /// last 24h of desynth runs that are STILL IN THE BAGS and MB-listable.
  /// Already-swept or consumed yields drop out; crystals and other unlistables
  /// never appear. Captured data only - no new game reads beyond the bag scan
  /// the Ledger already performs.
  /// </summary>
  private static List<(uint ItemId, string Name, bool IsHq, int Qty)> LoadFreshYields()
  {
    var since = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds();
    var yields = Plugin.DesynthYieldStore?.GetRecentYieldItems(since);
    if (yields is null || yields.Count == 0) return [];

    var bags = SnapshotBagItems();
    var sheet = Svc.Data.GetExcelSheet<Item>();
    var rows = new List<(uint, string, bool, int)>();
    foreach (var y in yields)
    {
      if (!bags.Contains((y.ItemId, y.IsHq))) continue;
      if (!sheet.TryGetRow(y.ItemId, out var row)) continue;
      if (row.ItemSearchCategory.RowId == 0) continue; // not MB-listable
      rows.Add((y.ItemId, row.Name.ToString(), y.IsHq, y.TotalQty));
    }
    return rows;
  }

  /// <summary>Item identities currently in the four main bags.</summary>
  private static unsafe HashSet<(uint ItemId, bool IsHq)> SnapshotBagItems()
  {
    var bags = new HashSet<(uint, bool)>();
    var im = InventoryManager.Instance();
    if (im == null) return bags;

    foreach (var containerType in new[]
    {
      InventoryType.Inventory1, InventoryType.Inventory2,
      InventoryType.Inventory3, InventoryType.Inventory4,
    })
    {
      var container = im->GetInventoryContainer(containerType);
      if (container == null) continue;
      for (int i = 0; i < container->Size; i++)
      {
        var slot = container->GetInventorySlot(i);
        if (slot == null || slot->ItemId == 0) continue;
        bags.Add((slot->ItemId, (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0));
      }
    }
    return bags;
  }

  // ==========================================================================
  // Intake
  // ==========================================================================

  /// <summary>
  /// Scans the player's bags (gear only), gathers evidence once, runs every item
  /// through the rules engine into routing exits, and scores each verdict's
  /// confidence. No-op on the bag scan when the routing brain is off - the Ledger
  /// still shows the absorbed triage rows.
  /// </summary>
  public void Refresh()
  {
    _items = [];
    _overrideCounts = LoadOverrideCounts();
    _persistedRulings = LoadPersistedRulings();
    if (!Plugin.Configuration.EnableRoutingBrain) return;

    _uniVersion = UniversalisStats.Version;
    _uniHistVersion = UniversalisHistory.Version;
    var batch = RoutingInputService.BeginBatch();
    _ventureStock = batch.VentureStock;
    _weeklyBurn = batch.WeeklyVentureBurn;
    var gearsetIds = DesynthInventoryScanner.SnapshotGearsetItemIds();
    var itemSheet = Svc.Data.GetExcelSheet<Item>();

    unsafe
    {
      var im = InventoryManager.Instance();
      if (im == null) return;

      var containers = new[]
      {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
      };

      foreach (var containerType in containers)
      {
        var container = im->GetInventoryContainer(containerType);
        if (container == null) continue;

        for (int i = 0; i < container->Size; i++)
        {
          var slot = container->GetInventorySlot(i);
          if (slot == null || slot->ItemId == 0) continue;

          var itemId = slot->ItemId;
          if (!itemSheet.TryGetRow(itemId, out var row)) continue;
          if (row.EquipSlotCategory.RowId == 0) continue; // gear only

          var isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;

          var gearsetKey = isHq ? itemId + 1_000_000u : itemId;
          var protections = new ItemProtections(
            InGearset: gearsetIds.Contains(gearsetKey),
            Spiritbond100: slot->SpiritbondOrCollectability >= 10000,
            HasMateria: HasAnyMateria(slot));

          if (RoutingInputService.Collect(batch, itemId, isHq, protections) is not { } inputs)
            continue;

          var verdict = RoutingRules.Evaluate(inputs, batch);

          // Zero-exit gear is not a decision (Sam, 07-23): no pile, no Review,
          // no bulk/sweep count, no receipt. THE single exclusion filter — the
          // determination lives in the pure core (RoutingRules.HasNoViableExit
          // via IsExcluded); this is the one place that acts on it. Skipping
          // before the receipt/add/persisted-ruling steps means a stale ruling
          // on the item (the Vana'dielian Melt squatter) is loaded but never
          // looked up — inert, not a phantom. The check is live every scan, so
          // the item re-enters automatically the moment SE grants it a sale
          // value; nothing is persisted here to unset.
          if (verdict.IsExcluded) continue;

          var item = new RoutedItem
          {
            ItemId = itemId,
            Name = inputs.Name,
            IsHq = isHq,
            Ilvl = inputs.Ilvl,
            Quantity = (int)slot->Quantity,
            Container = containerType,
            SlotIndex = i,
            LastSalePrice = inputs.LastSale?.Price ?? 0,
            CommunityMedian = inputs.CommunityMedian ?? 0,
            CommunitySampleCount = inputs.CommunitySampleCount,
            MarketVelocity = inputs.MarketVelocity,
            CommunityFallback = inputs.LastSale is null && inputs.CommunityMedian is not null,
            Verdict = verdict,
            Pile = verdict.Exit,
            InReview = verdict.IsReview,
            Confidence = ScoreRouted(inputs, verdict, batch.Rules),
          };
          ApplyPersistedRuling(item);
          _items.Add(item);

          // V20 routing receipt: the decision + its alternatives, deduped in
          // storage (same item/exit within 24h = same standing decision).
          // Telemetry only - never blocks routing.
          try
          {
            GilStorage.InsertRoutingReceipt(itemId, isHq, inputs.Ilvl,
              verdict.Exit.ToString(), verdict.Reason, verdict.IsReview,
              item.Confidence.ToString(), verdict.Scores,
              batch.SealToGilRate, batch.SealRateEmpirical,
              batch.VentureStock, batch.WeeklyVentureBurn,
              item.CommunityFallback ? "community" : "world");
          }
          catch { }
        }
      }
    }

    _items = _items.OrderByDescending(i => i.Ilvl).ThenBy(i => i.Name).ToList();
  }

  /// <summary>
  /// Re-applies the player's most recent persisted ruling to a freshly routed row.
  /// A ruling holds only while it answers the SAME question - keyed on the router's
  /// current verdict, so a changed verdict (new evidence) misses and re-asks. This
  /// is what stops the Ledger asking the same question every reload.
  /// </summary>
  private void ApplyPersistedRuling(RoutedItem item)
  {
    var routerVerdict = item.Verdict.IsReview ? "Review" : item.Verdict.Exit.ToString();
    if (!_persistedRulings.TryGetValue((item.ItemId, item.IsHq, routerVerdict), out var ruled))
      return;
    if (!Enum.TryParse<RoutingExit>(ruled, out var exit))
      return;
    item.Pile = exit;
    item.InReview = false;
    item.PlayerResolved = true;
  }

  private static Dictionary<(uint, bool, string), string> LoadPersistedRulings()
  {
    try { return GilStorage.GetLatestRoutingRulings(); }
    catch { return new Dictionary<(uint, bool, string), string>(); }
  }

  private static Dictionary<string, int> LoadOverrideCounts()
  {
    try { return GilStorage.GetRoutingOverrideCounts(); }
    catch { return new Dictionary<string, int>(StringComparer.Ordinal); }
  }

  /// <summary>
  /// Confidence for a bag-gear verdict. The evidence axes come from the same inputs
  /// the rules engine judged; the override history for this verdict class refines the
  /// tier. NOTE (Fable QA): the bag path has no full lane model, so lane n / spread
  /// are approximated from the community sample count and velocity - the signal is
  /// strongest on the triage (listed) rows that carry real sale history.
  /// </summary>
  private ConfidenceTier ScoreRouted(RoutingItemInputs inputs, RoutingVerdict verdict, RoutingConfig cfg)
  {
    var lean = verdict.IsReview ? VerdictLean.Neutral : verdict.Exit switch
    {
      RoutingExit.List => VerdictLean.OnMarket,
      RoutingExit.Vendor or RoutingExit.Gc or RoutingExit.Desynth => VerdictLean.OffMarket,
      _ => VerdictLean.Neutral,
    };
    var recentSales = inputs.CommunitySampleCount > 0 ? inputs.CommunitySampleCount
      : inputs.LastSale is not null ? 1 : 0;
    // The market-must-outbid axis (Sam's 07-22 ruling): the verdict defends the
    // worth the router actually scored it at; the market's bid is the List value
    // as weighed (own sale or community median). Null worth (pre-value exits,
    // Review) falls back to the existence rule.
    var verdictWorth = verdict.Scores is { } sc
      ? verdict.Exit switch
      {
        RoutingExit.Gc => sc.Gc,
        RoutingExit.Desynth => sc.Melt,
        RoutingExit.Vendor => sc.Vendor,
        RoutingExit.List => sc.List,
        _ => null,
      }
      : null;
    var marketBid = verdict.Scores?.List ?? inputs.CommunityMedian;
    var evidence = new LedgerConfidence.Evidence(
      Lean: lean,
      LaneSampleCount: Math.Max(inputs.CommunitySampleCount, inputs.LastSale is not null ? 1 : 0),
      LaneSpread: 0.0,
      VelocityPerDay: inputs.MarketVelocity,
      RecentSalesCount: recentSales,
      EvidenceAgeDays: inputs.MarketLastSaleDays ?? 0,
      LocalCommunityAccord: Accord.Unknown,
      MinSamples: cfg.CommunityMinSamples,
      StaleDays: 14,
      VerdictWorth: verdictWorth,
      MarketBid: marketBid);
    var overrides = _overrideCounts.GetValueOrDefault(verdict.Exit.ToString());
    return LedgerConfidence.Tier(evidence, overrides);
  }

  /// <summary>Confidence for an absorbed triage (listed) row - the Alexander Miniature path.</summary>
  private ConfidenceTier ScoreTriage(PricingItem item, LedgerPile natural)
  {
    var lean = natural switch
    {
      LedgerPile.Reprice or LedgerPile.List => VerdictLean.OnMarket,
      LedgerPile.PullAndVendor or LedgerPile.Melt or LedgerPile.Churn => VerdictLean.OffMarket,
      _ => VerdictLean.Neutral,
    };
    var evidence = new LedgerConfidence.Evidence(
      Lean: lean,
      LaneSampleCount: item.HistorySaleCount,
      LaneSpread: 0.0,
      VelocityPerDay: null,
      RecentSalesCount: item.HistorySaleCount, // sales in the past 14 days
      EvidenceAgeDays: 0,                       // the current listing is live evidence
      LocalCommunityAccord: Accord.Unknown,
      MinSamples: Plugin.Configuration.LaneMinHistorySamples,
      StaleDays: 14);
    var overrides = _overrideCounts.GetValueOrDefault(natural.ToString());
    return LedgerConfidence.Tier(evidence, overrides);
  }

  private static unsafe bool HasAnyMateria(InventoryItem* slot)
  {
    for (int m = 0; m < 5; m++)
      if (slot->Materia[m] != 0) return true;
    return false;
  }

  /// <summary>Loads open persistent flags (V12). Called on window open and after flag mutations.</summary>
  private void RefreshHeldFlags()
  {
    try { _heldFlags = GilStorage.GetOpenTriageFlags(); }
    catch { _heldFlags = []; }
  }

  /// <summary>The unified triage row list: fresh run items, then held flags not already shown as a live row.</summary>
  private List<InboxRow> BuildTriageRows()
  {
    var rows = new List<InboxRow>();
    foreach (var item in _triageItems)
      rows.Add(new InboxRow(item, null));
    foreach (var flag in _heldFlags)
    {
      if (_triageItems.Any(t => t.ItemId == flag.ItemId && t.IsHq == flag.IsHq && t.RetainerName == flag.RetainerName))
        continue;
      rows.Add(new InboxRow(SyntheticItem(flag), flag));
    }
    return rows;
  }

  private PricingItem SyntheticItem(TriageFlag flag)
  {
    if (_flagItems.TryGetValue(flag.Id, out var cached)) return cached;

    var item = new PricingItem
    {
      SlotIndex = flag.SlotIndex,
      ItemId = flag.ItemId,
      IsHq = flag.IsHq,
      ItemName = GilTracker.GetItemName(flag.ItemId),
      RetainerName = flag.RetainerName,
      Quantity = 0,
      CurrentListingPrice = flag.OldPrice > 0 ? flag.OldPrice : null,
      MbPrice = flag.FlaggedPrice > 0 ? flag.FlaggedPrice : null,
      VendorPrice = VendorPriceOf(flag.ItemId),
      Result = flag.Reason switch
      {
        "upward_held" or "outlier_warn" => PricingResult.UpwardHeld,
        "lane_held" => PricingResult.LaneHeld,
        "cap_blocked" => PricingResult.CapBlocked,
        _ => PricingResult.NoData,
      },
    };
    _flagItems[flag.Id] = item;
    return item;
  }

  private static int VendorPriceOf(uint itemId)
  {
    try
    {
      return (int)Svc.Data.GetExcelSheet<Item>().GetRow(itemId).PriceLow;
    }
    catch { return 0; }
  }

  // ==========================================================================
  // Draw
  // ==========================================================================

  public override void Draw()
  {
    // A batch killed by a task timeout leaves the orchestrator latched - recover.
    Plugin.TriageOrchestrator.RecoverIfStalled();

    var brainOn = Plugin.Configuration.EnableRoutingBrain;

    // Universalis answers land async - re-run the bag piles when data arrives so
    // "no evidence" verdicts settle, but never while the player is mid-decision.
    if (brainOn)
    {
      var uniLanded = UniversalisStats.Version != _uniVersion
                      || UniversalisHistory.Version != _uniHistVersion;
      var playerTouched = _items.Any(i =>
        i.OverrideRecorded || i.Pile != i.Verdict.Exit || i.InReview != i.Verdict.IsReview)
        || _actions.Count > 0;
      if (uniLanded && !playerTouched && !Plugin.GcTurnIn.IsRunning)
        Refresh();
    }

    var triageRows = BuildTriageRows();

    if (_items.Count == 0 && triageRows.Count == 0 && _listed.Count == 0)
    {
      ImGui.TextDisabled(brainOn
        ? "Nothing on the ledger - no routable gear in your bags and no open flags."
        : "The routing brain is off (config's Routing tab). No open flags to review.");
      if (ImGui.Button("Refresh")) { Refresh(); RefreshHeldFlags(); RefreshListings(); }
      return;
    }

    // Nothing actionable, but there are standing listings - show the header and
    // fall through to the read-only Listed pile.
    if (_items.Count == 0 && triageRows.Count == 0)
    {
      DrawHeader(triageRows);
      ImGui.Separator();
      DrawDeck(triageRows);
      DrawFreshYields();
      DrawListedPile();
      return;
    }

    DrawHeader(triageRows);
    ImGui.Separator();
    DrawDeck(triageRows);

    DrawReviewPile(triageRows);
    DrawListPile();
    DrawFreshYields();
    DrawRepricePile(triageRows);
    DrawPullAndVendorPile(triageRows);
    DrawMeltPile();
    DrawChurnPile();
    DrawWatchPile(triageRows);
    DrawListedPile();
  }

  // ==========================================================================
  // The sweep deck (v2.18): the whole errand behind one button
  // ==========================================================================

  /// <summary>
  /// The command deck: the full sweep - pinch, bell run, reprice, desynth,
  /// turn in - as one button pressed at each stop (Sam's endgame sentence,
  /// 2026-07-19: "easily kick off a full sweep... and be smart about what
  /// fires when"). v0 is deliberately dumb: it fires exactly the bulk actions
  /// the pile buttons fire, in workflow order, and tells you where to walk
  /// next. The smartness arrives as receipts ripen (4.0) - stages learn to
  /// skip themselves; this seam does not move.
  /// </summary>
  private void DrawDeck(List<InboxRow> triageRows)
  {
    // Work sets - recomputed per frame from the same sources as the pile
    // buttons, so the deck can never disagree with the piles it mirrors.
    var listRows = _items.Where(i => i.ActivePile == LedgerPile.List).ToList();
    var vendorRows = _items.Where(i => i.ActivePile == LedgerPile.PullAndVendor).ToList();
    var listSet = LedgerConfidence.BulkSet(listRows.Select(r => (r, r.Confidence, r.PlayerResolved)));
    var vendSet = LedgerConfidence.BulkSet(vendorRows.Select(r => (r, r.Confidence, r.PlayerResolved)));
    var repriceEligible = triageRows
      .Where(r => EffectiveTriagePile(r.Item) == LedgerPile.Reprice)
      .Where(r => LedgerConfidence.IsBulkEligible(ScoreTriage(r.Item, LedgerPiles.ForTriage(r.Item.Result))))
      .ToList();
    var meltCount = _items.Count(i => i.ActivePile == LedgerPile.Melt);
    var churnRows = _items.Where(i => i.ActivePile == LedgerPile.Churn).ToList();
    var churnSet = LedgerConfidence.BulkSet(churnRows.Select(r => (r, r.Confidence, r.PlayerResolved)));

    int CountOf(SweepStage s) => s switch
    {
      SweepStage.BellRun => listSet.Count + vendSet.Count,
      SweepStage.Reprice => repriceEligible.Count,
      SweepStage.Desynth => meltCount,
      SweepStage.TurnIn => churnSet.Count,
      _ => 0,
    };
    // The pinch always has work: the board read is what makes the rest honest.
    bool HasWork(SweepStage s) => s == SweepStage.Pinch || CountOf(s) > 0;

    // Rehydrate a persisted in-progress sweep once, here (not in the ctor):
    // every orchestrator is constructed by first draw, so the abort-epoch
    // baseline and the staleness chat notice are both safe.
    if (!_sweepRestored)
    {
      _sweepRestored = true;
      RestoreSweep();
    }

    // A melt run that died (timeout, occupied refusal, abort) HALTS the sweep -
    // it holds its place over the corpse and names the gap, instead of quietly
    // rolling the stage back onto the cursor. Only a sweep-fired melt (Desynth
    // marked done at fire time) halts; a stray desynth abort is ignored.
    if (Plugin.DesynthOrchestrator.AbortEpoch != _seenDesynthAbortEpoch)
    {
      _seenDesynthAbortEpoch = Plugin.DesynthOrchestrator.AbortEpoch;
      if (_sweep.Active && !_sweep.Halted && _sweep.IsDone(SweepStage.Desynth))
      {
        var reason = Plugin.DesynthOrchestrator.LastAbortReason ?? "the run stopped";
        _sweep.Halt(SweepHalt.Plainly(SweepStage.Desynth, "Melt", reason,
          "Clear it and Resume - the melt rescans the bags from where they are now."));
        PersistSweep();
      }
    }

    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Earned);
    var open = ImGui.CollapsingHeader("Sweep - the whole errand, one button###deckSweep",
      ImGuiTreeNodeFlags.DefaultOpen);
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("Pinch, bell run, reprice, melt, turn in - in order, one press per stop. The button tells you where to walk. Every stage still confirms; it gets quieter as the receipts teach it what to skip.");
    if (!open) return;

    if (!_sweep.Active)
    {
      if (ImGui.Button("Start sweep###sweepStart"))
      {
        _sweep.Start();
        PersistSweep();
      }
      ImGui.SameLine();
      ImGui.TextDisabled(
        $"pinch -> {listSet.Count + vendSet.Count} bell -> {repriceEligible.Count} reprice -> {meltCount} melt -> {churnSet.Count} turn in");
      return;
    }

    var next = _sweep.Next(HasWork);

    // The cursor readout: one line per stage.
    foreach (var s in SweepPlan.Order)
    {
      var isNext = next is SweepStage n && n == s;
      var isHalted = _sweep.HaltStage is SweepStage h && h == s;
      var (glyph, color) = isHalted ? ("!", ScroogeColors.Spent)
        : _sweep.IsDone(s) ? ("x", ScroogeColors.Earned)
        : isNext ? (">", ScroogeColors.Amber)
        : HasWork(s) ? (".", ScroogeColors.Muted)
        : ("-", ScroogeColors.Muted);
      ImGui.PushStyleColor(ImGuiCol.Text, color);
      ImGui.Text($" {glyph} {StageLabel(s, CountOf(s))}{(HasWork(s) || _sweep.IsDone(s) ? "" : "  (nothing to do)")}");
      ImGui.PopStyleColor();
    }

    // Halted: never offer the next stage past a corpse. Name the gap and offer
    // Resume, which re-fires the dead stage from its own rescan.
    if (_sweep.CurrentHalt is SweepHalt halt)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Spent);
      ImGui.TextWrapped(halt.Message);
      ImGui.PopStyleColor();
      if (ImGui.Button("Resume###sweepResume"))
      {
        _sweep.Resume();
        PersistSweep();
      }
      ImGui.SameLine();
      if (ImGui.SmallButton("cancel###sweepCancelHalt"))
      {
        _sweep.Cancel();
        PersistSweep();
      }
      return;
    }

    if (next is null)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Earned);
      ImGui.Text("Sweep complete - the board is worked.");
      ImGui.PopStyleColor();
      ImGui.SameLine();
      if (ImGui.SmallButton("done###sweepDone"))
      {
        _sweep.Cancel();
        PersistSweep();
      }
      return;
    }

    var stage = next.Value;
    var anyBusy = Plugin.AutoPinch.PinchBusy || Plugin.AutoPinch.HawkRunning
      || Plugin.TriageOrchestrator.IsRunning || Plugin.DesynthOrchestrator.IsRunning
      || Plugin.GcTurnIn.IsRunning;
    var here = stage switch
    {
      SweepStage.Pinch => AtBellRoster(),
      // "Anywhere" was a lie: the game refuses Desynthesize while occupied
      // (open bell, NPC talk). The 07-22 sweep lap died firing melt over the
      // still-open bell - gate the button the same way StartRun now does.
      SweepStage.Desynth => !DesynthOrchestrator.PlayerOccupied(out _),
      SweepStage.TurnIn => GcTurnInOrchestrator.AtExpertDelivery(),
      _ => AtRetainerBell(),
    };

    if (anyBusy)
    {
      ImGui.TextDisabled("run in progress - the sweep waits...");
    }
    else if (!here)
    {
      ImGui.BeginDisabled(true);
      ImGui.Button($"Sweep: {StageLabel(stage, CountOf(stage))}###sweepFire");
      ImGui.EndDisabled();
      ImGui.SameLine();
      ImGui.TextDisabled(stage == SweepStage.Desynth
        ? "close the retainer bell first - the game refuses desynth while occupied"
        : $"walk to {PlaceName(stage)}");
    }
    else if (ImGui.Button($"Sweep: {StageLabel(stage, CountOf(stage))}###sweepFire"))
    {
      FireSweepStage(stage, listSet, vendSet, repriceEligible, churnSet);
    }
    ImGui.SameLine();
    if (ImGui.SmallButton("cancel###sweepCancel"))
    {
      _sweep.Cancel();
      PersistSweep();
    }
  }

  /// <summary>
  /// Writes the sweep's HELD PLACE to the durable config after a transition
  /// (start, fire, halt, resume, cancel, complete). Called only on discrete
  /// events - never per frame - so the 07-22 lost-cursor reload cannot recur.
  /// </summary>
  private void PersistSweep()
  {
    Plugin.Configuration.Sweep = _sweep.Export();
    Plugin.Configuration.Save();
  }

  /// <summary>
  /// Rehydrates a persisted in-progress sweep on first draw, unless it is too
  /// old to trust: a sweep past the staleness ceiling is retired loudly (a
  /// half-done sweep from hours ago is history, not a sweep), never restored
  /// onto a stale world.
  /// </summary>
  private void RestoreSweep()
  {
    var saved = Plugin.Configuration.Sweep;
    if (saved is null || !saved.Active) return;

    var ceiling = TimeSpan.FromHours(Math.Max(1, Plugin.Configuration.SweepStalenessCeilingHours));
    var startedAt = saved.StartedAtUnix > 0
      ? DateTimeOffset.FromUnixTimeSeconds(saved.StartedAtUnix)
      : (DateTimeOffset?)null;

    if (startedAt is null || SweepPlan.IsStale(startedAt.Value, DateTimeOffset.UtcNow, ceiling))
    {
      Plugin.Configuration.Sweep = null;
      Plugin.Configuration.Save();
      Svc.Chat.PrintError(
        "[Scrooge] A stale sweep from a previous session was retired - a half-done sweep " +
        "from hours ago is history, not a sweep. Start a fresh one when you're ready.");
      return;
    }

    _sweep.Restore(saved);
    _seenDesynthAbortEpoch = Plugin.DesynthOrchestrator.AbortEpoch;
  }

  private static string StageLabel(SweepStage s, int count) => s switch
  {
    SweepStage.Pinch => "pinch the board",
    SweepStage.BellRun => $"bell run ({count})",
    SweepStage.Reprice => $"reprice ({count})",
    SweepStage.Desynth => $"open desynthesis ({count})",
    SweepStage.TurnIn => $"turn in ({count})",
    _ => "?",
  };

  private static string PlaceName(SweepStage s) => s switch
  {
    SweepStage.Pinch => "a retainer bell (roster view)",
    SweepStage.TurnIn => "your GC's Expert Delivery",
    _ => "a retainer bell",
  };

  /// <summary>
  /// Fires one sweep stage - exactly what the pile's own bulk button fires,
  /// nothing more. Marked done AT FIRE TIME: the busy gate keeps the next
  /// press honest, and a mid-run abort leaves the player exactly where the
  /// pile buttons would (nothing new can go wrong between stages, because
  /// between stages is just walking).
  /// </summary>
  private void FireSweepStage(SweepStage stage, List<RoutedItem> listSet,
    List<RoutedItem> vendSet, List<InboxRow> repriceEligible, List<RoutedItem> churnSet)
  {
    switch (stage)
    {
      case SweepStage.Pinch:
        Plugin.AutoPinch.StartPinchAllRetainers();
        break;
      case SweepStage.BellRun:
        FireBellRun(listSet, vendSet);
        break;
      case SweepStage.Reprice:
        foreach (var r in repriceEligible)
        {
          _actions[r.Item] = TriageAction.Reprice;
          RecordTriageSignal(r.Item, LedgerPiles.ForTriage(r.Item.Result), TriageAction.Reprice);
        }
        ExecuteTriageBatch();
        break;
      case SweepStage.Desynth:
        Plugin.DesynthPreview.OpenSalvageWithPileSelected();
        break;
      case SweepStage.TurnIn:
        foreach (var item in churnSet)
          RecordRoutedSignal(item, item.Pile); // bulk confirm = mass agreement
        ExecuteChurn(churnSet);
        break;
    }
    _sweep.MarkDone(stage);
    PersistSweep();
  }

  private static unsafe bool AtBellRoster()
    => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out _);

  // ---- Fresh yields (yields bridge, minimal): melt output -> Hawk run ----

  /// <summary>
  /// The yields bridge's visible seam: fresh melt output (last 24h, still in
  /// bags, MB-listable) with a one-click handoff into the Hawk run's checklist.
  /// Melt -> mats -> market stops depending on a human remembering the middle
  /// step. Minimal by ruling (2026-07-19): the full-sweep choreography is the
  /// 4.0 workflow layer; this is the data seam it will stand on.
  /// </summary>
  private void DrawFreshYields()
  {
    if (_freshYields.Count == 0) return;

    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Info);
    var open = ImGui.CollapsingHeader(
      $"Fresh yields - {_freshYields.Count} item type{(_freshYields.Count == 1 ? "" : "s")} from your last melts###pileYields");
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("Desynth output from the last 24h still sitting in your bags. Check them into the Hawk run and the melt->mats->market loop closes in one errand.");
    if (!open) return;

    if (ImGui.Button($"Check all {_freshYields.Count} in Hawk Run###yieldsToHawk"))
    {
      Plugin.HawkWindow.RefreshInventory();
      foreach (var y in _freshYields)
        Plugin.HawkWindow.SetItemSelected(y.ItemId, y.IsHq, true);
      Plugin.HawkWindow.IsOpen = true;
    }
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("Opens the Hawk run checklist with these pre-checked. Items the Hawk can't list right now simply stay unchecked.");

    foreach (var (_, name, isHq, qty) in _freshYields)
    {
      ImGui.Text($"  {Format.Hq(name, isHq)}");
      ImGui.SameLine();
      ImGui.TextDisabled($"x{qty}");
    }
  }

  // ---- Listed (session 3: everything on the board, per retainer) ----

  /// <summary>
  /// The Listed pile (item 1): a read-only, per-retainer view of everything you
  /// currently have on the board, from the captured listings snapshot. Not an
  /// action pile - the actionable listed rows (reprice / pull / outlier / vendor
  /// floor) surface in the piles above; this is the "what's out there" roll-up.
  /// Collapsed by default so it never crowds the worklist. Each row carries an
  /// HONEST age label (item 2): "&gt;=Nd" when first_seen is only a lower bound.
  /// </summary>
  private void DrawListedPile()
  {
    if (_listed.Count == 0) return;

    var groups = LedgerListings.GroupByRetainer(_listed);
    var totalGil = groups.Sum(g => g.GilAtAsk);
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var ageCfg = new ListedAgeConfig();

    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Info);
    var open = ImGui.CollapsingHeader(
      $"Listed - {_listed.Count} on the board across {groups.Count} retainer{(groups.Count == 1 ? "" : "s")} ({Format.Gil(totalGil)} at ask)###pileListed");
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("Everything you currently have listed, grouped by retainer, from the last board scan. Read-only - the actionable listed rows live in the piles above.");
    if (!open) return;

    foreach (var g in groups)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
      var groupOpen = ImGui.CollapsingHeader(
        $"{g.Retainer} - {g.Count} listed ({Format.Gil(g.GilAtAsk)} at ask)###listedRet_{g.Retainer}");
      ImGui.PopStyleColor();
      if (!groupOpen) continue;

      foreach (var l in _listed.Where(x => x.Retainer == g.Retainer).OrderByDescending(x => x.UnitPrice))
      {
        var ageDays = LedgerListings.AgeDays(l.FirstSeen, now);
        var exact = LedgerListings.AgeIsExact(l.FirstSeen, _earliestObs);
        var ageColor = LedgerListings.Tier(ageDays, ageCfg) switch
        {
          ListedAgeTier.Stale => ScroogeColors.Stale,
          ListedAgeTier.Aging => ScroogeColors.Amber,
          _ => ScroogeColors.Muted,
        };
        ImGui.Text($"  {Format.Hq(l.ItemName, l.IsHq)}");
        ImGui.SameLine();
        ImGui.TextDisabled(l.Quantity > 1 ? $"x{l.Quantity} @ {Format.Gil(l.UnitPrice)}" : $"@ {Format.Gil(l.UnitPrice)}");
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ageColor);
        ImGui.Text(LedgerListings.AgeLabel(ageDays, exact));
        ImGui.PopStyleColor();
      }
    }
  }

  private void DrawHeader(List<InboxRow> triageRows)
  {
    ImGui.Text("the ledger speaks in last-pinch tense");
    ImGui.SameLine();
    // Ripeness sensors (stretch 2): WHICH pinch - age of the last full scan and
    // how much the board has moved since. Sensors only, no gates: thresholds
    // wait for receipts data (4.0), a number nobody measured reads like a rule.
    ImGui.TextDisabled($"({RipenessSensors.HeaderLine(_lastFullScanAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), _eventsSinceScan)})");
    ImGui.SameLine();
    if (_ventureStock is int stock)
    {
      // Projection beside the stock: where the tokens WILL be in a week at the
      // measured burn - the operand the saturation tilt actually reads.
      ImGui.TextDisabled(_weeklyBurn is int wb && wb > 0
        ? $"- {stock:N0} venture tokens (~{stock - wb:N0} in 7d at your burn)"
        : $"- {stock:N0} venture tokens");
    }

    // Data-warming honesty (session 3, item 7): while Universalis fetches are in
    // flight the board is scoring on community fallback that world data will
    // shortly overwrite - say so in the header instead of silently asserting two
    // different truths per reload.
    var uniPending = UniversalisStats.PendingCount;
    if (uniPending > 0)
    {
      ImGui.SameLine();
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
      ImGui.Text($"- market data warming — {uniPending} pending");
      ImGui.PopStyleColor();
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip("Verdicts marked \"~ community read\" are built on DC-wide fallback while your world's Universalis data is still fetching. They may firm up on the next refresh.");
    }

    if (ImGui.Button("Refresh")) { Refresh(); RefreshHeldFlags(); RefreshListings(); }
    ImGui.SameLine();

    // The triage batch Go (absorbed): executes whatever actions are staged on the
    // listed rows. Staged by the per-pile bulk confirms or by row clicks.
    if (Plugin.TriageOrchestrator.IsRunning)
    {
      if (ImGui.Button("Cancel batch")) Plugin.TriageOrchestrator.Abort();
      ImGui.SameLine();
      ImGui.TextDisabled("Batch in progress...");
    }
    else
    {
      var staged = _actions.Count;
      ImGui.BeginDisabled(staged == 0);
      if (ImGui.Button($"Go ({DescribeActions()})"))
        ExecuteTriageBatch();
      ImGui.EndDisabled();
    }

    // The GC churn readout / Turn In button (location-gated). The RUNNING branch is
    // gated on the run, not the pile - the standard run readout (RunHostRender).
    if (Plugin.GcTurnIn.IsRunning)
    {
      ImGui.SameLine();
      if (ImGui.Button("Cancel turn-in")) Plugin.GcTurnIn.Abort();
      ImGui.SameLine();
      RunHostRender.Progress(Plugin.GcTurnIn.Run, "Turning in");
    }
  }

  private string DescribeActions()
  {
    var vend = _actions.Count(a => a.Value == TriageAction.Vendor);
    var pull = _actions.Count(a => a.Value == TriageAction.Pull);
    var reprice = _actions.Count(a => a.Value == TriageAction.Reprice);
    var parts = new List<string>();
    if (vend > 0) parts.Add($"{vend} vendor");
    if (pull > 0) parts.Add($"{pull} pull");
    if (reprice > 0) parts.Add($"{reprice} reprice");
    return parts.Count > 0 ? string.Join(", ", parts) : "nothing staged";
  }

  // ---- Review ----

  private void DrawReviewPile(List<InboxRow> triageRows)
  {
    var bagReview = _items.Where(i => i.ActivePile == LedgerPile.Review).ToList();
    var listedReview = triageRows.Where(r => EffectiveTriagePile(r.Item) == LedgerPile.Review).ToList();
    if (bagReview.Count == 0 && listedReview.Count == 0) return;

    if (!PileHeader("Review", "Review", bagReview.Count + listedReview.Count, ScroogeColors.Stale,
        "Too close to call, no evidence, or the market contradicts the verdict - your eyes, row by row. Never bulk."))
      return;

    foreach (var item in bagReview)
      DrawRoutedRow(item, showMoves: true);
    foreach (var row in listedReview)
      DrawTriageRow(row, LedgerPile.Review);
  }

  // ---- List ----

  private void DrawListPile()
  {
    var rows = _items.Where(i => i.ActivePile == LedgerPile.List).ToList();
    if (rows.Count == 0) return;

    var atBell = AtRetainerBell();
    if (!PileHeader($"List - at a retainer bell{LocationTag(atBell)}", "List", rows.Count, ScroogeColors.Earned,
        "Earns real gil on the market board. The Hawk run lists it - summon your retainers."))
      return;

    BellConfirmButton(atBell, "L");
    foreach (var item in rows)
      DrawRoutedRow(item, showMoves: true);
  }

  // ---- Reprice ----

  private void DrawRepricePile(List<InboxRow> triageRows)
  {
    var rows = triageRows.Where(r => EffectiveTriagePile(r.Item) == LedgerPile.Reprice).ToList();
    if (rows.Count == 0) return;

    if (!PileHeader("Reprice - at the listing's retainer", "Reprice", rows.Count, ScroogeColors.Warning,
        "A standing listing needs its price fixed (cap / undercut / upward-held). Reprices with the guards bypassed."))
      return;

    BulkConfirmTriage(rows, TriageAction.Reprice, "reprice");
    foreach (var row in rows)
      DrawTriageRow(row, LedgerPile.Reprice);
  }

  // ---- Pull-and-Vendor ----

  private void DrawPullAndVendorPile(List<InboxRow> triageRows)
  {
    var bagVendor = _items.Where(i => i.ActivePile == LedgerPile.PullAndVendor).ToList();
    var listed = triageRows.Where(r => EffectiveTriagePile(r.Item) == LedgerPile.PullAndVendor).ToList();
    if (bagVendor.Count == 0 && listed.Count == 0) return;

    var atBell = AtRetainerBell();
    if (!PileHeader($"Pull & Vendor - at a retainer bell{LocationTag(atBell)}", "PullVendor",
        bagVendor.Count + listed.Count, ScroogeColors.Muted,
        "No better exit in evidence: pull the listing and/or vendor-sell. Executed at a retainer."))
      return;

    // Bag gear rides the shared bell run; listed rows -> triage vendor batch.
    if (bagVendor.Count > 0)
      BellConfirmButton(atBell, "V");
    if (listed.Count > 0)
      BulkConfirmTriage(listed, TriageAction.Vendor, "vendor");

    foreach (var item in bagVendor)
      DrawRoutedRow(item, showMoves: true);
    foreach (var row in listed)
      DrawTriageRow(row, LedgerPile.PullAndVendor);
  }

  // ---- Melt ----

  private void DrawMeltPile()
  {
    var rows = _items.Where(i => i.ActivePile == LedgerPile.Melt).ToList();
    if (rows.Count == 0) return;

    if (!PileHeader("Melt - at the desynthesis window", "Melt", rows.Count, ScroogeColors.Amber,
        "Skillup value or yields beat the alternatives. Run from the desynth window (it pre-selects this pile)."))
      return;

    if (Plugin.DesynthOrchestrator.IsRunning)
      ImGui.TextDisabled("Desynth run in progress...");
    else if (ImGui.Button($"Open desynthesis ({rows.Count} pre-selected)##meltopen"))
      Plugin.DesynthPreview.OpenSalvageWithPileSelected();
    foreach (var item in rows)
      DrawRoutedRow(item, showMoves: true);
  }

  // ---- Churn ----

  private void DrawChurnPile()
  {
    var rows = _items.Where(i => i.ActivePile == LedgerPile.Churn).ToList();
    if (rows.Count == 0) return;

    var atGc = GcTurnInOrchestrator.AtExpertDelivery();
    if (!PileHeader($"Churn - at your GC's Expert Delivery{LocationTag(atGc)}", "Churn", rows.Count, ScroogeColors.Warning,
        "Seals beat gil (or venture stock demands it). Expert Delivery at your GC."))
      return;

    if (!Plugin.GcTurnIn.IsRunning)
    {
      // Player rulings ride the bulk exactly like the bell confirm - the third
      // place the sorted-but-not-executed gap appeared, closed the same way.
      var confirmable = LedgerConfidence.BulkSet(rows.Select(r => (r, r.Confidence, r.PlayerResolved)));
      var chosen = confirmable.Count(r => r.PlayerResolved && !LedgerConfidence.IsBulkEligible(r.Confidence));
      var label = chosen > 0
        ? $"Turn In ({confirmable.Count - chosen} unanimous + {chosen} chosen)"
        : $"Turn In ({confirmable.Count} unanimous)";
      ImGui.BeginDisabled(!atGc || confirmable.Count == 0);
      if (ImGui.Button(label))
      {
        foreach (var item in confirmable)
          RecordRoutedSignal(item, item.Pile); // bulk confirm = mass agreement
        ExecuteChurn(confirmable);
      }
      ImGui.EndDisabled();
      if (!atGc && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        ImGui.SetTooltip("Open your GC personnel officer's Expert Delivery window.");
    }
    foreach (var item in rows)
      DrawRoutedRow(item, showMoves: true);
  }

  // ---- Watch (ruling 3: always visible, collapsed count summary) ----

  private void DrawWatchPile(List<InboxRow> triageRows)
  {
    // Categorize: thin = lane_held listed rows; races/bait = lane outcomes (grown in
    // session 3's listed-rows slice); other = protected holds + observed bans + bag Watch.
    var counts = new Dictionary<WatchCategory, int>();
    void Bump(WatchCategory c) => counts[c] = counts.GetValueOrDefault(c) + 1;

    // Item 5: races / bait / thin come from the flag's machine reason (a held
    // flag carries lane_held / wall_ignored / outlier_warn / race_declined); a
    // fresh run row falls back to its pricing result. The categorizer is the
    // structural home so the buckets can't drift per-caller.
    var watchListed = triageRows.Where(r => EffectiveTriagePile(r.Item) == LedgerPile.Watch).ToList();
    foreach (var r in watchListed)
      Bump(r.IsFresh
        ? (r.Item.Result == PricingResult.LaneHeld ? WatchCategory.Thin : WatchCategory.Other)
        : LedgerListings.CategorizeWatchReason(r.Flag!.Reason));

    var bagWatch = _items.Where(i => i.ActivePile == LedgerPile.Watch).ToList();
    foreach (var _ in bagWatch) Bump(WatchCategory.Other);

    var summary = LedgerConfidence.WatchSummary(counts);

    // Always visible; expands to detail rows on demand (ruling 3: full rows break out
    // only when volume makes inline unwieldy - the collapsing header is that seam).
    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
    var open = ImGui.CollapsingHeader($"Watch - {summary}###pileWatch");
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("Correctly placed, leave it: thin lanes, floor-waiting races, ignored bait, protected holds. Always shown, never crowds the actionable piles.");
    if (!open) return;

    foreach (var item in bagWatch)
      ImGui.TextDisabled($"  {Format.Hq(item.Name, item.IsHq)} - {item.Verdict.Reason}");
    foreach (var r in watchListed)
      ImGui.TextDisabled($"  {Format.Hq(r.Item.ItemName, r.Item.IsHq)} @ {r.Item.RetainerName} - {(r.IsFresh ? BuildReason(r.Item) : r.Flag!.Detail)}");
  }

  // ==========================================================================
  // Row renderers
  // ==========================================================================

  /// <summary>
  /// Collapsing pile header with a colored title + count. Returns whether it is open.
  /// The <paramref name="id"/> is STABLE (never carries the dynamic location tag), so
  /// walking to a retainer does not reset the header's expand state.
  /// </summary>
  private static bool PileHeader(string title, string id, int count, Vector4 color, string hint)
  {
    ImGui.PushStyleColor(ImGuiCol.Text, color);
    var open = ImGui.CollapsingHeader($"{title}: {count}###pile{id}", ImGuiTreeNodeFlags.DefaultOpen);
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(hint);
    return open;
  }

  private static string LocationTag(bool here) => here ? "  [you are here]" : "";

  private void DrawRoutedRow(RoutedItem item, bool showMoves)
  {
    TierBadge(item.Confidence);
    ImGui.SameLine();
    ImGui.Text(Format.Hq(item.Name, item.IsHq));
    ImGui.SameLine();
    ImGui.TextDisabled($"ilvl {item.Ilvl}");
    ImGui.SameLine();
    // Item 7: a verdict built on community fallback while world data warms wears a
    // "~ community read" suffix - the two-truths-per-reload smell, made honest.
    var warming = UniversalisStats.PendingCount > 0;
    var reason = item.Verdict.Reason;
    if (item.CommunityFallback && warming)
      reason += "  ~ community read, world data pending";
    // Item 8 addendum: a Contradicted row must STATE the market number that
    // overruled it - the deciding evidence can't hide behind the "!" badge.
    if (item.Confidence == ConfidenceTier.Contradicted)
    {
      var note = LedgerListings.ContradictionNote(
        item.CommunityMedian > 0 ? item.CommunityMedian : null,
        item.CommunitySampleCount, item.MarketVelocity);
      if (note.Length > 0) reason += $" {note}";
    }
    ImGui.TextWrapped($"- {reason}");
    if (item.Verdict.RunnerUpReason.Length > 0 && ImGui.IsItemHovered())
      ImGui.SetTooltip($"Runner-up ({item.Verdict.RunnerUp}): {item.Verdict.RunnerUpReason}");

    if (showMoves)
      DrawMoveButtons(item);
  }

  private void DrawTriageRow(InboxRow row, LedgerPile pile)
  {
    var item = row.Item;
    var tier = ScoreTriage(item, LedgerPiles.ForTriage(item.Result));
    TierBadge(tier);
    ImGui.SameLine();
    var color = row.IsFresh ? ScroogeColors.Amber : ScroogeColors.Warning;
    ImGui.PushStyleColor(ImGuiCol.Text, color);
    ImGui.Text(Format.Hq(item.ItemName, item.IsHq));
    ImGui.PopStyleColor();
    ImGui.SameLine();
    ImGui.TextDisabled($"@ {item.RetainerName}");
    if (!row.IsFresh)
    {
      ImGui.SameLine();
      var ageDays = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - row.Flag!.CreatedAt) / 86400;
      ImGui.TextDisabled(ageDays < 1 ? "(today)" : $"({ageDays}d)");
    }
    ImGui.SameLine();
    var reason = row.IsFresh ? BuildReason(item) : row.Flag!.Detail;
    // Item 8 addendum: surface the market number that contradicted this listed
    // verdict inline (the settled-sale history the row was judged against).
    if (tier == ConfidenceTier.Contradicted)
    {
      var note = LedgerListings.ContradictionNote(
        item.HistoryMedianPrice is int m && m > 0 ? m : (long?)null,
        item.HistorySaleCount, null,
        payer: "settled sales pay"); // LOCAL lane history - never dressed as DC-wide
      if (note.Length > 0) reason += $" {note}";
    }
    ImGui.TextWrapped($"- {reason}");

    if (Plugin.TriageOrchestrator.IsRunning) return;

    // Action set - preserved from the triage inbox (Vend / Pull / Reprice / Dismiss).
    _actions.TryGetValue(item, out var current);
    var canReprice = item.Result is PricingResult.CapBlocked or PricingResult.UndercutTooDeep or PricingResult.UpwardHeld;
    var key = $"{item.ItemId}_{item.IsHq}_{item.RetainerName}";

    DrawActionToggle(item, key, TriageAction.Vendor, "Vend", current, pile);
    ImGui.SameLine(0, 2);
    DrawActionToggle(item, key, TriageAction.Pull, "Pull", current, pile);
    if (canReprice)
    {
      ImGui.SameLine(0, 2);
      DrawActionToggle(item, key, TriageAction.Reprice, "Reprc", current, pile);
    }
    ImGui.SameLine(0, 2);
    if (ImGui.SmallButton($"Dismiss##{key}"))
      DismissRow(row);
  }

  private static void TierBadge(ConfidenceTier tier)
  {
    var (glyph, color, label) = tier switch
    {
      ConfidenceTier.Unanimous => ("*", ScroogeColors.Earned, "Unanimous - evidence agrees; bulk-confirmable"),
      ConfidenceTier.Contradicted => ("!", ScroogeColors.Spent, "Contradicted - the market disagrees; in Review, immune to bulk"),
      _ => ("~", ScroogeColors.Amber, "Mixed - thin or partial evidence; needs your row click"),
    };
    ImGui.PushStyleColor(ImGuiCol.Text, color);
    ImGui.Text(glyph);
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
  }

  // ==========================================================================
  // Bulk confirm (design Section 4 - only Unanimous rows enumerate)
  // ==========================================================================

  /// <summary>
  /// ONE confirm for everything ruled at the bell: List and Pull-and-Vendor bag
  /// rows execute through the same Hawk run (it lists and vendors per item), so
  /// one trip to the bell is one click - the pile split is presentation, not two
  /// jobs. Drawn in both piles; either instance fires the whole set. Only rows
  /// the confidence gate cleared (Unanimous or player-ruled) ride.
  /// </summary>
  private void BellConfirmButton(bool atBell, string idSuffix)
  {
    var listRows = _items.Where(i => i.ActivePile == LedgerPile.List).ToList();
    var vendorRows = _items.Where(i => i.ActivePile == LedgerPile.PullAndVendor).ToList();
    var listSet = LedgerConfidence.BulkSet(listRows.Select(r => (r, r.Confidence, r.PlayerResolved)));
    var vendSet = LedgerConfidence.BulkSet(vendorRows.Select(r => (r, r.Confidence, r.PlayerResolved)));
    var total = listSet.Count + vendSet.Count;

    ImGui.BeginDisabled(!atBell || total == 0);
    if (ImGui.Button($"Confirm bell run ({listSet.Count} list + {vendSet.Count} vendor)##bellrun{idSuffix}"))
      FireBellRun(listSet, vendSet);
    ImGui.EndDisabled();
    if (!atBell && total > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
      ImGui.SetTooltip("Summon your retainers (any bell) to run these.");

    var pending = (listRows.Count - listSet.Count) + (vendorRows.Count - vendSet.Count);
    if (pending > 0)
    {
      ImGui.SameLine();
      ImGui.TextDisabled($"{pending} need a row click");
    }
  }

  /// <summary>The bell run's shared trigger: pile button and sweep deck fire the same set.</summary>
  private void FireBellRun(List<RoutedItem> listSet, List<RoutedItem> vendSet)
  {
    var all = listSet.Concat(vendSet).ToList();
    foreach (var item in all)
      RecordRoutedSignal(item, item.Pile); // a bulk confirm is mass agreement
    RunHawkForRouted(all);
  }

  private void BulkConfirmTriage(List<InboxRow> rows, TriageAction action, string verb)
  {
    var eligible = rows
      .Where(r => LedgerConfidence.IsBulkEligible(ScoreTriage(r.Item, LedgerPiles.ForTriage(r.Item.Result))))
      .ToList();
    ImGui.BeginDisabled(eligible.Count == 0 || Plugin.TriageOrchestrator.IsRunning);
    if (ImGui.Button($"Confirm all {verb} ({eligible.Count} unanimous)##bulkt{verb}{action}"))
    {
      foreach (var r in eligible)
      {
        _actions[r.Item] = action;
        RecordTriageSignal(r.Item, LedgerPiles.ForTriage(r.Item.Result), action);
      }
      ExecuteTriageBatch();
    }
    ImGui.EndDisabled();
    var mixed = rows.Count - eligible.Count;
    if (mixed > 0)
    {
      ImGui.SameLine();
      ImGui.TextDisabled($"{mixed} need a row click");
    }
  }

  // ==========================================================================
  // Actions + teaching signals
  // ==========================================================================

  private void DrawMoveButtons(RoutedItem item)
  {
    DrawMoveButton(item, RoutingExit.List, "List");
    ImGui.SameLine(0, 2);
    DrawMoveButton(item, RoutingExit.Desynth, "Melt");
    ImGui.SameLine(0, 2);
    DrawMoveButton(item, RoutingExit.Gc, "GC");
    ImGui.SameLine(0, 2);
    DrawMoveButton(item, RoutingExit.Vendor, "Vend");
  }

  private void DrawMoveButton(RoutedItem item, RoutingExit target, string label)
  {
    // Green means DECIDED - by evidence (Unanimous) or by the player - never just
    // "where the row happens to sit". An undecided row (Mixed, or demoted into
    // Review) shows no green and every button stays live, so the click IS the
    // ruling whether it confirms the router or moves the row.
    var settled = item.PlayerResolved || LedgerConfidence.IsBulkEligible(item.Confidence);
    var isCurrent = settled && item.ActivePile != LedgerPile.Review && item.Pile == target;
    if (isCurrent)
      ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.45f, 0.25f, 1f));

    if (ImGui.SmallButton($"{label}##move{item.Container}_{item.SlotIndex}_{target}") && !isCurrent)
    {
      RecordRoutedSignal(item, target); // confirmation or disagreement - both teach
      item.Pile = target;
      item.InReview = false;
      item.PlayerResolved = true;
    }

    if (isCurrent)
      ImGui.PopStyleColor();
  }

  /// <summary>
  /// Records a teaching signal for a bag verdict (design Section 4). Confirmations
  /// (player == router) and overrides (player != router) both write to V14
  /// routing_overrides; the confidence read side counts only the disagreements.
  /// Once per (item, target) per session so a held view does not spam the table.
  /// </summary>
  private void RecordRoutedSignal(RoutedItem item, RoutingExit playerExit)
  {
    var routerVerdict = item.Verdict.IsReview ? "Review" : item.Verdict.Exit.ToString();
    var key = (item.ItemId, item.IsHq, $"{routerVerdict}->{playerExit}");
    if (!_signalsRecorded.Add(key)) return;
    item.OverrideRecorded = true;
    try
    {
      GilStorage.InsertRoutingOverride(item.ItemId, item.IsHq, item.Ilvl,
        routerVerdict, item.Verdict.Reason, playerExit.ToString());
      // V20: flag the standing receipt so override rate by confidence tier is queryable.
      if (routerVerdict != playerExit.ToString())
        GilStorage.MarkRoutingReceiptOverridden(item.ItemId, item.IsHq);
    }
    catch { /* storage unavailable - the move still applies, just unrecorded */ }
  }

  /// <summary>Records a teaching signal for an absorbed triage row - the verdict class is its natural pile.</summary>
  private void RecordTriageSignal(PricingItem item, LedgerPile natural, TriageAction action)
  {
    var routerVerdict = natural.ToString();
    var key = (item.ItemId, item.IsHq, $"{routerVerdict}->{action}");
    if (!_signalsRecorded.Add(key)) return;
    try
    {
      GilStorage.InsertRoutingOverride(item.ItemId, item.IsHq, 0,
        routerVerdict, item.Result.ToString(), action.ToString());
    }
    catch { /* storage unavailable */ }
  }

  private void DrawActionToggle(PricingItem item, string key, TriageAction action, string label,
    TriageAction current, LedgerPile pile)
  {
    var isActive = current == action;
    if (isActive)
    {
      var highlight = action switch
      {
        TriageAction.Vendor => new Vector4(0.8f, 0.2f, 0.2f, 1f),
        TriageAction.Pull => new Vector4(0.2f, 0.5f, 0.8f, 1f),
        TriageAction.Reprice => new Vector4(0.7f, 0.6f, 0.1f, 1f),
        _ => new Vector4(0.4f, 0.4f, 0.4f, 1f),
      };
      ImGui.PushStyleColor(ImGuiCol.Button, highlight);
    }

    if (ImGui.SmallButton($"{label}##{key}_{action}"))
    {
      if (isActive)
        _actions.Remove(item);
      else
      {
        _actions[item] = action;
        RecordTriageSignal(item, pile, action);
      }
    }

    if (isActive)
      ImGui.PopStyleColor();
  }

  private void DismissRow(InboxRow row)
  {
    if (row.IsFresh)
    {
      _triageItems.Remove(row.Item);
      _actions.Remove(row.Item);
    }
    else
    {
      try { GilStorage.SetTriageFlagStatus(row.Flag!.Id, "dismissed"); } catch { /* storage unavailable */ }
      if (_flagItems.TryGetValue(row.Flag!.Id, out var stale)) _actions.Remove(stale);
      _flagItems.Remove(row.Flag!.Id);
      RefreshHeldFlags();
    }
    RecordTriageSignal(row.Item, EffectiveTriagePile(row.Item), TriageAction.None);
  }

  private LedgerPile EffectiveTriagePile(PricingItem item)
  {
    var natural = LedgerPiles.ForTriage(item.Result);
    return LedgerPiles.Effective(natural, ScoreTriage(item, natural));
  }

  // ==========================================================================
  // Executors (existing paths, now confidence-gated)
  // ==========================================================================

  /// <summary>Hands a specific set of bag rows to the Hawk run (List selected, Vendor flagged).</summary>
  private void RunHawkForRouted(List<RoutedItem> rows)
  {
    var hawkItems = new List<HawkWindow.HawkItem>();
    foreach (var item in rows.Where(i => i.Pile is RoutingExit.List or RoutingExit.Vendor))
    {
      hawkItems.Add(new HawkWindow.HawkItem
      {
        ItemId = item.ItemId,
        Name = item.Name,
        Quantity = item.Quantity,
        IsHq = item.IsHq,
        Selected = item.Pile == RoutingExit.List,
        IsAlwaysVendor = item.Pile == RoutingExit.Vendor,
        Container = item.Container,
        SlotIndex = item.SlotIndex,
        LastSalePrice = item.LastSalePrice,
      });
    }
    if (hawkItems.Count == 0)
    {
      Svc.Chat.PrintError("[Scrooge] None of the confirmed rows are List/Vendor exits - nothing for the Hawk run.");
      return;
    }
    Plugin.AutoPinch.NavigateAndStartHawkRun(hawkItems);
    IsOpen = false;
  }

  /// <summary>Hands a specific set of Churn rows to the GC turn-in orchestrator.</summary>
  private void ExecuteChurn(List<RoutedItem> rows)
  {
    var churnItems = rows
      .Select(i => new GcTurnInOrchestrator.GcTurnInItem(
        i.ItemId, i.IsHq, i.Name, GcSeals.For(i.ItemId) ?? 0))
      .Where(i => i.SealReward > 0)
      .OrderByDescending(i => i.SealReward)
      .ToList();
    var skipped = rows.Count - churnItems.Count;
    if (skipped > 0)
      Svc.Chat.Print($"[Scrooge] Turn-in: skipped {skipped} {(skipped == 1 ? "item" : "items")} with no resolvable seal value - {(skipped == 1 ? "it stays" : "they stay")} in your bags.");
    if (churnItems.Count == 0) return;
    foreach (var item in rows)
      RecordRoutedSignal(item, RoutingExit.Gc);
    Plugin.GcTurnIn.StartRun(churnItems);
  }

  private void ExecuteTriageBatch()
  {
    if (_actions.Count == 0) return;
    var batch = new Dictionary<PricingItem, TriageAction>(_actions);
    if (!Plugin.TriageOrchestrator.QueueTriageBatch(batch))
      return;
    // Rows are retired per-item on completion (RemoveItem callback), never here.
    _actions.Clear();
  }

  // ==========================================================================
  // Triage row lifecycle (called by the orchestrator + the run log)
  // ==========================================================================

  /// <summary>Stores this run's triage items so they survive after CurrentRun clears. Called by the run log.</summary>
  internal void SetRun(RunData run)
  {
    _triageItems = run.TriageItems;
    _actions.Clear();
  }

  /// <summary>
  /// Retires a row on COMPLETION of its action (vendored/pulled/repriced) or when the
  /// orchestrator skipped it as no-longer-listed. Closes any matching held flags.
  /// Called by TriageOrchestrator - the ONLY place actions resolve rows.
  /// </summary>
  internal void RemoveItem(PricingItem item)
  {
    _triageItems.Remove(item);
    _actions.Remove(item);
    foreach (var flag in _heldFlags.Where(f => f.ItemId == item.ItemId && f.IsHq == item.IsHq && f.RetainerName == item.RetainerName))
      try { GilStorage.SetTriageFlagStatus(flag.Id, "actioned"); } catch { /* storage unavailable */ }
    RefreshHeldFlags();
  }

  // ==========================================================================
  // Desynth preview hooks (location-session parity, unchanged contract)
  // ==========================================================================

  internal int MeltPileCount
    => Plugin.Configuration.EnableRoutingBrain
      ? _items.Count(i => i.ActivePile == LedgerPile.Melt) : 0;

  internal HashSet<(uint ItemId, bool IsHq)> MeltPileVariants()
  {
    if (!Plugin.Configuration.EnableRoutingBrain) return [];
    return _items.Where(i => i.ActivePile == LedgerPile.Melt)
      .Select(i => (i.ItemId, i.IsHq)).ToHashSet();
  }

  // ==========================================================================
  // Reason strings (absorbed from the triage inbox)
  // ==========================================================================

  private static string BuildReason(PricingItem item)
  {
    return item.Result switch
    {
      // RejectedPrice is the operand the guard actually compared (lane path) -
      // MbPrice is the board read, which can sit far above the rejected lane
      // candidate. Board path leaves RejectedPrice null; there MbPrice IS the
      // compared operand. (The Mossy Stone Daggers fix, 07-23.)
      PricingResult.BelowMinimum =>
        $"Below Minimum ({(item.RejectedPrice ?? item.MbPrice):N0} gil/ea < {Plugin.Configuration.MinimumListingPrice:N0} gil min)",
      PricingResult.BelowFloor =>
        Plugin.Configuration.PriceFloorMode == PriceFloorMode.DomanEnclave
          ? $"Below Floor ({(item.RejectedPrice ?? item.MbPrice):N0} gil/ea < {item.VendorPrice * 2:N0} gil enclave 2x)"
          : $"Below Floor ({(item.RejectedPrice ?? item.MbPrice):N0} gil/ea < {item.VendorPrice:N0} gil vendor)",
      PricingResult.CapBlocked =>
        $"Cap ({item.CurrentListingPrice:N0} -> {item.MbPrice:N0}, {item.PriceChangePercent:F0}%)",
      PricingResult.UndercutTooDeep =>
        $"Undercut Too Deep ({item.PriceChangePercent:F0}%)",
      PricingResult.UpwardHeld =>
        $"Upward Held (market {item.MbPrice:N0} exceeds {Plugin.Configuration.UpwardRepriceMultiplier:0.#}x own-sales sanity; listed {item.CurrentListingPrice:N0})",
      PricingResult.LaneHeld =>
        item.Lane?.Evidence is { } ev ? $"Held (thin history) - {ev}" : "Held (thin history)",
      PricingResult.NoData => "No Data (no listings)",
      _ => "Unknown"
    };
  }

  /// <summary>
  /// True anywhere Hawk can start from: the bell roster (NavigateAndStartHawkRun
  /// hops to a retainer's sell view itself) or already inside a sell view.
  /// </summary>
  private static unsafe bool AtRetainerBell()
    => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out _)
    || GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _);
}
