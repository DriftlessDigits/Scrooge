using System;

namespace Scrooge;

/// <summary>
/// The executors' per-action humanizer, written once. Deliberately independent of
/// <c>Configuration.EnableJitter</c> (a bell-scoped knob that defaults off):
/// pacing is "non-negotiable" per the spec - it must not be coupled to a
/// plugin-wide toggle a player might flip for unrelated reasons. Bands stay at the
/// call sites, which is where the spec's "Pacing and humanization" table reads.
///
/// <para>The pinch's own <c>ApplyJitter</c> is NOT this rule and does not live here:
/// it is config-gated and floored at 1000ms.</para>
/// </summary>
internal static class Pacing
{
  /// <summary>Base +- uniform jitter, floored at 1ms. The caller lends its randomness.</summary>
  internal static int Jitter(Random rng, int baseMs, int band)
  {
    var offset = (int)(((rng.NextDouble() * 2.0) - 1.0) * band);
    return Math.Max(1, baseMs + offset);
  }
}
