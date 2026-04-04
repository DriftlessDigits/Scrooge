using ECommons;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static ECommons.UIHelpers.AtkReaderImplementations.ReaderContextMenu;

namespace Scrooge;

/// <summary>
/// Static helper methods for clicking through retainer UI menus.
/// All methods return bool? for TaskManager compatibility:
/// true = done, false = retry, null = not ready.
/// </summary>
internal static class GameNavigation
{
  /// <param name="index">Retainer index in the RetainerList addon.</param>
  internal static unsafe bool? ClickRetainer(int index)
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      var retainerName = new AddonMaster.RetainerList(addon).Retainers[index].Name;

      Plugin.PinchRunLog?.SetCurrentRetainer(retainerName);
      Communicator.PrintRetainerName(retainerName);
      if (Plugin.Configuration.EnableGilTracking)
        GilTracker.SetRetainer(retainerName);

      ECommons.Automation.Callback.Fire(addon, true, 2, index);

      return true;
    }
    else
      return false;
  }

  internal static unsafe bool? ClickSellItems()
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      new AddonMaster.SelectString(addon).Entries[2].Select();
      return true;
    }
    else
      return false;
  }

  internal static unsafe bool? CloseRetainerSellList()
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      addon->Close(true);
      return true;
    }
    else
      return false;
  }

  internal static unsafe bool? CloseRetainer()
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      addon->Close(true);
      return true;
    }
    else
      return false;
  }

  /// <summary>
  /// Clicks "Sale History" in the retainer SelectString menu.
  /// Index may vary — validate text before clicking in production.
  /// </summary>
  internal static unsafe bool? ClickSaleHistory()
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      var selectString = new AddonMaster.SelectString(addon);
      var entries = selectString.Entries;
      // Validate entry text before clicking (guard against menu changes in patches)
      if (entries.Length > 4 && entries[4].Text.Contains("sale history", StringComparison.OrdinalIgnoreCase))
      {
        entries[4].Select();
        return true;
      }
      Svc.Log.Warning("[GilTrack] SelectString[4] is not Sale History — skipping");
      return true; // skip silently, don't block the run
    }
    return false;
  }

  /// <summary>Close the RetainerHistory (Sale History) addon.</summary>
  internal static unsafe bool? CloseSaleHistory()
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerHistory", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      addon->Close(true);
      return true;
    }
    return true; // might not be open yet — don't block
  }

  /// <param name="itemIndex">Item index in the RetainerSellList addon.</param>
  internal static unsafe bool? OpenItemContextMenu(int itemIndex)
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      Svc.Log.Debug($"Clicking item {itemIndex}");
      ECommons.Automation.Callback.Fire(addon, true, 0, itemIndex, 1); // click item
      return true;
    }

    return false;
  }

  /// <summary>
  /// Checks if an item is a mannequin item, by checking if there is
  /// the "adjust price" entry in the given <paramref name="contextMenuEntries"/>.
  /// </summary>
  /// <param name="contextMenuEntries">Context menu entries to check.</param>
  /// <returns>True if item is a mannequin item, false otherwise.</returns>
  internal static bool IsItemMannequin(List<ContextMenuEntry> contextMenuEntries)
  {
    return !contextMenuEntries.Any((e) => e.Name.Equals("adjust price", StringComparison.CurrentCultureIgnoreCase)
                                      || e.Name.Equals("preis ändern", StringComparison.CurrentCultureIgnoreCase)
                                      || e.Name.Equals("価格を変更する", StringComparison.CurrentCultureIgnoreCase)
                                      || e.Name.Equals("changer le prix", StringComparison.CurrentCultureIgnoreCase));
  }

  /// <summary>
  /// Clicks "Return Items to Inventory" in the retainer sell list context menu.
  /// Returns the item from the MB listing to the player's inventory.
  /// </summary>
  internal static unsafe bool? ClickReturnToInventory()
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon)
        && GenericHelpers.IsAddonReady(addon))
    {
      var reader = new ReaderContextMenu(addon);
      for (int i = 0; i < reader.Entries.Count; i++)
      {
        if (reader.Entries[i].Name.Equals(
            "Return Items to Inventory", StringComparison.OrdinalIgnoreCase))
        {
          ECommons.Automation.Callback.Fire(addon, true, 0, i, 0, 0, 0);
          return true;
        }
      }
      Svc.Log.Warning("[Triage] 'Return Items to Inventory' not in context menu");
      addon->Close(true);
      return true;
    }
    return false;
  }

  /// <summary>
  /// Clicks "Have Retainer Sell Items" in the context menu for an inventory item.
  /// Standalone version for triage flow (no PricingItem state dependency).
  /// </summary>
  internal static unsafe bool? ClickVendorSellItem()
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon)
        && GenericHelpers.IsAddonReady(addon))
    {
      var reader = new ReaderContextMenu(addon);
      for (int i = 0; i < reader.Entries.Count; i++)
      {
        if (reader.Entries[i].Name.Equals(
            "Have Retainer Sell Items", StringComparison.OrdinalIgnoreCase))
        {
          ECommons.Automation.Callback.Fire(addon, true, 0, i, 0, 0, 0);
          return true;
        }
      }
      Svc.Log.Warning("[Triage] 'Have Retainer Sell Items' not in context menu");
      addon->Close(true);
      return true;
    }
    return false;
  }

  /// <summary>
  /// Right-clicks an item in the player's inventory by item ID.
  /// Scans all 4 inventory bags to find the first matching stack.
  /// </summary>
  internal static unsafe bool? ClickInventoryItemById(uint itemId, bool isHq)
  {
    var im = InventoryManager.Instance();
    var containers = new[]
    {
      InventoryType.Inventory1, InventoryType.Inventory2,
      InventoryType.Inventory3, InventoryType.Inventory4,
    };

    foreach (var containerType in containers)
    {
      var container = im->GetInventoryContainer(containerType);
      if (container == null) continue;

      for (int i = 0; i < container->Size; i++)
      {
        var slot = container->GetInventorySlot(i);
        if (slot == null || slot->ItemId == 0) continue;

        var slotIsHq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        if (slot->ItemId == itemId && slotIsHq == isHq)
        {
          var agent = AgentInventoryContext.Instance();
          var addonId = AgentInventory.Instance()->OpenAddonId;
          agent->OpenForItemSlot(containerType, i, 0, addonId);
          return true;
        }
      }
    }

    Svc.Log.Warning($"[Triage] Item {itemId} not found in inventory after pull");
    return true;
  }

  /// <summary>
  /// Reads the "Selling X items" count for a retainer from the RetainerList addon.
  /// Layout: base offset 3, 10 AtkValues per retainer, offset 6 = selling text.
  /// </summary>
  internal static unsafe int GetRetainerListingCount(AtkUnitBase* addon, int retainerIndex)
  {
    var atkIdx = 3 + (retainerIndex * 10) + 6;
    var sellingText = addon->AtkValues[atkIdx].GetValueAsString();
    var match = Regex.Match(sellingText, @"\d+");
    return match.Success ? int.Parse(match.Value) : 0;
  }
}
