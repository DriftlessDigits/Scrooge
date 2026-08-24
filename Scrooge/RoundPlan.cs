using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>Where a round stage physically happens - no button teleports the player.</summary>
internal enum RoundPlace
{
  Bell,
  Anywhere,
  ExpertDelivery,
}

/// <summary>
/// The full round in workflow order (Drift's endgame sentence, 2026-07-19).
/// PUBLIC because the persisted round state (<see cref="RoundState"/>) carries
/// these values through the config JSON - a public property cannot expose an
/// internal enum.
///
/// <para>THE VALUES ARE EXPLICIT AND FROZEN. The 07-25 rebuild deleted the
/// Reprice stage (the bell absorbed it - see <see cref="RoundPlan.Order"/>), and
/// the persisted Done list stores these as numbers. Renumbering the survivors
/// would have silently re-read a saved "Desynth" as "TurnIn" on the next reload,
/// so 2 stays a retired hole forever. A restored round carrying the old value
/// deserializes harmlessly and is dropped on restore (see
/// <see cref="RoundPlan.Restore"/>): the reprice work re-derives into the bell.</para>
///
/// <para>THE CONTRACT THE FREEZE ACTUALLY BUYS, MEASURED 2026-08-10. The Rounds
/// rename asked whether renaming any of this was safe, and the answer was proven
/// against the live config rather than assumed: Dalamud writes the plugin config
/// through Newtonsoft with TypeNameHandling.Objects, which stores enums as
/// NUMBERS ("UndercutMode": 4 on disk) and class identities as strings
/// ("$type": "Scrooge.RoundState, Scrooge"). So the two halves of a rename carry
/// opposite risk, and this comment is the receipt for both:
/// <list type="bullet">
///   <item>Renaming a MEMBER of this enum is free - the number is what was saved,
///     and a renamed member re-reads its own saved value unchanged. That is what
///     lets Rounds re-word the stage list at all.</item>
///   <item>Renaming the NUMBERS is still fatal, for the reason above.</item>
///   <item>Renaming a persisted CLASS is fatal in a way nothing here warned about
///     until 08-10: the old $type no longer resolves and Newtonsoft throws while
///     reading the WHOLE config, not just that property. See
///     <see cref="LegacyRoundConfig"/> for the door that keeps out.</item>
/// </list></para>
///
/// <para>The Rounds stages (5 and 6) take the next free numbers rather than the
/// retired 2, for the same reason 2 was retired: a persisted round from before
/// this build carries 2 meaning "Reprice", and handing that number to Recon would
/// resurrect a dead mark wearing a live stage's name.</para>
/// </summary>
public enum RoundStage
{
  /// <summary>Read the board and reprice standing listings (the pinch).</summary>
  Pinch = 0,
  /// <summary>Everything that needs a retainer: list, reprice, pull-and-vendor.</summary>
  BellRun = 1,
  // 2 = the retired Reprice stage. Never reuse it - persisted rounds carry it.
  /// <summary>Melt the skillup/yield pile at the desynthesis window.</summary>
  Desynth = 3,
  /// <summary>Churn the rest to seals at the GC's Expert Delivery.</summary>
  TurnIn = 4,
  /// <summary>
  /// The hawk's front half, read-bank-cancel: walk the listable bag at a bell,
  /// run the pricing spine on a real board, bank the decision, and CANCEL the
  /// panel instead of posting. Recon buys the ground truth the act half spends.
  /// Executor since unit 2 (recon-run): <see cref="ReconRunOrchestrator"/>, whose
  /// work set derives from <see cref="ReconFreshness"/> rather than being chosen.
  /// </summary>
  Recon = 5,
  /// <summary>
  /// THE HUMAN HINGE - the one stage whose executor is Drift. It presents the
  /// board against recon's fresh decisions and marks done only when he presses
  /// Continue (see <see cref="RoundPlan.MarkDone"/> and the contract-B note on
  /// <see cref="RoundPlan.Order"/>). Its presenter is the Accountant's triage step
  /// (unit 5), which is also the board's only host.
  ///
  /// <para>THE NAME COLLISION IS GONE (unit 5). Until this unit <c>RunKind.Triage</c>
  /// was an unrelated meaning - the standing-listing EXECUTOR that reprices, pulls
  /// and vendors, and which maps to <see cref="BellRun"/>, not here. That family
  /// (StandingOrchestrator, StandingAction, StandingMemory, EffectiveStandingPile) was
  /// re-seated onto the rows it actually works - the ones already standing on the
  /// market - so Triage now means exactly one thing in this codebase: this stage.</para>
  /// </summary>
  Triage = 6,
}

