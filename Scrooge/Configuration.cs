using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using System;
using System.Collections.Generic;

namespace Scrooge;

// PriceFloorMode moved to PriceFloor.cs (review pricing item 1, 2026-08-16) so the
// floor's arithmetic and the mode that selects it live together and can be linked
// into Scrooge.Tests. Same namespace, so the persisted type name is unchanged.

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

  /// <summary>
  /// The write style the hand uses inside the seat the lane picked. Folded on the
  /// way in, so a retired or unknown stored value can never be honoured - see
  /// <see cref="LegacyUndercutMode"/>. The setter is where the guard lives because
  /// deserialization writes through it, which makes the migration a property of the
  /// type rather than of some load path remembering to call it.
  /// </summary>
  public UndercutMode UndercutMode
  {
    get => _undercutMode;
    set => _undercutMode = LegacyUndercutMode.Fold(value);
  }

  private UndercutMode _undercutMode = UndercutMode.FixedAmount;

  /// <summary>
  /// Amount to undercut by, in gil. Ignored in every mode but FixedAmount - the
  /// other write styles derive their own step from the price they are writing
  /// against. (It was also a percent until Percentage was retired, 2026-08-15.)
  /// </summary>
  public int UndercutAmount { get; set; } = 1;

  /// <summary>
  /// THE QUEUE-CONFIDENCE RAIL. An upward reprice is a move BACKWARD in the queue -
  /// we are putting people in front of us on purpose - and the farther back one
  /// decision puts us, the more confidence it has to be able to show. This clamp
  /// bounds how far back a single step may move us on a single board read.
  ///
  /// <para>When enabled, an upward reprice may not exceed the current listing price
  /// by more than MaxPriceIncreasePercentage in ONE step. Clamp-and-climb (ruled
  /// 07-26): the price steps to the cap and keeps climbing on later pinches -
  /// it never skips, because a skip fossilizes a crushed lane forever.</para>
  /// </summary>
  public bool EnableMaxPriceIncreaseCap { get; set; } = false;

  /// <summary>
  /// How far back in the queue one board read is allowed to move us: the per-pinch
  /// ceiling on an upward step, in percent of the current listing price. One read of
  /// a thinned board is weak evidence, so it buys a bounded move; successive passes
  /// that keep agreeing buy the rest, and the lane climbs the whole way over those
  /// passes rather than in one jump when competition delists.
  /// </summary>
  public float MaxPriceIncreasePercentage { get; set; } = 50.0f;

  /// <summary>Max random undercut in gil when Humanized mode rolls Random Pinch. Range: 1–10.</summary>
  public int HumanizedMaxPinch { get; set; } = 3;

  /// <summary>If true, undercut your own retainer listings too.</summary>
  public bool UndercutSelf { get; set; } = false;

  /// <summary>
  /// The mode half of the one floor law. None disables it (default), Vendor floors at
  /// the counter price, DomanEnclave at twice it. Combined with
  /// <see cref="MinimumListingPrice"/> by <c>PriceFloor.Effective</c> - see there for
  /// the law itself.
  /// </summary>
  public PriceFloorMode PriceFloorMode { get; set; } = PriceFloorMode.None;

  /// <summary>
  /// The player's own half of the one floor law, in gil. Set to 0 to disable (default).
  ///
  /// <para>THE FLOOR IS ONE NUMBER: <c>max(this, PriceFloor.For(mode, vendor))</c>. An
  /// honest ask under it forfeits the List exit - it is never clamped up to reach the
  /// floor, because a clamped ask is a price nobody decided.</para>
  /// </summary>
  public int MinimumListingPrice { get; set; } = 0;

  // --- Lane pricing ---
  // "Listings are what people want; sales are what people paid." The lane
  // (recency-weighted clearing price from settled sales) is the pricing model;
  // the board is positioning only. Replaced outlier/gap-geometry detection
  // (deleted 2026-07-13 per the lane design - no dormant fallback).

  /// <summary>
  /// Lane ceiling multiplier (promoted from the old upward-reprice sanity).
  /// Since A10 this is purely a RAIL: no listing we write may exceed it, so a
  /// board row above it cannot be cut in front of - that is the Highland Fence's
  /// cure and its only remaining guard. It grades nobody. One idea system-wide:
  /// 3x what it actually sells for = suspicious, in every direction.
  /// </summary>
  public float UpwardRepriceMultiplier { get; set; } = 3.0f;

  // LaneFloorPct and LaneOwnedMultiplier are GONE (cleanup pass). They were the
  // band-less stand-ins for the bottom and the top of demonstrated clearing, and
  // the band model out-evolved both: a lane with a going rate always carries a
  // band (n=1 band = the price), so neither line could ever fire.

  /// <summary>
  /// Minimum settled sales needed to build a lane. Below this the lane
  /// abstains: hold-and-flag instead of pricing off an unvalidated board.
  /// </summary>
  public int LaneMinHistorySamples { get; set; } = RoutingDefaults.MinHistorySamples;

  /// <summary>
  /// Recency half-life (days) for lane weighting. SEED value - resolver v0
  /// returns this for every item; receipts derive per-item values later.
  /// Seeded 30d from the 2026-07-13 sale-age query (median lane evidence ~42d
  /// old; erring long fails toward holding).
  /// </summary>
  public float LaneHalfLifeDays { get; set; } = 30f;

  /// <summary>
  /// How much extra an HQ item asks over its NQ price when there are no HQ sales
  /// to go on, as a percent (Phase 3b). Drift's number, not a seed: there is no
  /// measured HQ/NQ ratio anywhere in Scrooge and there is deliberately not going
  /// to be one, because measuring it needs the HQ sales this setting exists to
  /// cover for. Clamped 0-100.
  /// </summary>
  public int HqPremiumPercent { get; set; } = 25;

  /// <summary>
  /// THE SEAT RAIL's budget (A12): the deepest seat a pricing walk may take on
  /// its own authority, in foreign rows left ahead of us. "Use good judgement,
  /// but don't be wrong" (Drift, 2026-08-05) - spots 1-4 are the judgment zone,
  /// 6+ is wrong by definition; a walk that wants a deeper seat takes the front
  /// of the line instead. A preference, not a law - hence a knob.
  /// </summary>
  public int SeatBudget { get; set; } = 4;

  // --- Timing ---

  /// <summary>When enabled, adds a jitter element to simulate human interaction.</summary>
  public bool EnableJitter { get; set; } = false;

  /// <summary>Random ± variance in ms applied to configurable delays. Slider range: 500–3500.</summary>
  public int JitterMS { get; set; } = 2000;

  /// <summary>Delay before opening the MB price list. Too low = prices fail to load.</summary>
  public int GetMBPricesDelayMS { get; set; } = 5000;

  // MarketBoardKeepOpenMS the CONFIG KEY is GONE (3.1 sweep, ruled 08-23 at the
  // registry reconcile: "move those gaps onto the ladder, then delist this row").
  // Pre-ladder it was the whole board wait and tuning it mattered; post-ladder it
  // was only window 0 of four escalating retries plus three side jobs. With the
  // standing reprice folded onto the ladder (08-29) no flat tuning decision
  // remained, so the knob became the constant below. Old JSON values deserialize
  // into nothing.

  /// <summary>
  /// The board-read ladder's first window, the PostPinch hotkey's flat re-read
  /// wait, and recon's inter-item beat. A slow server escalates through the
  /// ladder's retry windows instead of needing this tuned up.
  /// </summary>
  public const int MarketBoardKeepOpenMS = 3000;

  // --- Desynth automation ---

  // EnableDesynthPreview is GONE (3.1 sweep) - the launcher toggle was killed
  // 08-23 and the field sat unread.

  /// <summary>
  /// Inject randomized 3–8s pauses every 8–15 items during a desynth run.
  /// Highest-value humanization tactic — bots almost never pause spontaneously.
  /// </summary>
  public bool DesynthHumanPauses { get; set; } = true;

  /// <summary>
  /// Base delay between item selections in a desynth run, in ms. Jittered ±400ms
  /// (real floor is Pacing.Jitter's 1 ms; the slider offers 800-4000). 1500 is the
  /// default and a comfortable human pace - there is no enforced floor.
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

  // --- The Ledger (the Round's transcript) ---

  /// <summary>
  /// When enabled, the Ledger opens with each run and collects its lines - the
  /// Round's transcript while a Round is up, one run's log otherwise.
  ///
  /// <para>Was <c>EnablePinchRunLog</c> until the Rounds re-seating (unit 5). The old
  /// key still lands somewhere - see <see cref="EnablePinchRunLog"/> - because a
  /// player who deliberately turned the log OFF must not find it back on because we
  /// re-worded ourselves.</para>
  /// </summary>
  public bool EnableLedger { get; set; } = true;

  /// <summary>
  /// Pre-Rounds spelling of <see cref="EnableLedger"/>, kept under its exact old key
  /// so the file still lands somewhere (the <see cref="Sweep"/> pattern). NULLABLE for
  /// the reason <see cref="SweepStalenessCeilingHours"/> is: absent and false are
  /// different facts, and a non-nullable bool would read every fresh install's missing
  /// key as a deliberate "off". Folded forward once and nulled; nothing writes it.
  /// </summary>
  public bool? EnablePinchRunLog { get; set; }

  /// <summary>
  /// Rolling average time per item in milliseconds, persisted across runs.
  /// Used for ETA calculation. Updated at the end of each completed run.
  /// </summary>
  public float AvgMsPerItem { get; set; } = 0f;

  /// <summary>
  /// Rolling per-item pace PER ROUND STAGE (keyed by <see cref="RoundStage"/> name),
  /// measured the same way <see cref="AvgMsPerItem"/> is and blended the same way.
  /// The round's stage rail quotes these so an ETA is the stage's OWN measured pace
  /// rather than one blended number standing in for five different errands.
  ///
  /// <para>Empty until a stage has actually run once. A stage with no measurement
  /// says "no timing yet" rather than borrowing a neighbour's number - a rate
  /// nobody measured reads like a rule.</para>
  /// </summary>
  public Dictionary<string, float> AvgMsPerItemByStage { get; set; } = new();

  // --- Gil Tracking ---

  // EnableGilTracking is GONE (3.1: "you installed Scrooge, you get Scrooge").
  // The master toggle half-gated the corpus (receipt true-up ungated, executed
  // stamps and never-cleared closes gated - trued receipts never stamped with
  // tracking off) and starved the board-freshness gate's clock (the full-pinch
  // stamp lived behind it). The recorder is always-on; old configs' saved value
  // deserializes into nothing.

  /// <summary>
  /// Number of days before a last sale price is considered stale.
  /// Used in the Hawk Window to dim old price data. Range: 0–100, default 30.
  /// </summary>
  public int StalePriceDays { get; set; } = 30;

  // --- Routing brain (the advisor IS the product; the door gates retired
  // with the master toggle - exits compete on score, nothing is routed by a
  // worth floor) ---

  /// <summary>
  /// Placeholder seals-to-gil conversion rate for scoring the GC exit -
  /// consulted ONLY until ten stamped venture returns exist, then the measured
  /// rate takes over (RoutingInputs). Seeded at 10 (Drift's ruling, 2026-08-29):
  /// a round number seated just above the first real measurement - 8 gil/seal,
  /// VentureStamp over a 30-day window, first printed by the sitrep's provenance
  /// line. The original 25 was a pre-evidence guess that, at full seal curve,
  /// priced a 2,000-seal turn-in at 50,000 gil and let GC win nearly every
  /// fresh-install contest; a brief seed of 3 the same day was a back-solve from
  /// a receipt that wrongly assumed the seal curve linear - it is smoothstep.
  /// Measurement replaces it per player.
  /// </summary>
  public int SealToGilRate { get; set; } = 10;

  // THE SEAL S-CURVE (Drift, 2026-08-05, replacing the 07-25 runway step).
  // Seal value is a smooth S on venture token STOCK: full at/below FullBelow,
  // nothing at/above ZeroAbove, smoothstep between - center at the midpoint
  // (Drift's 2k pivot). Anchored on stock because that was always Drift's dial,
  // and shaped so the stockpile self-centers: above the pivot seals cheapen
  // and turn-in slows; below it the loop reverses. See SealRunway.cs.

  public int SealCurveFullBelow { get; set; } = RoutingDefaults.SealCurveFullBelow;

  public int SealCurveZeroAbove { get; set; } = RoutingDefaults.SealCurveZeroAbove;

  /// <summary>
  /// Ambiguity band, percent. When the winning exit's gil score and the
  /// runner-up land within this band, the item goes to Review with both
  /// reasons instead of a confident guess.
  /// </summary>
  public int RoutingReviewBandPct { get; set; } = RoutingDefaults.ReviewBandPct;

  // Venture tilt bands (BP4 Q5) — configurable defaults, not product rules.
  // Above Full: GC competes on pure value. Below Full: borderline calls tilt
  // to churn.

  public int VentureBandFull { get; set; } = RoutingDefaults.VentureBandFull;

  // VentureBandLow/Panic are GONE (3.1 sweep, with their RoutingConfig snapshot
  // twins) - paint-only since the S-curve took the decisions; the dashboard's
  // ramp derives from the curve midpoint (GilWindow.Ventures).
  // VentureBandCruise ("around 2k is cruisin") retired 2026-08-05 with the
  // saturation tilt - the seal S-curve's center is the 2k now. Venture panic
  // retired with it in the cleanup pass: the S-curve owns the whole stock
  // range, so a panic stock needs no special pull - the full-value seals it
  // scores with already win on their own.

  // What a skillup is worth in gil (Drift 07-18: price the skillup, don't gate
  // it). The desynth candidate for a skillup-eligible item scores at least
  // this and competes in the ordinary value comparison; red is rarer than
  // yellow, so it is worth more. A sale comfortably above the worth wins the
  // market; below it, the melter wins; near it, Review.
  public int SkillupWorthYellow { get; set; } = RoutingDefaults.SkillupWorthYellow;
  public int SkillupWorthRed { get; set; } = RoutingDefaults.SkillupWorthRed;

  // Slow-mover pressure is GONE (cleanup pass). It was the one advisor-era
  // feature that moved real listing prices on its own ladder, and "keep it
  // simple" retired it whole - the eviction question is rethought at 3.1.

  // VentureTokensPerVenture is GONE (V42, 08-15). It carried "believed 2" as a
  // setting and fed the seals-to-gil conversion a guess. The RetainerTask sheet
  // states each venture's token cost outright and the capture now stamps it on the
  // row, so the number is measured per venture instead of assumed for all of them
  // (Drift: we don't need a mod knob for a thing we can directly measure in game).

  // --- Universalis almanac (two scopes: home-world velocity is advisor data;
  // DC settled sales CAN price, last in line - the community lane fires only
  // when own-sale/tape/Look are all silent. Old "never sets a price" header
  // was a fossil; ruled "code wins, fix words" 2026-08-23) ---

  /// <summary>
  /// Use Universalis (community market data) for two things: home-world
  /// velocity/recency to fill the pace axis for never-sold items, and DC-scope
  /// settled sale history, the List witness ladder's last rung - it prices
  /// only when local evidence is silent. Consumer only; offline = the
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

  // TTS is RETIRED (ruled 2026-08-29): inherited upstream, never used at this
  // table, and its Each announcement bunched at run end (Speak enqueued back,
  // item steps inserted front). The six keys (TTSWhenAllDone/Msg,
  // TTSWhenEachDone/Msg, TTSVolume, DontUseTTS) deserialize into nothing;
  // System.Speech left the csproj with them.

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

  /// <summary>
  /// The vendor rider (WALK unit 3): when true, unanimous Pull &amp; Vendor rows
  /// for a retainer are pulled and vendored inside THAT retainer's pinch visit,
  /// rather than waiting to be clicked as a separate errand. One honest escape
  /// hatch - flip it off if the rider ever misbehaves in the wild; mixed/demoted
  /// rows always stay in the pile for the player's judgment regardless.
  /// </summary>
  public bool PinchVendorRider { get; set; } = true;

  /// <summary>
  /// The Venture Coffer rider (Drift, 2026-07-23: "if there is a Venture Coffer in the
  /// inventory, it needs to be used to unlock an item"). When true, the round opens
  /// ALL Venture Coffers at the FRONT of the melt stage (which sits immediately
  /// before the bell per the 07-25 ruling - near the round's END, not its front) so
  /// the unlocked items join the routable pool for the next Refresh; each open is narrated in the
  /// run log. One honest escape hatch - flip it off if coffer-opening ever misbehaves
  /// in the wild. Round-context only; the manual desynth button never opens coffers.
  /// </summary>
  public bool OpenVentureCoffers { get; set; } = true;

  // --- Round run model (WALK unit 2) ---

  /// <summary>
  /// The in-progress round's HELD PLACE, persisted so a reload restores the same
  /// stages done / current / halted (the 07-22 lost-cursor reload is the ancestor
  /// bug). Null = no round underway. Written on every round transition, read once
  /// on first draw; retired (nulled) rather than restored if past the staleness
  /// ceiling below.
  /// </summary>
  public RoundState? Round { get; set; }

  /// <summary>
  /// WHAT THE LAST ROUND DID (SF-P1, 2026-08-15) - the idle Rounds screen's tally
  /// line, and the only thing about a finished errand that outlives it. Null until a
  /// round has ended under this build; the line is absent until then.
  ///
  /// <para>It is here rather than derived because nothing else survives the idle
  /// boundary: the report banner counts what is still WAITING and dies with the
  /// dismissal, the rail's per-stage tallies are in-memory and cleared by the next
  /// start, and <c>round_runs</c> banks timestamps and no counts. See
  /// <see cref="LastRoundTally"/> for the whole audit. Adding a PROPERTY is safe where
  /// renaming a persisted class is fatal (the <see cref="RoundState"/> receipts): a
  /// config written without this key reads null, which is "no round has ended yet".</para>
  /// </summary>
  public LastRoundTally? LastRound { get; set; }

  /// <summary>
  /// How old a persisted round may be before restore RETIRES it instead of
  /// trusting it - a half-done round from hours ago is history, not a round.
  /// Seed a few hours; receipts never tune this (it is a sanity ceiling, not a
  /// cadence knob). Floored at 1h on read.
  /// </summary>
  public int RoundStalenessCeilingHours { get; set; } = 4;

  /// <summary>
  /// HOW LONG A BANKED DECISION STAYS FRESH - the Rounds knob, ruled 2026-08-10
  /// ("do it right the first time"), default 24 hours.
  ///
  /// <para>ONE knob at TWO doors, deliberately. Recon's work set derives from it
  /// (an item whose decision_cache row is missing or older than this is stale, and
  /// the stale set IS what recon walks - nothing procedural decides), and unit 3's
  /// cached post gates on the same number (post from cache only while the row is
  /// within it; an older row pays the classic ComparePrice chain). "Fresh enough
  /// to skip re-reading" and "fresh enough to act on" are the same sentence, and
  /// two knobs saying it would eventually disagree - re-reading an item we were
  /// about to post from cache anyway, or posting from a row we had just called too
  /// old to trust.</para>
  ///
  /// <para>A config SEED, not a measured quantity. The evidence that will tune it
  /// is the veto's own disagreement receipts (was 24 right - measured, not
  /// guessed). Clamped to at least 1h on read by
  /// <see cref="ReconFreshness.Cutoff"/>, in the safe direction: a typo makes the
  /// round slow, never blind.</para>
  /// </summary>
  public int ReconFreshHours { get; set; } = 24;

  // --- The pre-Rounds keys (2026-08-10 naming sweep) ---
  //
  // READ ONCE, THEN NULLED. These two are the old spellings of the two properties
  // above, kept so the rename costs the player nothing: LegacyRoundConfig.Fold
  // moves their values forward on the first restore of the session and clears
  // them. Nothing writes them. See SweepState for why the BLOB's old class had to
  // survive as well - the $type in the file names it, and an unresolvable $type
  // takes the whole config down, not just its own property.
  //
  // Deletable once no config in the wild still carries the old keys.

  /// <summary>Pre-Rounds spelling of <see cref="Round"/>. Folded forward, then nulled.</summary>
  public SweepState? Sweep { get; set; }

  /// <summary>
  /// Pre-Rounds spelling of <see cref="RoundStalenessCeilingHours"/>. NULLABLE on
  /// purpose: a config that never carried the key must leave the new value alone,
  /// and a non-nullable int would hand every fresh install a hard-coded 0.
  /// </summary>
  public int? SweepStalenessCeilingHours { get; set; }

  /// <summary>
  /// The re-pinch floor for the fit check at the round's press (spec "Cadence
  /// gate", Drift 2026-07-22: "if the last pinch was less than 4 hours ago, don't
  /// suggest a pinch in a round run"). A board read younger than this means the
  /// round skips the pinch and opens past it. A config SEED, not a measured
  /// quantity - receipts tune the ripeness gate later (the SealToGilRate arc);
  /// re-ruled 2026-07-26 to Drift's 2h (was 4h) and exposed as a config-window
  /// slider the same day - his knob by design. Floored at 1h on read.
  /// </summary>
  public int RepinchFloorHours { get; set; } = 2;

  public void Save()
  {
    Plugin.PluginInterface.SavePluginConfig(this);
  }
}