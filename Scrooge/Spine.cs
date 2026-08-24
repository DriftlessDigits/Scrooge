using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The advisor's spine, in the vocabulary Drift ruled 2026-07-22:
/// <i>"the advisor really needs to understand current state vs. expected state
/// and to manage the transitions."</i>
///
/// This file is the PURE half - the declarative expected-state model plus the
/// transition-ladder decision logic. It reads no game state (that is
/// <see cref="SpineSensors"/>, Dalamud-side); it is handed a
/// <see cref="FacetReading"/> per expectation and decides which rung of the
/// ladder the gap sits on. Kept Dalamud-free on purpose so it links into the
/// test project - a game/config static leaking in here breaks that compile.
///
/// Every 07-22 bug was one action ASSUMING state (desynth assumed un-occupied,
/// Hawk Go assumed the sell view, the deck assumed "anywhere"). The spine names
/// the assumption up front and forces every fire through one evaluation.
/// </summary>
internal static class Spine
{
  /// <summary>
  /// The transition ladder (spec 2026-07-23), in order of preference. When an
  /// expectation is unmet, the advisor tries the highest rung the expectation
  /// declares it can reach:
  /// <list type="number">
  ///   <item><see cref="SelfNavigate"/> - the advisor closes the gap itself
  ///     (NavigateAndStartHawkRun is the model).</item>
  ///   <item><see cref="PortOnClick"/> - move the player across the world, but
  ///     only by an offered click, never a surprise. Built in WALK unit 7 (see
  ///     PortPlan): the turn-in declares this rung, and the deck and the stage
  ///     rail offer the teleport beside the walk line. It never fires itself.</item>
  ///   <item><see cref="WalkThere"/> - name the place and let the player walk;
  ///     cleared by ARRIVING, not clicking. Arrival is the ordinary place sensor
  ///     coming true - there is no arrival machinery, and unit 7 added none: a
  ///     port only shortens the walk, it does not end it.</item>
  ///   <item><see cref="Refuse"/> - no transition is possible; say exactly which
  ///     expectation failed and what would clear it.</item>
  /// </list>
  /// <see cref="Fire"/> is the "all expectations met" outcome - not a rung.
  /// </summary>
  internal enum Rung
  {
    Fire = 0,
    SelfNavigate = 1,
    PortOnClick = 2,
    WalkThere = 3,
    Refuse = 4,
  }

  /// <summary>
  /// HOW LONG AN ABSENCE MEANS ANYTHING (2026-07-26). Two refusals in one evening,
  /// both honest, both wrong: the bell run halted because a retainer was mid-summon,
  /// and the turn-in's port refused because the game was still busy closing the
  /// retainer UI the stage before it had used. Neither was a state the player needed
  /// to do anything about - both cleared themselves in under a second.
  ///
  /// <para>The round hits these BY CONSTRUCTION. Stage N's cleanup is exactly what
  /// makes stage N+1's precondition transiently false, and the flow advances the
  /// instant stage N reports done. So an auto-advancing round reads gaps a human
  /// never would, and believing them on the first read is the bug class.</para>
  ///
  /// <para>The classification is the fix: an expectation says whether a few seconds
  /// could plausibly change its answer. That is the ONE axis that separates "wait a
  /// beat" from "tell the player now", and it is a property of the expectation, not
  /// of the moment - so it is declared next to the expectation, once.</para>
  /// </summary>
  internal enum Persistence
  {
    /// <summary>
    /// A few seconds can flip this on its own: an addon mid-swap, an occupancy flag
    /// that has not cleared yet, an action the game says is busy right now. The
    /// DEFAULT, because every expectation this codebase declares is one of these -
    /// they are all addon-ready or occupancy reads.
    /// </summary>
    Transient,
    /// <summary>
    /// Waiting changes nothing: the wrong city, no Grand Company, no retainers, an
    /// item that is not in the bags. These refuse instantly and always - a grace
    /// window over one of them is just a delayed refusal, which is strictly worse
    /// than a prompt one.
    /// </summary>
    Stable,
  }

  /// <summary>The three facets of game state an executor can expect (spec).</summary>
  internal enum Facet
  {
    /// <summary>Un-occupied / a specific addon open (v2.20's occupied pre-flight).</summary>
    Occupancy,
    /// <summary>Which addon/agent must be open and ready (sell view, salvage, Expert Delivery).</summary>
    View,
    /// <summary>Where the player must be standing (a bell, the GC counter).</summary>
    Place,
  }

  // Ladder severity == the enum's own value: a less-actionable rung (higher
  // value) wins when several expectations are unmet at once, because the
  // hardest blocker must clear first - you cannot self-navigate while the game
  // has you occupied. Ties break by declaration order, so an executor lists its
  // checks in the order it wants them reported. (See SpineEvaluator.Evaluate.)
}

/// <summary>
/// One precondition an executor DECLARES before it fires: which facet, the
/// state it expects (phrased for a refusal message), and the best rung of the
/// ladder available if that expectation is unmet. Pure data - the reading of
/// whether it currently holds lives in <see cref="FacetReading"/>.
/// </summary>
internal sealed record SpineExpectation(
  Spine.Facet Facet,
  string Expected,
  Spine.Rung IfUnmet,
  Spine.Persistence Class = Spine.Persistence.Transient);

