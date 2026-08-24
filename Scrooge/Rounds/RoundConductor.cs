using Dalamud.Utility;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Scrooge.Board;
using Scrooge.Windows;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge.Rounds;

/// <summary>
/// WHAT THE ROUND ASKS OF THE BOARD IT IS WORKING. The conductor owns the errand - the
/// cursor, the latches, the flow, the persistence - and owns none of the composition:
/// which rows clear the confidence gate, what a staged verb means, how a Hawk run is
/// built out of routed gear. Those are the board's, and they arrive here as answers.
///
/// <para>The seam is what makes the conductor constructible in a test: everything it
/// cannot answer for itself is one small interface away, and none of it is ImGui.</para>
/// </summary>
internal interface IRoundBoard
{
  /// <summary>The whole errand as of right now - one derivation per frame, handed down.</summary>
  DeckState ComputeDeck();

  /// <summary>The bell's routed half, confidence gate and hinge commit applied.</summary>
  (List<RoutedItem> ListSet, List<RoutedItem> VendSet) RoutedBellSets();

  /// <summary>The melt set, one definition - the router's Desynth pile with the commit applied.</summary>
  List<RoutedItem> MeltPileRows();

  /// <summary>The turn-in set, one definition - the Churn pile through the gate, with the commit applied.</summary>
  List<RoutedItem> ChurnBellSet();

  /// <summary>The bell's non-gear half: the gate rows the router has no jurisdiction over, admitted.</summary>
  List<ListableItem> BellGateJoiners();

  /// <summary>
  /// Stages the bell's standing-listing leg - the deck's reprice and pull sets, plus
  /// whatever the human already staged - and hands the batch to its executor. True
  /// means a run is in flight and its completion is worth chaining on.
  /// </summary>
  bool StageStandingLeg(IReadOnlyList<InboxRow> repriceStage, IReadOnlyList<InboxRow> pullStage);

  /// <summary>Hands the bell's listing leg to the Hawk run.</summary>
  void FireBellRun(List<RoutedItem> listSet, List<RoutedItem> vendSet,
    List<ListableItem> gateJoiners);

  /// <summary>Hands the churn set to the GC turn-in. False = nothing with a resolvable seal value, no run fired.</summary>
  bool ExecuteChurn(List<RoutedItem> rows);

  /// <summary>Books the teaching signal a bulk confirm is - the player agreed with the router, en masse.</summary>
  void RecordRoutedSignal(RoutedItem item, RoutingExit playerExit);

  /// <summary>What the salvage window would select right now, through the commit.</summary>
  HashSet<(uint ItemId, bool IsHq)> MeltPileVariants();

  /// <summary>
  /// The Round is over. The walk dies here (Task 3): it is a set of answers to ONE
  /// hinge arrival, and one that outlived its errand would hand the next Round answers
  /// to questions the next board has not asked.
  /// </summary>
  void RoundTornDown();
}

/// <summary>
/// THE ROUND, AS A PROCESS (Rounds code-shine, batch 3-3a; Drift's ruling: <i>"rounds are
/// a process, the UI summarizes state"</i>). Fire, flow, halt, resume, teardown,
/// persistence - the whole state machine, off the window that used to host it.
///
/// <para>It is an Orchestrator in the contract sense rather than the filename one: the
/// lifecycle latch (<see cref="_stageInFlight"/>) holds the flow off a stage whose run
/// has not reported, the wedge cases clear their own latches rather than leaving one
/// nothing will ever clear, and every death is named out loud before the cursor
/// forgets whose run it was.</para>
///
/// <para>Nothing here paints and nothing here derives a board. The decisions are pure
/// (<see cref="FlowPlan"/>, <see cref="RoundPlan"/>, <see cref="GatePlan"/>,
/// <see cref="PortPlan"/>, <see cref="BellReach"/>, <see cref="WalletHalt"/>); the
/// composition is <see cref="IRoundBoard"/>'s; what is left here is the sequencing,
/// which is the thing that was never testable while it lived inside a Window.</para>
/// </summary>
internal sealed class RoundConductor
{
  private readonly IRoundBoard _board;
  private readonly LedgerCache _cache;

  // The one-button round's cursor (v0, zero intelligence - see RoundPlan).
  private readonly RoundPlan _round = new();

  /// <summary>Invariant B's four snapshots and the admission that reads them.</summary>
  internal HingeCommit Hinge { get; }

  internal RoundConductor(IRoundBoard board, LedgerCache cache)
  {
    _board = board;
    _cache = cache;
    Hinge = new HingeCommit(() => _round.Active && _round.IsDone(RoundStage.Triage));
    // The resume line's numbers ride the ledger's refresh clock, because the line is
    // drawn every frame a held round is on screen and neither number may cost a query
    // when it is.
    _cache.ListingsRefreshed += RefreshResumeFacts;
  }

  /// <summary>
  /// The cursor itself, for the surfaces that summarize it - what stage is offered, what
  /// is done, what halted, and whether there is a Round at all. Read-only in practice:
  /// every transition in the plugin comes through a method on this class, because a
  /// transition that skipped one would skip the persist that makes it survive a reload.
  /// </summary>
  internal RoundPlan Plan => _round;

  /// <summary>
  /// The last deck state computed - what there is to do, as of the most recent
  /// deck draw or run completion. The run log's stage rail reads this rather than
  /// recomputing, so the two surfaces cannot disagree about one errand.
  /// </summary>
  private DeckState? _deck;

  internal DeckState? Deck => _deck;

  /// <summary>The frame's one answer, published by whoever derived it. See <see cref="ComputeDeck"/>.</summary>
  internal void PublishDeck(DeckState deck) => _deck = deck;

  /// <summary>The persisted round has been rehydrated (or retired) - done once,
  /// lazily, on the first deck draw OR the first run completion, whichever the
  /// session reaches first (both need every orchestrator constructed, and both
  /// can be the moment the round state first matters).</summary>
  private bool _roundRestored;

  /// <summary>
  /// A parked round was RETIRED at the door rather than restored - too old to trust
  /// (see <see cref="RestoreRound"/>). The idle screen says so; nothing else reads it,
  /// and nothing about the round's state depends on it.
  ///
  /// <para>Cleared when the next round starts, because the news is "the errand you
  /// left is gone" and a fresh errand answers it. Session-only and never persisted: a
  /// retirement is told once, to the player it happened to.</para>
  /// </summary>
  internal bool RetiredStaleRound { get; private set; }

  /// <summary>
  /// THIS RUN'S DEFERRED STAGES (ruled ledger, stage 2a - see <see cref="RoundSkips"/>).
  /// The launch strip's four boxes write here and the deck's work answer reads it;
  /// nothing else in the round knows it exists.
  ///
  /// <para>NEVER STICKY, structurally: cleared when a round ends
  /// (<see cref="CancelRound"/>) and never persisted, so every run and every session
  /// opens with all four boxes checked. A skip that outlived the run it was meant
  /// for would be a stage quietly missing from every round afterwards - the shape of
  /// bug nobody finds by looking at a round.</para>
  /// </summary>
  private readonly HashSet<RoundStage> _skipped = [];

  internal IReadOnlySet<RoundStage> Skipped => _skipped;

  /// <summary>The pre-flight checklist's one press: defer this stage tonight, or take it back.</summary>
  internal void SetStageSkipped(RoundStage stage, bool skipped)
  {
    if (skipped) _skipped.Add(stage);
    else _skipped.Remove(stage);
  }

  /// <summary>
  /// The bell's SECOND LEG, waiting on its first (WALK unit 9). The bell stage is a
  /// chain - standing listings, then the listing run - and the two legs are separate
  /// executors that cannot share the screen, so the second is held here until the
  /// first reports done. Non-null means "the bell owns the wheel": the flow tick
  /// stands off, because a stage mid-chain has not finished being one stage.
  ///
  /// <para>Cleared on the first leg's DEATH as well as its completion - a listing
  /// run over the corpse of a reprice pass would be the round flowing past a halt,
  /// which is the one thing the halt exists to prevent. Not persisted: a reload
  /// mid-chain lands on a round whose bell is unmarked (stages mark at completion,
  /// SF2) with its in-flight latch reset, so the cursor simply re-offers the stage -
  /// the rows are all still there, and the bell can be pressed again.</para>
  ///
  /// <para>Returns whether the leg actually dispatched a run (a reprice-only bell
  /// declines the listing leg honestly), which is what the completion handler needs
  /// to know to mark the stage done when the chain settles without a second run.</para>
  /// </summary>
  private Func<bool>? _bellNext;

  /// <summary>
  /// THE STAGE IN FLIGHT (shake finding SF2, 2026-08-13): the round stage whose run
  /// has fired and whose completion has not yet landed. Stages used to be MARKED DONE
  /// AT FIRE TIME, and the mark was a lie for exactly as long as the run it promised:
  /// the 08-12 pinch died 3 items into 102 and the rail read "x pinch" while the
  /// round offered the hinge over an unpinched board. Contract B (the melt, 07-26;
  /// the hinge, unit 5) already knew the answer - a stage is done when its run
  /// COMPLETES - and this latch is that contract generalized to every stage.
  ///
  /// <para>While non-null the flow stands off (the stage owns the wheel, same rule
  /// as <see cref="_bellNext"/> and <see cref="_meltStaged"/>), which also closes
  /// the one-tick gap between a run's queue draining and its completion pumping -
  /// without it the flow could re-fire the stage into that gap. Cleared by the
  /// completion that marks the stage, by the halt that names its death, and by the
  /// round's own cancel. Not persisted, same residue class as the bell chain: a
  /// reload mid-run lands on an unmarked stage the held cursor re-offers, which is
  /// the truth - the run died with the reload.</para>
  /// </summary>
  private RoundStage? _stageInFlight;

  /// <summary>
  /// The hinge's once-per-arrival flash latch (ruled 08-15: stages that chain don't
  /// flash, stops do). Set when the flow first offers Triage, cleared when the offer
  /// moves off it and on round teardown - so a resumed round's hinge flashes again,
  /// and a new round never inherits a spent latch.
  /// </summary>
  private bool _hingeFlashed;

  /// <summary>
  /// THE MELT-STAGE LATCH (ruled 07-26, contract B - the destructive-act guard).
  /// The round opens the salvage window and STAGES the melt; Run Desynth stays a
  /// human press, because desynth is the one act in the whole errand with no undo
  /// (a listing gets pulled, a vendor sale has buyback - melted gear is gone).
  /// While true, the Desynth stage is fired-but-not-done: the flow holds (the
  /// stage owns the wheel, same rule as <see cref="_bellNext"/>) and the deck says
  /// "staged - press Run Desynth" instead of a checkmark that would be a lie. The
  /// stage marks done at the melt run's COMPLETION event, never at window-open.
  ///
  /// <para>Not persisted, same residue class as the bell chain: a reload
  /// mid-staged restores a round whose Desynth stage is neither done nor staged,
  /// so the flow re-fires it - the rider finds no coffers, the window re-opens,
  /// the melt is staged again. A re-derive, not a loss.</para>
  /// </summary>
  private bool _meltStaged;

