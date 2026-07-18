using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The routing thresholds as plain data, snapshotted once per batch from
/// Configuration by RoutingInputService.BeginBatch. The rules engine and the
/// listing gate read ONLY this — it is what keeps them pure functions a test
/// project can drive without Dalamud. Defaults mirror Configuration's.
/// </summary>
internal sealed record RoutingConfig
{
  public int ListingFloorGil { get; init; } = 15000;
  public int ListingVelocityDays { get; init; } = 10;
  public int ListingWorthGil { get; init; } = 5000;
  public int RoutingReviewBandPct { get; init; } = 15;
  /// <summary>Minimum DC-scope settled sales before community evidence counts (snapshot of LaneMinHistorySamples — same bar the lane uses).</summary>
  public int CommunityMinSamples { get; init; } = 3;
  public int VentureBandFull { get; init; } = 1250;
  public int VentureBandLow { get; init; } = 750;
  public int VentureBandPanic { get; init; } = 500;
  public double VenturePanicValueMultiplier { get; init; } = 3.0;
  /// <summary>
  /// The top band: "around 2k is cruisin". The saturation check tilts borderline
  /// calls AWAY from GC only when the 7-day PROJECTION (stock minus measured
  /// weekly burn) still clears this line - current stock is the wrong operand,
  /// where-will-I-be is the right one.
  /// </summary>
  public int VentureBandCruise { get; init; } = 2000;
}

/// <summary>
/// Batch-scoped context for routing evaluations: one DB pass for sales and
/// melt values, one venture-stock read, one config snapshot. Build once per
/// evaluation sweep (e.g. a Hawk window refresh), then Collect() per item is
/// pure lookups. Pure data — no game or storage references.
/// </summary>
internal sealed class RoutingBatch
{
  public Dictionary<(uint ItemId, bool IsHq), (int Price, long Timestamp, int? SoldAfterDays)> LastSales { get; init; } = [];
  public Dictionary<(uint ItemId, bool IsHq), long> MeltValues { get; init; } = [];
  /// <summary>Venture token stock, or null when the inventory read failed. Rules-engine input (venture tilt bands).</summary>
  public int? VentureStock { get; init; }
  /// <summary>
  /// Measured tokens burned over the trailing FULL week (gil_snapshots downward
  /// deltas), or null until a whole week of data exists. Whole-week windows make
  /// the weekday/weekend shape cancel out of the 7-day projection. No measurement
  /// = no saturation tilt (fail toward current behavior), never a config guess.
  /// </summary>
  public int? WeeklyVentureBurn { get; init; }
  /// <summary>Seals-to-gil rate for this batch: empirical (venture returns) when enough data exists, else the config placeholder.</summary>
  public int SealToGilRate { get; init; }
  /// <summary>True when SealToGilRate was measured from venture returns; false = config placeholder (reasons say so).</summary>
  public bool SealRateEmpirical { get; init; }
  /// <summary>The routing thresholds this batch evaluates under.</summary>
  public required RoutingConfig Rules { get; init; }
}

/// <summary>
/// Protection flags read off the inventory slot + gearset module at scan time.
/// Any set flag routes the item to Hold — never auto-routed.
/// </summary>
internal readonly record struct ItemProtections(bool InGearset, bool Spiritbond100, bool HasMateria)
{
  public bool Any => InGearset || Spiritbond100 || HasMateria;

  public string Describe()
  {
    if (!Any) return "";
    var parts = new List<string>(3);
    if (InGearset) parts.Add("in a gearset");
    if (Spiritbond100) parts.Add("spiritbond 100%");
    if (HasMateria) parts.Add("has materia");
    return string.Join(", ", parts);
  }
}

/// <summary>
/// Everything the routing rules need to know about one item variant.
/// Inputs only — no verdicts here. All evidence is local and free
/// (own sales, own yields, game sheets, player flags).
/// </summary>
internal sealed record RoutingItemInputs
{
  // Identity + sheet facts
  public required uint ItemId { get; init; }
  public required bool IsHq { get; init; }
  public string Name { get; init; } = "";
  public int Ilvl { get; init; }
  public bool IsEquipment { get; init; }
  /// <summary>Has a market search category — can be listed at all. Untradable gear's only exits are melt/GC/vendor.</summary>
  public bool IsMarketable { get; init; }
  public int VendorPrice { get; init; }

  // Evidence
  /// <summary>Own last sale for this variant (price, when, days listed before selling).</summary>
  public (int Price, long Timestamp, int? SoldAfterDays)? LastSale { get; init; }
  /// <summary>Yield gil per desynth attempt from the player's own ledger.</summary>
  public long? MeltValuePerAttempt { get; init; }
  /// <summary>GC Expert Delivery seals, when eligible.</summary>
  public int? SealValue { get; init; }

  /// <summary>
  /// Home-world sale velocity for this quality (units/day) from the
  /// Universalis almanac. Null = no trusted data (offline, stale past the
  /// trust window, unmarketable, or still fetching) — behave as before.
  /// </summary>
  public double? MarketVelocity { get; init; }
  /// <summary>Days since ANYONE last bought this item here (Universalis).</summary>
  public int? MarketLastSaleDays { get; init; }

  /// <summary>
  /// Median DC-scope settled sale price for this quality (Universalis
  /// community history). Null = no trusted data. Fills the hole where gear
  /// with no LOCAL sale can never produce a list score and seals win by
  /// forfeit — the DC's buyers are the missing witness.
  /// </summary>
  public long? CommunityMedian { get; init; }
  /// <summary>How many DC settled sales back that median (0 = none).</summary>
  public int CommunitySampleCount { get; init; }

  // Desynth skill state (null color = not desynthesizable / no repair class)
  public DesynthSkillupColor? DesynthColor { get; init; }
  public bool DesynthSkillupEligible { get; init; }

  // Player flags
  public bool IsBanned { get; init; }
  public bool IsAlwaysVendor { get; init; }

  // Protections (gearset / spiritbond / materia) — Hold pile material.
  // Slot-level facts the caller reads at scan time; default = unprotected.
  public bool IsProtected { get; init; }
  public string ProtectionReason { get; init; } = "";
}
