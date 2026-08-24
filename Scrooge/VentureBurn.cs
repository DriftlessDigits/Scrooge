using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The weekly venture-token burn, measured from wallet snapshots - the pure
/// core (no storage, no clock; linked into Scrooge.Tests). The burn is the sum
/// of every DROP between consecutive snapshots; rises are restocks and belong
/// to a different story.
///
/// <para><b>The bracketed window.</b> The first cut measured only snapshots
/// INSIDE the trailing week and gated on the oldest one being 6.5 days old -
/// so a quiet morning at the window's left edge shrank coverage below the gate
/// and the readout flickered "unmeasured" over 26 days of banked history
/// (08-06). The fix is structural, not a looser gate: the last snapshot AT OR
/// BEFORE the window opens seeds the walk, which both anchors coverage at the
/// full week and catches a burn that happened across the boundary. The gate
/// itself is untouched - a genuinely young install still says "needs 6.5d"
/// honestly.</para>
/// </summary>
internal static class VentureBurn
{
  /// <summary>The coverage gate: the measure must span most of the week it claims.</summary>
  internal const double MinCoverageDays = 6.5;

  /// <summary>
  /// The week's burn, or null when coverage can't honestly claim a week.
  /// </summary>
  /// <param name="seedTokens">The counter at the last look at or before the
  /// window opened - the bracket. Null when no such look exists (young install).</param>
  /// <param name="inWindow">Snapshots inside the window, ascending by time.</param>
  /// <param name="now">The right edge of the window.</param>
  /// <param name="windowStart">The left edge - where the seed anchors coverage.</param>
  internal static int? Measure(int? seedTokens, IReadOnlyList<(long At, int Tokens)> inWindow,
    long now, long windowStart)
  {
    int? prev = seedTokens;
    long? coverageFrom = seedTokens is not null ? windowStart : null;
    var burn = 0;
    foreach (var (at, tokens) in inWindow)
    {
      coverageFrom ??= at;
      if (prev is int p && tokens < p)
        burn += p - tokens;
      prev = tokens;
    }

    if (coverageFrom is not long first || now - first < (long)(MinCoverageDays * 86400))
      return null;
    return burn;
  }
}
