using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// Snapshots the player's four bags into a list of <see cref="ListableItem"/> -
/// THE GATE'S ANSWER about what could be listed right now.
///
/// Read-only. No mutations to game state.
/// </summary>
internal static class ListableInventoryScanner
{
  /// <summary>
  /// THE GATE'S ANSWER over all four bags: every item the Hawk could list right
  /// now (MB search category, tradeable, not banned), each carrying its listing-gate
  /// verdict and its Always Vendor flag. This is the checklist's own source, and
  /// since WALK unit 4 it is also the Ledger's row source for the one-door bell -
  /// the Ledger BORROWS this answer rather than forking the gate, so the bell and
  /// the checklist can never disagree about what "the Hawk would list" means.
  /// Rows come back unselected; the caller decides what rides.
  ///
  /// Costs a bag walk plus one routing batch (DB) - call it on a refresh, never
  /// per frame.
  /// </summary>
  internal static List<ListableItem> Scan()
  {
    var inventory = new List<ListableItem>();
    var items = Svc.Data.GetExcelSheet<Item>();

    // Route-tag evidence doubles as the Last Sale column — one DB pass.
    var batch = RoutingInputService.BeginBatch();
    var lastSales = batch.LastSales;
    var staleCutoff = Plugin.Configuration.StalePriceDays > 0
        ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (Plugin.Configuration.StalePriceDays * 24L * 3600)
        : 0L;

    unsafe
    {
      var im = InventoryManager.Instance();
      if (im == null) return inventory;

      var containers = new[]
      {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
      };

      foreach (var containerType in containers)
      {
        var container = im->GetInventoryContainer(containerType);
        if (container == null) continue;

        for (int i = 0; i < container->Size; i++)
        {
          var slot = container->GetInventorySlot(i);
          if (slot == null || slot->ItemId == 0) continue;

          var itemId = slot->ItemId;
          var item = items.GetRow(itemId);

          // Must have a market board search category
          if (item.ItemSearchCategory.RowId == 0) continue;

          // Must not be inherently untradeable
          if (item.IsUntradable) continue;

          // HQ-aware ID for ban/vendor list checks
          var isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
          var fullId = isHq ? itemId + 1_000_000u : itemId;

          // Must not be on the ban list
          if (Plugin.Configuration.BannedItemIds.Contains(fullId)) continue;

          var hasLastSale = lastSales.TryGetValue((itemId, isHq), out var lastSale);

          inventory.Add(new ListableItem
          {
            ItemId = itemId,
            Name = item.Name.ToString(),
            Quantity = (int)slot->Quantity,
            IsHq = isHq,
            Selected = false,
            IsAlwaysVendor = Plugin.Configuration.AlwaysVendorItemIds.Contains(fullId),
            Container = containerType,
            SlotIndex = i,
            LastSalePrice = hasLastSale ? lastSale.Price : 0,
            LastSaleStale = hasLastSale && staleCutoff > 0 && lastSale.Timestamp < staleCutoff,
            RouteTag = RoutingInputService.Collect(batch, itemId, isHq) is { } inputs
              ? RouteTagMap.Evaluate(inputs, batch)
              : new RouteTagMap.Result(RouteTagMap.Verdict.None, ""),
          });
        }
      }
    }

    return inventory.OrderBy(i => i.Name).ToList();
  }
}
