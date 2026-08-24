using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Scrooge.Windows;
using System;

namespace Scrooge;

/// <summary>
/// THE HANDS (review pricing split, 2026-08-16). Every raw touch of the RetainerSell
/// and ContextMenu addons on the pricing path lives in this file and nothing else does:
/// the context-menu clicks that open a panel, the reads that lift an item's identity
/// off it, the asking-price write, and the confirm/cancel that closes it.
///
/// <para><b>Game thread only, by construction.</b> Every method here dereferences a
/// pointer the game owns, so every one of them is a task-manager step or is called from
/// inside one. Nothing in here decides anything - the decisions live in the pipeline,
/// the lane and the voice, all of which can be reasoned about (and two of which can be
/// tested) without a game running. Splitting the file this way is what makes that claim
/// checkable instead of merely believed: if an <c>unsafe</c> block appears in the
/// pricing spine again, it is visible as a departure rather than as more of the same.
/// </para>
///
/// <para>Static because none of it needs state. The panel is found by name at the
/// moment it is used - never held across a task boundary, which is the same discipline
/// <see cref="CancelPanelAfterThrow"/> was written under.</para>
/// </summary>
internal static unsafe class RetainerPanelActions
{
  // --- Pinch run item interaction ---

  /// <summary>Clicks "Adjust Price" in the retainer sell list context menu. Detects mannequin items.</summary>
  internal static bool? ClickAdjustPrice()
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      var reader = new ReaderContextMenu(addon);
      if (!GameNavigation.IsItemMannequin(reader.Entries))
      {
        Svc.Log.Debug($"Clicking adjust price");
        ECommons.Automation.Callback.Fire(addon, true, 0, 0, 0, 0, 0); // click adjust price
      }
      else
      {
        Svc.Log.Debug("Current item is a mannequin item and will be skipped");
        var currentItem = Plugin.CurrentRun?.CurrentItem;
        if (currentItem != null) currentItem.Result = PricingResult.Skipped;
        addon->Close(true);
      }

