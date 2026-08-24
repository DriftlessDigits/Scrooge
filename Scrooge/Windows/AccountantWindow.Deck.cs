using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Linq;

using Scrooge.Board;
using Scrooge.Rounds;

namespace Scrooge.Windows;

/// <summary>
/// THE DECK: what there is to do right now, derived ONCE per frame and handed down.
/// The window's whole answer to <see cref="IRoundBoard"/> - the work sets the round
/// spends, the counts its rail quotes, the judgment queue its Continue refuses over.
///
/// <para><b>Every set has ONE definition here</b> (review ruling S4, addendum 2). The
/// count on the button and the set the run spends are the same call, because a filter
/// applied to one copy of an arithmetic is a filter the other copy quietly does not
/// have - which is exactly what invariant B turned from a tidiness problem into a
/// correctness one.</para>
/// </summary>
internal sealed partial class AccountantWindow
{
  /// <summary>
  /// The deck for a caller with no rows in hand - the round itself, asking off the pump.
  /// It builds the standing rows the same way every draw does, because "what there is to
  /// do" cannot mean two things depending on who asked.
  /// </summary>
  public DeckState ComputeDeck() => ComputeDeck(BuildStandingRows());

  /// <summary>
  /// Derives the whole deck state from the cached ledger. Called by the deck's own
  /// draw and by the completion handler (so the run log's rail stays live even
  /// with the Ledger shut) - never anywhere that would make it two answers.
  /// </summary>
  private DeckState ComputeDeck(List<InboxRow> standingRows)
  {
    // Work sets - derived from the same sources as the pile buttons, so the deck
    // can never disagree with the piles it mirrors.
    var (listSet, vendSet) = RoutedBellSets();
    // Reprice threads the player's resolution exactly like its three siblings
    // (list / vendor / churn): a human who clicked Reprc on the row HAS resolved
    // it, and the row belongs in the round's work set regardless of tier. Filtering
    // on the raw tier alone left hand-picked reprices out of the deck's count and
    // out of its fire (07-24).
    var repriceEligible = BoardConfidence.BulkSet(standingRows
      .Where(r => EffectiveStandingPile(r.Item) == BoardPile.Reprice)
      .Select(r => (r,
        _cache.ScoreStanding(r.Item, BoardPiles.ForStanding(r.Item.Result)),
        _actions.TryGetValue(r.Item, out var staged) && staged.Action == StandingAction.Reprice)));
    // The reprice work set (WALK unit 6, now the bell's first leg). The pile's
    // confidence-cleared rows PLUS any row a human explicitly staged to Reprice from
    // somewhere else - a Contradicted cap-blocked row is drawn in Review, keeps its
    // Reprc button, and a click on it is a ruling the stage will honour. The bell
    // counts and drains this one set, so its label can never promise a different
    // number than it does.
    var repriceStage = repriceEligible
      .Concat(standingRows.Where(r =>
        !repriceEligible.Contains(r)
        && _actions.TryGetValue(r.Item, out var staged) && staged.Action == StandingAction.Reprice))
      .ToList();
    // The PULL-AND-VENDOR work set, the same shape (WALK unit 9). These are standing
    // listings to retrieve and vendor - retainer work, so the bell's. The pinch's
    // vendor rider usually reaches them first and retires them, at which point this
    // set empties on the next refresh and the bell's count drops with it; on a
    // pinch-skipped night the bell is the door that keeps them from being stranded.
    // Melt and Gc joined the pull family in unit 4 (pull-for-X, walk ruling 2):
    // all four are "retrieve at the retainer" jobs; where the item goes AFTER
    // the bags is the staged verb's business, not the pull door's.
    var pullEligible = BoardConfidence.BulkSet(standingRows
      .Where(r => EffectiveStandingPile(r.Item) == BoardPile.PullAndVendor)
      .Select(r => (r,
        _cache.ScoreStanding(r.Item, BoardPiles.ForStanding(r.Item.Result)),
        _actions.TryGetValue(r.Item, out var staged)
          && staged.Action is StandingAction.Vendor or StandingAction.Pull or StandingAction.Melt or StandingAction.Gc)));
    var pullStage = pullEligible
      .Concat(standingRows.Where(r =>
        !pullEligible.Contains(r)
        && _actions.TryGetValue(r.Item, out var staged)
        && staged.Action is StandingAction.Vendor or StandingAction.Pull or StandingAction.Melt or StandingAction.Gc))
      .ToList();
    // Both through their one definition, so the commit reaches them (review ruling S4):
    // the count on the rail, the count on the button and the set the stage spends are
    // one set for the melt and the turn-in exactly as they already were for the bell.
    var meltCount = MeltPileRows().Count;
    var churnSet = ChurnBellSet();
    // The one-door bell: the routed gear survivors PLUS everything else the Hawk
    // would list, through a single count and a single confirm.
    var gateJoiners = BellGateJoiners();
    var bell = TallyBell(listSet, vendSet, gateJoiners, repriceStage.Count, pullStage.Count);

    // The fit check at press (WALK unit 8; estimate reworked 08-16 round walk): two
    // MEASURED clocks - the round's whole machine-time plan vs the soonest venture
    // return - plus the board-read age against the re-pinch floor. Advises, never
    // gates: DOESN'T-FIT costs one deliberate click, BOARD-FRESH skips the pinch,
    // NO-DATA fires without a check rather than lying about a clock it can't see.
    // The estimate is the SAME arithmetic the step list prices its rows with (each
    // stage's own banked pace times what it is holding, pinch over the book-kept
    // roster) - one derivation, so the fit line can never quote a number the rows
    // above it contradict. The retired feed here was the legacy blended pace times
    // the stale scan count, and it said "~11m" under a pinch row that said "~15m".
    // RECON'S WORK ANSWER, DERIVED - nothing procedural decides what it walks. The
    // stale-or-missing half of the listable scan IS the work set (ruled 08-10), so
    // "does recon have work" and "how many" are the same question asked once, and a
    // night where everything is fresh answers zero and the cursor skips the stage
    // silently like any empty one. Both operands are already cached (the scan and
    // the bank times refresh together in RefreshListings); the arithmetic here is
    // in memory. Derived BEFORE the fit check because the round estimate prices this
    // stage like every other.
    // With a Re-Look armed the stage's work is the WHOLE listable set, because that
    // is what the run will actually walk (see ReLook). The count and the run read one
    // answer or the press is a no-op the deck cannot explain.
    var reconStale = _conductor.Plan.ReLookArmed
      ? ReconFreshness.VariantCount(_cache.BellGateRows, r => (r.ItemId, r.IsHq))
      : ReconFreshness.StaleCount(
        _cache.BellGateRows, r => (r.ItemId, r.IsHq), _cache.ReconBankTimes,
        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Plugin.Configuration.ReconFreshHours);

    var estRoundMs = StageRail.PlanMachineMs(
      s => s switch
      {
        RoundStage.BellRun => bell.Total,
        RoundStage.Desynth => meltCount,
        RoundStage.TurnIn => churnSet.Count,
        RoundStage.Recon => reconStale,
        _ => 0,
      },
      RoundConductor.RateFor, _cache.ListedNow);
    var ventureSecs = GameSafe.SoonestVentureReturnSeconds();
    var boardAge = _cache.LastFullScanAt > 0
      ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _cache.LastFullScanAt
      : (long?)null;
    var repinchFloor = TimeSpan.FromHours(Math.Max(1, Plugin.Configuration.RepinchFloorHours));
    var fit = FitCheck.AtPress(estRoundMs, ventureSecs, boardAge, repinchFloor);

    // THE FIT CHECK'S SECOND SENSOR (07-26): does the seal wallet hold the pile the
    // turn-in is about to be handed? The wallet read is the SAME one the mid-run halt
    // uses (GameSafe.CompanySeals - cap included, never a literal 90,000), and the
    // rewards are the same GcSeals lookup ExecuteChurn hands off, so the plan and the
    // stop cannot disagree about either number. Computed ONCE per frame here because
    // two surfaces read it - the deck's plan line and the Churn pile header.
    var wallet = GameSafe.CompanySeals();
    var sealFit = SealFit.Assess(wallet?.Current, wallet?.Max,
      churnSet.Select(r => GcSeals.For(r.ItemId) ?? 0).ToList());

    // THE HINGE'S COUNT, DERIVED HERE SO THERE IS ONE OF IT (unit 5). The judgment
    // queue moved from the launch button's lock to the hinge's Continue gate (ruled
    // Q1), and it is also the hinge's rail count and the completion banner's unruled
    // number. Computing it in the deck rather than at each of those three draw sites
    // is the same discipline the deck itself was built on: one answer per frame, handed
    // down, because deriving it per surface is how two of them come to describe one
    // round differently.
    //
    // THE QUEUE ITSELF RIDES THE DECK (3-3b), not just its count. The Continue's
    // refusal names the STAGES the unruled rows would have ridden, and it used to
    // re-derive the whole queue - rebuilding every standing row, re-walking every bag
    // row - inside a hover, per frame. Same rule the count already obeyed, applied to
    // the thing the count is a count OF.
    var judgmentStages = JudgmentQueue(standingRows, fit).Select(e => e.Stage).ToList();

    // THE HINGE'S COUNT NOW COMES FROM THE WALK, whenever there is a walk (Task 3).
    // Bag decisions IS the launch refusal's former job - Review rows withheld until
    // answered - so the count the Continue refuses over has to be the walk's own
    // <see cref="TriageWalk.CallsLeft"/> and not a second arithmetic beside it. Two
    // counts would be two gates, and the one the player is looking at would be the one
    // that did not fire.
    //
    // IDLE STILL READS THE QUEUE. There is no walk before the hinge is reached, and the
    // launch preview's "waiting on you" line and the completion banner's unruled tally
    // both need an answer then - so the judgment queue stays exactly what it was, in
    // the seat it was always in, and the walk takes over only while it exists.
    //
    // WHAT THIS LOOSENS, said out loud: the queue counted every unstaged standing
    // contest; the walk counts undecided CASES only. A standing lane the player leaves
    // alone is a Board Call and an answer (the book keeps its ask), so it no longer
    // holds the Continue shut. That is the walk's own ruling - see TriageWalk.Rail -
    // and it is a real change in what the hinge refuses over.
    var rulingsNeeded = _walk is { } walk ? walk.CallsLeft : judgmentStages.Count;

    return new DeckState(repriceStage, pullStage, meltCount, churnSet, bell, _cache.CofferCount,
      Plugin.Configuration.OpenVentureCoffers, fit, estRoundMs, _cache.ListedNow, ventureSecs,
      sealFit, _conductor.Skipped, reconStale, rulingsNeeded, judgmentStages);
  }

