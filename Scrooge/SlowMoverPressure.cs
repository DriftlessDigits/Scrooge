using ECommons.DalamudServices;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The routing brain pointed at ALREADY-LISTED inventory. Rides the pinch
/// run - the motion that already happens - with zero added decisions.
///
/// The liveness diagnostic decides everything: the MB 14-day history is
/// already fetched per item during the pinch. Others' sales + my listing
/// sitting = my price is the blocker -> deepen the cut. Dead history =
/// nobody buys at any price -> cutting is value destruction -> skip the
/// pressure and flag the item for eviction (persistent triage, V12) with
/// the rules engine's verdict on where it should go instead.
/// </summary>
internal static class SlowMoverPressure
{
  // Per-retainer listing-age cache - one DB pass per retainer, not per item.
  private static Dictionary<(uint ItemId, bool IsHq), long>? _ages;
  private static string _agesRetainer = "";

  // Lazy evidence batch for eviction verdicts - built at most once per run.
  private static RoutingBatch? _evictBatch;

  /// <summary>Drops caches so the next pinch starts fresh.</summary>
  internal static void ResetRun()
  {
    _ages = null;
    _agesRetainer = "";
    _evictBatch = null;
  }

  /// <summary>
  /// Applies slow-mover pressure to a computed pinch price. Returns the
  /// (possibly deepened) price. May flag the item for eviction as a side
  /// effect - in that case the price comes back untouched.
  /// </summary>
  internal static int Apply(PricingItem item, int newPrice)
  {
    var cfg = Plugin.Configuration;
    if (AgeDays(item) is not int ageDays)
      return newPrice;

    var marketAlive = item.HistorySaleCount > 0;

    // Dead market at eviction age: cutting destroys value. Flag and hold.
    if (!marketAlive)
    {
      if (ageDays >= cfg.EvictAfterDays && !UniversalisContradicts(item))
        FlagEviction(item, ageDays);
      return newPrice;
    }

    // Alive market: my price is the blocker - deepen by the ladder.
    var pct = ageDays >= 14 ? cfg.PressureDeepenMaxPct
      : ageDays >= cfg.PressureAfterDays ? cfg.PressureDeepenPct
      : 0;
    if (pct <= 0)
      return newPrice;

    var deepened = (int)(newPrice * (100 - pct) / 100.0);
    // Never pressure below vendor - at that point eviction logic owns it.
    // VendorPrice 0 (unvendorable) is read as NO vendor floor, deliberately:
    // there is nothing to undercut against, and the pinch's own cut caps
    // still bound how deep the ladder can go.
    if (item.VendorPrice > 0 && deepened <= item.VendorPrice)
      return newPrice;

    Plugin.PinchRunLog?.AddEntry(Windows.ItemOutcome.Outlier,
      item.ItemName, $"Slow mover: listed {ageDays}d, market alive - deepened {pct}% ({newPrice:N0} → {deepened:N0})");
    return deepened;
  }

  /// <summary>
  /// Cross-checks a locally-dead market against the Universalis almanac
  /// before evicting: an empty 14-day history also happens on a failed MB
  /// fetch, and evicting a live market is the expensive mistake. A recent
  /// sale by anyone on the home world = someone IS buying — skip the flag
  /// this run. No almanac data = trust the local read as today.
  /// </summary>
  private static bool UniversalisContradicts(PricingItem item)
  {
    var market = UniversalisStats.TryGet(item.ItemId, item.IsHq);
    if (market?.LastSaleDaysAgo is not int saleDays
        || saleDays >= Plugin.Configuration.EvictAfterDays)
      return false;

    Svc.Log.Debug($"[Pressure] {item.ItemName}: local history empty but Universalis shows a sale {saleDays}d ago — evict skipped this run");
    return true;
  }

  /// <summary>Days this variant has sat listed on the item's retainer, or null when untracked.</summary>
  private static int? AgeDays(PricingItem item)
  {
    if (item.ItemId == 0 || string.IsNullOrEmpty(item.RetainerName))
      return null;

    if (_ages == null || _agesRetainer != item.RetainerName)
    {
      try { _ages = GilStorage.GetRetainerListingAges(item.RetainerName); }
      catch { _ages = []; }
      _agesRetainer = item.RetainerName;
    }

    if (!_ages.TryGetValue((item.ItemId, item.IsHq), out var firstSeen))
      return null;

    var days = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - firstSeen) / 86400;
    return days >= 0 ? (int)days : null;
  }

  /// <summary>
  /// Persistent eviction flag (V12 dedup-on-open keeps repeat pinches from
  /// stacking duplicates). Detail carries the rules engine's verdict so the
  /// triage row already says where the item should go.
  /// </summary>
  private static void FlagEviction(PricingItem item, int ageDays)
  {
    var exitText = "your call";
    try
    {
      _evictBatch ??= RoutingInputService.BeginBatch();
      if (RoutingInputService.Collect(_evictBatch, item.ItemId, item.IsHq) is { } inputs)
      {
        var verdict = RoutingRules.Evaluate(inputs, _evictBatch);
        exitText = verdict.IsReview
          ? $"Review - {verdict.Reason}"
          : $"{verdict.Exit} - {verdict.Reason}";
      }
    }
    catch { /* evidence unavailable - flag still lands, just unrouted */ }

    try
    {
      GilStorage.UpsertTriageFlag(item.ItemId, item.IsHq, item.RetainerName,
        item.SlotIndex, "slow_evict",
        $"Evict: listed {ageDays}d, no MB sales in 14d. Router: {exitText}",
        item.CurrentListingPrice ?? 0, 0);
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Pressure] Failed to persist eviction flag: {ex.Message}");
    }
  }
}