/// <summary>
/// The named gap a HALT holds over a dead stage (spec 2026-07-23,
/// "Failure semantics: HALT-NAME-RESUME"). A mid-flow death does not silently
/// un-mark and roll on; it stops the round and names what died so the deck can
/// say it out loud. The message is composed at halt time and persisted verbatim
/// - the two factories are the two spec-sanctioned ways to phrase it.
/// </summary>
public sealed record RoundHalt(RoundStage Stage, string Message)
{
  /// <summary>
  /// The halt named PLAINLY - for a death that is not a spine facet (a server
  /// timeout, a watchdog kill): what died, and what would clear it. Progress is
  /// folded into <paramref name="reason"/> when the caller has it ("aborted at
  /// 6/13").
  /// </summary>
  internal static RoundHalt Plainly(RoundStage stage, string what, string reason, string wouldClear)
    => new(stage, $"{what} halted - {reason}. {wouldClear}");

  /// <summary>
  /// The halt named in the SPINE's vocabulary - for a death whose reason maps to
  /// a declared expectation (occupancy, view, place). Borrows the evaluation's
  /// "expected X, but Y" message shape verbatim so a halt reads exactly like the
  /// pre-fire refusal that would have named the same gap.
  /// </summary>
  internal static RoundHalt FromSpine(RoundStage stage, SpineEvaluation eval)
    => new(stage, eval.Message);
}

/// <summary>
/// The stage the deck puts under the player's finger, and whether it got there
/// OPPORTUNISTICALLY - because the player is already standing where that stage
/// happens, rather than because it is the strict next link in the chain. The flag
/// is presentation only: an opportunistic offer is still just an offer, and the
/// stage fires on the human's press of its own button like every other stage.
/// </summary>
internal readonly record struct RoundOffer(RoundStage Stage, bool Opportunistic);

/// <summary>
/// The serialization-friendly snapshot of a round's HELD PLACE (spec: what
/// survives a reload is exactly the held place - stage cursor, completion marks,
/// halted-or-not + the named gap, and the start timestamp for the staleness
/// guard). Pure data with public get/set + a parameterless ctor so the config
/// JSON serializer round-trips it; <see cref="RoundPlan.Export"/> /
/// <see cref="RoundPlan.Restore"/> are the only things that build or consume it.
/// </summary>
public sealed class RoundState
{
  /// <summary>A round was underway when this was written.</summary>
  public bool Active { get; set; }

  /// <summary>Stages marked done - stamped at the stage's run COMPLETION (SF2, 2026-08-13; fire-time marks lied for the length of the run).</summary>
  public List<RoundStage> Done { get; set; } = new();

  /// <summary>The halted stage, or null if the round was flowing normally.</summary>
  public RoundStage? HaltStage { get; set; }

  /// <summary>The named gap over the halted stage (verbatim halt message).</summary>
  public string? HaltMessage { get; set; }

  /// <summary>When the round started, unix seconds. 0 = unknown (retire on restore).</summary>
  public long StartedAtUnix { get; set; }

  /// <summary>
  /// THE ROUND'S IDENTITY (Rounds unit 4): the <c>round_runs.id</c> the DB issued when
  /// this round started. 0 = no banked run - a round from before this build, a round
  /// whose start could not reach storage, or no round at all.
  ///
  /// <para>Adding a PROPERTY to this class is safe where renaming the class would be
  /// fatal (see the receipts on <see cref="RoundStage"/>): the persisted $type still
  /// resolves, a config written without this key simply reads 0, and a config written
  /// with it reads back into an older build as an unmapped property. It joins here
  /// rather than living beside the state because the cursor, the banked transcript
  /// and the decision-cache rows have to agree about WHICH round they belong to, and
  /// a second home for that fact is a second thing that can be stale.</para>
  /// </summary>
  public long RunId { get; set; }

  /// <summary>
  /// WHAT THE HUMAN RULED AGAINST (invariant B, the unit-5 addendum): the gate-joiner
  /// variants in the bags at the hinge's Continue press, encoded one per number (see
  /// <see cref="BellCommit.Encode"/>). Empty means no snapshot - either the hinge has
  /// not committed yet, or it committed under a build that did not take one.
  ///
  /// <para><b>It is persisted, and that is the whole call.</b> The alternative - let a
  /// reload mid-act re-snapshot at Resume - restores the exact drift this closes, at
  /// the one moment nobody would notice: everything that landed in the bags between
  /// the hinge and the reload would be re-admitted as though the human had ruled on
  /// it. The reload is precisely what the persisted cursor exists to survive, so the
  /// commit has to survive it too. Adding a property is safe where renaming the class
  /// is fatal (see the receipts on <see cref="RoundStage"/>): a config written without
  /// this key reads an empty list, which the admission rule treats as fail-closed.</para>
  /// </summary>
  public List<long> CommittedGateRows { get; set; } = new();

