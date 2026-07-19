using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// Pure core for the Ledger's LISTED-ITEM rows (M6 session 3). Renders from
/// CAPTURED data only - the listings table, settled-sale lanes, market events -
/// never a fresh game read. In the LanePricing/Ledger mold: no game reads, no
/// storage, no Dalamud statics, linked into Scrooge.Tests. The window builds the
/// inputs off real facts and asks this core for the labels, the outlier/vendor
/// verdicts, the Watch category, and the contradiction note.
/// </summary>

/// <summary>Age-tier of a standing listing, by config bands.</summary>
internal enum ListedAgeTier { Fresh, Aging, Stale }

/// <summary>Stale-tier bands for a standing listing (config-snapshotted).</summary>
internal sealed record ListedAgeConfig
{
  /// <summary>At or past this many days on the board, a listing reads as aging.</summary>
  public int AgingDays { get; init; } = 7;
  /// <summary>At or past this many days, a listing reads as stale.</summary>
  public int StaleDays { get; init; } = 30;
}

/// <summary>
/// One own standing listing, projected off the listings table for the Listed
/// pile. A minimal captured-data record (NOT ListingRecord, which lives in the
/// Dalamud-side model) so the grouping/totals stay unit-testable.
/// </summary>
internal readonly record struct ListedLine(
  string Retainer,
  uint ItemId,
  string ItemName,
  bool IsHq,
  long UnitPrice,
  int Quantity,
  long FirstSeen);

/// <summary>Per-retainer roll-up for a collapsible Listed group: count + gil at ask.</summary>
internal readonly record struct RetainerListedTotals(string Retainer, int Count, long GilAtAsk);

internal static class LedgerListings
{
  // ========================================================================
  // Item 1 - per-retainer grouping + totals (gil at ask)
  // ========================================================================

  /// <summary>
  /// Groups own standing listings into per-retainer roll-ups, richest group
  /// first. Gil-at-ask is the unit price times the stack - what the board is
  /// asking if every listing cleared at its current price.
  /// </summary>
  internal static List<RetainerListedTotals> GroupByRetainer(IEnumerable<ListedLine> listings)
    => listings
      .GroupBy(l => l.Retainer)
      .Select(g => new RetainerListedTotals(
        g.Key,
        g.Count(),
        g.Sum(l => l.UnitPrice * Math.Max(1, l.Quantity))))
      .OrderByDescending(r => r.GilAtAsk)
      .ThenBy(r => r.Retainer, StringComparer.Ordinal)
      .ToList();

  // ========================================================================
  // Item 2 - honest age labels (no invented precision)
  // ========================================================================

  /// <summary>Whole days a listing has been on the board since first_seen (never negative).</summary>
  internal static int AgeDays(long firstSeen, long now)
    => (int)Math.Max(0, (now - firstSeen) / 86400);

  /// <summary>Age-tier of a listing under the config bands.</summary>
  internal static ListedAgeTier Tier(int ageDays, ListedAgeConfig cfg)
    => ageDays >= cfg.StaleDays ? ListedAgeTier.Stale
     : ageDays >= cfg.AgingDays ? ListedAgeTier.Aging
     : ListedAgeTier.Fresh;

  /// <summary>
  /// Whether a first_seen is an EXACT age or only a lower bound. A listing that
  /// was already present at our first-ever board observation cannot have a true
  /// age we measured - we can't see before we started looking - so its age is a
  /// floor (">="). Anything first seen strictly after we began observing is exact.
  /// Structural, not a per-row migration marker (the tripwire forbids new writes).
  /// </summary>
  internal static bool AgeIsExact(long firstSeen, long firstObservationEver)
    => firstObservationEver <= 0 || firstSeen > firstObservationEver;

  /// <summary>
  /// The honest age label: "3d listed" when the age is measured, ">=3d listed"
  /// when first_seen is only a lower bound (a migration/first-scan backfill).
  /// Never invents precision the data does not have.
  /// </summary>
  internal static string AgeLabel(int ageDays, bool exact)
    => exact ? $"{ageDays}d listed" : $">={ageDays}d listed";

  // ========================================================================
  // Item 3 - outlier own-listing detection (the Highland Fence smell)
  // ========================================================================

