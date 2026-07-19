using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// Determines how the undercut price is calculated relative to the lowest MB listing.
/// </summary>
public enum UndercutMode
{
  /// <summary>Subtract a fixed gil amount from the lowest listing.</summary>
  FixedAmount,
  /// <summary>Subtract a percentage of the lowest listing's price.</summary>
  Percentage,
  /// <summary>Match the lowest listing exactly — no undercut.</summary>
  GentlemansMatch,
  /// <summary>Undercut by rounding down to a clean number. Interval scales with price.</summary>
  CleanNumbers,
  /// <summary>Randomly picks Random Pinch, Gentleman's Match, or Clean Numbers per item.</summary>
  Humanized
}

/// <summary>
/// Determines the minimum price floor when listing items.
/// Items priced below the floor are skipped during auto-pinch.
/// </summary>
public enum PriceFloorMode
{
  /// <summary>No price floor — list at any price.</summary>
  None,
  /// <summary>Skip if undercut price falls below vendor sell price (Item.PriceLow).</summary>
  Vendor,
  /// <summary>Skip if undercut price falls below 2x vendor sell price (Doman Enclave rate).</summary>
  DomanEnclave
}

/// <summary>
/// Persisted plugin configuration. Serialized to JSON by Dalamud.
/// Default values are used both for new installs and when deserializing
/// older configs that are missing newly added properties.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
  public int Version { get; set; } = 0;

  // --- Pricing behavior ---

  /// <summary>Use HQ price when the listed item is HQ.</summary>
  public bool HQ { get; set; } = true;

  public UndercutMode UndercutMode { get; set; } = UndercutMode.FixedAmount;

  /// <summary>
  /// Amount to undercut by. Interpreted as gil (FixedAmount) or percent (Percentage).
  /// Ignored in GentlemansMatch mode.
  /// </summary>
  public int UndercutAmount { get; set; } = 1;

  /// <summary>
  /// Safety cap: skip the item if the undercut would exceed this percentage.
  /// Prevents catastrophic price drops from outlier listings.
  /// </summary>
  public float MaxUndercutPercentage { get; set; } = 100.0f;

  /// <summary>
  /// When enabled, skips items where the new price would exceed the current
  /// listing price by more than MaxPriceIncreasePercentage.
  /// </summary>
  public bool EnableMaxPriceIncreaseCap { get; set; } = false;

  /// <summary>
  /// Safety cap: skip the item if the new price would increase by more than this percentage.
  /// Prevents overpricing when competition delists and the next listing is much higher.
  /// </summary>
  public float MaxPriceIncreasePercentage { get; set; } = 50.0f;

  /// <summary>Max random undercut in gil when Humanized mode rolls Random Pinch. Range: 1–10.</summary>
  public int HumanizedMaxPinch { get; set; } = 3;

  /// <summary>If true, undercut your own retainer listings too.</summary>
  public bool UndercutSelf { get; set; } = false;

  /// <summary>
  /// Determines the price floor check mode. None disables the check (default),
  /// Vendor skips items below vendor sell price,
  /// DomanEnclave skips items below 2x vendor sell price.
  /// </summary>
  public PriceFloorMode PriceFloorMode { get; set; } = PriceFloorMode.None;

  /// <summary>
  /// Minimum gil price for any listing. Items below this price are skipped.
  /// Set to 0 to disable (default).
  /// </summary>
  public int MinimumListingPrice { get; set; } = 0;

  // --- Lane pricing ---
  // "Listings are what people want; sales are what people paid." The lane
  // (recency-weighted clearing price from settled sales) is the pricing model;
  // the board is positioning only. Replaced outlier/gap-geometry detection
  // (deleted 2026-07-13 per the lane design - no dormant fallback).

  /// <summary>
  /// Lane ceiling multiplier (promoted from the old upward-reprice sanity):
  /// board listings above (lane median x this) are walls and never anchor a
  /// price. One idea system-wide: 3x what it actually sells for = suspicious,
  /// in every direction.
  /// </summary>
  public float UpwardRepriceMultiplier { get; set; } = 3.0f;

  /// <summary>Lane floor: listings below (lane median x this) are bait and never anchor a price.</summary>
  public float LaneFloorPct { get; set; } = 0.5f;

  /// <summary>
  /// When we own the lane (no in-lane competition, all walls or empty board),
  /// the anchor is lane median x this. A premium above the going rate, but not
  /// the ceiling: the UpwardRepriceMultiplier ceiling stays the hard cap.
  /// Clamped to a sane 1.0-3.0 range.
  /// </summary>
  public float LaneOwnedMultiplier { get; set; } = 2.0f;

  /// <summary>
  /// Minimum settled sales needed to build a lane. Below this the lane
  /// abstains: hold-and-flag instead of pricing off an unvalidated board.
  /// </summary>
  public int LaneMinHistorySamples { get; set; } = 3;

  /// <summary>
  /// Recency half-life (days) for lane weighting. SEED value - resolver v0
  /// returns this for every item; receipts derive per-item values later.
  /// Seeded 30d from the 2026-07-13 sale-age query (median lane evidence ~42d
  /// old; erring long fails toward holding).
  /// </summary>
  public float LaneHalfLifeDays { get; set; } = 30f;

  // --- Timing ---

  /// <summary>When enabled, adds a jitter element to simulate human interaction.</summary>
  public bool EnableJitter { get; set; } = false;

  /// <summary>Random ± variance in ms applied to configurable delays. Slider range: 500–3500.</summary>
  public int JitterMS { get; set; } = 2000;

  /// <summary>Delay before opening the MB price list. Too low = prices fail to load.</summary>
  public int GetMBPricesDelayMS { get; set; } = 5000;

  /// <summary>How long to keep the MB open when fetching prices.</summary>
  public int MarketBoardKeepOpenMS { get; set; } = 3000;

  // --- Desynth automation ---

  /// <summary>Master toggle for the desynth preview launcher overlay.</summary>
  public bool EnableDesynthPreview { get; set; } = true;

  /// <summary>
  /// Highlight items as yellow when the player's desynth level is above the item's
  /// level but still within the +50 skillup range. Matches the SimpleTweaks default.
  /// </summary>
  public bool YellowForSkillGain { get; set; } = true;

  /// <summary>
  /// Inject randomized 3–8s pauses every 8–15 items during a desynth run.
  /// Highest-value humanization tactic — bots almost never pause spontaneously.
  /// </summary>
  public bool DesynthHumanPauses { get; set; } = true;

  /// <summary>
  /// Base delay between item selections in a desynth run, in ms. Jittered ±400ms.
  /// 1500ms is the floor; raise if a run still feels too fast.
  /// </summary>
  public int DesynthPerActionBaseMs { get; set; } = 1500;

  /// <summary>
  /// Ceiling for waits that span a server round trip (desynth Confirm ->
  /// SalvageResult, and future sites of the same shape), in ms. One shared
  /// knob, not per-site bumps: a laggy server slows every round trip the
  /// same way. The 2026-07-19 02:38 abort was SalvageResult taking >4000ms
  /// at a laggy hour; UI-local waits (SalvageDialog) keep their own ceiling.
  /// </summary>
  public int ServerRoundTripCeilingMs { get; set; } = 10_000;

  // --- Hotkeys ---

  /// <summary>Enable hotkey to start auto-pinch from the retainer sell list.</summary>
  public bool EnablePinchKey { get; set; } = false;

  public VirtualKey PinchKey { get; set; } = VirtualKey.Q;

  /// <summary>Enable hotkey to auto-pinch when posting a new item.</summary>
  public bool EnablePostPinchkey { get; set; } = true;

  public VirtualKey PostPinchKey { get; set; } = VirtualKey.SHIFT;

  // --- Chat output ---

  public bool ShowErrorsInChat { get; set; } = true;

  public bool ShowPriceAdjustmentsMessages { get; set; } = true;

  public bool ShowRetainerNames { get; set; } = true;

  // --- Pinch Run Log ---

  /// <summary>
  /// When enabled, opens a separate window during auto-pinch that collects
  /// errors and warnings for review after the run.
  /// </summary>
  public bool EnablePinchRunLog { get; set; } = true;

  /// <summary>
  /// Rolling average time per item in milliseconds, persisted across runs.
  /// Used for ETA calculation. Updated at the end of each completed run.
  /// </summary>
  public float AvgMsPerItem { get; set; } = 0f;

  // --- Gil Tracking ---

  /// <summary>
  /// When enabled, captures sale history, listing snapshots, and gil balances
  /// during pinch runs. Adds ~1.5s per retainer to view sale history.
  /// </summary>
  public bool EnableGilTracking { get; set; } = true;

  /// <summary>
  /// Number of days before a last sale price is considered stale.
  /// Used in the Hawk Window to dim old price data. Range: 0–100, default 30.
  /// </summary>
  public int StalePriceDays { get; set; } = 30;

  // --- Routing brain (advisor era; master toggle stays off until the era ships) ---

  /// <summary>
  /// Master toggle for routing-brain features. Increment 0 is the listing
  /// gate: Hawk items whose better exit is desynth or GC turn-in get a
  /// routing verdict, are excluded from Select All, and default unchecked.
  /// </summary>
  public bool EnableRoutingBrain { get; set; } = false;

  /// <summary>
  /// Equipment listing floor in gil. Gear whose own-sales evidence lands
  /// below this is a gate candidate (when a better exit exists).
  /// </summary>
  public int ListingFloorGil { get; set; } = 15000;

  /// <summary>
  /// Equipment velocity floor in days. Gear that took longer than this to
  /// sell last time is a gate candidate (when a better exit exists).
  /// </summary>
  public int ListingVelocityDays { get; set; } = 10;

  /// <summary>
  /// Non-gear listing floor in gil (simple, no velocity axis). Rules-engine
  /// input; gear uses ListingFloorGil x ListingVelocityDays instead.
  /// </summary>
  public int ListingWorthGil { get; set; } = 5000;

  /// <summary>
  /// Placeholder seals-to-gil conversion rate for scoring the GC exit.
  /// Replaced by the empirical gil-per-venture number once venture-return
  /// tracking ships — until then this is an honest rough cut.
  /// </summary>
  public int SealToGilRate { get; set; } = 25;

  /// <summary>
  /// Ambiguity band, percent. When the winning exit's gil score and the
  /// runner-up land within this band, the item goes to Review with both
  /// reasons instead of a confident guess.
  /// </summary>
  public int RoutingReviewBandPct { get; set; } = 15;

  // Venture tilt bands (BP4 Q5) — configurable defaults, not product rules.
  // Above Full: GC competes on pure value. Below Full: borderline calls tilt
  // to churn. Below Low: churn unless the item is worth
  // ListingFloorGil x VenturePanicValueMultiplier. Below Panic: churn
  // everything GC-eligible until stock recovers.

  public int VentureBandFull { get; set; } = 1250;

  public int VentureBandLow { get; set; } = 750;

  public int VentureBandPanic { get; set; } = 500;

  public float VenturePanicValueMultiplier { get; set; } = 3.0f;

  // The top band ("around 2k is cruisin"): when the 7-day projection - current
  // stock minus MEASURED weekly burn - still clears this line, borderline calls
  // tilt away from churn (seals saturated). Projection-based on purpose: where
  // you WILL be, not where you are. Inert until a full week of snapshots exists.
  public int VentureBandCruise { get; set; } = 2000;

  // What a skillup is worth in gil (Sam 07-18: price the skillup, don't gate
  // it). The desynth candidate for a skillup-eligible item scores at least
  // this and competes in the ordinary value comparison; red is rarer than
  // yellow, so it is worth more. A sale comfortably above the worth wins the
  // market; below it, the melter wins; near it, Review.
  public int SkillupWorthYellow { get; set; } = 50_000;
  public int SkillupWorthRed { get; set; } = 100_000;

  // Slow-mover pressure - the routing brain pointed at already-listed
  // inventory. Rides the pinch run; gated by EnableRoutingBrain too.

  /// <summary>
  /// Master toggle for slow-mover pressure (deepen cuts / evict flags).
  /// Default OFF: this is the one advisor-era feature that changes real
  /// listing prices, so it never activates silently with the routing brain -
  /// it gets its own explicit opt-in. (Renamed from EnableSlowMoverPressure
  /// at the era review precisely to drop the old default-on stored value.)
  /// </summary>
  public bool SlowMoverPressureOptIn { get; set; } = false;

  /// <summary>Days listed before pressure starts deepening the pinch cut.</summary>
  public int PressureAfterDays { get; set; } = 7;

  /// <summary>Extra undercut percent at PressureAfterDays (market alive).</summary>
  public int PressureDeepenPct { get; set; } = 2;

  /// <summary>Extra undercut percent at 14+ days listed (market alive).</summary>
  public int PressureDeepenMaxPct { get; set; } = 5;

  /// <summary>Days listed with a dead 14-day MB history before the item is flagged for eviction.</summary>
  public int EvictAfterDays { get; set; } = 14;

  /// <summary>
  /// Venture tokens consumed per quick venture (VERIFY in-game; believed 2).
  /// Feeds the empirical seals-to-gil conversion.
  /// </summary>
  public int VentureTokensPerVenture { get; set; } = 2;

  // --- Universalis almanac (advisor data only - never sets a price) ---

  /// <summary>
  /// Use Universalis (community market data, home world) to fill the velocity
  /// axis for items with no local history. Consumer only; offline = the
  /// plugin behaves exactly as if this were off.
  /// </summary>
  public bool EnableUniversalis { get; set; } = true;

  /// <summary>Data older than this is treated as NO data (stale = unknown).</summary>
  public int UniversalisTrustDays { get; set; } = 7;

  /// <summary>Cache TTL - velocity moves slowly, so refetch rarely.</summary>
  public int UniversalisCacheTtlHours { get; set; } = 18;

  // --- DTR / server info bar ---

  /// <summary>Show today's gil delta in the server info bar. Click opens the dashboard.</summary>
  public bool EnableDtrToday { get; set; } = true;

  // --- Gil Goals ---
  // Three independent buckets; 0 = that goal is off. Set from the dashboard's
  // Goals tab. Crossings celebrate once per target value — changing a target
  // re-arms it (see GilGoals).

  /// <summary>Bank target per retainer, in gil. Progress reads "N of M retainers at target".</summary>
  public long GoalPerRetainer { get; set; } = 0;

  /// <summary>Walking-around player gil target.</summary>
  public long GoalPlayerGil { get; set; } = 0;

  /// <summary>Total worth target (player + all retainers).</summary>
  public long GoalTotalGil { get; set; } = 0;

  // Celebration bookkeeping — owned by GilGoals, not user-facing.
  public long GoalPlayerCelebratedTarget { get; set; } = 0;
  public long GoalTotalCelebratedTarget { get; set; } = 0;
  public long GoalPerRetainerBaselineTarget { get; set; } = 0;
  public int GoalPerRetainerCelebratedCount { get; set; } = 0;

  /// <summary>Achieved-goal history, newest last.</summary>
  public List<GilGoalRecord> GoalHistory { get; set; } = [];

  // --- Text-to-speech ---

  public bool TTSWhenAllDone { get; set; } = false;

  public string TTSWhenAllDoneMsg { get; set; } = "Finished auto pinching all retainers";

  public bool TTSWhenEachDone { get; set; } = false;

  public string TTSWhenEachDoneMsg { get; set; } = "Auto Pinch done";

  public int TTSVolume { get; set; } = 20;

  /// <summary>Auto-set to true on platforms where System.Speech is unavailable.</summary>
  public bool DontUseTTS { get; set; } = false;

  /// <summary>
  /// Set of retainer names that are enabled for auto pinch.
  /// If empty or null, all retainers are enabled by default.
  /// If contains ALL_DISABLED_SENTINEL, all retainers are disabled.
  /// </summary>
  public const string ALL_DISABLED_SENTINEL = "__ALL_DISABLED__";
  
  public HashSet<string> EnabledRetainerNames { get; set; } = [];

  /// <summary>
  /// List of retainer names that were last fetched from the game.
  /// Used to display retainer selection even when the retainer list is not open.
  /// </summary>
  public List<string> LastKnownRetainerNames { get; set; } = [];

  /// <summary>
  /// Items permanently excluded from the Hawk Window.
  /// Keyed by Lumina item ID. Managed from the Hawk Window and ConfigWindow.
  /// </summary>
  public HashSet<uint> BannedItemIds { get; set; } = [];

  /// <summary>
  /// Items always vendor-sold during hawk runs via "Have Retainer Sell Items".
  /// Keyed by item ID — HQ items stored with +1M offset (e.g., HQ item 4856 = 1004856).
  /// Mutually exclusive with BannedItemIds (per HQ/NQ variant).
  /// </summary>
  public HashSet<uint> AlwaysVendorItemIds { get; set; } = [];

  /// <summary>
  /// When true, items that fail price floor or minimum listing price checks
  /// during hawk runs are vendor-sold instead of skipped.
  /// Only effective when PriceFloorMode is not DomanEnclave.
  /// </summary>
  public bool AutoVendorSellOnPriceCheckFail { get; set; } = false;

  public void Save()
  {
    Plugin.PluginInterface.SavePluginConfig(this);
  }
}