      return true;
    }

    return false;
  }

  // --- Hawk run item interaction ---

  /// <summary>
  /// Right-clicks an item in the player's inventory to open the context menu.
  /// Uses AgentInventoryContext to open the context menu for a specific slot.
  /// </summary>
  internal static bool? ClickInventoryItem(ListableItem hawkItem)
  {
    // Safety check: verify the item is still in the expected slot
    var im = InventoryManager.Instance();
    var container = im == null ? null : im->GetInventoryContainer(hawkItem.Container);
    if (container == null) return true;

    var slot = container->GetInventorySlot(hawkItem.SlotIndex);
    if (slot == null || slot->ItemId != hawkItem.ItemId)
    {
      Svc.Log.Warning($"[HawkRun] {hawkItem.Name} no longer at expected slot — skipping");
      var currentItem = Plugin.CurrentRun?.CurrentItem;
      if (currentItem != null) currentItem.Result = PricingResult.Skipped;
      return true;
    }

    var agent = AgentInventoryContext.Instance();
    var addonId = AgentInventory.Instance()->OpenAddonId;
    agent->OpenForItemSlot(hawkItem.Container, hawkItem.SlotIndex, 0, addonId);

    return true;
  }

  /// <summary>
  /// Clicks "Put Up for Sale" in the inventory context menu.
  /// If the option is missing, the sell list may be full or the item is bound.
  /// </summary>
  internal static bool? ClickPutUpForSale()
  {
    if (Plugin.CurrentRun?.CurrentItem?.Result == PricingResult.Skipped)
      return true;

    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      var reader = new ReaderContextMenu(addon);

      for (int i = 0; i < reader.Entries.Count; i++)
      {
        var name = reader.Entries[i].Name;
        if (name.Equals("Put Up for Sale", StringComparison.OrdinalIgnoreCase))
        {
          ECommons.Automation.Callback.Fire(addon, true, 0, i, 0, 0, 0);
          return true;
        }
      }

      // "Put Up for Sale" not found — sell list full or item is bound
      Svc.Log.Warning("[HawkRun] 'Put Up for Sale' not in context menu — sell list may be full or item is bound");
      var currentItem = Plugin.CurrentRun?.CurrentItem;
      if (currentItem != null) currentItem.Result = PricingResult.Skipped;
      addon->Close(true);
      return true;
    }
    return false;
  }

  /// <summary>
  /// Clicks "Have Retainer Sell Items" in the inventory context menu.
  /// Vendor-sells the item through the retainer at vendor price.
  /// </summary>
  internal static bool? ClickHaveRetainerSellItems()
  {
    if (Plugin.CurrentRun?.CurrentItem?.Result == PricingResult.Skipped)
      return true;

    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon)
        && GenericHelpers.IsAddonReady(addon))
    {
      var reader = new ReaderContextMenu(addon);

      for (int i = 0; i < reader.Entries.Count; i++)
      {
        var name = reader.Entries[i].Name;
        // NOTE: English-only text match. Non-English clients will not match
        // and the item will be skipped. Acceptable for now — Scrooge targets EN.
        if (name.Equals("Have Retainer Sell Items", StringComparison.OrdinalIgnoreCase))
        {
          ECommons.Automation.Callback.Fire(addon, true, 0, i, 0, 0, 0);
          return true;
        }
      }

      // Option not found — item may not be vendorable
      Svc.Log.Warning("[HawkRun] 'Have Retainer Sell Items' not in context menu");
      var currentItem = Plugin.CurrentRun?.CurrentItem;
      if (currentItem != null) currentItem.Result = PricingResult.Skipped;
      addon->Close(true);
      return true;
    }
    return false;
  }

  // --- The sell panel itself ---

  /// <summary>
  /// Closes the market-board results window if it is standing open. The pricing pass
  /// opened it; the sell panel underneath is what gets written.
  /// </summary>
  internal static void CloseItemSearchResult()
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var searchAddon))
      searchAddon->Close(true);
  }

  /// <summary>
  /// Finds the RetainerSell panel and reports whether it is ready to be worked on.
  /// The one place the pricing pass acquires the pointer it then hands back here.
  /// </summary>
  internal static bool TryGetReadySellPanel(out AddonRetainerSell* panel)
  {
    if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out panel)
        && GenericHelpers.IsAddonReady(&panel->AtkUnitBase))
      return true;

    panel = null;
    return false;
  }

  /// <summary>Reads the panel's current asking price (the pre-filled or standing ask).</summary>
  internal static int ReadAskingPrice(AddonRetainerSell* retainerSell)
    => retainerSell->AskingPrice->Value;

  /// <summary>Writes the decided ask into the panel. The only price write in the plugin.</summary>
  internal static void SetAskingPrice(AddonRetainerSell* retainerSell, int price)
    => retainerSell->AskingPrice->SetValue(price);

  /// <summary>
  /// THE ONE WAY OUT OF THE PANEL. Confirm writes whatever stands in the asking-price
  /// field (which on a kept price is the old one, unchanged); cancel writes nothing at
  /// all. Every exit from the pricing pass goes through here with an explicit answer,
  /// which is what makes "recon never writes a price" a fact about the code.
  /// </summary>
  internal static void ConfirmOrCancel(AddonRetainerSell* retainerSell, bool confirm)
  {
    ECommons.Automation.Callback.Fire(&retainerSell->AtkUnitBase, true, confirm ? 0 : 1);
    retainerSell->AtkUnitBase.Close(true);
  }

  /// <summary>
  /// Cancels and closes the sell panel after an exception, re-reading the addon
  /// rather than trusting a pointer from a scope that just blew up (review ruling S9).
  ///
  /// <para>Silent on its own failure, deliberately: this runs while the caller is
  /// already handling one exception, and a second one thrown out of the cleanup would
  /// replace a named pricing error with a meaningless teardown error. A panel that
  /// cannot be reached is also a panel that is not standing open.</para>
  /// </summary>
  internal static void CancelPanelAfterThrow()
  {
    try
    {
      if (!GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var panel)) return;
      ECommons.Automation.Callback.Fire(&panel->AtkUnitBase, true, 1); // 1 = cancel
      panel->AtkUnitBase.Close(true);
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Pricing] Couldn't cancel the sell panel after a throw: {ex.Message}");
    }
  }

  /// <summary>
  /// Reads the RetainerSell addon and populates PricingItem with identity + prices.
  /// Returns the raw item name (needed for chat messages with SeString control chars).
  /// </summary>
  /// <param name="listingFromBags">
  /// Whether this pass opened the panel over a BAG SLOT rather than a standing listing.
  /// The pinch is the one pass whose panel opens over a real ask; every other pass is
  /// looking at the game's pre-filled suggestion. Passed in rather than read off
  /// <c>Plugin.CurrentRun</c> here because this file does not ask what run it is in -
  /// see <see cref="ListingAccounting.StandingAsk"/> for what the answer decides.
  /// </param>
  internal static string PopulateItemFromAddon(
    AddonRetainerSell* retainerSell, PricingItem? currentItem, bool listingFromBags,
    out ItemPayload? itemPayload, out int listingQuantity)
  {
    var itemName = retainerSell->ItemName->NodeText.ToString();
    var cleanName = Communicator.CleanItemName(itemName, out var isHq);
    itemPayload = Communicator.RawItemNameToItemPayload(itemName);
    if (itemPayload != null)
      GilTracker.GetItemCategory(itemPayload.ItemId);
    listingQuantity = retainerSell->AtkValues[8].Int;

    if (currentItem != null)
    {
      currentItem.ItemName = cleanName;
      currentItem.IsHq = isHq;
      currentItem.ItemId = itemPayload?.ItemId ?? 0;
      currentItem.Quantity = listingQuantity;
      // THE ONE PLACE THE PRE-FILL IS JUDGED (SF-P6, 2026-08-15). The panel hands us a
      // number either way; only this method knows whether anything is actually standing
      // behind it. Gating here rather than at each narrator keeps one source of truth -
      // the field means "the ask on the board" on every surface that reads it, and an
      // item pulled out of the bags has none. Widened from hawk-only to every non-pinch
      // pass (ruled 08-15): recon walks the same bag slots, and though nothing phantom
      // reached its spoken line, the operand was live in its receipts and queue walk -
      // the pinch is the ONE pass whose panel opens over a standing listing.
      // See ListingAccounting.StandingAsk.
      currentItem.CurrentListingPrice =
        ListingAccounting.StandingAsk(retainerSell->AskingPrice->Value, listingFromBags: listingFromBags);
      currentItem.RetainerName = Plugin.CurrentRun?.CurrentRetainer ?? "";
      if (itemPayload != null)
        currentItem.VendorPrice = (int)Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(itemPayload.ItemId).PriceLow;
    }

    return itemName;
  }
}