  /// <summary>
  /// An own listing priced at or above the wall boundary (median x CeilingMult)
  /// is a self-inflicted wall: it sits unsold for months because nothing ever
  /// LOOKED at existing listings (the 55M-for-months case). Needs a real lane -
  /// a thin lane cannot say a price is an outlier.
  /// </summary>
  internal static bool IsOutlierListing(long listedPrice, LaneModel lane, LaneConfig cfg)
    => lane.SampleCount >= cfg.MinHistorySamples
       && lane.Median > 0
       && listedPrice >= lane.Median * cfg.CeilingMult;

  /// <summary>The Review-row reason for an outlier own listing - names the gap.</summary>
  internal static string OutlierReason(long listedPrice, LaneModel lane)
  {
    var going = (long)Math.Round(lane.Median);
    var mult = lane.Median > 0 ? listedPrice / lane.Median : 0;
    return $"Listed at {listedPrice:N0} but this sells around {going:N0} - about {mult:0.#}x the going rate. "
         + "It has been sitting because nothing looked at it. Pull and reprice, or vendor.";
  }

  // ========================================================================
  // Item 4 - vendor-floor pull-forward (vendor pays more than the board)
  // ========================================================================

  /// <summary>
  /// True when the lane's own clearing price is at or below the NPC vendor price:
  /// the board can never beat the vendor, so keep-listing is strictly worse than
  /// pull-and-vendor. Needs a positive vendor price to compare against.
  /// </summary>
  internal static bool VendorBeatsBoard(long lanePrice, int vendorPrice)
    => vendorPrice > 0 && lanePrice <= vendorPrice;

  /// <summary>The Pull-and-Vendor reason when the vendor beats the board.</summary>
  internal static string VendorFloorReason(long lanePrice, int vendorPrice)
    => $"The vendor pays {vendorPrice:N0}, the board only clears ~{lanePrice:N0} - "
     + "never keep listing what the vendor beats. Pull and vendor.";

  // ========================================================================
  // Item 5 - Watch-pile categorization
  // ========================================================================

  /// <summary>
  /// A standing listing's Watch category from its lane outcome. races = waiting
  /// at the floor under a race to the bottom; bait = anchored under ignored walls
  /// (wall-anchored board); thin = history too thin to price. Everything else
  /// (owned lanes, protected holds, bans) is Other.
  /// </summary>
  internal static WatchCategory CategorizeWatch(LaneOutcome outcome) => outcome switch
  {
    LaneOutcome.RaceDeclined => WatchCategory.Race,
    LaneOutcome.WallIgnored => WatchCategory.Bait,
    LaneOutcome.HeldThinHistory => WatchCategory.Thin,
    _ => WatchCategory.Other,
  };

  /// <summary>
  /// Watch category from a persisted flag's machine reason (triage_flags.reason)
  /// when no live lane outcome is in hand - the held-flag path.
  /// </summary>
  internal static WatchCategory CategorizeWatchReason(string reason) => reason switch
  {
    "lane_held" => WatchCategory.Thin,
    "race_declined" => WatchCategory.Race,
    "wall_ignored" or "outlier_warn" => WatchCategory.Bait,
    _ => WatchCategory.Other,
  };

  // ========================================================================
  // Item 8 addendum - a Contradicted row must STATE its objection inline
  // ========================================================================

  /// <summary>
  /// The market evidence that overruled a Contradicted verdict, spelled inline -
  /// the deciding number can no longer be invisible behind a bare "!" badge.
  /// "...but the DC pays ~X on N sales / moves ~Y/day". Empty when no market
  /// evidence backs the contradiction (the badge stands alone).
  /// <paramref name="payer"/> names the evidence's PROVENANCE honestly: "the DC
  /// pays" for community history, "settled sales pay" for local lanes - the
  /// note must never dress local numbers as DC-wide ones.
  /// </summary>
  internal static string ContradictionNote(long? median, int sales, double? velocityPerDay,
    string payer = "the DC pays")
  {
    var parts = new List<string>(2);
    if (median is long m && m > 0 && sales > 0)
      parts.Add($"{payer} ~{m:N0} on {sales} sale{(sales == 1 ? "" : "s")}");
    if (velocityPerDay is double v && v > 0)
      parts.Add($"moves ~{v:0.##}/day");
    return parts.Count > 0 ? $"...but {string.Join(" / ", parts)}." : "";
  }
}