  // ==========================================================================
  // The one-door bell (WALK unit 4)
  // ==========================================================================

  /// <summary>
  /// THE GATE HALF OF THE BELL: every cached Hawk-gate row that is neither in the
  /// router's jurisdiction nor gated toward a better exit. Fresh melt yields land
  /// here (they are ordinary listable bag items - the old "check all in Hawk Run"
  /// side door was doing by hand what the gate already knew), as do coffer dyes,
  /// venture loot, mats and furniture.
  ///
  /// <para>INVARIANT B'S GATE (the unit-5 addendum, ruled 2026-08-10). Once the hinge
  /// has committed, this stops being a live answer and becomes a HELD one: the gate
  /// rows the human was looking at when he pressed Continue, plus whatever this Round's
  /// own melt made since. Everything else waits for the next Round.</para>
  ///
  /// <para>The filter sits HERE rather than at the fire site because two things read
  /// this method - the deck's tally and the bell's composition - and the whole point
  /// of a commit is that the number on the button and the set the run spends are one
  /// set. Filtering only at fire time would leave the bell advertising rows it had
  /// already decided not to touch.</para>
  /// </summary>
  public List<ListableItem> BellGateJoiners()
    => _conductor.Hinge.AdmitGate(BellPlan.Join(
      _cache.BellGateRows.Select(r =>
        (r, new BellVariant(r.ItemId, r.IsHq), r.RouteTag.Verdict is RouteTagMap.Verdict.GateDesynth or RouteTagMap.Verdict.GateGc)),
      _cache.RouterJurisdiction));

