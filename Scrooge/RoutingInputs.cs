using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The routing brain's input aggregation layer. Assembles per-item evidence
/// from storage, game sheets, and player state — the rules engine and the
/// listing gate consume this instead of gathering their own inputs. The pure
/// data types live in RoutingModels.cs (shared with the test project).
/// </summary>
internal static class RoutingInputService
{
  /// <summary>
  /// Loads the batch context. Storage failures degrade to empty evidence
  /// (items read as Unknown downstream), never throw.
  /// </summary>
  internal static RoutingBatch BeginBatch()
  {
    Dictionary<(uint, bool), (int, long, int?)> sales = [];
    Dictionary<(uint, bool), long> melts = [];

    try { sales = GilStorage.GetLastSalePrices(); }
    catch { /* storage unavailable — no sale evidence this batch */ }

    try
    {
      if (Plugin.DesynthYieldStore is { } store)
        melts = BuildMeltValues(store.ReadSourceSummary(0));
    }
    catch { /* storage unavailable — no melt evidence this batch */ }

    var ventures = GameSafe.VentureTokenCount();
    if (ventures is int v)
      Svc.Log.Debug($"[Routing] venture stock: {v}");

    // Empirical seals-to-gil replaces the placeholder once venture returns
    // have enough data (fail-soft to the config rate).
    int? empirical = null;
    try { empirical = VentureReturns.EmpiricalSealToGilRate(); }
    catch { /* storage unavailable - placeholder rate */ }

    // Measured weekly burn (whole-week window) - null until a full week of
    // snapshots exists, and the saturation tilt simply stays off.
    int? weeklyBurn = null;
    try { weeklyBurn = GilStorage.MeasureWeeklyVentureBurn(); }
    catch { /* storage unavailable - no saturation tilt */ }

    var cfg = Plugin.Configuration;
    return new RoutingBatch
    {
      LastSales = sales,
      MeltValues = melts,
      VentureStock = ventures,
      WeeklyVentureBurn = weeklyBurn,
      SealToGilRate = empirical ?? cfg.SealToGilRate,
      SealRateEmpirical = empirical is not null,
      Rules = new RoutingConfig
      {
        ListingFloorGil = cfg.ListingFloorGil,
        ListingVelocityDays = cfg.ListingVelocityDays,
        ListingWorthGil = cfg.ListingWorthGil,
        RoutingReviewBandPct = cfg.RoutingReviewBandPct,
        CommunityMinSamples = cfg.LaneMinHistorySamples,
        VentureBandFull = cfg.VentureBandFull,
        VentureBandLow = cfg.VentureBandLow,
        VentureBandPanic = cfg.VentureBandPanic,
        VenturePanicValueMultiplier = cfg.VenturePanicValueMultiplier,
        VentureBandCruise = cfg.VentureBandCruise,
        SkillupWorthYellow = cfg.SkillupWorthYellow,
        SkillupWorthRed = cfg.SkillupWorthRed,
      },
    };
  }

  /// <summary>
  /// Per-quality melt values from the desynth ledger, keyed by source item.
  /// Aggregation, so it lives here (the gate consumed it, but never owned it).
  /// </summary>
  internal static Dictionary<(uint ItemId, bool IsHq), long> BuildMeltValues(List<DesynthSourceSummary> summaries)
  {
    var melts = new Dictionary<(uint, bool), long>();
    foreach (var s in summaries)
      if (s.Attempts > 0)
        melts[(s.SourceItemId, s.SourceIsHq)] = s.YieldValue / s.Attempts;
    return melts;
  }

  /// <summary>
  /// Assembles the inputs for one item variant. Returns null when the item
  /// has no sheet row (event items, tokens) — nothing to route. Protections
  /// are slot-level facts only the caller's scan can see; omit for contexts
  /// (like the Hawk listing gate) that don't route protected items anyway.
  /// </summary>
  internal static RoutingItemInputs? Collect(RoutingBatch batch, uint itemId, bool isHq,
    ItemProtections protections = default)
  {
    if (!Svc.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
      return null;

    var isEquipment = item.EquipSlotCategory.RowId != 0;
    var fullId = isHq ? itemId + 1_000_000u : itemId;

    // Desynth skill state — the sheet's Desynth flag is the real
    // desynthesizable test; repair class alone is a leaky proxy (finding
    // #18: current-patch raid gear carries a repair class but Desynth=0,
    // so the router piled it on an exit the game refuses). The flag is
    // patch data - when SE lifts the restriction the next scan just
    // starts routing these to desynth again, no code change.
    DesynthSkillupColor? color = null;
    var isDesynthable = item.Desynth != 0;
    var repairClass = (byte)item.ClassJobRepair.RowId;
    if (isEquipment && repairClass != 0 && isDesynthable)
      color = DesynthSkillup.Classify(
        GameSafe.GetDesynthLevel(repairClass),
        (int)item.LevelItem.RowId);

    // Universalis almanac — marketable items only (untradable gear has no
    // market to ask about). A cache miss queues an async fetch; this batch
    // evaluates on local evidence and the UI re-runs when the answer lands.
    var isMarketable = item.ItemSearchCategory.RowId != 0;
    (double Velocity, int? LastSaleDaysAgo)? market = null;
    if (isMarketable)
      market = UniversalisStats.TryGet(itemId, isHq);

    // DC-scope settled sales (community history) — the almanac cross-check's
    // evidence. Same miss-queues-a-fetch lifecycle as velocity above, so a
    // window sweep self-warms the cache and re-runs when answers land.
    long? communityMedian = null;
    var communityCount = 0;
    if (isMarketable && UniversalisHistory.TryGet(itemId) is { } dcSales)
    {
      var prices = new List<long>();
      foreach (var s in dcSales)
        if (s.IsHq == isHq)
          prices.Add(s.UnitPrice);
      if (prices.Count > 0)
      {
        prices.Sort();
        communityCount = prices.Count;
        var mid = prices.Count / 2;
        communityMedian = prices.Count % 2 == 1
          ? prices[mid]
          : (prices[mid - 1] + prices[mid]) / 2;
      }
    }

    return new RoutingItemInputs
    {
      ItemId = itemId,
      IsHq = isHq,
      Name = item.Name.ToString(),
      Ilvl = (int)item.LevelItem.RowId,
      IsEquipment = isEquipment,
      IsMarketable = isMarketable,
      IsDesynthable = isDesynthable,
      VendorPrice = (int)item.PriceLow,
      LastSale = batch.LastSales.TryGetValue((itemId, isHq), out var sale) ? sale : null,
      MeltValuePerAttempt = batch.MeltValues.TryGetValue((itemId, isHq), out var melt) ? melt : null,
      SealValue = GcSeals.For(itemId),
      MarketVelocity = market?.Velocity,
      MarketLastSaleDays = market?.LastSaleDaysAgo,
      CommunityMedian = communityMedian,
      CommunitySampleCount = communityCount,
      DesynthColor = color,
      DesynthSkillupEligible = color is { } c && DesynthSkillup.IsSkillupEligible(c),
      IsBanned = Plugin.Configuration.BannedItemIds.Contains(fullId),
      IsAlwaysVendor = Plugin.Configuration.AlwaysVendorItemIds.Contains(fullId),
      IsProtected = protections.Any,
      ProtectionReason = protections.Describe(),
    };
  }
}
