using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Scrooge.Windows;
using System;
using System.Collections.Generic;
using static ECommons.UIHelpers.AtkReaderImplementations.ReaderContextMenu;

namespace Scrooge;

/// <summary>
/// Handles per-item pricing: context menu interaction, MB price lookup,
/// price validation, and price application. Per-item state lives on
/// PricingItem (sole source of truth). Price cache lives on RunData.
/// </summary>
internal sealed class ItemPricingPipeline : IDisposable
{
  private readonly TaskManager _taskManager;
  private readonly MarketBoardHandler _mbHandler;
  private readonly Func<int, int> _applyJitter;

  // Fallback for PostPinch hotkey path (no CurrentRun/CurrentItem)
  private int? _hotKeyPrice;

  // Per-run cache lives on RunData — accessor for convenience
  // Outside a run (e.g., PostPinch hotkey), cache writes are silently discarded
  private Dictionary<string, int?> _cachedPrices => Plugin.CurrentRun?.CachedPrices ?? _noRunCacheStub;
  private readonly Dictionary<string, int?> _noRunCacheStub = [];

  /// <summary>Current run mode. Reads from Plugin.CurrentRun.</summary>
  private bool IsPinchRun => Plugin.CurrentRun?.Mode == RunMode.Pinch;

  /// <summary>Current run mode. Reads from Plugin.CurrentRun.</summary>
  private bool IsHawkRun => Plugin.CurrentRun?.Mode == RunMode.Hawk;

  internal ItemPricingPipeline(TaskManager taskManager, Func<int, int> applyJitter)
  {
    _taskManager = taskManager;
    _applyJitter = applyJitter;
    _mbHandler = new MarketBoardHandler();
    _mbHandler.NewPriceReceived += OnNewPriceReceived;
  }

  public void Dispose()
  {
    _mbHandler.NewPriceReceived -= OnNewPriceReceived;
    _mbHandler.Dispose();
  }

  /// <summary>Clears per-item and per-run state. Called at the start of each run.</summary>
  internal void ClearState()
  {
    _hotKeyPrice = null;
  }

  /// <summary>Clears the cached price lookup table. Called when price floor settings change.</summary>
  internal void ClearCachedPrices() => Plugin.CurrentRun?.CachedPrices.Clear();

  // --- Pinch run item interaction ---