  /// <summary>
  /// THE BELL'S ROUTED HALF, ONE DEFINITION (addendum 2). The confidence gate's answer
  /// over the two bag piles the bell spends, with the hinge's commit applied.
  ///
  /// <para>Both readers come here - the deck's tally and the bell's own composition -
  /// for the same reason <see cref="BellGateJoiners"/> filters in one place: the number
  /// on the button and the set the run spends have to be one set, and a filter applied
  /// to one copy of an arithmetic is a filter the other copy does not have.</para>
  /// </summary>
  public (List<RoutedItem> ListSet, List<RoutedItem> VendSet) RoutedBellSets()
  {
    var listRows = _cache.Items.Where(i => i.ActivePile == BoardPile.List).ToList();
    var vendorRows = _cache.Items.Where(i => i.ActivePile == BoardPile.PullAndVendor).ToList();
    // DEFER ROWS RIDE (08-06). The fourth element is the entire structural claim of the
    // headliner: a row drawn in the Defer group is spent by the round exactly as if it
    // sat here, so the work set that feeds the bell has to take it. Drop this and Defer
    // is Watch with better prose.
    var listSet = BoardConfidence.BulkSet(
      listRows.Select(r => (r, r.Confidence, r.PlayerResolved, r.Deferred)));
    var vendSet = BoardConfidence.BulkSet(
      vendorRows.Select(r => (r, r.Confidence, r.PlayerResolved, r.Deferred)));
    return (_conductor.Hinge.AdmitRouted(listSet), _conductor.Hinge.AdmitRouted(vendSet));
  }