  /// <summary>
  /// The ROUTED half of the same commit (addendum 2): the bag-gear variants the
  /// confidence gate had cleared at the press. Two lists rather than one, because they
  /// are two scans with two precedence rules and a merged list could not tell a gate
  /// row that vanished from a routed row that did.
  ///
  /// <para><b>Empty and absent are one answer here, provably.</b> Both decode to a null
  /// snapshot, which the admission rule reads as fail-closed - and a genuinely empty
  /// commit admits exactly the same set as a lost one (nothing, modulo this Round's
  /// yields, which are asked first). So the encoding does not need a presence flag to
  /// stay honest; it needs one only if the two cases ever come to differ, and the day
  /// they do is the day this comment is wrong rather than the day the data is.</para>
  /// </summary>
  public List<long> CommittedRoutedRows { get; set; } = new();

  /// <summary>
  /// The MELT half of the same commit (review ruling S4, 2026-08-12): the variants the
  /// router had routed to Desynth when the human pressed Continue. The melt executor
  /// selects a pile in the salvage window off a LIVE read, so before this a piece of
  /// gear that reached the bags between the press and the melt was selected and
  /// destroyed - the harshest verb in the Round, off a press that was about other gear.
  ///
  /// <para>Same encoding, same empty-is-null collapse, same fail-closed reading as the
  /// two halves above: one doctrine, four verbs, no per-verb spelling of it.</para>
  /// </summary>
  public List<long> CommittedMeltRows { get; set; } = new();

  /// <summary>
  /// The TURN-IN half of the same commit (review ruling S4). The variants routed to the
  /// GC counter at the press. Handed to Expert Delivery the item is gone exactly as a
  /// melted one is, and the turn-in stage read its set live for the same reason the
  /// melt did - nobody had asked the question of it yet.
  /// </summary>
  public List<long> CommittedChurnRows { get; set; } = new();

  /// <summary>
  /// The <c>desynth_runs.id</c> of every melt this Round fired - the join that answers
  /// "is this item something WE made tonight?", which is invariant B's one declared
  /// exception. A list, not a scalar: a halted melt that the human resumes is a second
  /// run row, and both runs' yields are equally this Round's.
  ///
  /// <para>Persisted for the same reason above and one more: the yields themselves are
  /// already durable (<c>desynth_yields</c>), so the only thing a reload could lose is
  /// the pointer to them - and losing the pointer would hold back exactly the rows the
  /// ruling most wants admitted.</para>
  /// </summary>
  public List<long> MeltRunIds { get; set; } = new();

  /// <summary>
  /// A RE-LOOK IS OWED (review ruling S6, 2026-08-12): the player pressed Re-Look and
  /// no recon has read a board since. While it stands, the recon stage's work set is
  /// the WHOLE listable bag rather than the stale half, and the deck's count says so.
  ///
  /// <para><b>It is persisted for the same reason the cursor is.</b> The latch used to
  /// live only in the window, so a reload between the press and the walk to the bell
  /// dropped it silently: the round came back holding, the player pressed Resume, and
  /// recon re-derived the ordinary stale-half work set - which, twenty minutes after a
  /// Look, is empty. The cursor skips the stage, the rail shows Recon done, and the
  /// player believes every board was re-read when none was. A verb whose entire meaning
  /// is "ignore the freshness window" cannot be the one piece of the round that a
  /// reload forgets.</para>
  ///
  /// <para>Adding a property is safe where renaming the class is fatal (see the
  /// receipts on <see cref="RoundStage"/>): a config written without this key reads
  /// false, which is a round with no Re-Look owed - the fail-closed answer.</para>
  /// </summary>
  public bool ReLookPending { get; set; }
}