  /// <summary>Clicks "Adjust Price" in the retainer sell list context menu. Detects mannequin items.</summary>
  internal unsafe bool? ClickAdjustPrice()
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
  internal unsafe bool? ClickInventoryItem(HawkWindow.HawkItem hawkItem)
  {
    // Safety check: verify the item is still in the expected slot
    var im = InventoryManager.Instance();
    var container = im->GetInventoryContainer(hawkItem.Container);
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
  internal unsafe bool? ClickPutUpForSale()
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
  internal unsafe bool? ClickHaveRetainerSellItems()
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

  // --- Market board & pricing ---

  /// <summary>
  /// Conditionally adds a delay before opening the MB. If we already have a
  /// cached price for this item, skip the delay (and the MB query entirely).
  /// </summary>
  internal unsafe bool? DelayMarketBoard()
  {
    if (Plugin.CurrentRun?.CurrentItem?.Result == PricingResult.Skipped)
      return true;

    if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
    {
      var itemName = addon->ItemName->NodeText.ToString();
      if (!_cachedPrices.TryGetValue(itemName, out int? value) || value <= 0)
      {
        Svc.Log.Debug($"{itemName} has no cached price (or that price was <= 0), delaying next mb open");
        _taskManager.InsertDelayNext(_applyJitter(Plugin.Configuration.GetMBPricesDelayMS));
      }

      return true;
    }

    return false;
  }

  /// <summary>
  /// Opens the "Compare Prices" MB window — unless we have a cached price,
  /// in which case we skip the MB entirely and use the cache.
  /// </summary>
  internal unsafe bool? ClickComparePrice()
  {
    if (Plugin.CurrentRun?.CurrentItem?.Result == PricingResult.Skipped)
      return true;

    if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
    {
      var itemName = addon->ItemName->NodeText.ToString();
      if (_cachedPrices.TryGetValue(itemName, out int? value) && value > 0)
      {
        Svc.Log.Debug($"{itemName}: using cached price");
        // Cache hit skips the MB query entirely — MBHandler never fires, so no
        // outlier detection and no history fallback.
        var currentItem = Plugin.CurrentRun?.CurrentItem;
        if (currentItem != null) currentItem.FinalPrice = value;
        return true;
      }
      else
      {
        Svc.Log.Debug($"Clicking compare prices");
        ECommons.Automation.Callback.Fire(&addon->AtkUnitBase, true, 4);
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Final step: applies the calculated price to the listing.
  /// Orchestrates addon reading, price evaluation, and confirm/cancel.
  /// Business logic lives in ApplyPriceDecision.
  /// </summary>
  internal unsafe bool? SetNewPrice()
  {
    var currentItem = Plugin.CurrentRun?.CurrentItem;
    ItemPayload? itemPayload = null;
    var listingQuantity = 1;

    try
    {
      if (currentItem?.Result == PricingResult.Skipped)
        return true;

      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var searchAddon))
        searchAddon->Close(true);

      if (!GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var retainerSell)
          || !GenericHelpers.IsAddonReady(&retainerSell->AtkUnitBase))
        return false;

      var itemName = PopulateItemFromAddon(retainerSell, currentItem, out itemPayload, out listingQuantity);

      // Always populate 14-day history stats for triage context
      // Must run after PopulateItemFromAddon which sets ItemId
      if (currentItem != null)
        _mbHandler.PopulateHistoryStats(currentItem);

      // Ban check — item goes through MB lookup but price is never changed
      if (itemPayload != null)
      {
        var banId = itemPayload.IsHQ ? itemPayload.ItemId + 1_000_000u : itemPayload.ItemId;
        if (Plugin.Configuration.BannedItemIds.Contains(banId))
        {
          var cleanName = Communicator.CleanItemName(itemName, out _);
          var listed = currentItem?.CurrentListingPrice;
          var mb = currentItem?.MbPrice;
          var detail = mb.HasValue
            ? $"Listed at {listed:N0}, MB at {mb:N0}"
            : $"Listed at {listed:N0}, no MB data";
          Plugin.PinchRunLog?.AddEntry(ItemOutcome.Banned, cleanName, detail);
          if (currentItem != null) currentItem.Result = PricingResult.Banned;

          // Confirm without changing (keeps original price)
          ECommons.Automation.Callback.Fire(&retainerSell->AtkUnitBase, true, 0);
          retainerSell->AtkUnitBase.Close(true);
          return true;
        }
      }

      // History fallback — history data arrives for free with Compare Prices.
      // If no valid price was found (outliers, zero listings, no matching quality),
      // try the sale history median. Skip if price failed floor/min checks — those
      // are definitive answers.
      if (currentItem != null
          && currentItem.Result != PricingResult.BelowMinimum
          && currentItem.Result != PricingResult.BelowFloor
          && (currentItem.FinalPrice == null || currentItem.FinalPrice <= 0))
      {
        var historyPrice = (_mbHandler.HistoryItemId == currentItem.ItemId)
          ? _mbHandler.GetHistoryPrice()
          : null;

        if (historyPrice.HasValue && historyPrice > 0)
        {
          currentItem.HistoryPrice = historyPrice;
          currentItem.FinalPrice = historyPrice;
          currentItem.Result = PricingResult.Pending;
          Communicator.PrintHistoryFallback(
            currentItem.ItemName, historyPrice.Value, _mbHandler.HistoryListingCount);
        }
        _mbHandler.ClearHistory();
      }
      var newPrice = currentItem?.FinalPrice ?? _hotKeyPrice;
      var confirmed = ApplyPriceDecision(retainerSell, currentItem, itemName, newPrice);

      ECommons.Automation.Callback.Fire(&retainerSell->AtkUnitBase, true, confirmed ? 0 : 1);
      retainerSell->AtkUnitBase.Close(true);
      return true;
    }
    finally
    {
      var result = currentItem?.Result ?? PricingResult.Pending;

      // Track listing value for run summary
      if (result != PricingResult.Skipped && result != PricingResult.VendorSell)
      {
        var listingValue = (currentItem?.FinalPrice.HasValue == true && currentItem.FinalPrice > 0)
          ? currentItem.FinalPrice.Value
          : currentItem?.CurrentListingPrice ?? 0;
        if (listingValue > 0)
        {
          Plugin.PinchRunLog.AddListingValue(listingValue * listingQuantity);
          if (!IsHawkRun && Plugin.Configuration.EnableGilTracking && itemPayload != null)
            GilTracker.RecordFinalPrice(itemPayload.ItemId, listingValue, listingQuantity);
        }
      }

      _hotKeyPrice = null;

      // Triage collection — save skipped items for post-run review
      if (currentItem != null && RunData.IsTriageResult(result))
        Plugin.CurrentRun?.TriageItems.Add(currentItem);

      if (result != PricingResult.VendorSell)
        Plugin.PinchRunLog.IncrementProcessed();
    }
  }



  /// <summary>
  /// Reads the RetainerSell addon and populates PricingItem with identity + prices.
  /// Returns the raw item name (needed for chat messages with SeString control chars).
  /// </summary>
  private unsafe string PopulateItemFromAddon(
    AddonRetainerSell* retainerSell, PricingItem? currentItem,
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
      currentItem.CurrentListingPrice = retainerSell->AskingPrice->Value;
      currentItem.RetainerName = Plugin.CurrentRun?.CurrentRetainer ?? "";
      if (itemPayload != null)
        currentItem.VendorPrice = (int)Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(itemPayload.ItemId).PriceLow;
    }

    return itemName;
  }

