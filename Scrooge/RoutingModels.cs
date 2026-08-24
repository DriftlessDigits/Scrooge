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
  public int RoutingReviewBandPct { get; init; } = 15;
  /// <summary>Minimum DC-scope settled sales before community evidence counts (snapshot of LaneMinHistorySamples — same bar the lane uses).</summary>
  public int CommunityMinSamples { get; init; } = 3;
  // LocalSaleStaleDays used to sit here as an init property with a default of 14 that
  // BeginBatch never copied - so the record default ALWAYS ran, against a field that
  // did not exist on Configuration at all. It behaved correctly by accident, which is
  // the worst kind of fossil: a knob-shaped thing nobody could set, defended by nothing
  // but the omission that made it work. Promoted to an honest named constant on the
  // rule that reads it (RoutingRules.LocalSaleSeniorityDays, 3b-8 / RULED B1.5b).
  public int VentureBandFull { get; init; } = 1250;
  public int VentureBandLow { get; init; } = 750;
  public int VentureBandPanic { get; init; } = 500;
  // VentureBandCruise ("around 2k is cruisin") retired with the saturation
  // tilt (2026-08-05): the seal S-curve's continuous devaluation covers
  // everything the projection tie-break did - and its 2k lives on as the
  // curve's own center.
  /// <summary>
  /// THE SEAL S-CURVE's anchors (Drift, 2026-08-05): at or below FullBelow, seals
  /// score at full value; at or above ZeroAbove, at nothing; smoothstep between
  /// (center = the midpoint, Drift's 2k pivot). Stock-anchored on purpose - Drift's
  /// dial was always tokens, and the goal is a thermostat that keeps the
  /// stockpile hovering near the center. See <see cref="SealRunway"/>.
  /// </summary>
  public int SealCurveFullBelow { get; init; } = 1_000;
  /// <summary>The curve's melt line - see <see cref="SealCurveFullBelow"/>.</summary>
  public int SealCurveZeroAbove { get; init; } = 3_000;
  /// <summary>
  /// What a skillup is WORTH in gil (Drift's iteration 07-18: price the skillup,
  /// don't gate it). A skillup-eligible item's desynth candidate scores at least
  /// this, then competes in the ordinary value comparison - "very-very-high gil
  /// beats a skillup" becomes emergent instead of a special-case threshold, and
  /// near-worth sales land in Review like any honest coin flip. Red is rarer
  /// than yellow, so it is worth more.
  /// </summary>
  public int SkillupWorthYellow { get; init; } = 50_000;
  public int SkillupWorthRed { get; init; } = 100_000;

  /// <summary>
  /// THE ONE FLOOR LAW at the router's door (ruled 2026-08-21). The rules engine asks
  /// the same question the pinch asks - does the honest ask clear
  /// <c>max(MinimumListingPrice, PriceFloor.For(mode, vendor))</c> - so the Ledger can
  /// never propose a List exit the pinch would then refuse to write.
  /// </summary>
  public PriceFloorMode FloorMode { get; init; } = PriceFloorMode.None;

  /// <summary>The player's own floor, snapshotted with the mode. 0 = disabled.</summary>
  public int MinimumListingPrice { get; init; }
}

/// <summary>
/// Batch-scoped context for routing evaluations: one DB pass for sales and
/// melt values, one venture-stock read, one config snapshot. Build once per
/// evaluation round (e.g. a Hawk window refresh), then Collect() per item is
/// pure lookups. Pure data — no game or storage references.
/// </summary>
internal sealed class RoutingBatch
{
  /// <summary>
  /// THE BOOK IS STILL NEW (ruled 08-22): too few banked settled sales AND too
  /// few warmed almanac answers for silence to testify. One batch-level fact -
  /// see <see cref="BoardConfidence.IsNewBook"/> for the epistemology. Default
  /// false (experienced), which is also the fail-soft when the counts cannot be
  /// read: a broken storage layer must not demote a mature install's verdicts.
  /// </summary>
  public bool BookIsNew { get; init; }

