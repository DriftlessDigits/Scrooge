using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// The pinch's vendor rider (WALK unit 3): pure row-selection for the Pull &amp;
/// Vendor rows that ride a pinch instead of waiting to be clicked as a separate
/// errand. Vendor is a rider, not a stage - the unanimous Pull &amp; Vendor rows
/// for a retainer are pulled and vendored inside THAT retainer's pinch visit, so
/// the 07-22 "rows ride along forever" bug dies without a sixth sweep stage.
///
/// This is only the decision half - which rows ride, grouped by the retainer
/// whose visit executes them. The execution (pull the listing, vendor the item)
/// lives in the pinch's task chain (AutoPinch). Kept Dalamud-free and linked into
/// the test project: the selection rule is the one thing worth pinning down, and
/// it is exactly the rule every bulk button obeys.
///
/// UNANIMOUS ONLY, by construction: <see cref="Riders{T}"/> is
/// <see cref="LedgerConfidence.BulkSet{T}(IEnumerable{ValueTuple{T, ConfidenceTier}})"/>,
/// the same confidence gate the manual Pull &amp; Vendor button uses. Mixed and
/// demoted rows are NOT the rider's business - they stay in the pile for the
/// player's judgment (the 07-18 lesson: every pile button builds on BulkSet or it
/// is wrong by default).
/// </summary>
internal static class VendorRider
{
  /// <summary>
  /// The rows that ride the pinch: only the Unanimous-tier candidates, drawn from
  /// the pile's own confidence gate. An empty or all-Mixed candidate set returns
  /// an empty list - the rider stays silent.
  /// </summary>
  internal static List<T> Riders<T>(IEnumerable<(T Item, ConfidenceTier Tier)> candidates)
    => LedgerConfidence.BulkSet(candidates);

  /// <summary>
  /// The riders grouped by the retainer whose visit will execute them (per-retainer
  /// grouping is how the rider weaves into the pinch's per-retainer task chain).
  /// Retainers with no riding rows never appear - the empty map means the rider
  /// stays silent everywhere.
  /// </summary>
  internal static Dictionary<string, List<T>> ByRetainer<T>(
    IEnumerable<(T Item, ConfidenceTier Tier)> candidates, Func<T, string> retainerOf)
  {
    var map = new Dictionary<string, List<T>>(StringComparer.Ordinal);
    foreach (var item in Riders(candidates))
    {
      var name = retainerOf(item);
      if (!map.TryGetValue(name, out var list))
        map[name] = list = new List<T>();
      list.Add(item);
    }
    return map;
  }

  /// <summary>The riders for one retainer's visit; empty when none ride there.</summary>
  internal static List<T> ForRetainer<T>(
    IEnumerable<(T Item, ConfidenceTier Tier)> candidates, Func<T, string> retainerOf, string retainerName)
    => Riders(candidates)
       .Where(i => string.Equals(retainerOf(i), retainerName, StringComparison.Ordinal))
       .ToList();
}