  /// <summary>
  /// Evaluates the price and applies it to the RetainerSell addon.
  /// Handles valid price (undercut/cap checks), error routing (floor/min/nodata),
  /// and hawk-run vendor-sell. All chat messages and log entries happen here.
  /// Returns true = confirm (price applied or kept), false = cancel (error/skip).
  /// </summary>
  /// <remarks>
  /// CapBlocked and UndercutTooDeep return true (confirm) because the addon value
  /// was never changed — confirming just keeps the old price (no-op).
  /// </remarks>
  private unsafe bool ApplyPriceDecision(
    AddonRetainerSell* retainerSell, PricingItem? currentItem,
    string itemName, int? newPrice)
  {
    var cleanName = Communicator.CleanItemName(itemName, out _);

    // --- Valid price path ---
    if (newPrice.HasValue && newPrice > 0)
    {
      if (IsHawkRun)
      {
        Svc.Log.Debug($"Setting new listing price");
        retainerSell->AskingPrice->SetValue(newPrice.Value);
        Communicator.PrintPriceUpdate(itemName, currentItem?.CurrentListingPrice, newPrice.Value, 0f);
        Plugin.PinchRunLog?.IncrementAdjusted();
        if (currentItem != null) currentItem.Result = PricingResult.Listed;
      }
      else
      {
        var oldPrice = currentItem?.CurrentListingPrice ?? retainerSell->AskingPrice->Value;
        var cutPercentage = oldPrice > 0 ? ((float)newPrice.Value - oldPrice) / oldPrice * 100f : 0f;

        if (cutPercentage >= -Plugin.Configuration.MaxUndercutPercentage
            || currentItem?.BypassPriceGuards == true)
        {
          if (IsPinchRun && Plugin.Configuration.EnableMaxPriceIncreaseCap
              && cutPercentage > Plugin.Configuration.MaxPriceIncreasePercentage
              && currentItem?.BypassPriceGuards != true)
          {
            Communicator.PrintAboveMaxIncreaseError(itemName, cutPercentage);
            Plugin.PinchRunLog?.AddEntry(ItemOutcome.Skipped, cleanName,
              $"Price increase exceeds cap ({Plugin.Configuration.MaxPriceIncreasePercentage}%)");
            if (currentItem != null) { currentItem.Result = PricingResult.CapBlocked; currentItem.PriceChangePercent = cutPercentage; }
          }
          else
          {
            Svc.Log.Debug($"Setting new price");
            _cachedPrices.TryAdd(itemName, newPrice);
            retainerSell->AskingPrice->SetValue(newPrice.Value);
            Communicator.PrintPriceUpdate(itemName, oldPrice, newPrice.Value, cutPercentage);
            Plugin.PinchRunLog?.IncrementAdjusted();
            if (currentItem != null) currentItem.Result = PricingResult.Applied;
          }
        }
        else
        {
          Communicator.PrintAboveMaxCutError(itemName);
          Plugin.PinchRunLog?.AddEntry(ItemOutcome.Skipped, cleanName,
            $"Undercut exceeds max ({Plugin.Configuration.MaxUndercutPercentage}%)");
          if (currentItem != null) { currentItem.Result = PricingResult.UndercutTooDeep; currentItem.PriceChangePercent = cutPercentage; }
        }
      }

      return true; // confirm — price was applied, or kept unchanged (cap/undercut)
    }

    // --- Error path: no valid price ---
    var result = currentItem?.Result ?? PricingResult.NoData;

    switch (result)
    {
      case PricingResult.BelowMinimum:
      case PricingResult.BelowFloor:
        if (IsHawkRun && Plugin.Configuration.AutoVendorSellOnPriceCheckFail)
        {
          if (currentItem != null) currentItem.Result = PricingResult.VendorSell;
          Svc.Log.Debug($"[HawkRun] Price check failed — will vendor-sell");
        }
        else if (result == PricingResult.BelowMinimum)
        {
          Communicator.PrintBelowMinimumListingPriceError(itemName);
          Plugin.PinchRunLog?.AddEntry(ItemOutcome.Skipped, cleanName,
            $"Below minimum listing price ({Plugin.Configuration.MinimumListingPrice:N0} gil)");
        }
        else
        {
          Communicator.PrintBelowPriceFloorError(itemName);
          var floorLabel = Plugin.Configuration.PriceFloorMode == PriceFloorMode.Vendor
            ? "Vendor price" : "Doman Enclave price (2x vendor)";
          Plugin.PinchRunLog?.AddEntry(ItemOutcome.Skipped, cleanName, $"Below {floorLabel}");
        }
        break;

      default:
        Svc.Log.Warning("SetNewPrice: No price to set");
        Communicator.PrintNoPriceToSetError(itemName);
        Plugin.PinchRunLog?.AddEntry(ItemOutcome.NoData, cleanName, "No market board data");
        if (currentItem != null) currentItem.Result = PricingResult.NoData;
        break;
    }

    return false; // cancel
  }

  private void OnNewPriceReceived(object? sender, NewPriceEventArgs e)
  {
    Svc.Log.Debug($"New price received: {e.NewPrice}");

    // Always capture for hotkey path (no CurrentRun/CurrentItem)
    _hotKeyPrice = e.NewPrice > 0 ? e.NewPrice : null;

    var item = Plugin.CurrentRun?.CurrentItem;
    if (item != null)
    {
      item.MbPrice = _mbHandler.LastCheckedPrice > 0 ? _mbHandler.LastCheckedPrice : null;

      if (e.NewPrice > 0)
      {
        item.FinalPrice = e.NewPrice;
      }
      else if (e.NewPrice == -2)
        item.Result = PricingResult.BelowFloor;
      else if (e.NewPrice == -3)
        item.Result = PricingResult.BelowMinimum;
      else
        item.Result = PricingResult.NoData;
    }
  }
}