  internal bool MeltStaged => _meltStaged;

  /// <summary>
  /// WHAT EACH STEP COMPLETED (unit 5), so the rail can say it. The number is the
  /// finishing run's own processed count, banked at its completion event - a stage's
  /// LIVE count is what it has left, and reading that for a finished stage would
  /// report "0 lanes banked" over a recon that banked eighty-seven.
  ///
  /// <para>Not persisted: it is a narration of this session's rail, and a restored
  /// round simply shows its done steps without tallies rather than inventing numbers
  /// for runs it never watched.</para>
  /// </summary>
  private readonly Dictionary<RoundStage, int> _stageTally = [];

  internal IReadOnlyDictionary<RoundStage, int> StageTally => _stageTally;

  /// <summary>
  /// A round ended (ran dry, not cancelled) and its banner has not been dismissed.
  /// The banner reads live tallies at draw time; this only says one is owed.
  /// Cleared by the banner's done button and by the next launch.
  /// </summary>
  private bool _roundEnded;

  internal bool RoundEnded => _roundEnded;

  /// <summary>
  /// The restored/held round's Look time, for the resume line. Read on the ledger's
  /// refresh schedule and cached beside the fresh-decision count, never per frame.
  /// </summary>
  private DateTimeOffset? _lookDoneAt;

  internal DateTimeOffset? LookDoneAt => _lookDoneAt;

  /// <summary>How many of this run's banked decisions are still fresh - the resume line's first count.</summary>
  private int _cachedDecisionsThisRun;

  internal int CachedDecisionsThisRun => _cachedDecisionsThisRun;

  /// <summary>
  /// The Look half's checkpoint has been stamped for this round. Kept in memory so the
  /// stamp is attempted once per transition-storm rather than on every persist; the
  /// column's own <c>look_done_at IS NULL</c> guard is what actually makes it
  /// one-shot, so losing this flag to a reload costs nothing.
  /// </summary>
  private bool _lookDoneStamped;

  /// <summary>
  /// The wallet reading at the last wallet auto-resume - the anti-spin operand (see
  /// <see cref="WalletHalt.ShouldReoffer"/>). Cleared with the round, because a fresh
  /// round's first auto-resume is never a repeat of anything.
  /// </summary>
  private uint? _lastWalletAutoResumeSeals;

  /// <summary>The last reach for the bell, on the monotonic clock. 0 = never this session.</summary>
  private long _lastBellReachMs;
  private long _lastBellDistanceLogMs;

  /// <summary>
  /// How long Teleport has been refusing. Observed every time the port decision is
  /// asked (the deck's draw and the run log's rail), which is per-frame while the
  /// turn-in is the live question and not at all otherwise - so a long gap between
  /// asks reads as "settled" on the first ask back, which is the honest answer: the
  /// player was not being shown anything to wait through in the meantime.
  /// </summary>
  private readonly TransientGrace _castGrace = new(GracePlan.AutoFireGraceMs);

  // ==========================================================================
  // What the executors read
  // ==========================================================================

  /// <summary>
  /// A human-pressed round is underway and flowing. Read by the executors that need
  /// to know whether a refusal is a LOUD answer to a button the player is looking at
  /// or a death that will halt an errand - the bell run's grace window is the first
  /// (see SpineGrace), and it is the only reason this is exposed at all.
  /// </summary>
  internal bool RoundActive => _round.Active && !_round.Halted;

  /// <summary>
  /// A Round exists but is HOLDING - halted over a corpse, paused on purpose, or
  /// restored fail-closed after a reload. Distinct from <see cref="RoundActive"/>
  /// because the executors' grace windows care whether the round is FLOWING, and the
  /// window routing cares whether one EXISTS. Two questions, two properties; folding
  /// them would give one of the two callers the wrong answer.
  /// </summary>
  internal bool RoundHeld => _round.Active && _round.Halted;

  /// <summary>
  /// There is a Round to be in - flowing or held. The screen keying's one operand
  /// (see <see cref="AccountantPlan.ScreenFor"/>): a banked round that drew the launch
  /// preview would offer a second errand over the top of the one it is holding, and
  /// would put the Resume line on a screen the player is not looking at.
  /// </summary>
  internal bool RoundLive => _round.Active;

  /// <summary>
  /// THE ROUND'S LINEAGE KEY, as recon's banked rows carry it: the DB-issued
  /// <c>round_runs.id</c> of the round underway, or 0 outside one (a hand-fired recon
  /// still banks its answers - the rows are just not claimed by any Round).
  ///
  /// <para>THE INTERIM ENDED IN UNIT 4, as unit 2's note promised. It used to answer
  /// the round's start instant in unix seconds, because a timestamp was the only
  /// identifier a round HAD; now the persisted state carries a real id
  /// (<see cref="RoundState.RunId"/>) and the banked transcript, the header row and
  /// these cache rows all join on it. The write site in the pricing pipeline did not
  /// change at all - it asked this property before and asks it now, which is exactly
  /// what having one reader for a fact buys.</para>
  ///
  /// <para>Cache rows on Drift's machine still carrying timestamp-era run ids (values
  /// around 1.7e9) are pre-campaign debris. Nothing reads run_id as anything but an
  /// equality key, so an old row simply never matches a live round and ages out of
  /// the freshness window on its own; there is nothing to migrate.</para>
  /// </summary>
  internal long RoundRunId => _round.RunId;

  // ==========================================================================
  // The sensors
  // ==========================================================================

  /// <summary>
  /// Whether the player is standing where a stage happens. The ONE definition,
  /// composed from the sensors that already exist - the deck's fire gate, the
  /// deck's cursor, and the run log's rail all ask it here.
  /// </summary>
  internal static bool LocationSatisfied(RoundStage s) => s switch
  {
    RoundStage.Pinch => AtBellRoster(),
    // "Anywhere" was a lie: the game refuses Desynthesize while occupied (open
    // bell, NPC talk). The 07-22 round lap died firing melt over the still-open
    // bell - gate it the same way StartRun now does.
    //
    // WALK unit 9 widens it by exactly one case: a player held ONLY by a retainer
    // window the advisor knows how to close is REACHABLE, because the fire closes
    // it on the way in (see OccupancyTransition). Without this the melt sat behind
    // a door the advisor was holding shut with its own hand - the round walks the
    // player to a bell, then refuses the next stage because he is at a bell.
    RoundStage.Desynth => OccupancyTransition.ReachableUnoccupied(),
    RoundStage.TurnIn => GcTurnInOrchestrator.AtExpertDelivery(),
    // The hinge's place is Anywhere, so its location precondition is always met -
    // and it must be stated here rather than left to the bell fallthrough below,
    // which would send a player on a walk to read his own decisions. (The note that
    // used to sit here calling this arm unreachable dated from unit 1, when Triage
    // answered no work; the hinge has hosted the board since unit 5 and this arm is
    // read on every flow tick of every round.)
    RoundStage.Triage => true,
    _ => AtRetainerBell(), // the pinch's siblings - and recon - happen at a summoned retainer
  };

  /// <summary>
  /// Is ANY executor working right now? The flow's one concession to the world:
  /// a stage whose predecessor is still in flight has not had a predecessor
  /// complete. One definition, read by the deck's draw and by the flow tick.
  /// </summary>
  internal static bool AnyRunBusy()
    => Plugin.PinchHost.PinchBusy || Plugin.PinchHost.HawkRunning
      || Plugin.PinchHost.ReconRunning
      || Plugin.StandingOrchestrator.IsRunning || Plugin.DesynthOrchestrator.IsRunning
      || Plugin.GcTurnIn.IsRunning || Plugin.CofferOrchestrator.IsRunning
      || OccupancyTransition.IsClearing;

  /// <summary>
  /// A stage's measured per-item pace, or 0 when it has never run. Never a
  /// borrowed number - an unmeasured stage says "no timing yet".
  /// </summary>
  internal static float RateFor(RoundStage stage)
    => Plugin.Configuration.AvgMsPerItemByStage.GetValueOrDefault(stage.ToString());

  /// <summary>
  /// THE RUN IN FLIGHT'S OWN REMAINING TIME (SF-P2, 2026-08-15), in milliseconds, or
  /// null when nothing is running or the run has no honest estimate yet.
  ///
  /// <para>This is the SAME value the wizard's progress line renders one pane over -
  /// <see cref="RunLifecycle.Eta"/>, the self-calibrating countdown - reached the same
  /// way the run pane reaches it, GC split included (the turn-in drives its
  /// own lifecycle because it counts seals, not gil). Shared rather than re-derived: the
  /// shake caught the rail and the progress line disagreeing about one run's remaining
  /// time, and a second calculation of a countdown is how that happens again.</para>
  ///
  /// <para>Null is a working answer, not a failure. Before its first item a run has no
  /// pace unless the adapter seeded one, and the rail then falls back to the banked
  /// estimate - exactly what it said before this fix.</para>
  /// </summary>
  internal static long? LiveRunEtaMs()
  {
    var run = Plugin.GcTurnIn.IsRunning ? Plugin.GcTurnIn.Run
      : Plugin.CurrentRun is { IsComplete: false } live ? live.Lifecycle
      : null;
    return run?.Eta(DateTime.UtcNow) is TimeSpan eta ? (long)eta.TotalMilliseconds : null;
  }

