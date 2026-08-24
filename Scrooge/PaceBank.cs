using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// HOW FAST A RUN WENT, learned. Every finished run measures one number — milliseconds
/// per item — and banks it two ways: into an overall seed (what a pinch quotes before
/// it has processed anything) and into its own stage's key (what
/// <see cref="StageRail"/> quotes per row). Both are the same exponential blend, and
/// both used to be written out longhand inside the Ledger window's end-of-run method,
/// with the 0.7/0.3 weights spelled twice and a settings save fired after each.
///
/// <para>That is a learning model living in a draw file. It is arithmetic — no addon,
/// no clock, no config type — so it is here instead, linked into Scrooge.Tests, where
/// the blend can be pinned rather than eyeballed.</para>
///
/// <para>The window keeps exactly two things: applying the update to
/// <c>Plugin.Configuration</c> and the ONE save afterwards. Configuration is a Dalamud
/// type and cannot cross into the test project, so the values come in as plain
/// numbers and the update goes back out as plain numbers.</para>
/// </summary>
internal static class PaceBank
{
  /// <summary>
  /// How much of the banked pace survives a new measurement. 0.7 prior / 0.3 sample:
  /// slow to move on one weird run, quick enough to follow a real change over a
  /// handful. The one place this number exists.
  /// </summary>
  internal const float PriorWeight = 0.7f;

  /// <summary>
  /// The banked pace after folding in one fresh measurement. A prior of zero (or
  /// less) is not a measurement at all — it is "never run" — so the first sample is
  /// adopted whole rather than blended against a phantom.
  /// </summary>
  internal static float Blend(float prior, float sample)
    => prior <= 0f ? sample : (prior * PriorWeight) + (sample * (1f - PriorWeight));

  /// <summary>
  /// Whether this run may move the OVERALL seed. GC turn-in and the coffer rider
  /// have cadences nothing like a pinch/hawk pass over items, and folding them in
  /// poisons the number a pinch quotes before its first item. They still learn their
  /// own stage key — that exclusion is why the GC run had no measured pace at all
  /// until the per-stage bank existed.
  /// </summary>
  internal static bool CountsTowardOverall(bool isGcRun, bool isCofferRun)
    => !isGcRun && !isCofferRun;

  /// <summary>
  /// What a finished run teaches, or <see cref="PaceUpdate.None"/> when it teaches
  /// nothing (no items processed, so there is no per-item pace to measure).
  ///
  /// <para><paramref name="stageKey"/> is null when the run is not a round stage
  /// (the coffer rider rides ahead of the bell and is not one); only the overall seed
  /// moves then. <paramref name="stagePaces"/> is read, never written — the caller
  /// applies the returned update.</para>
  /// </summary>
  internal static PaceUpdate Record(
    long elapsedMs, int itemsProcessed,
    bool isGcRun, bool isCofferRun,
    string? stageKey,
    float overallPrior,
    IReadOnlyDictionary<string, float> stagePaces)
  {
    if (itemsProcessed <= 0) return PaceUpdate.None;

    var pace = (float)elapsedMs / itemsProcessed;

    float? overall = CountsTowardOverall(isGcRun, isCofferRun)
      ? Blend(overallPrior, pace)
      : null;

    (string Key, float Value)? stage = null;
    if (stageKey != null)
    {
      stagePaces.TryGetValue(stageKey, out var prior);
      stage = (stageKey, Blend(prior, pace));
    }

    return new PaceUpdate(overall, stage);
  }

  /// <summary>
  /// The two banked numbers a finished run moved, each null when it moved nothing.
  /// <see cref="Any"/> is what decides whether the settings file is written at all —
  /// one save for the pair, where there used to be one per blend.
  /// </summary>
  internal readonly record struct PaceUpdate(float? Overall, (string Key, float Value)? Stage)
  {
    /// <summary>A run that taught nothing.</summary>
    internal static PaceUpdate None => new(null, null);

    /// <summary>True when something changed and the bank is worth persisting.</summary>
    internal bool Any => Overall != null || Stage != null;
  }
}
