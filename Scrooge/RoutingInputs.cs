using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// Batch-scoped context for routing evaluations: one DB pass for sales and
/// melt values, one venture-stock read. Build once per evaluation sweep
/// (e.g. a Hawk window refresh), then Collect() per item is pure lookups.
/// </summary>
internal sealed class RoutingBatch
{
  public Dictionary<(uint ItemId, bool IsHq), (int Price, long Timestamp, int? SoldAfterDays)> LastSales { get; init; } = [];
  public Dictionary<(uint ItemId, bool IsHq), long> MeltValues { get; init; } = [];
  /// <summary>Venture token stock, or null when the inventory read failed. Rules-engine input (venture tilt bands).</summary>
  public int? VentureStock { get; init; }
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
  public int VendorPrice { get; init; }

  // Evidence
  /// <summary>Own last sale for this variant (price, when, days listed before selling).</summary>
  public (int Price, long Timestamp, int? SoldAfterDays)? LastSale { get; init; }
  /// <summary>Yield gil per desynth attempt from the player's own ledger.</summary>
  public long? MeltValuePerAttempt { get; init; }
  /// <summary>GC Expert Delivery seals, when eligible.</summary>
  public int? SealValue { get; init; }

  // Desynth skill state (null color = not desynthesizable / no repair class)
  public DesynthSkillupColor? DesynthColor { get; init; }
  public bool DesynthSkillupEligible { get; init; }

  // Player flags
  public bool IsBanned { get; init; }
  public bool IsAlwaysVendor { get; init; }
}

/// <summary>
/// The routing brain's input aggregation layer. Assembles per-item evidence
/// from storage, game sheets, and player state — the rules engine and the
/// listing gate consume this instead of gathering their own inputs.
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
        melts = ListingGate.BuildMeltValues(store.ReadSourceSummary(0));
    }
    catch { /* storage unavailable — no melt evidence this batch */ }

    var ventures = GameSafe.VentureTokenCount();
    if (ventures is int v)
      Svc.Log.Debug($"[Routing] venture stock: {v}");

    return new RoutingBatch { LastSales = sales, MeltValues = melts, VentureStock = ventures };
  }

  /// <summary>
  /// Assembles the inputs for one item variant. Returns null when the item
  /// has no sheet row (event items, tokens) — nothing to route.
  /// </summary>
  internal static RoutingItemInputs? Collect(RoutingBatch batch, uint itemId, bool isHq)
  {
    if (!Svc.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
      return null;

    var isEquipment = item.EquipSlotCategory.RowId != 0;
    var fullId = isHq ? itemId + 1_000_000u : itemId;

    // Desynth skill state — repair class as the desynthesizable proxy
    // (equipment with no repair class can't be melted).
    DesynthSkillupColor? color = null;
    var repairClass = (byte)item.ClassJobRepair.RowId;
    if (isEquipment && repairClass != 0)
      color = DesynthSkillup.Classify(
        DesynthSkillup.GetDesynthLevel(repairClass),
        (int)item.LevelItem.RowId);

    return new RoutingItemInputs
    {
      ItemId = itemId,
      IsHq = isHq,
      Name = item.Name.ToString(),
      Ilvl = (int)item.LevelItem.RowId,
      IsEquipment = isEquipment,
      VendorPrice = (int)item.PriceLow,
      LastSale = batch.LastSales.TryGetValue((itemId, isHq), out var sale) ? sale : null,
      MeltValuePerAttempt = batch.MeltValues.TryGetValue((itemId, isHq), out var melt) ? melt : null,
      SealValue = GcSeals.For(itemId),
      DesynthColor = color,
      DesynthSkillupEligible = color is { } c && DesynthSkillup.IsSkillupEligible(c),
      IsBanned = Plugin.Configuration.BannedItemIds.Contains(fullId),
      IsAlwaysVendor = Plugin.Configuration.AlwaysVendorItemIds.Contains(fullId),
    };
  }
}