  /// <summary>
  /// THE MELT SET, ONE DEFINITION (review ruling S4). The router's Desynth pile with the
  /// hinge's commit applied - and the only place that pile is read, for the same reason
  /// <see cref="BellGateJoiners"/> filters in one place: a count taken upstream of the
  /// filter would advertise rows the stage had already decided not to touch.
  /// </summary>
  public List<RoutedItem> MeltPileRows()
    => _conductor.Hinge.AdmitMelt(_cache.Items.Where(i => i.ActivePile == BoardPile.Melt).ToList());

  /// <summary>
  /// THE TURN-IN SET, ONE DEFINITION (review ruling S4). The router's Churn pile through
  /// the confidence gate, with the hinge's commit applied. Both readers come here - the
  /// deck's derivation and the commit itself.
  /// </summary>
  public List<RoutedItem> ChurnBellSet()
  {
    var churnRows = _cache.Items.Where(i => i.ActivePile == BoardPile.Churn).ToList();
    return _conductor.Hinge.AdmitChurn(BoardConfidence.BulkSet(
      churnRows.Select(r => (r, r.Confidence, r.PlayerResolved, r.Deferred))));
  }

  /// <summary>
  /// The bell's one count, split by where each row came from - the two bag halves
  /// that ride the Hawk run, and the two standing-listing verbs that ride the
  /// triage executor. One stop, one number, three verbs.
  /// </summary>
  private static BellTally TallyBell(List<RoutedItem> listSet, List<RoutedItem> vendSet,
    List<ListableItem> gateJoiners, int repriceRows, int pullRows)
    => new(
      RoutedList: listSet.Count(r => r.Pile == RoutingExit.List),
      RoutedVendor: vendSet.Count(r => r.Pile == RoutingExit.Vendor),
      GateList: gateJoiners.Count(r => !r.IsAlwaysVendor),
      GateVendor: gateJoiners.Count(r => r.IsAlwaysVendor),
      Reprice: repriceRows,
      Pull: pullRows);

  /// <summary>
  /// What the salvage window selects. Reads <see cref="MeltPileRows"/> and therefore
  /// carries the hinge's commit (review ruling S4): inside a committed Round this is the
  /// pile the human pressed Continue over, never the pile as it stands right now, so a
  /// piece of gear that arrived in the bags between the press and the melt is not ticked
  /// and is not destroyed. It waits for the next Round, where recon reads it and the
  /// hinge asks about it properly.
  /// </summary>
  public HashSet<(uint ItemId, bool IsHq)> MeltPileVariants()
    => MeltPileRows().Select(i => (i.ItemId, i.IsHq)).ToHashSet();

  // ==========================================================================
  // The front-load gate (WALK unit 5), now the LAUNCH LOCK (stage 2a)
  //
  // The gate's screen is gone - the ruled board put every one of its rulings on
  // the row itself, so presenting them a second time above the piles was the same
  // question asked twice. Its ARITHMETIC is what mattered and it is untouched: the
  // derived queue below is what the one launch control refuses over, and what the
  // completion banner counts as still waiting.
  // ==========================================================================

  /// <summary>
  /// One judgment-queue row, whichever half of the Ledger it came from, carrying
  /// the pile it is DRAWN in.
  /// </summary>
  private readonly record struct GateRow(RoutedItem? Routed, InboxRow? Standing, BoardPile Pile);