/// <summary>
/// THE PRE-ROUNDS SHAPE, KEPT ALIVE ON PURPOSE. This is <see cref="RoundState"/>
/// under its old name, and it exists for exactly one reason: the config on disk
/// names it.
///
/// <para>Dalamud serializes the plugin config with Newtonsoft's
/// TypeNameHandling.Objects, which writes a class IDENTITY into the file
/// alongside the data - <c>"$type": "Scrooge.SweepState, Scrooge"</c>, sitting in
/// every config written before 2026-08-10. Measured that day rather than assumed
/// (probe against the live Scrooge.json + a reproduction on the same serializer
/// settings): if a property still MAPS and its recorded $type no longer resolves,
/// Newtonsoft throws <i>Error resolving type specified in JSON</i> and the whole
/// config load dies - not the property, the file. Every setting Drift has ever
/// tuned, gone to a default, because a class got a better name.</para>
///
/// <para>The same probe found the escape: a property that maps to NOTHING is
/// skipped whole, $type unread, no throw. So the rename is safe the moment the
/// old KEY stops resolving too - and the old data is then simply lost. Keeping
/// this class is what buys the third option: the old key still lands somewhere,
/// so an in-flight round survives the version it was renamed in. It is read once
/// by <see cref="LegacyRoundConfig.Fold"/>, folded forward, and nulled.</para>
///
/// <para><b>This type is write-never.</b> Nothing exports to it; <c>Export</c>
/// writes <see cref="RoundState"/> only. It is the one sanctioned exception to the
/// naming sweep's "zero live Sweep* identifiers", on the same footing as migration
/// history: it does not name a thing the plugin does, it names a thing the plugin
/// USED to write, and it can be deleted once no config in the wild still carries
/// the old key.</para>
/// </summary>
public sealed class SweepState
{
  public bool Active { get; set; }

  /// <summary>
  /// The old stage marks. Typed as the NEW enum deliberately - the persisted
  /// values are numbers and those numbers are frozen (see
  /// <see cref="RoundStage"/>), so the old list re-reads correctly under the new
  /// name with no conversion at all. A second frozen-number enum would be two
  /// copies of one contract, which is how the copies drift.
  /// </summary>
  public List<RoundStage> Done { get; set; } = new();

  public RoundStage? HaltStage { get; set; }

  public string? HaltMessage { get; set; }

  public long StartedAtUnix { get; set; }
}

/// <summary>
/// THE ONE-WAY FOLD from the pre-Rounds config keys to the Rounds ones. Two keys
/// were renamed when the sweep became the round - <c>Sweep</c> (the held place)
/// and <c>SweepStalenessCeilingHours</c> (the sanity ceiling) - and the ruling on
/// the naming sweep was that config keys migrate SILENTLY: a player does not
/// re-tune his settings because we re-worded ours.
///
/// <para>Pure, so the migration is testable without a game running - which
/// matters more here than usual, because the failure mode this guards is one
/// nobody sees until a config in the wild fails to load.</para>
///
/// <para><b>One way, once.</b> The caller writes the folded values back, nulls the
/// legacy pair, and saves; the next session finds nothing to fold. New-shape data
/// always WINS - a live <paramref name="round"/> is never clobbered by a stale
/// legacy blob, because the only way both exist is a downgrade-then-upgrade, and
/// in that order the newer write is the truthful one.</para>
/// </summary>
internal static class LegacyRoundConfig
{
  /// <summary>
  /// What the config should hold after the fold, and whether anything moved.
  /// <paramref name="legacyCeilingHours"/> is nullable because ABSENT and ZERO are
  /// different facts: a config that never had the key must keep the current
  /// value, not inherit a 0 that would floor to 1h and retire live rounds early.
  /// </summary>
  internal static (RoundState? Round, int CeilingHours, bool LogEnabled, bool Migrated) Fold(
    RoundState? round, SweepState? legacyRound, int ceilingHours, int? legacyCeilingHours,
    bool logEnabled, bool? legacyLogEnabled)
  {
    var migrated = false;

    if (round is null && legacyRound is not null)
    {
      round = new RoundState
      {
        Active = legacyRound.Active,
        Done = new List<RoundStage>(legacyRound.Done),
        HaltStage = legacyRound.HaltStage,
        HaltMessage = legacyRound.HaltMessage,
        StartedAtUnix = legacyRound.StartedAtUnix,
        // RunId stays 0 and that is the honest answer: the old shape predates the
        // round_runs table, so there is no banked run for this round to claim. It
        // flows on with an in-memory-only transcript, exactly as it did before, and
        // the next round starts a banked one.
      };
      migrated = true;
    }
    else if (legacyRound is not null)
    {
      // Both present: the new key stands, but the legacy one still has to be
      // cleared or it folds again forever.
      migrated = true;
    }

    if (legacyCeilingHours is int hours)
    {
      ceilingHours = hours;
      migrated = true;
    }

    // The run log's key came across in unit 5, when the Ledger name was reissued to
    // the transcript. Same rule, same reason: a player who turned the log OFF must
    // not find it back on because the window got a better name.
    if (legacyLogEnabled is bool enabled)
    {
      logEnabled = enabled;
      migrated = true;
    }

    return (round, ceilingHours, logEnabled, migrated);
  }
}