  public Dictionary<(uint ItemId, bool IsHq), (int Price, long Timestamp, int? SoldAfterDays)> LastSales { get; init; } = [];
  public Dictionary<(uint ItemId, bool IsHq), long> MeltValues { get; init; } = [];
  /// <summary>
  /// The ilvl-banded melt prior (THE MELT PRIOR, 07-25), rolled up ONCE per batch
  /// from the same yield rollup <see cref="MeltValues"/> is built from. Null when
  /// the almanac was unreadable - no prior, and melt falls back to null exactly as
  /// it did before. Per-item lookups against it are a dictionary hit.
  /// </summary>
  public MeltPriorTable? MeltPriors { get; init; }
  /// <summary>Venture token stock, or null when the inventory read failed. The seal curve's one operand (and the tilt bands').</summary>
  public int? VentureStock { get; init; }
  /// <summary>Seals-to-gil rate for this batch: empirical (venture returns) when enough data exists, else the config placeholder.</summary>
  public int SealToGilRate { get; init; }
  /// <summary>True when SealToGilRate was measured from venture returns; false = config placeholder (reasons say so).</summary>
  public bool SealRateEmpirical { get; init; }
  /// <summary>The routing thresholds this batch evaluates under.</summary>
  public required RoutingConfig Rules { get; init; }
  /// <summary>
  /// The batch's wall clock, unix seconds - stamped once by BeginBatch so every
  /// item's sale-age reads against the same instant (and pure tests can pin it).
  /// 0 = unstamped: ages read null and staleness never fires, the pre-SF-B2 shape.
  /// </summary>
  public long NowUnix { get; init; }
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
  /// <summary>
  /// The sheet's Desynth flag is non-zero — the item can be melted at all
  /// (finding #18: current-tier raid gear carries a repair class but Desynth=0,
  /// so it CANNOT desynth). The faithful melt-eligibility signal, independent of
  /// whether any yield history exists yet. Closes the desynth exit for the
  /// zero-exit test when false.
  /// </summary>
  public bool IsDesynthable { get; init; }
  public int VendorPrice { get; init; }

  // Evidence
  /// <summary>Own last sale for this variant (price, when, days listed before selling).</summary>
  public (int Price, long Timestamp, int? SoldAfterDays)? LastSale { get; init; }
  /// <summary>
  /// How many days ago that sale settled, against the batch's clock. Null when
  /// there is no sale or the batch is unstamped. The community-veto gate reads
  /// this (SF-B2): a sale past <see cref="RoutingRules.LocalSaleSeniorityDays"/> no
  /// longer blocks the DC.
  /// </summary>
  public int? LastSaleAgeDays { get; init; }
  /// <summary>Yield gil per desynth attempt from the player's own ledger.</summary>
  public long? MeltValuePerAttempt { get; init; }
  /// <summary>
  /// The ilvl band's expected melt value, for a DESYNTHABLE item the ledger has no
  /// history for. Strictly subordinate to <see cref="MeltValuePerAttempt"/> - the
  /// same seed-then-measured arc as the seal rate, where the measurement supersedes
  /// the estimate the moment it exists. Null when the item is not desynthable, or
  /// when its band could not clear the evidence floor.
  /// </summary>
  public MeltPrior? MeltBandPrior { get; init; }
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
  /// <summary>
  /// THE SETTLED TAPE'S READ (F6, ruled 08-22): the recency-weighted median of
  /// THIS WORLD's banked settled sales for this quality - everyone's sales, the
  /// same sale_history ring the lane already prices from, built with the lane's
  /// own age weighting (no hard cutoff: age decays authority, never flips the
  /// sign - a stale 100k tape is a slow 100k item, not a vendor row). The
  /// router's List witness between the own sale and the DC: local outranks DC
  /// (F4's precedence law), and the tape was already trusted to BUILD prices -
  /// refusing to let it SCORE was the accuser-not-witness defect (the 75-gil
  /// Cotton Cloth referred over a table routing declined to read).
  /// </summary>
  public long? LocalTapeMedian { get; init; }
  /// <summary>How many banked local settled sales back that median (0 = none).</summary>
  public int LocalTapeSampleCount { get; init; }
  /// <summary>The tape's recency-weighted evidence age in days, for narration.</summary>
  public double? LocalTapeAgeDays { get; init; }

  /// <summary>
  /// THE LOOK'S TESTIMONY (ruled 08-23, the Nightsteel Sword): the banked recon
  /// decision's would-ask, when a FRESH Look exists (the ONE definition of fresh -
  /// <see cref="ReconFreshness"/> on <c>ReconFreshHours</c>, the same rule the
  /// hawk's cached post already acts on). Recon runs the whole pricing spine on a
  /// real board; refusing to let its answer testify in routing was the Nightsteel
  /// defect - a 5-seller queue at 20,456+ lost to 1,450 seals with List never
  /// scored, "Unanimous" over a lane that never voted. Best available evidence
  /// includes the machine's own freshest read (Drift's correction of the F6
  /// ladder's too-literal reading). Null = no fresh Look, or the Look HELD.
  /// </summary>
  public long? LookAsk { get; init; }
  /// <summary>That Look's age in hours, for the reason line. Null with LookAsk null.</summary>
  public double? LookAgeHours { get; init; }

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