  /// <summary>
  /// THE JUDGMENT QUEUE, derived (see <see cref="GatePlan"/>): every row that
  /// would ride a stage of THIS run if ruled, but currently will not, because it
  /// is waiting on a human click. Nothing here is a list of question types - each
  /// candidate is a real row paired with the stage its ruling feeds, and
  /// <see cref="GatePlan.NeedsRuling"/> (the exact inverse of the bulk gate)
  /// decides which of them are still open.
  ///
  /// Deliberately absent: the Melt pile (the melt run takes it whole, tier and
  /// all, so those rows ride already), the DEFER rows (the round acts on them
  /// unasked - that is their whole contract), and the
  /// broader ledgerbook backlog, which stays off-cadence in the Ledger as ruled.
  ///
  /// <para>ALSO ABSENT: anything feeding a stage the player DEFERRED this run
  /// (stage 2a). The queue's whole definition is "rows that would ride a stage of
  /// THIS RUN if ruled", and a deferred stage is not part of this run - asking
  /// about its rows would make the skip a trap, because the launch control refuses
  /// over exactly this count.</para>
  ///
  /// <para>ASKED ONCE PER FRAME, from <see cref="ComputeDeck"/>, and banked on the
  /// deck (3-3b). Every reader takes it from there.</para>
  /// </summary>
  private List<JudgmentEntry<GateRow>> JudgmentQueue(List<InboxRow> standingRows, FitCheck fit)
  {
    var candidates = new List<(GateRow Row, RoundStage Stage, ConfidenceTier Tier, bool PlayerResolved,
      bool InReview, bool RidesWholePile, bool Deferred)>();

    // The stage a bag pile's rows ride. List and Pull & Vendor share the one-door
    // bell; Churn is the turn-in; the silent board feeds nothing.
    static RoundStage? BagStage(BoardPile pile) => pile switch
    {
      BoardPile.List or BoardPile.PullAndVendor => RoundStage.BellRun,
      BoardPile.Melt => RoundStage.Desynth,
      BoardPile.Churn => RoundStage.TurnIn,
      _ => null,
    };

    foreach (var item in _cache.Items)
    {
      // A Review row is attributed to the stage the ROUTER's own proposal would
      // land it in - that is what a confirm click does. Melt counts here even
      // though the melt PILE does not: a Review-demoted melt row is NOT in the
      // melt pile, so ruling it genuinely changes the stage.
      var stage = item.ActivePile == BoardPile.Review
        ? BagStage(BoardPiles.ForRoutingExit(item.Pile, isReview: false))
        : BagStage(item.ActivePile);

      if (stage is not RoundStage s || _conductor.Skipped.Contains(s)) continue;
      // The melt pile's rows ride whole-pile - the predicate is told so rather
      // than dropped here (item 9, 08-06), because the board reads the same
      // predicate and the two must answer alike. See GatePlan.NeedsRuling.
      // A DEFER row is not a decision either - the round spends it whatever the
      // player does - so it is told the same way, and the launch control stops
      // refusing over rows that were never going to wait.
      candidates.Add((new GateRow(item, null, item.DrawnPile), s, item.Confidence, item.PlayerResolved,
        item.ActivePile == BoardPile.Review, item.ActivePile == BoardPile.Melt, item.Deferred));
    }

    // Listed (triage) rows, attributed by their NATURAL pile - that is the ruling
    // the row is waiting for even when a Contradicted tier has it drawn in Review.
    // Every one of them is retainer work, so every one of them is the BELL's -
    // EXCEPT the pull-and-vendor shapes on a night the pinch's vendor rider will
    // reach them first, which is a fast path to the same job at an earlier stop.
    //
    // The rows are asked about EITHER WAY now (07-25). Before, a rider-less night
    // dropped them from the queue entirely on the grounds that no stage would run
    // them - true then, false now: the bell is their door, and a row nobody asked
    // about is a row that cannot ride it. No row is ever doorless.
    var riderRuns = Plugin.Configuration.PinchVendorRider && !fit.SkipPinch;
    foreach (var row in standingRows)
    {
      var natural = BoardPiles.ForStanding(row.Item.Result);
      RoundStage? stage = natural switch
      {
        BoardPile.Reprice => RoundStage.BellRun,
        BoardPile.PullAndVendor or BoardPile.Review =>
          riderRuns ? RoundStage.Pinch : RoundStage.BellRun,
        _ => null,
      };
      if (stage is not RoundStage s || _conductor.Skipped.Contains(s)) continue;
      // ANY staged action is a ruling: the human touched the row, so it is no
      // longer an open question. Which verb he chose is his call, not the gate's.
      // InReview is TRUE for every listed candidate: a triage row IS a contest
      // against a standing call, and a contest wants eyes whatever its evidence
      // tier says - same predicate, same reason a Review verdict does (standing
      // is a ruling; the tier gate was a proxy for the contest, 08-02).
      // No listed row ever rides whole-pile: a standing listing has to be pulled
      // or repriced at the retainer one lane at a time, so the melt stage cannot
      // round one up the way it rounds a bag row.
      // The pinch-side lane_held rehome is the one listed shape that defers:
      // keeping the ask IS the act, so there is no ruling for it to be waiting
      // on and the launch must not refuse over it.
      candidates.Add((new GateRow(null, row, EffectiveStandingPile(row.Item)), s,
        _cache.ScoreStanding(row.Item, natural), _actions.ContainsKey(row.Item), true, false,
        natural == BoardPile.Defer));
    }

    return GatePlan.Queue(candidates);
  }

