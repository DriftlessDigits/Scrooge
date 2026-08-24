using System;

namespace Scrooge;

/// <summary>The four things the fit check can say at the round's press.</summary>
internal enum FitVerdict
{
  /// <summary>The padded pinch estimate clears the venture return - fire normally.</summary>
  Fits,
  /// <summary>The pinch would still be running when the haul lands - advise waiting, but never refuse.</summary>
  DoesntFit,
  /// <summary>The board read is younger than the re-pinch floor - the round skips the pinch.</summary>
  BoardFresh,
  /// <summary>One of the two clocks can't be read - say so and DON'T block (advise-only degradation).</summary>
  NoData,
}

/// <summary>
/// The fit check at the round's press (WALK unit 8, spec "The press contract").
/// Two MEASURED clocks compared with a human's honest advice, never a gate:
/// <list type="bullet">
///   <item>the round estimate - the WHOLE round's machine time
///     (<see cref="StageRail.PlanMachineMs"/>: every stage's own banked pace times
///     what it is holding, pinch over the book-kept roster). Ruled 08-16 round walk:
///     the question is "will the entire round finish before the retainers return",
///     and the original pinch-only feed predated the per-stage pace bank - once
///     every stage had its own measured pace, quoting only the pinch made this line
///     contradict the step list four rows above it. The turn-in is summed too as a
///     deliberate simplification, not a collision claim (it happens at the GC
///     counter, away from the bell).</item>
///   <item>the return countdown - seconds until the soonest venture completes,
///     read from ClientStructs RetainerManager (see
///     <see cref="GameSafe.SoonestVentureReturnSeconds"/>).</item>
/// </list>
/// Drift's ruling (2026-07-22, verbatim): "if a full pinch is gonna take an
/// estimated 20 minutes, but retainers return in 5, then don't suggest a pinch.
/// or if the last pinch was less than 4 hours ago, don't suggest a pinch in a
/// round run." The advisor owns the arithmetic; Drift owns the decision - a
/// DOESN'T-FIT never refuses, it costs one deliberate "Start anyway" click.
///
/// Pure and Dalamud-free (linked into the test project): the deck feeds it the
/// two clocks plus the board age and the re-pinch floor; it answers with a
/// verdict and the honest line to show.
/// </summary>
internal sealed record FitCheck(FitVerdict Verdict, string Message)
{
  /// <summary>
  /// "Comfortably" from the spec: a small safety pad on the estimate so a pinch
  /// that only JUST fits is treated as not fitting. A named constant, not a
  /// config knob - it is the meaning of "comfortably", not a tunable cadence
  /// number. (est * 1.25: a 20m pinch wants 25m of runway.)
  /// </summary>
  internal const double SafetyMargin = 1.25;

  /// <summary>Board fresh - the deck's pinch stage has no work (opens past the pinch).</summary>
  internal bool SkipPinch => Verdict == FitVerdict.BoardFresh;

  /// <summary>Doesn't fit - the deck relabels the fire button to a deliberate "Start anyway".</summary>
  internal bool RequiresConfirm => Verdict == FitVerdict.DoesntFit;

  /// <summary>
  /// The verdict at press. <paramref name="estMs"/> is the pinch ETA in
  /// milliseconds (null when no timing is on record); <paramref name="returnSeconds"/>
  /// is seconds until the soonest venture returns (null when no venture clock is
  /// readable - not at a bell, retainers all idle, no ventures out);
  /// <paramref name="boardAgeSeconds"/> is the age of the last full board read
  /// (null when the board was never scanned - which is NOT fresh);
  /// <paramref name="repinchFloor"/> is the config re-pinch floor (seeded 4h).
  /// </summary>
  internal static FitCheck AtPress(long? estMs, long? returnSeconds,
    long? boardAgeSeconds, TimeSpan repinchFloor)
  {
    // 1. Board fresh wins first: a current board means the pinch is skipped, so
    // there is no long pinch to collide with the return - the clocks are moot.
    if (boardAgeSeconds is long age && age >= 0 && age < repinchFloor.TotalSeconds)
    {
      // Player language (strings pass, 08-02): say what happens and why, not
      // the mechanism's pet names ("pinch", "re-pinch floor").
      var floor = $"{repinchFloor.TotalHours:0.#}h";
      return new FitCheck(FitVerdict.BoardFresh,
        $"prices were checked {RipenessSensors.AgeText(age)} ago - still fresh (under {floor}), so this round skips the re-check");
    }

    // 2. A blind clock never gates - name which one it can't see and fire anyway.
    if (estMs is not long est || returnSeconds is not long ret)
    {
      var blind = (estMs, returnSeconds) switch
      {
        (null, null) => "can't see either clock (no pinch timing yet, no venture clock)",
        (null, _) => "can't estimate the round yet (no pinch timing on record)",
        _ => "can't see the retainer clock (no ventures out, or not at a bell)",
      };
      return new FitCheck(FitVerdict.NoData, $"{blind} - firing without a fit check");
    }

    // 3. Two measured clocks. The padded estimate (ms) must clear the return (ms).
    //
    // THE PAD IS SAID OUT LOUD (V27, ruled B9). The comparison has always been the
    // PADDED estimate against the return, and quoting only the raw estimate made the
    // line's own arithmetic look broken: "~9m vs ~20m - fits" then "~15m vs ~6m -
    // wait" reads as two rules. Naming the pad as a WORD closes that - and only as a
    // word: 1.25 is the mechanism, and a verdict that recites its multiplier is the
    // icon announcing that dark mode is dark. The knob's own tooltip is where a
    // number would belong, and this pad has no knob because it is not one.
    var estText = ShortDur(est / 1000);
    var retText = ShortDur(ret);
    if (est * SafetyMargin <= ret * 1000L)
      return new FitCheck(FitVerdict.Fits,
        $"round est. ~{estText} + safety vs ~{retText} return - fits");
    return new FitCheck(FitVerdict.DoesntFit,
      $"~{estText} + safety vs ~{retText} return - wait for the haul and fold it into one run.");
  }

  /// <summary>
  /// A duration said the way Drift says it - see <see cref="Durations.Span"/>, which
  /// owns the grammar. Kept under this name because the message lines, the rail's
  /// ETA slot and the deck's "Start anyway" relabel all reach for it here.
  /// </summary>
  internal static string ShortDur(long seconds) => Durations.Span(seconds);
}
