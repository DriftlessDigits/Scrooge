using System;

namespace Scrooge;

/// <summary>The four things the fit check can say at the sweep's press.</summary>
internal enum FitVerdict
{
  /// <summary>The padded pinch estimate clears the venture return - fire normally.</summary>
  Fits,
  /// <summary>The pinch would still be running when the haul lands - advise waiting, but never refuse.</summary>
  DoesntFit,
  /// <summary>The board read is younger than the re-pinch floor - the sweep skips the pinch.</summary>
  BoardFresh,
  /// <summary>One of the two clocks can't be read - say so and DON'T block (advise-only degradation).</summary>
  NoData,
}

/// <summary>
/// The fit check at the sweep's press (WALK unit 8, spec "The press contract").
/// Two MEASURED clocks compared with a human's honest advice, never a gate:
/// <list type="bullet">
///   <item>the sweep estimate - the pinch's own persisted ms/item x the current
///     standing-listing count (the market-facing, bell-bound stage the return
///     collides with). Melt/turn-in happen elsewhere and off-market, so the pinch
///     ETA alone is the honest v0 estimate - no per-stage melt history is folded
///     in (the melt lifecycle has no persisted ms/item to draw on); the return
///     says as much rather than inventing a number.</item>
///   <item>the return countdown - seconds until the soonest venture completes,
///     read from ClientStructs RetainerManager (see
///     <see cref="GameSafe.SoonestVentureReturnSeconds"/>).</item>
/// </list>
/// Sam's ruling (2026-07-22, verbatim): "if a full pinch is gonna take an
/// estimated 20 minutes, but retainers return in 5, then don't suggest a pinch.
/// or if the last pinch was less than 4 hours ago, don't suggest a pinch in a
/// sweep run." The advisor owns the arithmetic; Sam owns the decision - a
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
      var floor = $"{repinchFloor.TotalHours:0.#}h";
      return new FitCheck(FitVerdict.BoardFresh,
        $"board is {RipenessSensors.AgeText(age)} fresh - the sweep skips the pinch (last read under {floor})");
    }

    // 2. A blind clock never gates - name which one it can't see and fire anyway.
    if (estMs is not long est || returnSeconds is not long ret)
    {
      var blind = (estMs, returnSeconds) switch
      {
        (null, null) => "can't see either clock (no pinch timing yet, no venture clock)",
        (null, _) => "can't estimate the sweep yet (no pinch timing on record)",
        _ => "can't see the retainer clock (no ventures out, or not at a bell)",
      };
      return new FitCheck(FitVerdict.NoData, $"{blind} - firing without a fit check");
    }

    // 3. Two measured clocks. The padded estimate (ms) must clear the return (ms).
    var estText = ShortDur(est / 1000);
    var retText = ShortDur(ret);
    if (est * SafetyMargin <= ret * 1000L)
      return new FitCheck(FitVerdict.Fits,
        $"sweep est. ~{estText}, retainers return in ~{retText} - fits");
    return new FitCheck(FitVerdict.DoesntFit,
      $"sweep est. ~{estText}, retainers return in ~{retText} - wait for the haul and fold it into one run");
  }

  /// <summary>
  /// A duration said the way Sam says it: minutes under 90m, decimal hours past
  /// that, "&lt;1m" under a minute (never a bare "0m"). Shared by the message
  /// lines and the deck's "Start anyway" relabel so both read identically.
  /// </summary>
  internal static string ShortDur(long seconds)
  {
    var s = Math.Max(0, seconds);
    if (s < 60) return "<1m";
    if (s < 90 * 60) return $"{s / 60}m";
    return $"{s / 3600.0:0.#}h";
  }
}
