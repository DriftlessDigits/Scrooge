using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// Phase B of the Rattan Sofa arc (2026-08-30): the melt run's category walk.
///
/// The game's desynthesis window shows ONE category at a time, and the run can
/// only melt what the window shows (acts fire by list index through the addon
/// callback). Melt is BAGS-ONLY, and bag items appear under exactly two of the
/// eight SalvageItemCategory values - Equipment/Items (0) and Housing (1) - so
/// a run that has drained its starting view owes a visit to the bag categories
/// it has not seen, and never the armoury four.
///
/// Pure and Dalamud-free: the ints mirror SalvageItemCategory's ordinals
/// (InventoryEquipment = 0, InventoryHousing = 1), pinned by the tests so a
/// ClientStructs renumbering is caught at the seam rather than in a live melt.
/// </summary>
internal static class MeltWalk
{
  /// <summary>SalvageItemCategory.InventoryEquipment - the window's default view.</summary>
  internal const int Equipment = 0;

  /// <summary>SalvageItemCategory.InventoryHousing - where furniture hides.</summary>
  internal const int Housing = 1;

  /// <summary>
  /// The bag categories the run still owes a visit, given where its primary
  /// pass ran. Equipment first when both are owed - it is the default view and
  /// the common home of the pile.
  /// </summary>
  internal static IReadOnlyList<int> CategoriesToVisit(int currentCategory)
    => currentCategory switch
    {
      Equipment => [Housing],
      Housing => [Equipment],
      _ => [Equipment, Housing],
    };

  /// <summary>The game's own name for a bag category, for the pickup line.</summary>
  internal static string CategoryLabel(int category)
    => category == Housing ? "Housing" : "Equipment/Items";
}
