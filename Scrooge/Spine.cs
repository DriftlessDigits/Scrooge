using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The advisor's spine, in the vocabulary Sam ruled 2026-07-22:
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
  ///     only by an offered click, never a surprise. STUB this unit: a valid
  ///     outcome value with no teleport implementation behind it yet.</item>
  ///   <item><see cref="WalkThere"/> - name the place and let the player walk;
  ///     cleared by ARRIVING, not clicking (arrival detection stubbed here).</item>
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
  Spine.Rung IfUnmet);

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
internal sealed record SpineEvaluation(Spine.Rung Rung, string Message, SpineExpectation? Gap)
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

    for (var i = 0; i < readings.Count; i++)
    {
      if (readings[i].Met) continue;
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
    return new SpineEvaluation(gap.IfUnmet, message, gap);
  }
}
