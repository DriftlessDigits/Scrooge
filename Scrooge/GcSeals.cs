using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace Scrooge;

/// <summary>
/// Grand Company Expert Delivery seal values — the fifth exit for gear
/// (gear → seals → venture tokens → quick ventures). Seal payout is a
/// game-sheet lookup keyed by the item's item level; eligibility is
/// approximated as "equippable gear of green rarity or better."
/// </summary>
internal static class GcSeals
{
  /// <summary>
  /// Expert Delivery seal value for an item, or null when the item isn't
  /// eligible (not gear, rarity too low, or no sheet row for its ilvl).
  /// </summary>
  internal static int? For(uint itemId)
  {
    if (!Svc.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
      return null;

    // Expert Delivery takes equippable gear, green rarity (2) or better.
    if (item.Rarity < 2 || item.EquipSlotCategory.RowId == 0)
      return null;

    if (!Svc.Data.GetExcelSheet<GCSupplyDutyReward>().TryGetRow(item.LevelItem.RowId, out var reward))
      return null;

    var seals = (int)reward.SealsExpertDelivery;
    return seals > 0 ? seals : null;
  }
}