/// <summary>
/// The one-button round's cursor: pinch -&gt; melt -&gt; bell -&gt; turn in, walking
/// between stops. A stage with no work is skipped silently, and a stage is marked
/// done AT FIRE TIME.
///
/// <para>WHAT STARTS A STAGE (WALK unit 9, Drift 07-25). Originally: one player
/// press per stage. Now: ONE press starts the round, and the stages FLOW - the
/// deck fires the next stage when its predecessor's run completes and the player
/// is standing where it happens, or on ARRIVAL when he isn't. The first law is
/// unchanged and now structural rather than procedural: the advance rule
/// (<see cref="FlowPlan.Advance"/>) answers Nothing unless a round the human
/// pressed is live and un-halted, so nothing in this file can ever fire without
/// that press behind it. The per-stage button survives as an override.</para>
///
/// WALK unit 2 adds the RUN MODEL on top of that dumb cursor:
/// <list type="bullet">
///   <item><b>Halt</b> - a mid-flow stage death does not un-mark and roll on; it
///     HALTS the round, holding the cursor and the completion marks in place and
///     naming the gap. The deck must never offer the next stage past a corpse,
///     so <see cref="Next"/> returns null while halted.</item>
///   <item><b>Resume</b> - re-offers the HALTED stage as current (its executor
///     rescans at fire time), never a replay from the top and never a re-fire of
///     a completed stage.</item>
///   <item><b>Persistence</b> - <see cref="Export"/> / <see cref="Restore"/> carry
///     the held place across a reload; <see cref="IsStale"/> retires a round too
///     old to trust.</item>
/// </list>
/// The distinction from <see cref="Unmark"/> is deliberate: Unmark is the quiet
/// fire-time revert (the stage goes straight back onto the cursor); Halt is the
/// loud stop (the round freezes at the corpse until the player Resumes).
/// Pure and Dalamud-free (linked into the test project): the window feeds it
/// work counts, location, timestamps, and abort reasons; it answers "what's next,
/// where, and is anything blocking."
/// </summary>
internal sealed class RoundPlan
{
  /// <summary>
  /// THE RULED ORDER (Drift, 2026-07-25): <b>pinch -&gt; melt -&gt; bell -&gt; turn
  /// in</b> - "figure out what to melt, melt it, THEN post gained mats + other
  /// sellables". The 07-24 lap ran the old order (bell before melt) and 18 melts'
  /// worth of yields sat in the bags with no prompt while the round completed
  /// around them: the melt FEEDS the bell, so it cannot follow it.
  ///
  /// <para>The Reprice stage is gone from this list, not moved. The bell is now
  /// the retainer errand entire - list, reprice, pull-and-vendor - so a separate
  /// stage for one of its three verbs was a second door onto the same stop.</para>
  ///
  /// <para><b>THE LOOK/ACT WAIST (Rounds, ruled 2026-08-06, ordered 08-10):</b>
  /// pinch -&gt; recon -&gt; <i>triage</i> -&gt; melt -&gt; list -&gt; GC. The list is
  /// no longer four errands in a convenient sequence; it is two halves with a
  /// human between them. LOOK is Pinch + Recon - both are board reads at a bell,
  /// the same errand at the same stop, which is why they sit adjacent and why
  /// <see cref="PlaceOf"/> answers Bell for both. ACT is Desynth + BellRun +
  /// TurnIn, and every one of them spends something that cannot be un-spent.</para>
  ///
  /// <para><b>Recon-before-Desynth is the irreversibility interlock, and it is the
  /// ONE ordering constraint in this array that is not about efficiency.</b> The
  /// melt destroys the item. If the melt ran before its List door had seen a real
  /// board, the round would be answering "is this worth more melted than listed?"
  /// off banked guesses, and the wrong answer is unrecoverable in a way a bad
  /// listing never is (a bad price gets repriced next round; a melted item is
  /// gone). So the read half completes, in full, before anything irreversible
  /// begins. Moving Recon after Desynth would compile, pass every other test in
  /// this file, and quietly cost real items - which is why the position has a
  /// test of its own.</para>
  ///
  /// <para><b>Triage sits at the waist and is CONTRACT B</b> (the melt precedent,
  /// 07-26): it is offered and fired like any stage, but it marks done only on the
  /// human's explicit press - <see cref="MarkDone"/> called with
  /// <see cref="RoundStage.Triage"/> from the Continue button. Because
  /// <see cref="Next"/> walks the array in order and stops at the first unfinished
  /// stage that has work, a fired-not-done Triage BLOCKS the cursor: no act stage
  /// can be offered while the hinge is standing open. That is not a rule written
  /// anywhere in the flow - it is what the cursor already does, applied to a stage
  /// that declines to mark itself.</para>
  /// </summary>
  internal static readonly RoundStage[] Order =
  {
    // --- LOOK: reads only, nothing spent, nothing destroyed ---
    RoundStage.Pinch,
    RoundStage.Recon,
    // --- THE WAIST: the human ---
    RoundStage.Triage,
    // --- ACT: every one of these is irreversible in its own way ---
    RoundStage.Desynth,
    RoundStage.BellRun,
    RoundStage.TurnIn,
  };