/// <summary>
/// The sensor's answer for one expectation: whether it currently holds, and a
/// short description of the state that IS (used verbatim in the refusal - "but
/// the retainer bell is open"). Built Dalamud-side; consumed here as plain data.
/// </summary>
internal sealed record FacetReading(bool Met, string Current);

/// <summary>
/// The declarative precondition set for a single executor's fire: the action's
/// name (for messages) and its expectations in report order.
/// </summary>
internal sealed record ExpectedState(string Action, IReadOnlyList<SpineExpectation> Expectations)
{
  internal ExpectedState(string action, params SpineExpectation[] expectations)
    : this(action, (IReadOnlyList<SpineExpectation>)expectations) { }
}

/// <summary>
/// The outcome of a pre-fire evaluation: the ladder rung and the message that
/// names the gap (expected vs current). <see cref="CanFire"/> is the one thing
/// most callers check.
/// </summary>
internal sealed record SpineEvaluation(
  Spine.Rung Rung,
  string Message,
  SpineExpectation? Gap,
  bool AllGapsTransient = true)
{
  /// <summary>All expectations met - the executor may proceed.</summary>
  internal bool CanFire => Rung == Spine.Rung.Fire;
}

internal static class SpineEvaluator
{
  /// <summary>
  /// Compares an executor's declared <see cref="ExpectedState"/> against a
  /// current-state <paramref name="readings"/> list (one per expectation, in
  /// declaration order) and returns the ladder outcome.
  ///
  /// All met -> <see cref="Spine.Rung.Fire"/>. Otherwise the unmet expectation
  /// with the least-actionable rung wins (ties break by declaration order), and
  /// the message names the gap: "Can't {action} - expected {X}, but {current}."
  /// </summary>
  internal static SpineEvaluation Evaluate(ExpectedState expected, IReadOnlyList<FacetReading> readings)
  {
    if (readings.Count != expected.Expectations.Count)
      throw new ArgumentException(
        $"Spine reading count ({readings.Count}) does not match expectation count " +
        $"({expected.Expectations.Count}) for '{expected.Action}'.");

    int worstIndex = -1;
    int worstSeverity = -1;
    // EVERY unmet expectation, not just the worst one: a gap that seconds cannot fix
    // makes the whole refusal un-waitable, however waitable its neighbours are. One
    // stable blocker in the set is enough to say "tell him now".
    var allTransient = true;

    for (var i = 0; i < readings.Count; i++)
    {
      if (readings[i].Met) continue;
      if (expected.Expectations[i].Class == Spine.Persistence.Stable) allTransient = false;
      var sev = (int)expected.Expectations[i].IfUnmet;
      if (sev > worstSeverity)
      {
        worstSeverity = sev;
        worstIndex = i;
      }
    }

    if (worstIndex < 0)
      return new SpineEvaluation(Spine.Rung.Fire, $"{expected.Action}: all preconditions met.", null);

    var gap = expected.Expectations[worstIndex];
    var current = readings[worstIndex].Current;
    var message = $"Can't {expected.Action} - expected {gap.Expected}, but {current}.";
    return new SpineEvaluation(gap.IfUnmet, message, gap, allTransient);
  }
}

/// <summary>What a grace window says about an absence it has been watching.</summary>
internal enum GraceVerdict
{
  /// <summary>The expectation holds right now - nothing to wait on.</summary>
  Met,
  /// <summary>Absent, but not for long enough to be believed. Keep watching.</summary>
  Waiting,
  /// <summary>Absent continuously past the grace window. This one is real.</summary>
  Expired,
}

/// <summary>
/// THE TRANSIENT-ABSENCE GRACE (2026-07-26). A sensor that reads game state through
/// ADDONS and ACTION STATUS cannot tell "you walked away" from "the window you were
/// looking at is being swapped for the next one", nor "you can't teleport here" from
/// "the game is still busy for another 300ms". Both read absent, and in one evening
/// that cost two stages: the bell run halted with <i>"expected to be at a retainer
/// bell, but you're not at a retainer bell"</i> while Drift stood at the bell
/// (AutoRetainer had summoned a retainer, and mid-summon neither bell addon is ready),
/// and the turn-in's port refused with <i>"the game won't let you cast Teleport right
/// now (it answered 579)"</i> because the retainer UI from the stage before was still
/// closing.
///
/// <para>The fix is not a better sensor - it is REPEATED asking. An absence must
/// PERSIST before it is believed. A UI transition cannot hold "absent" for seconds; a
/// player who genuinely walked off, or genuinely cannot teleport, holds it forever. So
/// the window separates them on the one axis they actually differ on: duration.</para>
///
/// <para>Pure and clock-injected (the caller passes <c>nowMs</c>), so the persistence
/// decision links into the test project while the sensors stay Dalamud-bound.</para>
/// </summary>
internal sealed class TransientGrace
{
  private readonly int _graceMs;
  private long? _firstAbsentAt;