  private static unsafe bool AtBellRoster()
    => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out _);

  /// <summary>
  /// True anywhere Hawk can start from: the bell roster (NavigateAndStartHawkRun
  /// hops to a retainer's sell view itself) or already inside a sell view.
  /// </summary>
  internal static unsafe bool AtRetainerBell()
    => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out _)
    || GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _);

  /// <summary>
  /// THE STAGE THE ROUND IS ACTUALLY ON, for every display surface (SF3-D,
  /// 2026-08-13). The cursor re-derives per frame from live work counts, and a
  /// running stage can EXTINGUISH ITS OWN WORK as it runs - the pinch freshens the
  /// board retainer by retainer, so mid-run the cursor walk skipped the very stage
  /// that was walking, and every surface that borrowed the cursor then called the
  /// live pinch a recon (both shake nights: the rail read "recon (N stale)" and the
  /// wizard's progress line said "recon 3/79" over a pinch's own numbers). While
  /// the round has a run in flight - or contract B's staged melt, whose pile drains
  /// the same way - THAT stage is current, whatever the counts now say. The halt
  /// override stays with the callers: a frozen round is about its corpse.
  /// </summary>
  internal RoundStage? CurrentDisplayStage(RoundOffer? offer)
    => _stageInFlight ?? (_meltStaged ? RoundStage.Desynth : offer?.Stage);

  /// <summary>
  /// The round as the RUN LOG shows it: the stage rail, the halt (if any), and
  /// which stage is being offered. Null when no round is underway - the run log
  /// then draws an ordinary single-run log, exactly as it always did.
  ///
  /// <para>Reads the published deck state rather than recomputing: the deck and
  /// the rail are two views of one errand, and deriving it twice is how they
  /// would come to disagree. A round restored from config before any deck draw
  /// has no deck state yet and simply shows no rail until the first refresh.</para>
  /// </summary>
  internal (List<RailRow> Rows, RoundHalt? Halt, RoundStage? Current)? RoundRail()
  {
    if (!_round.Active || _deck is not DeckState deck) return null;

    // The SAME offer the deck makes, location rule and all - a rail that computed
    // its own cursor would point at a different stage than the button the player
    // is about to press, which is the two-surfaces-disagreeing bug in miniature.
    // Routed through CurrentDisplayStage for the same reason the wizard is (SF3-D):
    // a live run's stage outranks a cursor whose work counts the run is draining.
    var offer = _round.Offer(deck.HasWork, LocationSatisfied);
    var current = CurrentDisplayStage(offer);
    // The live operands the pure rail cannot reach for itself (SF-P2): the running
    // stage's own countdown, and the ask count an armed pinch would walk. Handed in
    // from here for the same reason the counts are - one derivation, two surfaces.
    var rows = StageRail.Build(deck.CountOf, deck.HasWork, _round.IsDone,
      current, _round.HaltStage, RateFor, LiveRunEtaMs(), deck.PinchAsks);
    return (rows, _round.CurrentHalt, current);
  }

  // ==========================================================================
  // The port (WALK unit 7): one decision, two surfaces
  // ==========================================================================

  /// <summary>
  /// The turn-in's port offer - THE ONE DEFINITION, asked by the deck's walk line
  /// and by the run log's stage rail. Unit 6 paid for computing "where am I" twice
  /// (a rail pointing at one stage while the button offered another); the port is
  /// the same shape of question, so it gets the same treatment: one method, two
  /// callers, no chance of a rail offering a port the deck is refusing.
  ///
  /// <para>Everything it decides lives in <see cref="PortPlan"/>. This only
  /// gathers the operands - the round's marks, the deck's pile, the location
  /// sensors, the GC sheet and the game's Teleport status - and never fires.</para>
  /// </summary>
  internal PortDecision TurnInPort()
  {
    if (!_round.Active || _round.Halted || _deck is not DeckState deck)
      return PortDecision.Nothing;

    // A run in flight owns the screen; nothing offers travel mid-run.
    if (Plugin.PinchHost.PinchBusy || Plugin.PinchHost.HawkRunning
        || Plugin.StandingOrchestrator.IsRunning || Plugin.DesynthOrchestrator.IsRunning
        || Plugin.GcTurnIn.IsRunning)
      return PortDecision.Nothing;

    if (_round.IsDone(RoundStage.TurnIn)) return PortDecision.Nothing;

    // "Behind us", not "done" - an empty bell is skipped silently and never
    // marked, and a night with no bell work still ends at the counter.
    var bellBehind = _round.IsDone(RoundStage.BellRun) || !deck.HasWork(RoundStage.BellRun);

    var dest = PortOrchestrator.Destination();
    var castable = PortOrchestrator.Castable(out var blocked);

    // The castability grace (2026-07-26). Castability is the most transient reading in
    // the whole round: it goes false for a beat every time a window closes, and the
    // turn-in's port is asked for the first time RIGHT as the bell run's retainer UI
    // is closing. Both codes Drift saw that evening (579, then 580 on a retry) were the
    // same blink wearing different numbers, which is why the discriminator is this
    // window rather than a list of "harmless" codes.
    var settled = _castGrace.Observe(castable, Environment.TickCount64) == GraceVerdict.Expired;

    return PortPlan.Decide(
      bellBehind,
      deck.CountOf(RoundStage.TurnIn),
      LocationSatisfied(RoundStage.TurnIn),
      dest?.Name,
      // The CITY, not the exact territory: Upper Decks counts as Limsa, and the
      // walk line beats selling a gil teleport to a plaza you can see (08-02).
      dest is { } d && PortOrchestrator.InSameCity(d.TerritoryId),
      castable,
      blocked,
      settled,
      // The standing block the grace can never wait out (2026-08-05): still in
      // the bell session the round itself walked the player into. Same sensor
      // the melt's fire trusts (unit 9) - closeable means the click clears it.
      OccupancyTransition.KnownOccupier(out _));
  }

  // ==========================================================================
  // Persistence and restore
  // ==========================================================================

  /// <summary>
  /// Writes the round's HELD PLACE to the durable config after a transition
  /// (start, fire, halt, resume, cancel, complete). Called only on discrete
  /// events - never per frame - so the 07-22 lost-cursor reload cannot recur.
  /// </summary>
  internal void PersistRound()
  {
    var state = _round.Export();
    // The commit rides with the cursor it belongs to (invariant B, the addendum). It
    // is written HERE rather than inside RoundPlan.Export because it is a fact about
    // the BOARD, not about the cursor - the pure plan has no business knowing what was
    // in the bags - and because every transition already comes through this method.
    Hinge.WriteTo(state);
    Plugin.Configuration.Round = state;
    Plugin.Configuration.Save();
    // The Look checkpoint does NOT ride this method any more (review ruling S3). A
    // transition is when a stage is MARKED, and Recon marked at fire time then - so
    // stamping here dated the Look to the moment recon started. It hangs off the
    // recon's own completion now (see OnRunCompleted), as do the stage marks
    // themselves (SF2).
  }

  /// <summary>
  /// Rehydrates the persisted round exactly once per session, lazily. Called from
  /// the deck's draw AND from the completion handler, because a run can finish
  /// before the Ledger was ever opened - and a halt written against a round that
  /// was never restored would be a halt on an empty cursor.
  ///
  /// <para>AND FROM THE ROUND'S DOOR (review ruling S15, 2026-08-12: "restore before
  /// routing"). <see cref="Plugin.OpenRoundDoor"/> used to route on
  /// <see cref="RoundLive"/>, and on the first invocation after a reload nothing had
  /// restored the held round yet - so the one command whose job is "take me to the
  /// work" sent the player to the dashboard and only the SECOND press found the
  /// Round. The routing is gone (Movement 3) and the restore is not: the screen
  /// keying asks the same cursor, and a cursor that has not read its state yet would
  /// draw the launch preview over a round that is holding.</para>
  /// </summary>
  internal void EnsureRoundRestored()
  {
    if (_roundRestored) return;
    _roundRestored = true;
    FoldLegacyRoundConfig();
    RestoreRound();
  }

  /// <summary>
  /// Moves the pre-Rounds config keys onto their new names, once, BEFORE anything
  /// reads them (the 08-10 naming sweep - see <see cref="LegacyRoundConfig"/>).
  /// Ordered ahead of <see cref="RestoreRound"/> deliberately: the restore reads
  /// both of the renamed values, and a fold that ran after it would retire an
  /// in-flight round against a ceiling it had not inherited yet.
  ///
  /// <para>Lazy, like the restore it precedes - a session that never opens the
  /// Ledger and never finishes a run has nothing to migrate, and the keys are
  /// still sitting there for the session that does.</para>
  /// </summary>
  private static void FoldLegacyRoundConfig()
  {
    var cfg = Plugin.Configuration;
    var (round, ceiling, logEnabled, migrated) = LegacyRoundConfig.Fold(
      cfg.Round, cfg.Sweep, cfg.RoundStalenessCeilingHours, cfg.SweepStalenessCeilingHours,
      cfg.EnableLedger, cfg.EnablePinchRunLog);
    if (!migrated) return;

    cfg.Round = round;
    cfg.RoundStalenessCeilingHours = ceiling;
    cfg.EnableLedger = logEnabled;
    cfg.Sweep = null;
    cfg.SweepStalenessCeilingHours = null;
    cfg.EnablePinchRunLog = null;
    cfg.Save();
  }

  /// <summary>
  /// Rehydrates a persisted in-progress round on first draw, unless it is too
  /// old to trust: a round past the staleness ceiling is retired (a half-done round
  /// from hours ago is history, not a round), never restored onto a stale world.
  ///
  /// <para><b>The retirement is audible at the DOOR, not in chat</b> (B1.4, ruled
  /// 08-21). This is the only place the ceiling ever bites - it runs once per session,
  /// on the first draw after a reload, and there is no mid-round check that can fire
  /// while the player is inside a round. That timing is exactly why chat was the wrong
  /// seat: the drop happens on a frame he is not reading, and the sentence is only
  /// news when he opens the Round expecting to find his errand. So the fact is RAISED
  /// here (<see cref="RetiredStaleRound"/>) and SAID on the idle screen.</para>
  /// </summary>
  private void RestoreRound()
  {
    var saved = Plugin.Configuration.Round;
    if (saved is null || !saved.Active)
    {
      // No round to restore - but the LAST round's book may still be banked (ruled
      // 08-15: the most recent transcript stays readable across a reload; two
      // rounds ago is nobody's question). Same lines, same Ledger, released state.
      RestoreEndedBook();
      return;
    }

    var ceiling = TimeSpan.FromHours(Math.Max(1, Plugin.Configuration.RoundStalenessCeilingHours));
    var startedAt = saved.StartedAtUnix > 0
      ? DateTimeOffset.FromUnixTimeSeconds(saved.StartedAtUnix)
      : (DateTimeOffset?)null;

    if (startedAt is null || RoundPlan.IsStale(startedAt.Value, DateTimeOffset.UtcNow, ceiling))
    {
      Plugin.Configuration.Round = null;
      Plugin.Configuration.Save();
      RetiredStaleRound = true;
      // The retired round's transcript is still the most recent book there is -
      // history is exactly what it just became, so it reads back like any other.
      RestoreEndedBook();
      return;
    }

    _round.Restore(saved);
    Hinge.Restore(saved);
    // FAIL-CLOSED (2026-07-26): the flow auto-fires, so a restore that lands live
    // is a reload acting without a press. The hold parks the cursor on the first
    // unfinished stage until the player says Resume - a real halt in the restored
    // state outranks it, and a fully-done restore ends on the flow's first tick.
    _round.HoldForRestore(
      "Restored after a reload - this errand was mid-flight when the plugin went down. " +
      "Press Resume to pick it back up, or cancel it.");
    // The Look stamp is the BANK's, not this session's: a round restored after its
    // checkpoint must not re-stamp one (the column's own guard refuses, but the flag
    // keeps us from asking on every transition for the rest of the round).
    _lookDoneStamped = LookAlreadyStamped(_round.RunId);
    PersistRound();
    // A restored round is still one errand - and since unit 4 its transcript comes
    // back with it. The banked chapters are rehydrated into the carry and the stages
    // still ahead go on writing into the same book, under the same run id.
    RehydrateTranscript();
    // The resume line's numbers, NOW rather than on the next refresh: a restored round
    // arrives holding, so the very first frame the deck draws is the one that owes the
    // sentence.
    RefreshResumeFacts();
    if (_round.Halted)
      Svc.Chat.Print("[Scrooge] A round from before the reload is holding in the Ledger - Resume when ready.");
  }

  /// <summary>
  /// Reads the restored round's banked transcript back into the run log. A round with
  /// no banked run, or a bank that will not answer, simply holds an empty book - which
  /// is what every restored round did before this unit.
  /// </summary>
  private void RehydrateTranscript()
  {
    var runId = _round.RunId;
    if (runId <= 0 || Plugin.RoundLogStore is not { } store)
    {
      Plugin.Ledger.HoldForRound(runId);
      return;
    }

    try
    {
      var banked = store.Read(runId);
      Plugin.Ledger.RehydrateForRound(runId, banked);
      if (banked.Count > 0)
        Svc.Log.Info($"[Round] Restored {banked.Count} transcript lines for run {runId}");
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Round] Couldn't read the banked transcript: {ex.Message}");
      Plugin.Ledger.HoldForRound(runId);
    }
  }

  /// <summary>
  /// Reads the most recent ENDED round's banked transcript back into the Ledger
  /// (ruled 08-15: "I might want to read the most recent one if I closed it by
  /// mistake"). Runs only from the no-active-round restore paths; ShowEndedBook
  /// refuses to clobber a session that already has a book or a run. A bank that
  /// will not answer costs exactly what the pre-fix behaviour was: no book.
  /// </summary>
  private static void RestoreEndedBook()
  {
    if (Plugin.RoundLogStore is not { } store) return;
    try
    {
      var runId = store.LatestBankedRunId();
      if (runId <= 0) return;
      var banked = store.Read(runId);
      if (banked.Count == 0) return;
      Plugin.Ledger.ShowEndedBook(banked);
      Svc.Log.Info($"[Round] The last round's book ({banked.Count} lines, run {runId}) is back on the shelf");
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Round] Couldn't read the last round's book: {ex.Message}");
    }
  }

  /// <summary>Has this run's Look checkpoint already been stamped? Storage down reads as no.</summary>
  private static bool LookAlreadyStamped(long runId)
  {
    if (runId <= 0) return false;
    try { return Plugin.RoundLogStore?.GetRun(runId)?.LookDoneAt is not null; }
    catch { return false; }
  }

  /// <summary>
  /// Re-reads the two numbers the resume line quotes. Called from the ledger's own
  /// refresh, on the same schedule as every other cached read on this surface - the
  /// line is drawn every frame a halted round is on screen and neither of these may
  /// cost a query when it is.
  /// </summary>
  private void RefreshResumeFacts()
  {
    _lookDoneAt = null;
    _cachedDecisionsThisRun = 0;

    var runId = _round.RunId;
    if (runId <= 0) return;

    try
    {
      if (Plugin.RoundLogStore?.GetRun(runId)?.LookDoneAt is long stamped)
        _lookDoneAt = DateTimeOffset.FromUnixTimeSeconds(stamped);

      var cutoff = ReconFreshness.Cutoff(
        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Plugin.Configuration.ReconFreshHours);
      _cachedDecisionsThisRun = GilStorage.CountFreshDecisionCacheForRun(runId, cutoff);
    }
    catch (Exception ex)
    {
      Svc.Log.Debug($"[Round] Resume facts unavailable: {ex.Message}");
    }
  }

  // ==========================================================================
  // The completion event (WALK unit 6): one handler, every executor
  // ==========================================================================

  /// <summary>
  /// A run ended - complete or dead. This is the single subscriber that closes
  /// three of the 07-24 lap's four faces:
  /// <list type="number">
  ///   <item><b>The re-read.</b> Every completion refreshes the bag routing, the
  ///     held flags, and the listings snapshot. This is why the header can no
  ///     longer sit on a 4.6h-stale board read after a pinch and propose a
  ///     RE-PINCH off the stale picture ("had to manually refresh the board a
  ///     fair bit"). It runs whether the window is open or shut, and whether the
  ///     run was fired from the deck, a pile button, or the Hawk window - the
  ///     event knows nothing about who pressed what.</item>
  ///   <item><b>The death.</b> An aborted stage HALTS the round at event time and
  ///     persists immediately, so the truth never lives only in a counter waiting
  ///     for a draw.</item>
  ///   <item><b>The cursor.</b> Nothing here advances it, still. The re-read above
  ///     is this handler's whole contribution to the flow: it makes the deck's
  ///     counts current, and the framework tick's <see cref="FlowTick"/> asks the
  ///     one advance rule what to do about them. Keeping the decision in one polled
  ///     rule is what lets ARRIVAL - which no event raises - fire a stage on the
  ///     same terms a completion does.</item>
  ///   <item><b>The chain.</b> The bell's second leg, if its first just landed.
  ///     That is a stage continuing, not a stage starting: the human's press
  ///     authorized the whole errand at that stop.</item>
  /// </list>
  /// Runs on the framework tick (RunFlow.Pump), never inside an ImGui frame, so
  /// refreshing these collections cannot land mid-iteration of a draw.
  /// </summary>
  internal void OnRunCompleted(RunCompletion completion)
  {
    EnsureRoundRestored();

    // THE COMPLETION CARRIES ITS FACTS (review ruling S1/S2, 2026-08-10). Everything
    // below reads completion.Facts and NOTHING here reads Plugin.CurrentRun: this
    // handler runs on the pump, a tick after the report, and every executor nulls its
    // run as it ends. Both of the reads that used to live here were therefore dead the
    // day they shipped - the tally never rendered and the melt's id never joined the
    // set. See RunFacts for the whole shape of the mistake.

    // THE STEP'S TALLY, banked at the one moment it is true (unit 5). The rail says
    // what a finished step DID, and the only honest source for that is the run that
    // just finished saying how much it processed - a stage's pile count is what it has
    // LEFT, and by now the run has drained it. Recorded before the re-read below,
    // which is what empties those piles.
    if (completion.Outcome == RunOutcome.Complete
        && FlowPlan.StageOf(completion.Kind) is RoundStage finished
        && completion.Facts.ItemsProcessed > 0)
      // THE BELL IS ONE STAGE WITH TWO LEGS, so its tally ADDS (2026-08-12, found while
      // landing S4). Standing and Bell both map to RoundStage.BellRun - the reprice/pull
      // leg reports, then the listing leg reports - so a plain assignment had the second
      // leg overwrite the first and the rail credited the stop with the listings only.
      // The legs are sequential and their row sets are disjoint (one retainer, one
      // window; the standing batch drains rows the Hawk run never sees), so the sum is
      // what the stop actually did. Every other stage keeps the assignment: a resumed
      // recon or a Re-Look re-walks rows it has already counted, and summing THOSE would
      // report a stage doing more work than there ever was.
      _stageTally[finished] = finished == RoundStage.BellRun
        ? _stageTally.GetValueOrDefault(finished) + completion.Facts.ItemsProcessed
        : completion.Facts.ItemsProcessed;

    // INVARIANT B'S ONE EXCEPTION (the addendum): this Round's own melt just ended, so
    // its run id joins the set the bell may admit yields from. Recorded on an ABORT as
    // well as a completion - a melt that died halfway still put real materials in the
    // bags, and holding those back would punish the player for the run's death with a
    // Round's delay on work he watched happen.
    if (_round.Active
        && completion.Kind == RunKind.Melt
        && completion.Facts.DesynthRunId is long meltRunId
        && Hinge.NoteMeltRun(meltRunId))
      PersistRound();

    // The re-read. Each source is independently guarded inside its own method,
    // and a throw here must not cost us the halt below.
    try
    {
      _cache.RefreshAll();
      // Recompute the deck off the fresh data, so the run log's rail is current
      // even when the Ledger is shut and the wizard never drew this session.
      _deck = _board.ComputeDeck();
    }
    catch (Exception ex)
    {
      Svc.Log.Error(ex, "[Flow] Ledger re-read failed after a run completion");
    }

    // THE LOOK IS DONE WHEN IT IS DONE, NOT WHEN WE START (review ruling S3). The
    // checkpoint used to ride PersistRound, which fires at the moment a stage is
    // MARKED - and Recon marked at fire time then, so a recon that halted at item 40
    // of 87 still stamped a Look time and invited Continue off reads that never
    // happened. (SF2 later moved the stage marks themselves to completion - S3's
    // lesson, one level up.)
    // It hangs here instead: only a Look-half run that actually COMPLETED can stamp,
    // and only after the re-read above, so the cursor's own "is the Look behind us"
    // rule is asked against the world this run just made. An aborted recon reaches
    // this line and fails the outcome test, which is the whole fix.
    if (completion.Outcome == RunOutcome.Complete
        && FlowPlan.StageOf(completion.Kind) is RoundStage ended
        && RoundResume.IsLookStage(ended))
      StampLookDone();

    // The death. FlowPlan owns the rule; this only applies its answer. "Fired by
    // the round" is the in-flight latch now (SF2 - stages mark at completion, so a
    // dead run's stage is never Done while the round's own run is live), plus the
    // staged melt - contract B moved its checkmark to completion, not its
    // ownership. Deliberately NOT IsDone: a stage that is Done finished its
    // round-fired run, so a death arriving over it can only be a stray manual run,
    // and halting there would freeze the round (and un-mark a finished stage) for
    // a run the round never fired.
    if (FlowPlan.Reaction(completion, _round.Active, _round.Halted,
          s => s == _stageInFlight || (s == RoundStage.Desynth && _meltStaged))
        == FlowReaction.Halt)
    {
      var dead = FlowPlan.StageOf(completion.Kind)!.Value;
      _round.Halt(FlowPlan.HaltFor(dead, completion.Reason));
      // THE FLASH MEANS "THE ERRAND STOPPED NEEDING NOTHING" (ruled 08-15: a stage
      // that hands off to the next stage automatically shouldn't flash). A halt is
      // exactly that stop - the round is waiting on the player now, wherever he is.
      Util.FlashWindow();
      // The bell's held second leg dies with its first: a listing run over the
      // corpse of a reprice pass would be the round flowing past its own halt.
      _bellNext = null;
      _meltStaged = false; // a staged melt dies with its stage - resume fires it fresh
      _stageInFlight = null; // the halt holds the corpse; resume re-offers the stage
      PersistRound();
      return;
    }

    // Contract B (ruled 07-26): the staged melt's press landed and its run just
    // finished - NOW the checkmark is true. The flow sees the cursor move on the
    // next tick, exactly as it does for every other completion.
    if (completion.Kind == RunKind.Melt
        && completion.Outcome == RunOutcome.Complete
        && _meltStaged)
    {
      _meltStaged = false;
      _round.MarkDone(RoundStage.Desynth);
      PersistRound();
      return;
    }

    // The bell's CHAIN (WALK unit 9). Its standing-listing leg just finished, so
    // the listing leg it was holding runs now - off the world this handler has
    // already re-read, which is why the chain hangs off the completion event rather
    // than off the executor: the Hawk run composes from fresh rows or from nothing.
    if (completion.Kind == RunKind.Standing
        && completion.Outcome == RunOutcome.Complete
        && _bellNext is { } next)
    {
      _bellNext = null;
      try
      {
        // False = the listing leg declined honestly (reprice-only bell) - the chain
        // just settled with no second run, so no completion is coming to mark the
        // stage. Mark it here (SF2): the reprice pass that just reported IS the
        // whole of what this bell had to do. A throw skips the mark and reports the
        // leg's death instead; the halt machinery holds the stage un-done.
        if (!next() && _stageInFlight == RoundStage.BellRun)
        {
          _stageInFlight = null;
          _round.MarkDone(RoundStage.BellRun);
          PersistRound();
        }
      }
      catch (Exception ex)
      {
        Svc.Log.Error(ex, "[Flow] The bell's listing leg threw");
        RunFlow.ReportDied(RunKind.Bell, "the listing leg couldn't start", run: null);
      }
      return;
    }

    // Any OTHER completion while a leg is held means the chain lost its turn (a
    // manual run, a cancel). Drop it rather than firing a listing run into a world
    // that has moved on - the bell can be pressed again, and its rows are all still
    // on the ledger. The in-flight latch drops with it (SF2): a chain that lost its
    // turn has no completion coming, and a latch nothing will clear is a wedge - the
    // unmarked stage goes back to the cursor instead.
    if (_bellNext != null && completion.Kind != RunKind.Coffer)
    {
      _bellNext = null;
      if (_stageInFlight == RoundStage.BellRun) _stageInFlight = null;
    }

    // THE COMPLETION MARK (shake finding SF2, 2026-08-13). A stage is done when its
    // run says Complete - not when it fired. The in-flight latch is the round's own
    // claim on the run (a manual run outside the round matches nothing and marks
    // nothing); the bell's standing leg is excluded inside MarksStageDone because
    // the chain above, not the leg, decides when that stage is over.
    if (FlowPlan.MarksStageDone(completion, _stageInFlight))
    {
      var done = FlowPlan.StageOf(completion.Kind)!.Value;
      _stageInFlight = null;
      _round.MarkDone(done);
      PersistRound();
    }

    // THE STANDALONE FLASH (ruled 08-15). Inside a round the executors stay quiet -
    // the flow chains the next stage itself, and the taskbar speaks only at the
    // stops (the hinge, a halt, the round's end). A run fired OUTSIDE a round has
    // no flow behind it: its completion IS the stop, so it flashes here. Coffer is
    // excluded because its rider hands off on its own (Handoff) and never flashed.
    if (!_round.Active && completion.Kind != RunKind.Coffer)
      Util.FlashWindow();
  }

  // ==========================================================================
  // The flow
  // ==========================================================================

  /// <summary>
  /// THE FLOW TICK (WALK unit 9). Asked once per framework tick, off any ImGui
  /// frame: does the round have a stage to fire right now?
  ///
  /// <para>This is the whole of "stages flow". Two triggers collapse into one
  /// question here - a run completing (the deck's counts are refreshed by
  /// <see cref="OnRunCompleted"/>, and the next tick sees the cursor moved) and the
  /// player ARRIVING (the location precondition flips true and the next tick sees
  /// it). Neither needs machinery of its own; a poll of one rule catches both, and
  /// cannot disagree with itself the way two paths could.</para>
  ///
  /// <para>THE FIRST LAW. <see cref="FlowPlan.Advance"/> answers Nothing unless a
  /// round is Active - and Active means the human pressed Start. Nothing in this
  /// method can fire on an idle deck, and the round completing or halting shuts it
  /// off again on the same tick. It runs even with the Ledger closed, which is the
  /// point: the walk is when you close the window.</para>
  /// </summary>
  internal void FlowTick()
  {
    if (!_round.Active) return;
    if (_round.Halted)
    {
      // The one halt that can see its own gap close (Fix 4b). Everything below this
      // line still refuses to run past a corpse - this only asks whether the corpse
      // got up, and only for the wallet.
      TryClearWalletHalt();
      return;
    }
    if (_bellNext != null) return; // a stage's own chain is mid-flight - it owns the wheel
    if (_meltStaged) return; // the melt is staged, the press is the player's - the stage owns the wheel
    // A fired stage's completion has not landed (SF2) - the stage owns the wheel.
    // This also closes the one-tick gap between a run's queue draining and its
    // completion pumping, in which the un-marked stage would otherwise re-fire.
    if (_stageInFlight != null) return;
    if (_deck is not DeckState deck) return; // nothing has computed the world yet

    var offer = _round.Offer(deck.HasWork, LocationSatisfied);

    // Nothing left to offer, nothing in flight: the errand is OVER, and it says so
    // in its own persisted state right now - never "when the banner gets clicked".
    // This is the line that makes a zombie round unwritable (see FlowPlan.ErrandOver).
    if (FlowPlan.ErrandOver(_round.Active, _round.Halted, AnyRunBusy(), offer is not null))
    {
      EndRound();
      return;
    }

    if (FlowPlan.Advance(_round.Active, _round.Halted, AnyRunBusy(),
          offer?.Stage, offer is { } o && LocationSatisfied(o.Stage)) != FlowAdvance.Fire)
    {
      // The stage is not ready to fire - and the commonest reason at a bell stage is
      // that the player is STANDING at the bell and nobody has clicked it. That is
      // the missing rung; take it.
      TryReachForBell(offer?.Stage);
      return;
    }

    var stage = offer!.Value.Stage;

    // THE HINGE IS NOT THE FLOW'S TO FIRE (unit 5). Its executor is Drift pressing
    // Continue, and FireRoundStage's Triage arm is already a no-op that returns before
    // the mark - but a tick that calls it sixty times a second is a loop of no-ops
    // pretending to be a flow. Say it here, once, where the rule lives: the flow
    // advances runs, and the hinge is not a run.
    if (stage == RoundStage.Triage)
    {
      // The round just arrived at the one stop that waits on a human (ruled 08-15:
      // stages that chain don't flash; the stops do). Once per arrival - the latch
      // clears the moment the offer moves off the hinge.
      //
      // ARRIVAL MEANS THE CURSOR, not the offer (live shake 08-16: the taskbar
      // flashed between pinch and recon). The cursor's own stage happens at a
      // retainer, and AtRetainerBell is "the roster addon exists" - so while the
      // game plays the withdraw transition after the pinch's final close, the
      // cursor reads unreachable for a few frames and the hinge becomes the
      // OPPORTUNISTIC offer. That is reachability, not arrival: the round is
      // still walking, and it has not stopped needing nothing.
      if (!offer.Value.Opportunistic && !_hingeFlashed) { _hingeFlashed = true; Util.FlashWindow(); }
      return;
    }
    _hingeFlashed = false;

    // The ONE thing the flow will not fire on its own: a pinch the fit check wants
    // a deliberate confirm for. That button says "Start anyway" and names two
    // clocks; auto-firing it would answer a question the deck asked the player.
    if (stage == RoundStage.Pinch && deck.Fit.RequiresConfirm) return;

    FireRoundStage(stage, deck);
  }

  /// <summary>
  /// Fires one round stage - exactly what the pile's own bulk button fires,
  /// nothing more. The stage goes IN FLIGHT here and MARKS DONE at its run's
  /// completion event (shake finding SF2, 2026-08-13 - contract B generalized):
  /// a fire-time mark said "x pinch" over a run that could still die at item 3
  /// of 102, and the round then offered the hinge over an unpinched board. The
  /// in-flight latch holds the flow off exactly as the fire-time mark's busy
  /// gate did; a mid-run abort halts through the ordinary machinery and leaves
  /// the stage unmarked, which is the truth.
  ///
  /// <para>THE ONE DOOR (WALK unit 9). The human's press and the flow's auto-fire
  /// both arrive here - there is no second path that starts a stage. That is what
  /// makes "auto-fire rides the same code path as the button" a fact about the
  /// code rather than a promise about it: anything true of a pressed stage (the
  /// in-flight latch, the completion mark, the halt machinery downstream) is true
  /// of a flowed one, because they are the same call.</para>
  ///
  /// <para>The zero-work settles are the one exception, and they mark HERE: a
  /// reprice-only bell whose listing leg has nothing to fire and a churn set with
  /// no resolvable seal value both end their stage at the fire site, honestly -
  /// there is no run coming whose completion could say it for them.</para>
  /// </summary>
  internal void FireRoundStage(RoundStage stage, DeckState deck)
  {
    switch (stage)
    {
      case RoundStage.Pinch:
        Plugin.PinchHost.StartPinchAllRetainers();
        break;
      case RoundStage.BellRun:
        // The bell composes its OWN rows at fire time, so nothing the deck counted
        // a frame ago is handed in stale. False = neither leg had a run to fire -
        // a complete bell, not a failed one; mark it now or nothing ever will.
        if (!FireBellStage(deck))
        {
          _round.MarkDone(stage);
          PersistRound();
          return;
        }
        break;
      case RoundStage.Desynth:
        // Contract B (ruled 07-26): the melt STAGES here and marks done at the
        // desynth run's completion event - Run Desynth is a human press, and a
        // checkmark before it would say melted about gear still in the bags.
        _meltStaged = true;
        FireMeltStage();
        return;
      case RoundStage.TurnIn:
        foreach (var item in deck.ChurnSet)
          _board.RecordRoutedSignal(item, item.Pile); // bulk confirm = mass agreement
        // False = nothing with a resolvable seal value, no run fired - the stage
        // is over at the fire site (the chat line already told the player why).
        if (!_board.ExecuteChurn(deck.ChurnSet))
        {
          _round.MarkDone(stage);
          PersistRound();
          return;
        }
        break;
      case RoundStage.Recon:
        // Recon composes its OWN work set at fire time, from a fresh bag scan and a
        // fresh cache read - the deck's count is a frame old and the freshness
        // filter moves with the clock. A mid-run death reports and halts through
        // the ordinary machinery (RunKind.Recon -> RoundStage.Recon).
        //
        // A Re-Look is READ here and spent nowhere near here (review ruling S6): the
        // latch belongs to the round, survives a reload with it, and is consumed by
        // the recon orchestrator at the one moment it actually starts reading boards.
        // Spending it at this line meant every refusal on the road to the sell view -
        // and there are four - burned a press that read nothing.
        Plugin.PinchHost.StartReconRun(bypassFreshness: _round.ReLookArmed);
        break;
      case RoundStage.Triage:
        // CONTRACT B, AND THE WHOLE OF IT. The hinge FIRES here - the flow reaches it,
        // the wizard puts the board on screen, the player is standing in front of his
        // own decisions - and it marks done nowhere but the Continue press
        // (<see cref="CommitHinge"/>). So this arm returns before the latch below - the
        // hinge has no run whose completion could clear it - and the interlock is
        // unchanged: RoundPlan.Next stops at the first unfinished stage with work, so a
        // fired-not-done hinge BLOCKS the cursor and no act stage can be offered
        // while it stands open. Delete the return and the round walks straight past
        // the human into the irreversible half.
        return;
    }
    // The stage owns the wheel until its completion lands - the completion handler
    // marks it done (Complete), or the halt names its death (Aborted). This latch is
    // what the fire-time mark used to be, minus the lie.
    _stageInFlight = stage;
  }

  /// <summary>
  /// THE BELL IS THE RETAINER ERRAND (Drift, 2026-07-25: <i>"'bell' should mean
  /// anything that needs a retainer"</i>). One stage, one visit chain, every job
  /// done per stop:
  /// <list type="number">
  ///   <item>the STANDING-LISTING leg - reprices and pull-and-vendors, through the
  ///     triage executor at each row's own retainer;</item>
  ///   <item>the LISTING leg - the Hawk run over the routed gear, the fresh melt
  ///     yields and everything else the gate would list.</item>
  /// </list>
  ///
  /// <para>The old Reprice stage is DELETED, not moved: it was a second door onto
  /// the same stop, and the 07-24 lap paid for that twice. The bell's question is
  /// no longer "what would the Hawk list?" but "what does each retainer need?".</para>
  ///
  /// <para>SEQUENTIAL, NOT FORKED. Both legs run their existing executors, untouched
  /// - the triage orchestrator's own reprice/pull paths and the Hawk run machinery.
  /// They cannot overlap (one retainer, one window), so the listing leg waits on the
  /// standing-listing leg's completion event rather than racing it: see
  /// <see cref="_bellNext"/>. Each leg still reports its own RunFlow completion and
  /// both answer to the BellRun stage, so either one dying halts the round with the
  /// executor's own words.</para>
  ///
  /// <para>Composing at fire time rather than at draw time is what lets the melt
  /// feed the bell: a set snapshotted before the melt ran would be missing exactly
  /// the yields the ruled order exists to catch.</para>
  /// </summary>
  private bool FireBellStage(DeckState deck)
  {
    // The standing-listing leg. Everything the human has staged, plus the two work
    // sets the deck counted - all of it retainer work, all of it this stop's.
    //
    // Drained WHOLE, not narrowed to one verb (which is what the retired Reprice
    // stage had to do): every triage verb is a retainer job, and the bell is the
    // retainer errand. A row the pinch's vendor rider already took is long gone
    // from these sets - the rider retires what it vendors.
    if (_board.StageStandingLeg(deck.RepriceStage, deck.PullStage))
    {
      _bellNext = FireBellListing;
      return true;
    }

    return FireBellListing();
  }

  /// <summary>
  /// The bell's listing leg: re-read the world (the standing-listing leg just
  /// changed it), recompose, one Hawk run. Called directly when there were no
  /// standing listings to work, or from the completion of the leg that did.
  /// Returns whether it DISPATCHED - a dispatched run always reports (done, died,
  /// or refusal), so a completion is coming. False is the honest decline, the
  /// reprice-only bell with nothing to list; the caller marks the stage done
  /// because no run will ever say it (SF2).
  /// </summary>
  private bool FireBellListing()
  {
    _cache.Refresh();
    _cache.RefreshListings();
    // The SAME sets the deck counted - four-part confidence gate and the hinge's
    // commit both - or the run would spend a different set than the wizard promised.
    // This used to be a second copy of the arithmetic living six hundred lines from
    // the first; invariant B made the copies a correctness problem rather than a
    // tidiness one, because a filter applied to one of them is a filter the other
    // quietly does not have.
    var (listSet, vendSet) = _board.RoutedBellSets();
    var joiners = _board.BellGateJoiners();

    // A bell whose whole errand was reprices is a COMPLETE bell, not a failed one.
    // FireBellRun refuses an empty set loudly (a confirm button that no-ops reads as
    // broken, and it was, twice) - but that refusal is about a button the player
    // pressed expecting listings. Here it would fire on every reprice-only night.
    if (listSet.Count == 0 && vendSet.Count == 0 && joiners.Count == 0) return false;

    _board.FireBellRun(listSet, vendSet, joiners);
    return true;
  }

  /// <summary>
  /// The melt stage, front to back: the COFFER RIDER cracks every Venture Coffer,
  /// then the salvage window opens with the router's melt pile selected.
  ///
  /// <para>The rider rides HERE as of 07-25, having ridden the bell since 07-24.
  /// Drift's reason for moving it forward was that coffer yields are overwhelmingly
  /// listable mats and dyes, so they must be in the bags before the bell composes
  /// its rows - and with the ruled order (melt immediately before the bell) the
  /// melt is the last stop that satisfies that. It is also the only stop that CAN
  /// host it: the rider needs an un-occupied player, the bell needs an open
  /// retainer window, and those two are the same flag with opposite signs.</para>
  ///
  /// <para>The occupancy clear wraps both: after the pinch the player is standing
  /// at the bell with the roster open, and the roster is a window the advisor knows
  /// how to close. That happens INSIDE this fire - after the press (or the flow's
  /// auto-fire within a pressed round), never speculatively.</para>
  /// </summary>
  private void FireMeltStage()
  {
    OccupancyTransition.ClearThenRun("melt",
      proceed: () => Plugin.CofferOrchestrator.OpenAllForRound(() =>
      {
        // The rider's products join the Round's exception BEFORE the re-read, so
        // the same frame that first sees the new items also sees them admitted.
        // Every way the rider ends banks its gains - the bags-full halt included.
        foreach (var (gainId, gainHq) in Plugin.CofferOrchestrator.LastRiderGains)
          Hinge.NoteCofferGain(gainId, gainHq);
        // The bags changed under the router - re-read before the pile is selected,
        // or the melt selects a pile computed before the coffers opened.
        try { _cache.Refresh(); }
        catch (Exception ex) { Svc.Log.Error(ex, "[Flow] Ledger re-read failed after the coffer rider"); }
        if (_board.MeltPileVariants().Count == 0)
        {
          // Coffers only - nothing to melt, so there is no press to owe. The
          // stage completes HERE, honestly: the rider ran, the melt was empty.
          _meltStaged = false;
          _round.MarkDone(RoundStage.Desynth);
          PersistRound();
          return;
        }
        Plugin.DesynthPreview.OpenSalvageWithPileSelected();
        // The press is now owed (ruled 07-26): reach the player at the desk he
        // actually sits at - same signal a finishing pinch sends.
        Util.FlashWindow();
      }),
      onRefused: message =>
      {
        // A refusal reports a death and the halt machinery names the gap; the
        // latch drops so the resumed round can fire the stage fresh.
        _meltStaged = false;
        Svc.Chat.PrintError($"[Scrooge] {message}");
        RunFlow.ReportDied(RunKind.Melt, message, run: null);
      });
  }

  // ==========================================================================
  // The presses
  // ==========================================================================

  /// <summary>
  /// THE HINGE'S ONE PRESS (contract B, ruled Q1). Continue is the human's mark: it
  /// calls <see cref="RoundPlan.MarkDone"/> for the hinge and nothing else does, ever,
  /// which is what makes "the act half never runs unasked" structural rather than
  /// procedural - the cursor stops at a fired-not-done stage by its own rules.
  ///
  /// <para>The snapshot is taken BEFORE the mark, deliberately - the mark is what makes
  /// the gate admission start holding, and a frame where it holds against a snapshot
  /// that does not exist yet would fail closed over the whole bell.</para>
  /// </summary>
  internal void CommitHinge()
  {
    var (listSet, vendSet) = _board.RoutedBellSets();
    Hinge.CommitTheHinge(
      _board.BellGateJoiners(),
      listSet.Concat(vendSet),
      _board.MeltPileRows(),
      _board.ChurnBellSet());
    // The hinge marks done HERE and only here. Everything downstream - the flow's
    // next tick, the walk narration, the halt machinery - is unchanged, because a
    // marked stage is a marked stage however it got marked.
    _round.MarkDone(RoundStage.Triage);
    PersistRound();
  }

  /// <summary>
  /// PAUSE HERE - the wizard's own verb, and structurally the same act as the
  /// fail-closed restore (<see cref="RoundPlan.HoldForRestore"/>): the Round holds on
  /// the step it is standing on and the ONE resume door is the way out.
  ///
  /// <para>It is a <see cref="RoundPlan.Halt"/> rather than a new state, and that is
  /// the point - a paused round is a round the flow refuses to advance, which is
  /// exactly what a halt already means. A second spelling of "do not fire" would be a
  /// second thing every fire path has to remember to ask.</para>
  ///
  /// <para>Halt un-marks its own stage; the step being paused is the CURSOR's stage,
  /// which by definition is not done, so nothing is lost. A run in flight blocks the
  /// press: pausing over a live executor would freeze the round while its stage kept
  /// working, and the halt would then be holding a gap that was never a gap.</para>
  /// </summary>
  internal void PauseRound()
  {
    if (AnyRunBusy() || !_round.Active || _round.Halted) return;
    if (_deck is not DeckState deck) return;
    if (_round.Offer(deck.HasWork, LocationSatisfied) is not { } offer) return;

    _round.Halt(new RoundHalt(offer.Stage,
      "Paused here - you held this Round on purpose. Everything it has banked is on "
      + "disk; press Resume when you're ready to pick it back up."));
    PersistRound();
  }

  /// <summary>
  /// Clears the round's halt and re-offers the halted stage. THE ONE DOOR - the deck's
  /// Resume button, the run log's Resume button and the wallet's self-clear all arrive
  /// here, so "resume" means exactly one thing and persists exactly once.
  /// </summary>
  internal void ResumeRound()
  {
    _round.Resume();
    PersistRound();
  }

  /// <summary>
  /// THE RE-LOOK (ruled 2026-08-10). Read the boards again before acting - and read
  /// ALL of them.
  ///
  /// <para><b>It bypasses the freshness filter, and that is the whole verb.</b> Recon's
  /// work set derives from "which decisions are stale" (see
  /// <see cref="ReconFreshness"/>), so a Re-Look pressed twenty minutes after the Look
  /// would find every row inside the window, walk zero items, and report a completed
  /// recon. The player asked for fresh reads and got a run that read nothing while
  /// telling him it had looked - which is worse than not offering the verb.</para>
  ///
  /// <para><b>It goes through the round's one door, not around it.</b> The latch un-marks
  /// Recon, clears the halt, and lets the ordinary cursor offer the stage - so the
  /// Re-Look inherits the walk narration, the busy gate, the grace window and the halt
  /// machinery instead of a second spelling of all of them. The latch also widens the
  /// deck's Recon count to the full listable set for as long as it is armed, because a
  /// stage whose count says 0 is a stage the cursor skips: the button and the run have
  /// to agree about how much work there is, or the press does nothing and the deck says
  /// nothing about why.</para>
  ///
  /// <para><b>IT RETURNS THE ROUND TO THE LOOK HALF, AND THE WAIST RE-ASSERTS</b>
  /// (Drift, ruled 2026-08-10, addendum 2). Pressed after the hinge committed, it also
  /// un-marks Triage and discards both commit snapshots. Before this it was half a
  /// re-look in the exact scenario the verb was built for: the player comes back from
  /// an AFK, the Look-age label prompts him, he re-reads every board - and then the act
  /// half spends the ruling he made against the OLD reads, on a snapshot taken before
  /// he asked for fresh ones. Everything that arrived in the meantime stayed invisible:
  /// held by invariant B, and not shown at the hinge either, because the hinge was
  /// closed.</para>
  ///
  /// <para>Re-opening fixes both ends at once. Recon runs, the cursor comes back to an
  /// un-marked hinge that has work, the board re-presents against reads minutes old,
  /// the late arrivals are finally VISIBLE as counts, and the new Continue takes new
  /// snapshots that include them. Nothing new had to be built for any of that - the
  /// cursor already stops at a fired-not-done stage, which is what makes the waist a
  /// structural fact rather than a rule somebody enforces.</para>
  ///
  /// <para>Pre-commit the behaviour is exactly what it was: Triage was never marked, so
  /// un-marking it is a no-op and there is no snapshot to discard.</para>
  /// </summary>
  internal void ReLook()
  {
    _round.ArmReLook();
    // THE LOOK-AGE LABEL GOES WITH IT (review ruling S19). Both the latch and the
    // banked column clear, so the hinge shows NO Look time until the fresh recon
    // completes and stamps one. Leaving the old time standing made the label quote a
    // read the player had just declared out of date - it errs safe on the arithmetic
    // and lies about the verb, which is the worse half.
    ClearLookDone();
    _round.Unmark(RoundStage.Recon);
    // The waist re-asserts. Both un-marks are Unmark rather than Halt: the stages are
    // going back onto the cursor, not dying, and a halt would freeze the Round over a
    // gap the player just asked to close.
    _round.Unmark(RoundStage.Triage);
    // The old ruling stops governing. To NULL, not to empty - see DiscardTheCommit: a
    // crash between here and the new Continue must fail closed rather than fall back
    // on a snapshot of the board the player has just asked to re-read.
    Hinge.DiscardTheCommit();
    _round.Resume();
    // The deck's counts are what the cursor reads, and the latch just changed one of
    // them. Recompute now rather than waiting for the next completion event, or the
    // flow tick asks a deck that has never heard of the Re-Look.
    try { _deck = _board.ComputeDeck(); }
    catch (Exception ex) { Svc.Log.Error(ex, "[Round] Deck recompute after Re-Look failed"); }
    PersistRound();
  }

  /// <summary>
  /// THE WALLET HALT CLEARS ITSELF (Fix 4b, 2026-07-26). Drift spent seals in front of a
  /// halted round and it sat there. <see cref="WalletHalt"/> owns the decision and the
  /// reasoning for why this halt and no other; this method feeds it the wallet and the
  /// waiting pile, and on a yes takes the ORDINARY resume path - the same
  /// <see cref="ResumeRound"/> the button calls, after which the flow re-offers the
  /// turn-in and its executor re-reads the delivery list at the counter exactly as the
  /// halt line always promised. No second resume, no replay.
  /// </summary>
  private void TryClearWalletHalt()
  {
    if (AnyRunBusy()) return;
    if (_deck is not DeckState deck) return;

    var seals = GameSafe.CompanySeals();
    var cheapest = deck.ChurnSet
      .Select(r => GcSeals.For(r.ItemId) ?? 0)
      .Where(s => s > 0)
      .DefaultIfEmpty(0)
      .Min();

    if (!WalletHalt.ShouldReoffer(_round.Active, _round.HaltStage, _round.CurrentHalt?.Message,
          seals?.Current, seals?.Max, cheapest, _lastWalletAutoResumeSeals))
      return;

    _lastWalletAutoResumeSeals = seals!.Value.Current;
    ResumeRound();
    Svc.Chat.Print("[Scrooge] The seal wallet has room again - resuming the turn-in.");
  }

  /// <summary>
  /// THE BELL REACH (2026-07-26). Drift, watching a round sit at a bell it had walked
  /// him to: <i>"I had also expected the round to retarget the retainer bell."</i>
  /// He was right, and the gap was structural - every bell stage's location gate reads
  /// "a bell ADDON is open", so the round's own definition of arriving required a
  /// click it would not make. The melt has opened the salvage window for two days;
  /// this is the same act at the other stop.
  ///
  /// <para><see cref="BellReach.ShouldReach"/> owns the whole gate and this method
  /// owns nothing but the sensors feeding it, so "never outside a pressed round, never
  /// over a busy run, never past a staged melt" is one readable predicate rather than
  /// a condition scattered down a method. The cooldown burns only on an attempt that
  /// actually queued - a reach refused by a busy task manager is not an attempt.</para>
  /// </summary>
  private void TryReachForBell(RoundStage? offered)
  {
    if (offered is not RoundStage stage) return;

    var now = Environment.TickCount64;
    // ONE sighting per attempt: the same measurement decides the reach and narrates
    // it. Reading the distance twice is how a receipt ends up quoting a number the
    // decision never saw.
    var sighting = GameSafe.NearestSummoningBell();
    var inRange = sighting is { InRange: true };
    if (!BellReach.ShouldReach(
          roundActive: _round.Active && !_round.Halted,
          stageHappensAtBell: RoundPlan.PlaceOf(stage) == RoundPlace.Bell,
          anyRunBusy: AnyRunBusy(),
          meltStaged: _meltStaged,
          unoccupied: !DesynthOrchestrator.PlayerOccupied(out _),
          bellAddonOpen: AtRetainerBell(),
          bellInRange: inRange,
          nowMs: now,
          lastReachMs: _lastBellReachMs))
    {
      // A bell we can SEE but stood off from is the only decline worth a number: it
      // is the interact range being exercised at its edge, which is the one thing
      // nobody has measured. Debug level, never the run log - and rate-limited
      // (strings pass, 08-02): this fires on ticks, and 1,651 copies of the same
      // sentence drowned the honest lines around it. One reading per 2s keeps
      // the calibration receipt without the flood.
      if (sighting is { } seen && !inRange && !AtRetainerBell()
          && now - _lastBellDistanceLogMs >= 2000)
      {
        _lastBellDistanceLogMs = now;
        Svc.Log.Debug($"[Bell] nearest bell measured {seen.Text} - outside its {seen.RangeText} reach, still walking.");
      }
      return;
    }

    if (!Plugin.PinchHost.ReachForBell()) return;

    _lastBellReachMs = now;
    // THE REACH IS A CALIBRATION RECEIPT (07-26). 4.5y is inherited, not measured -
    // the game does not publish the interact radius and vanilla FFXIV has no distance
    // display - but Scrooge computes the hitbox-adjusted number on every reach anyway.
    // Saying it out loud makes each live round a free reading against the seed, so the
    // constant can eventually justify itself with a distribution instead of a citation.
    var where = sighting?.Text ?? "unmeasured";
    Svc.Chat.Print($"[Scrooge] Reached for the retainer bell ({where}).");
    Plugin.CurrentRun?.AddRunEntry(RunEvent.Summary, $"reached for the bell ({where})");
  }

  // ==========================================================================
  // The Look checkpoint
  // ==========================================================================

  /// <summary>
  /// THE LATCH IS SPENT BY THE WORK (review ruling S6). Called by
  /// <see cref="ReconRunOrchestrator"/> at the one moment a recon pass has cleared
  /// every refusal it owns and is about to walk its first item.
  ///
  /// <para>It goes through the conductor rather than being set on the round directly so
  /// the spend is PERSISTED on the same tick it happens: the latch's whole job is to
  /// survive a reload, and a spend that lived only in memory would re-arm itself the
  /// next time the round rehydrated - re-running the whole bag on a Resume the player
  /// never asked it of.</para>
  ///
  /// <para>Idempotent and silent when nothing is armed: an ordinary recon calls this
  /// on every run, and a persist per recon start is not a cost worth a branch at the
  /// call site.</para>
  /// </summary>
  internal void ConsumeReLookLatch()
  {
    if (!_round.ReLookArmed) return;
    _round.ConsumeReLook();
    PersistRound();
  }

  /// <summary>
  /// THE CHECKPOINT STAMP. Called when a LOOK-HALF RUN COMPLETES, and fires the once
  /// the Look half stops having work.
  ///
  /// <para><b>Not from PersistRound any more</b> (review ruling S3, 2026-08-10): "the
  /// Look is done when it is done, not when we start". Every persisted transition used
  /// to ask, and Recon is marked done at FIRE time - so the stamp landed the instant
  /// recon started, and a run that then halted at item 40 of 87 still showed "Look done
  /// 20:02" over a Continue button. The caller is the completion handler now, gated on
  /// an outcome of Complete, so a halted Look leaves the checkpoint unwritten and the
  /// hinge says nothing rather than something false.</para>
  ///
  /// <para>Asked of the cursor's own rule rather than of the completion marks: a stage
  /// with nothing to do is skipped silently and never marked, so "both Look stages are
  /// marked done" would never be true on the commonest good night of all - a fresh
  /// board and a fresh cache. See <see cref="RoundResume.LookComplete"/>.</para>
  /// </summary>
  private void StampLookDone()
  {
    if (_lookDoneStamped || !_round.Active || _round.RunId <= 0) return;
    if (_deck is not DeckState deck) return;
    if (!RoundResume.LookComplete(_round.IsDone, deck.HasWork)) return;

    _lookDoneStamped = true;
    try { Plugin.RoundLogStore?.StampLookDone(_round.RunId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }
    catch (Exception ex) { Svc.Log.Debug($"[Round] Couldn't stamp the Look checkpoint: {ex.Message}"); }
    // The label reads the BANK, not this method's local sense of the time - so the
    // surfaces that draw it (the hinge, the wizard's verb bar) get it by re-reading
    // rather than by being told twice.
    RefreshResumeFacts();
  }

  /// <summary>
  /// UN-STAMPS THE CHECKPOINT for a Re-Look (review ruling S19) - the latch, the
  /// banked column and the cached label, which are one fact wearing three coats. The
  /// latch alone would not do it: the column's own one-shot guard would refuse the
  /// fresh stamp and the hinge would go on quoting the old read forever.
  /// </summary>
  private void ClearLookDone()
  {
    _lookDoneStamped = false;
    _lookDoneAt = null;
    if (_round.RunId <= 0) return;
    try { Plugin.RoundLogStore?.ClearLookDone(_round.RunId); }
    catch (Exception ex) { Svc.Log.Debug($"[Round] Couldn't clear the Look checkpoint: {ex.Message}"); }
  }

  // ==========================================================================
  // Beginning and end
  // ==========================================================================

  /// <summary>
  /// Starts the round and raises its FACE. The run log carries the stage rail now,
  /// so it is the surface that answers "where is this errand" - including between
  /// stages, which is exactly when the question gets asked (between stages is just
  /// walking, and the walk is when you look).
  ///
  /// <para>The skips are deliberately NOT cleared here: they were set by the player
  /// in the seconds before this press, and this is the run they were set for. They
  /// clear at the far end (<see cref="CancelRound"/>).</para>
  /// </summary>
  internal void StartRound()
  {
    // A ROUND DOES NOT LAUNCH OVER A LIVE RUN (SF3-N, ruled 2026-08-13: "both").
    // The 08-12 receipt: Make the Rounds pressed at 23:05:16 over a pinch hung on a
    // market-board read; the timeout cleared that queue one second later and the
    // new round's flow fired its own pinch into the same bell 21ms after that -
    // with fire-time marks, the rail then called the walk a recon. The refusal is
    // loud and names the way out; nothing here cancels anything on its own.
    if (AnyRunBusy())
    {
      Svc.Chat.PrintError(
        "[Scrooge] A run is still working - let it finish or cancel it, then Make the Rounds.");
      return;
    }

    var startedAt = DateTimeOffset.UtcNow;
    _round.Start(startedAt, BeginBankedRun(startedAt));
    _roundEnded = false; // the new errand owns the surface; the old banner is history
    RetiredStaleRound = false; // the retirement notice is answered by the fresh errand
    ResetRoundResidue();
    // The rail's per-step tallies belong to the Round that earned them. Cleared HERE and
    // not at the end, deliberately: a finished Round's rail keeps saying what it did
    // until the next Round supersedes it, and the next Round's bell now ADDS its two
    // legs - so a tally that outlived its errand would compound across rounds.
    _stageTally.Clear();
    // ONE errand, one transcript (2026-07-26). The round tells the log to hold here
    // rather than every orchestrator learning what a round is - the log asks nobody
    // and is told once, at the two moments a round begins and ends. Since unit 4 the
    // hold carries the run id, so the transcript banks as it goes.
    Plugin.Ledger.HoldForRound(_round.RunId);
    PersistRound();
    if (Plugin.Configuration.EnableLedger)
      Plugin.Ledger.IsOpen = true;
  }

  /// <summary>
  /// EVERYTHING ONE ROUND LEAVES BEHIND, cleared in one place. The two ends of a
  /// Round - the press that starts one and the teardown that ends one - both need the
  /// per-round block empty, and they used to spell it out separately: two lists of
  /// eight-ish assignments that drifted, so a start never cleared the hinge's flash
  /// latch and a teardown never cleared the resume line's two numbers. Neither gap was
  /// visible from either method, because neither method could see the other.
  ///
  /// <para>The asymmetries stay at their call sites and only the asymmetries:
  /// <see cref="_stageTally"/> clears at the START (a finished Round's rail keeps
  /// saying what it did until the next one supersedes it) and <see cref="_skipped"/>
  /// clears at the END (a deferral belongs to the run it was set for). Everything else
  /// here is residue by definition - it describes a Round, and after this call there
  /// is no Round it describes.</para>
  /// </summary>
  private void ResetRoundResidue()
  {
    _bellNext = null;
    _meltStaged = false;
    _stageInFlight = null;
    _hingeFlashed = false;
    _lookDoneStamped = false;
    // The last round's Look time is not this round's, and is not the idle screen's
    // either: a halt early in a new round would otherwise quote a checkpoint that
    // belongs to a round that is over.
    _lookDoneAt = null;
    _cachedDecisionsThisRun = 0;
    _lastWalletAutoResumeSeals = null;
    // THE WALK AND THE CONTESTS DIE WITH THE ROUND (Task 3). They are a session's worth
    // of pulls and verdicts against one hinge arrival, and none of it is a commit - a
    // walk that outlived its errand would hand the NEXT round a set of answers to
    // questions the next board has not asked. Deliberately not persisted, same reason.
    _board.RoundTornDown();
    // The commit dies with the Round it belongs to (invariant B, the addendum). A
    // snapshot that outlived its errand would hold the NEXT Round's bell to a board
    // read before the last one finished - the same drift, pointed the other way.
    Hinge.ClearForRound();
  }

  /// <summary>
  /// THE ROUND'S RUNS DIE WITH THE ROUND (SF3-N, ruled 2026-08-13: "both"). On
  /// 08-12 an abandoned round's pinch kept walking the boards for minutes - Abandon
  /// retired the cursor and left the task queue working, and the NEXT round then
  /// launched over the hung corpse and auto-fired into the same bell the moment the
  /// old queue died. Every abort goes through the executor's own teardown, so each
  /// death is reported in its own words; the completions pump onto a round that is
  /// no longer Active, so nothing halts (FlowPlan.Reaction gates on roundActive).
  ///
  /// <para>Scoped to the ROUND'S OWN CLAIM - the in-flight stage, plus the staged
  /// melt and its coffer rider (contract B has no in-flight latch). A manual run
  /// the player started himself is not the round's to kill, so it walks on.</para>
  /// </summary>
  private void AbortRoundRuns(string reason)
  {
    if (_meltStaged)
    {
      if (Plugin.CofferOrchestrator.IsRunning) Plugin.CofferOrchestrator.Abort(reason);
      if (Plugin.DesynthOrchestrator.IsRunning) Plugin.DesynthOrchestrator.Abort();
    }
    switch (_stageInFlight)
    {
      case RoundStage.Pinch:
        if (Plugin.PinchHost.PinchBusy) Plugin.PinchHost.CancelPinchRun(reason);
        break;
      case RoundStage.Recon:
        if (Plugin.PinchHost.ReconRunning) Plugin.PinchHost.CancelReconRun(reason);
        break;
      case RoundStage.BellRun:
        // The bell's two legs, either of which can be the live one. The held
        // second leg itself is dropped by the caller (_bellNext = null).
        if (Plugin.StandingOrchestrator.IsRunning) Plugin.StandingOrchestrator.Abort();
        if (Plugin.PinchHost.HawkRunning) Plugin.PinchHost.CancelHawkRun();
        break;
      case RoundStage.TurnIn:
        if (Plugin.GcTurnIn.IsRunning) Plugin.GcTurnIn.Abort();
        break;
    }
  }

  /// <summary>
  /// Ends the round - the cancel buttons and the "done" button, all three of them.
  /// One method because ending a round now has to disarm the FLOW as well as clear
  /// the cursor: an un-cancelled chain leg would be a stage still owed to a round
  /// that no longer exists. (RoundPlan.Active going false is what actually stops
  /// the flow - see FlowPlan.Advance - so this is belt and braces on the leg.)
  ///
  /// <para>THE BOXES RE-CHECK THEMSELVES HERE (stage 2a). Every path out of a round
  /// runs through this method, so "a skip never outlives the run it was meant for"
  /// is structural rather than a rule somebody has to remember at four call sites.</para>
  /// </summary>
  internal void CancelRound()
  {
    // Before anything forgets whose runs these were (SF3-N): the in-flight latch
    // and the melt stage are about to be cleared, and they are the round's claim.
    AbortRoundRuns("the round was abandoned");
    // The header's ending is stamped BEFORE the cursor forgets which run it was:
    // "how long did the entire run take" is the reason round_runs exists, and an
    // un-ended row is a round the table thinks is still going.
    EndBankedRun(_round.RunId);
    BankLastRoundTally();
    _round.Cancel();
    ResetRoundResidue();
    // THE BOXES RE-CHECK THEMSELVES AT THE FAR END ONLY. A skip is set in the seconds
    // BEFORE a press and is meant for the run it was set for, so the start must not
    // clear it and the ending must.
    _skipped.Clear();
    // The errand is over - the log goes back to a book per run. The BANKED transcript
    // is deliberately left standing: it is the last round's record until the next
    // round's start supersedes it (see BeginBankedRun), and a Ledger that wiped it on
    // the way out would leave the player with nothing to read about the round he just
    // finished.
    Plugin.Ledger.ReleaseRoundHold();
    PersistRound();
  }

  /// <summary>
  /// The errand finished on its own: same teardown as a cancel - Active=false is
  /// PERSISTED here, which is the whole zombie fix - plus the ended flag, so the
  /// completion banner outlives the round it reports on. The flow calls this the
  /// tick the cursor runs dry (FlowPlan.ErrandOver); no click involved.
  /// </summary>
  private void EndRound()
  {
    CancelRound();
    _roundEnded = true;
    // The errand ran dry on its own - the one completion the whole round was for.
    // Flash here, not in CancelRound: a player pressing Cancel is at the keyboard.
    Util.FlashWindow();
  }

  /// <summary>
  /// The report's one exit. Ended rounds are already inactive - the button only
  /// dismisses the report. (Active is still reachable here for one frame between the
  /// last mark and the flow's errand-over tick; the cancel keeps that frame honest.)
  /// </summary>
  internal void DismissReport()
  {
    _roundEnded = false;
    if (_round.Active)
      CancelRound();
  }

  /// <summary>
  /// WHAT THE ROUND DID, BANKED AT THE ONE MOMENT IT IS TRUE (SF-P1, 2026-08-15) - the
  /// idle screen's tally line, written on the single teardown every path out of a Round
  /// already runs through.
  ///
  /// <para>Nothing is counted here. <see cref="_stageTally"/> is the rail's own
  /// dictionary - each entry a finished run's reported <c>ItemsProcessed</c>, banked at
  /// its completion event - and this copies it into the config with the ending's
  /// timestamp on it. The dictionary itself survives until the NEXT round's start
  /// clears it; the copy is what survives a reload.</para>
  ///
  /// <para>Guarded on an ACTIVE round, so a teardown with no errand behind it - a stale
  /// restore being retired, a cancel that raced the flow's own ending - cannot overwrite
  /// a real round's tally with an empty one. A round that genuinely processed nothing
  /// still banks: its line is the honest "Last round ended 20:14" with nothing after it.</para>
  /// </summary>
  private void BankLastRoundTally()
  {
    if (!_round.Active) return;
    Plugin.Configuration.LastRound =
      LastRoundTally.From(_stageTally, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    // Persisted by the PersistRound at the end of the same teardown - one config save
    // for the whole ending, rather than two writes of one fact's worth of state.
  }

  /// <summary>
  /// Opens a Round's banked run and RETIRES whatever came before it - the whole of
  /// the abandon path's structural half.
  ///
  /// <para>The explicit Abandon verb (the deck's cancel) stamps an ending; this
  /// catches everything that never got to. A round killed by a crash, retired as
  /// stale, or simply left running when the plugin went down has a header row nobody
  /// will ever close and transcript lines nobody can name - and the V36 ruling is
  /// that there are no immortal tenants. So a new round's start is the eviction: every
  /// other run's lines are deleted and every open header is stamped ended, keeping the
  /// table to one round's transcript no matter how many rounds died badly.</para>
  ///
  /// <para>Returns 0 when storage is unavailable, which is a working round with no
  /// banked run - the cursor and the flow do not know the difference.</para>
  /// </summary>
  private static long BeginBankedRun(DateTimeOffset startedAt)
  {
    var store = Plugin.RoundLogStore;
    if (store == null) return 0;

    try
    {
      var runId = store.StartRun(startedAt.ToUnixTimeSeconds());
      store.RetireAllExcept(runId, startedAt.ToUnixTimeSeconds());
      return runId;
    }
    catch (Exception ex)
    {
      // A round that cannot be banked is still a round. It runs with an in-memory
      // transcript exactly as every round did before this unit.
      Svc.Log.Warning($"[Round] Couldn't open a banked run - this round won't survive a reload: {ex.Message}");
      return 0;
    }
  }

  /// <summary>Stamps a banked run's ending. Quiet on failure - the next start evicts it anyway.</summary>
  private static void EndBankedRun(long runId)
  {
    if (runId <= 0) return;
    try { Plugin.RoundLogStore?.EndRun(runId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }
    catch (Exception ex) { Svc.Log.Debug($"[Round] Couldn't stamp the run's ending: {ex.Message}"); }
  }
}