  // HasExecutor IS DELETED (Rounds unit 5), on its own instructions. It was the
  // campaign's interim register - which stages this build could actually RUN - and it
  // existed so the deck's work answer, the skip checkboxes and the stage rail could
  // not disagree about a temporary fact. Recon came off it in unit 2; the hinge came
  // off it here, when the Accountant's triage step became its presenter and Drift's
  // Continue press became its executor. The day it returned true for everything was
  // the day it stopped earning its place, and a register that always says yes is a
  // register nobody reads.

  private readonly HashSet<RoundStage> _done = new();
  private RoundHalt? _halt;

  /// <summary>A round is underway - the deck shows the cursor.</summary>
  internal bool Active { get; private set; }

  /// <summary>When the current round started - the staleness clock's zero. Null when idle.</summary>
  internal DateTimeOffset? StartedAt { get; private set; }

  /// <summary>
  /// The banked run this cursor belongs to (<c>round_runs.id</c>), or 0 when the
  /// round has no banked run - which is a working state, not an error: the cursor,
  /// the flow and every stage behave identically, and only the transcript and the
  /// cache rows lose their lineage. Fail-closed by construction, since every consumer
  /// treats 0 as "not claimed by any Round" already.
  /// </summary>
  internal long RunId { get; private set; }

  /// <summary>
  /// A Re-Look is armed: the next recon reads the WHOLE listable set rather than the
  /// stale half. See <see cref="RoundState.ReLookPending"/> for why the latch lives on
  /// the round (and therefore survives a reload) instead of on the window.
  /// </summary>
  internal bool ReLookArmed { get; private set; }

  /// <summary>The round is frozen over a dead stage, holding its place.</summary>
  internal bool Halted => _halt != null;

  /// <summary>The named gap the halt holds, or null when flowing normally.</summary>
  internal RoundHalt? CurrentHalt => _halt;

  /// <summary>The halted stage, or null when flowing normally.</summary>
  internal RoundStage? HaltStage => _halt?.Stage;

  internal static RoundPlace PlaceOf(RoundStage stage) => stage switch
  {
    RoundStage.Desynth => RoundPlace.Anywhere,
    RoundStage.TurnIn => RoundPlace.ExpertDelivery,
    // The hinge is a conversation, not an errand - it happens wherever the player
    // is standing when the Look half finishes. Naming a place for it would send
    // him on a walk to read his own board.
    RoundStage.Triage => RoundPlace.Anywhere,
    // Recon is the pinch's other half at the pinch's own stop: it needs a summoned
    // retainer with the sell panel reachable, which is the bell.
    RoundStage.Recon => RoundPlace.Bell,
    _ => RoundPlace.Bell, // the pinch and the bell run both live at a bell
  };

  /// <summary>Start a round now (wall clock). The shell's entry point.</summary>
  internal void Start() => Start(DateTimeOffset.UtcNow);

  /// <summary>
  /// Start a round stamped at an explicit time - the testable seam for the staleness
  /// clock - and claimed by a banked run. <paramref name="runId"/> defaults to 0 so
  /// every pure test of the cursor stays a test of the cursor.
  /// </summary>
  internal void Start(DateTimeOffset startedAt, long runId = 0)
  {
    Active = true;
    _done.Clear();
    _halt = null;
    StartedAt = startedAt;
    RunId = runId;
    ReLookArmed = false;
  }

  internal void Cancel()
  {
    Active = false;
    _done.Clear();
    _halt = null;
    StartedAt = null;
    RunId = 0;
    ReLookArmed = false;
  }

  /// <summary>
  /// THE RE-LOOK LATCH, ARMED. Paired with <see cref="ConsumeReLook"/>, and the pair
  /// is the whole of review ruling S6: <b>the latch is spent by the WORK, not by the
  /// attempt.</b>
  ///
  /// <para>Recon has four ways to refuse before it reads anything - another run is
  /// still working, every sell list is 20/20, the bell addons are mid-summon when the
  /// spine reads them, and the sell view could not be reached at all. Each of those
  /// ends in a reported death with no board touched. Clearing the latch at the fire
  /// site meant all four spent it anyway: the player's Resume then re-derived an
  /// ordinary recon, found every decision inside the freshness window, walked zero
  /// items, and the rail checked the stage off. The press did nothing and the deck
  /// could not say why.</para>
  /// </summary>
  internal void ArmReLook() => ReLookArmed = true;

