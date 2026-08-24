using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The venture stamp's pure half: what kind of venture a RetainerTask row is,
/// and what the stamped rows say a seal is worth in gil.
///
/// <para>Both halves are here rather than in <see cref="VentureReturns"/> because
/// both are arithmetic over primitives, and the file they'd otherwise live in is
/// welded to Lumina and the game's chat. Sheet lookup and DB reads stay on the
/// Dalamud side; the derivations they feed are linked-source testable.</para>
///
/// <para>The whole leg exists because of one ruling (Drift, 08-15): <b>we don't need
/// a mod knob for a thing we can directly measure in game.</b> The retired
/// VentureTokensPerVenture config value guessed "believed 2" at every venture's
/// token cost; the sheet knows the real number per venture, so the rate below is
/// measured, not configured.</para>
/// </summary>
internal static class VentureStamp
{
  /// <summary>Seals per venture token at the GC quartermaster.</summary>
  internal const int SealsPerToken = 200;

  /// <summary>Stamped rows needed before the measured rate is worth reporting.</summary>
  internal const int MinStampedVentures = 10;

  /// <summary>
  /// The coarse kind of a venture, from the RetainerTask row's IsRandom flag and
  /// (when random) the linked RetainerTaskRandom row's name.
  ///
  /// <para>Exploration ventures are named "&lt;Terrain&gt; Exploration"; the terrain
  /// word IS the category, so the name's first word lowercased carries it without a
  /// hardcoded list that a new expansion's terrain would silently fall out of.
  /// "Quick Exploration" is the one that isn't a terrain, so it is matched by name.
  /// Non-random rows are targeted item hunts.</para>
  ///
  /// <para>Null when the row claims random but no name resolved - an unstamped
  /// category is honest, and per the 08-15 ruling nothing downstream imputes it.</para>
  /// </summary>
  internal static string? Category(bool isRandom, string? randomTaskName)
  {
    if (!isRandom) return "targeted";
    if (string.IsNullOrWhiteSpace(randomTaskName)) return null;
    if (randomTaskName.Contains("Quick", System.StringComparison.OrdinalIgnoreCase))
      return "quick";
    var firstWord = randomTaskName.Trim().Split(' ')[0];
    return firstWord.Length == 0 ? null : firstWord.ToLowerInvariant();
  }

  /// <summary>
  /// The measured seals-to-gil rate: what the STAMPED ventures returned in gil,
  /// spread over the seals their tokens actually cost.
  ///
  /// <para>Unstamped rows (venture_cost NULL - captured before V42, or a stamp read
  /// that failed) are excluded from both halves of the fraction, value included.
  /// Counting their gil against a cost they never declared would inflate the rate by
  /// exactly the history we refuse to impute (ruled 08-15).</para>
  ///
  /// <para>Null until <see cref="MinStampedVentures"/> stamped rows exist, and null
  /// when the stamped costs total zero - a rate over no measured spend is a divide,
  /// not a finding.</para>
  /// </summary>
  internal static int? StampedSealToGilRate(IReadOnlyList<(long Value, int? VentureCost)> rows)
  {
    long value = 0, cost = 0;
    var stamped = 0;
    foreach (var r in rows)
    {
      if (r.VentureCost is not { } c) continue;
      stamped++;
      value += r.Value;
      cost += c;
    }

    if (stamped < MinStampedVentures) return null;
    var seals = cost * SealsPerToken;
    if (seals <= 0) return null;
    var rate = (int)(value / seals);
    return rate > 0 ? rate : null;
  }
}
