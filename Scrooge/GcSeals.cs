using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace Scrooge;

/// <summary>
/// Grand Company Expert Delivery seal values — the fifth exit for gear
/// (gear → seals → venture tokens → quick ventures). Seal payout is a
/// game-sheet lookup keyed by the item's item level; eligibility is
/// "equippable gear of green rarity or better WITH an NPC sell value" -
/// the vendor-price test is what excludes current-tier ("too new") gear.
/// </summary>
internal enum GcSealState
{
  /// <summary>The counter takes it, for a seal value we can name.</summary>
  Eligible,
  /// <summary>No item sheet row at all - the id is not something we can read.</summary>
  Unknown,
  /// <summary>Not equippable gear, or below green rarity. Expert Delivery has no door for it.</summary>
  NotGear,
  /// <summary>Gear the counter refuses because the sheet carries no NPC sell value - the "too new" gate.</summary>
  TooNew,
  /// <summary>Eligible gear whose item level pays no Expert Delivery seals.</summary>
  NoSeals,
}

internal static class GcSeals
{
  /// <summary>
  /// Expert Delivery seal value for an item, or null when the item isn't
  /// eligible (not gear, rarity too low, or no sheet row for its ilvl).
  /// </summary>
  internal static int? For(uint itemId) => Explain(itemId).Seals;

  /// <summary>
  /// THE SAME TEST, WITH ITS REASON (V9, ruled 08-22). The Desynth tab's GC Seals
  /// column drew ONE dash over four different facts - "we can't read the item",
  /// "it isn't gear", "the counter won't take it yet", and "it is eligible and pays
  /// nothing" - which is four answers wearing one glyph. The eligibility law stays
  /// here, in one place; only the reason is new, so no surface has to re-derive it.
  /// </summary>
  internal static (int? Seals, GcSealState State) Explain(uint itemId)
  {
    if (!Svc.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
      return (null, GcSealState.Unknown);

    // Expert Delivery takes equippable gear, green rarity (2) or better.
    if (item.Rarity < 2 || item.EquipSlotCategory.RowId == 0)
      return (null, GcSealState.NotGear);

    // The counter also demands an NPC sell value. Current-tier gear ships with
    // PriceLow zeroed (no "Sell to Vendor" line) and SE flips it later in the
    // patch cycle - this IS the "too new" gate (finding #18's twin: one sheet
    // column, observed live 07-22 when the counter refused the Vana'dielian
    // pair; confirmed against Allagan Tools' Uses predicate 07-23).
    if (item.PriceLow == 0)
      return (null, GcSealState.TooNew);

    if (!Svc.Data.GetExcelSheet<GCSupplyDutyReward>().TryGetRow(item.LevelItem.RowId, out var reward))
      return (null, GcSealState.NoSeals);

    var seals = (int)reward.SealsExpertDelivery;
    return seals > 0 ? (seals, GcSealState.Eligible) : (null, GcSealState.NoSeals);
  }
}