  /// <summary>
  /// The latch is SPENT - called at the one moment recon actually starts reading
  /// boards, past every refusal it owns. A run that then dies mid-pass has still
  /// spent it, and correctly: the boards it reached are banked fresh, so the ordinary
  /// stale-half work set is exactly what the resumed stage should walk.
  /// </summary>
  internal void ConsumeReLook() => ReLookArmed = false;

  internal void MarkDone(RoundStage stage) => _done.Add(stage);

  /// <summary>
  /// Reverts a MarkDone, QUIETLY: the stage goes straight back onto the cursor
  /// with no halt held. The gentle sibling of <see cref="Halt"/> - use it when
  /// the death needs no naming and the round can simply re-offer the stage (the
  /// Re-Look re-opens completed Look stages through exactly this door).
  /// </summary>
  internal void Unmark(RoundStage stage) => _done.Remove(stage);

  /// <summary>
  /// HALTS the round over a dead stage: any completion mark it holds is reverted
  /// (it did not finish) AND the round freezes, holding the named gap. Prior
  /// stages keep their completion marks - the held place survives - but
  /// <see cref="Next"/> offers nothing until <see cref="Resume"/>, so the deck
  /// can never flow past the corpse.
  /// </summary>
  internal void Halt(RoundHalt halt)
  {
    _done.Remove(halt.Stage);
    _halt = halt;
  }

  /// <summary>
  /// Clears the halt and re-offers the halted stage as current. The stage was
  /// un-marked by <see cref="Halt"/>, so the very next <see cref="Next"/> returns
  /// it (its executor rescans at fire time) - never a replay from the top, never
  /// a re-fire of a completed stage. If the rescan finds no work, the cursor
  /// skips it forward like any empty stage.
  /// </summary>
  internal void Resume() => _halt = null;

  /// <summary>
  /// FAIL-CLOSED RESTORE (2026-07-26, the zombie round). A round rehydrated from
  /// config arrives HOLDING: the flow auto-fires stages by design, so a restore
  /// that lands live is a reload acting without a press - the resurrected 6:26 PM
  /// round ran the pinch over tonight's board on its own. The hold sits on the
  /// first unfinished stage and clears only through the ONE resume door.
  ///
  /// <para>Set directly rather than via <see cref="Halt"/>: a hold is a
  /// precaution, not a death, and must not revert any stage's completion mark. A
  /// real halt carried in the restored state outranks it (the corpse's message is
  /// the one the player needs). A fully-done restore is left unhalted for the
  /// flow's errand-over rule to retire on its first tick.</para>
  /// </summary>
  internal void HoldForRestore(string message)
  {
    if (!Active || _halt != null) return;
    foreach (var stage in Order)
    {
      if (_done.Contains(stage)) continue;
      _halt = new RoundHalt(stage, message);
      return;
    }
  }

  internal bool IsDone(RoundStage stage) => _done.Contains(stage);

  /// <summary>
  /// The first unfinished stage that has work, in round order; null when the
  /// round is complete (everything left is done or empty) OR while the round is
  /// HALTED (never offer a stage past a corpse - the deck renders the halt banner
  /// and a Resume, not the next fire button). Skipped-empty stages are NOT marked
  /// done - if work appears (a pinch flags reprices), the cursor picks the stage
  /// up on its way through.
  /// </summary>
  internal RoundStage? Next(Func<RoundStage, bool> hasWork)
  {
    if (_halt != null) return null;
    foreach (var stage in Order)
      if (!_done.Contains(stage) && hasWork(stage))
        return stage;
    return null;
  }