  /// <summary>
  /// WHAT THE ROUND LEFT (ruled ledger, stage 2a). Three sources, none of them a
  /// new count: the rows nobody ruled (the frame's own banked judgment queue - the
  /// same number the launch control refuses over, so a round cannot end saying
  /// "clean" about rows the next press will refuse to start over), the rows staged
  /// to a verb that never got handed off, and the rows in a stage the player
  /// deferred.
  ///
  /// <para>NOTHING IS COUNTED TWICE. The judgment queue already excludes deferred
  /// stages, and the staged rows are excluded when the BELL is the deferred stage -
  /// the bell is what drains <see cref="_actions"/>, so on a bell-deferred night
  /// those rows already sit inside the bell's own deferred count.</para>
  /// </summary>
  private WaitingTally WaitingWork(DeckState deck)
    => new(
      Unruled: deck.JudgmentStages.Count,
      Staged: deck.Skipped.Contains(RoundStage.BellRun) ? 0 : _actions.Count,
      Deferred: RoundSkips.Deferred(deck.Skipped, deck.CountOf));

  // ==========================================================================
  // THE DASHBOARD'S HALF (Rounds unit 5, ruled Q4; re-cut by Movement 3)
  //
  // The gil dashboard carries every READOUT the old judgment desk carried, plus the
  // standing asks, plus a door to the Round. It does not carry a verb: no pile
  // buttons, and since Movement 3 no launch either - reading the world must never
  // require starting an errand, and the reverse detour (dashboard first, then the
  // Round button again) was the same wall from the other side.
  //
  // Every surface below is drawn BY THIS CLASS and hosted by GilWindow, which is the
  // whole trick - the numbers come from the same cached refresh and the same DeckState
  // the wizard reads, so the dashboard and the Round can never quote different ones.
  // ==========================================================================

  /// <summary>
  /// Whether the dashboard has ever paid for a full refresh this session. This
  /// window's own Draw refreshes on open; the dashboard can be the FIRST surface to
  /// ask - for a deck, or (since Movement 3) for the standing asks - and either one
  /// computed off empty caches would report a world nobody has read.
  /// </summary>
  private bool _dashboardPrimed;

  /// <summary>
  /// The caches, for a caller that is not this window's own Draw. Paid once per
  /// session; every later frame reads what it left behind, exactly as the wizard does.
  /// </summary>
  private void PrimeForDashboard()
  {
    if (_dashboardPrimed) return;
    _dashboardPrimed = true;
    try { RefreshAll(); }
    catch (Exception ex) { Svc.Log.Error(ex, "[Board] First dashboard refresh failed"); }
  }

  /// <summary>
  /// The deck, for a caller that is not this window's own Draw. Primes the caches
  /// once, then derives per frame exactly as the wizard does - the derivation is
  /// in-memory (LINQ over the cached scans plus two game reads), which is what it has
  /// always been; the storage reads live in Refresh, on discrete events.
  /// </summary>
  private DeckState DashboardDeck()
  {
    EnsureRoundRestored();
    PrimeForDashboard();

    var deck = ComputeDeck(BuildStandingRows());
    _conductor.PublishDeck(deck);
    return deck;
  }
}