  internal TransientGrace(int graceMs) => _graceMs = graceMs;

  /// <summary>When the current unbroken run of absences began. Null when nothing is absent.</summary>
  internal long? FirstAbsentAt => _firstAbsentAt;

  /// <summary>
  /// One reading, one verdict. A MET reading clears the window outright - that is
  /// what makes a flicker (absent -> present -> absent) start the clock over rather
  /// than accumulate toward a halt, which is exactly the shape a UI swap produces.
  /// </summary>
  internal GraceVerdict Observe(bool met, long nowMs)
  {
    if (met)
    {
      _firstAbsentAt = null;
      return GraceVerdict.Met;
    }

    _firstAbsentAt ??= nowMs;
    return nowMs - _firstAbsentAt.Value >= _graceMs
      ? GraceVerdict.Expired
      : GraceVerdict.Waiting;
  }

  /// <summary>Forget the current absence - the window is reused, not rebuilt.</summary>
  internal void Reset() => _firstAbsentAt = null;
}

/// <summary>
/// WHEN A REFUSAL IS WORTH WAITING OUT - the pure gate over
/// <see cref="TransientGrace"/>. Two questions, in this order: is anybody LOOKING at
/// this refusal right now, and could a few seconds change it?
/// </summary>
internal static class GracePlan
{
  /// <summary>
  /// The grace over a PLACE facet that goes absent while a run is being started, in
  /// milliseconds.
  ///
  /// <para>2.5 seconds. The number it has to clear is a retainer SUMMON: the Talk
  /// dialog, the SelectString menu and the sell view's own load, which together run
  /// well under a second even when AutoRetainer is driving them faster than a human
  /// could. The number it must stay under is a WALK - a player who left the bell is
  /// gone for as long as it takes him to come back, which is never 2.5 seconds. The
  /// gap between those two is enormous, so the constant sits in the middle of it and
  /// is not worth tuning: anywhere from 1.5s to 5s separates the same two cases.
  /// Erring long is cheap (a halt that arrives 2.5s later is the same halt); erring
  /// short is what this exists to stop.</para>
  /// </summary>
  internal const int PlaceGraceMs = 2500;

  /// <summary>
  /// The grace a stage the FLOW fired gets before its refusal is believed, in
  /// milliseconds.
  ///
  /// <para>5 seconds - twice the place window, deliberately. A stage boundary is the
  /// worst moment in the whole round to take a reading: the previous stage's windows
  /// are closing, its occupancy flag is draining, and the game's action system is
  /// still refusing everything for a beat afterwards. Those settle in well under a
  /// second, but the round advances on the completion EVENT, which can land before any
  /// of it has finished. The wider window buys the whole teardown rather than just the
  /// part we happened to measure. It is still short enough that a genuine blocker
  /// (wrong city, no seals) reaches the player while he is still watching the stage he
  /// was watching - and stable-class gaps skip the window entirely anyway.</para>
  /// </summary>
  internal const int AutoFireGraceMs = 5000;

  /// <summary>
  /// Should this refusal be WAITED OUT rather than said out loud?
  ///
  /// <para>Only when nobody just pressed a button (<paramref name="playerPressed"/>),
  /// and every unmet expectation is transient-class. A human who pressed a button a
  /// frame ago is owed an answer NOW, right or wrong; a round advancing between its
  /// own stages is owed patience, because it is the round's own teardown that made the
  /// reading false.</para>
  /// </summary>
  internal static bool ShouldWaitOut(SpineEvaluation eval, bool playerPressed)
    => !eval.CanFire && !playerPressed && eval.AllGapsTransient;

  /// <summary>
  /// Does a SURFACED refusal owe the flow a report?
  ///
  /// <para><see cref="ShouldWaitOut"/>'s twin, and deliberately the same operand. The
  /// fact that earns a refusal patience is the fact that makes it worth reporting: if
  /// nobody pressed a button, then something FIRED this stage, and a fired stage that
  /// refuses is a run that ended. Before this existed, the turn-in and the pinch could
  /// both refuse without reporting - the round marked the stage done at fire time and
  /// walked on to the next stop with no run behind it and no halt. A silent skip, and
  /// the one failure mode the whole completion hub was built to make impossible.</para>
  ///
  /// <para><b>Why it is safe to report now, and was not before.</b> Transient grace
  /// (07-26) means an auto-fired refusal only reaches here after the gap has held
  /// still for the whole window. A refusal that survives that is a genuine one, not
  /// the previous stage putting its toys away - so halting on it names a real gap
  /// rather than flapping on a blink.</para>
  ///
  /// <para>A player-pressed refusal reports NOTHING. He is looking at the button he
  /// pressed; the chat error is the answer, and there may be no round to halt. (Where
  /// there IS a live round, FlowPlan.Reaction still refuses to halt a stage the round
  /// did not fire - two independent guards on the same mistake.)</para>
  /// </summary>
  internal static bool ShouldReport(bool playerPressed) => !playerPressed;
}