  /// <summary>
  /// THE LOCATION-AWARE CURSOR (WALK unit 6, from the 07-24 lap). <see cref="Next"/>
  /// is strictly linear, and on 07-24 that cost a lap: Drift walked to the GC counter
  /// for the turn-in - the round's own next stage - and mid-walk the board crossed
  /// the re-pinch floor, so the pinch re-armed upstream and the cursor snapped back
  /// to it. The deck then told a player standing at Expert Delivery, with
  /// "[you are here]" rendered on the same frame, to go walk to a retainer bell.
  ///
  /// <para>That violated the spine's own ruling: a stage is ready when its
  /// predecessor is done AND the player is standing where the stage needs them. So
  /// the cursor now asks the second half of that sentence too. If the linear next
  /// stage needs a walk but some other ARMED stage (has work, not done) is already
  /// where the player is standing, the deck offers THAT one. A re-arming upstream
  /// stage can no longer block a downstream stage whose place the player already
  /// reached.</para>
  ///
  /// <para>Precedence is deliberate and dumb: the linear cursor WINS whenever its
  /// own place is satisfied, so ordinary flow is untouched and the opportunistic
  /// path only ever opens when the strict answer would have been "go walk". When
  /// nothing armed is reachable, the linear stage is offered anyway - the deck
  /// still needs a stage to name the walk for.</para>
  ///
  /// <para>OFFER, NEVER FIRE. This returns what the button should say; the human
  /// still presses it. Nothing about standing somewhere starts a run.</para>
  /// </summary>
  internal RoundOffer? Offer(Func<RoundStage, bool> hasWork, Func<RoundStage, bool> locationSatisfied)
  {
    // Halted or complete: Next already answers null, and neither state has a
    // stage to offer - the deck renders the halt banner or the done line.
    if (Next(hasWork) is not RoundStage cursor) return null;

    if (locationSatisfied(cursor)) return new RoundOffer(cursor, Opportunistic: false);

    foreach (var stage in Order)
    {
      if (stage == cursor) continue;
      if (_done.Contains(stage) || !hasWork(stage)) continue;
      if (locationSatisfied(stage)) return new RoundOffer(stage, Opportunistic: true);
    }

    return new RoundOffer(cursor, Opportunistic: false);
  }

  /// <summary>Snapshot the held place for persistence.</summary>
  internal RoundState Export() => new()
  {
    Active = Active,
    Done = _done.ToList(),
    HaltStage = _halt?.Stage,
    HaltMessage = _halt?.Message,
    StartedAtUnix = StartedAt?.ToUnixTimeSeconds() ?? 0,
    RunId = RunId,
    ReLookPending = ReLookArmed,
  };

  /// <summary>
  /// Rehydrate the held place from a persisted snapshot - the deck shows the same
  /// stages done / current / halted as before the reload. The caller is
  /// responsible for the staleness check (<see cref="IsStale"/>) BEFORE restoring;
  /// a stale round is retired, not restored.
  ///
  /// <para>MIGRATION (07-25): a mark for a stage this build no longer runs - the
  /// retired Reprice, or anything a future edit drops - is DISCARDED rather than
  /// carried. A restored round from the old shape therefore re-derives: the cursor
  /// asks the current stage list what still has work, and the reprice rows the old
  /// mark stood for are simply part of the bell now. Keeping the mark would have
  /// meant a done-flag no stage can ever clear; crashing on it would have meant a
  /// reload eating a live round, which is the bug the persistence exists to fix.</para>
  /// </summary>
  internal void Restore(RoundState state)
  {
    Active = state.Active;
    _done.Clear();
    foreach (var stage in state.Done)
      if (Array.IndexOf(Order, stage) >= 0)
        _done.Add(stage);
    // A halt held over a retired stage is dropped with it - the corpse belongs to
    // a stage that no longer exists, and a halt nothing can resume past would
    // freeze the round forever.
    _halt = state.HaltStage is RoundStage hs && Array.IndexOf(Order, hs) >= 0
      ? new RoundHalt(hs, state.HaltMessage ?? "")
      : null;
    StartedAt = state.StartedAtUnix > 0
      ? DateTimeOffset.FromUnixTimeSeconds(state.StartedAtUnix)
      : null;
    // The lineage rides the restore whole. A restored round that lost its run id
    // would go on writing transcript lines and cache rows under a DIFFERENT identity
    // from the ones it wrote before the reload, which is the exact failure this
    // column exists to make impossible.
    RunId = state.RunId;
    // The owed Re-Look rides the restore whole (S6). The reload is precisely what a
    // press-then-walk-to-the-bell has to survive, and a latch dropped here would send
    // the resumed stage back to the freshness window the verb exists to overrule.
    ReLookArmed = state.ReLookPending;
  }

  /// <summary>
  /// A restored round older than the ceiling is history, not a round - retire it
  /// loudly instead of trusting a stale world (spec staleness guard, deliberately
  /// dumb: one timestamp vs one ceiling, no cleverer logic). Pure decision; the
  /// shell supplies the wall clock and the config ceiling.
  /// </summary>
  internal static bool IsStale(DateTimeOffset startedAt, DateTimeOffset now, TimeSpan ceiling)
    => now - startedAt > ceiling;
}
