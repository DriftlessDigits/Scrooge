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
/// price validation, and price application. Owns per-item state
/// (_oldPrice, _newPrice, _skipCurrentItem) and the price cache.
/// </summary>
internal sealed class ItemPricingPipeline : IDisposable
{
  private readonly TaskManager _taskManager;
  private readonly MarketBoardHandler _mbHandler;
  private readonly Func<int, int> _applyJitter;

  // Per-item state (reset after each item in SetNewPrice finally block)
  private int? _oldPrice;
  private int? _newPrice;
  internal bool _skipCurrentItem;

  // VendorSellPending and ItemWasListed replaced by PricingItem.Result
  // (PricingResult.VendorSell and PricingResult.Listed respectively)

  // Per-run cache lives on RunData — accessor for convenience
  private Dictionary<string, int?> _cachedPrices => Plugin.CurrentRun?.CachedPrices ?? _emptyCacheStub;
  private static readonly Dictionary<string, int?> _emptyCacheStub = [];

  /// <summary>Current run mode. Reads from Plugin.CurrentRun.</summary>
  private bool IsPinchRun => Plugin.CurrentRun?.Mode == RunMode.Pinch;

  /// <summary>Current run mode. Reads from Plugin.CurrentRun.</summary>
  private bool IsHawkRun => Plugin.CurrentRun?.Mode == RunMode.Hawk;

  internal ItemPricingPipeline(TaskManager taskManager, Func<int, int> applyJitter)
  {
    _taskManager = taskManager;
    _applyJitter = applyJitter;
    _mbHandler = new MarketBoardHandler();
    _mbHandler.NewPriceReceived += MBHandler_NewPriceReceived;
  }

  public void Dispose()
  {
    _mbHandler.NewPriceReceived -= MBHandler_NewPriceReceived;
    _mbHandler.Dispose();
  }

  /// <summary>Clears per-item and per-run state. Called at the start of each run.</summary>
  internal void ClearState()
  {
    _oldPrice = null;
    _newPrice = null;
    _skipCurrentItem = false;
    // VendorSellPending/ItemWasListed now on PricingItem.Result — no reset needed
    // Cache is now owned by RunData — cleared by creating a new RunData per run
    // IsPinchRun/IsHawkRun now derived from Plugin.CurrentRun.Mode — no reset needed
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
        _skipCurrentItem = true;
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
      _skipCurrentItem = true;
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
    if (_skipCurrentItem)
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
      _skipCurrentItem = true;
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
    if (_skipCurrentItem)
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
      _skipCurrentItem = true;
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
    if (_skipCurrentItem)
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
    if (_skipCurrentItem)
      return true;

    if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
    {
      var itemName = addon->ItemName->NodeText.ToString();
      if (_cachedPrices.TryGetValue(itemName, out int? value) && value > 0)
      {
        Svc.Log.Debug($"{itemName}: using cached price");
        _newPrice = value;
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
  /// Handles the happy path (valid price) and all error sentinels
  /// (vendor floor, no listings, etc). Confirms or cancels the addon accordingly.
  /// </summary>
  internal unsafe bool? SetNewPrice()
  {
    var listingQuantity = 1;  // captured for finally block
    ItemPayload? itemPayload = null;

    try
    {
      if (_skipCurrentItem)
        return true;

      // close compare price window
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var addon))
        addon->Close(true);

      if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var retainerSell) && GenericHelpers.IsAddonReady(&retainerSell->AtkUnitBase))
      {
        var ui = &retainerSell->AtkUnitBase;
        var itemName = retainerSell->ItemName->NodeText.ToString();
        itemPayload = Communicator.RawItemNameToItemPayload(itemName);
        if (itemPayload != null)
          GilTracker.GetItemCategory(itemPayload.ItemId);
        _oldPrice = retainerSell->AskingPrice->Value;
        listingQuantity = retainerSell->AtkValues[8].Int;  // listing stack size

        // Populate PricingItem with identity + prices from addon
        var currentItem = Plugin.CurrentRun?.CurrentItem;
        if (currentItem != null)
        {
          var cleanName = Communicator.CleanItemName(itemName, out var isHq);
          currentItem.ItemName = cleanName;
          currentItem.IsHq = isHq;
          currentItem.ItemId = itemPayload?.ItemId ?? 0;
          currentItem.Quantity = listingQuantity;
          currentItem.CurrentListingPrice = _oldPrice;
          currentItem.RetainerName = Plugin.CurrentRun?.CurrentRetainer ?? "";
          if (itemPayload != null)
            currentItem.VendorPrice = (int)Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(itemPayload.ItemId).PriceLow;
        }

        if (_newPrice.HasValue && _newPrice > 0)
        {
          if (IsHawkRun)
          {
            // New listings: no meaningful _oldPrice to compare against — skip percentage guards
            Svc.Log.Debug($"Setting new listing price");
            retainerSell->AskingPrice->SetValue(_newPrice.Value);
            Communicator.PrintPriceUpdate(itemName, _oldPrice.Value, _newPrice.Value, 0f);
            if (currentItem != null) { currentItem.Result = PricingResult.Listed; currentItem.FinalPrice = _newPrice.Value; }
          }
          else
          {
            var cutPercentage = ((float)_newPrice.Value - _oldPrice.Value) / _oldPrice.Value * 100f;
            if (cutPercentage >= -Plugin.Configuration.MaxUndercutPercentage)
            {
              // Check if the price increase exceeds the cap
              if (IsPinchRun && Plugin.Configuration.EnableMaxPriceIncreaseCap && cutPercentage > Plugin.Configuration.MaxPriceIncreasePercentage)
              {
                Communicator.PrintAboveMaxIncreaseError(itemName, cutPercentage);
                if (currentItem != null) { currentItem.Result = PricingResult.CapBlocked; currentItem.PriceChangePercent = cutPercentage; }
              }
              // Price normally
              else
              {
                Svc.Log.Debug($"Setting new price");
                _cachedPrices.TryAdd(itemName, _newPrice);
                retainerSell->AskingPrice->SetValue(_newPrice.Value);
                Communicator.PrintPriceUpdate(itemName, _oldPrice.Value, _newPrice.Value, cutPercentage);
                if (currentItem != null) { currentItem.Result = PricingResult.Applied; currentItem.FinalPrice = _newPrice.Value; }
              }
            }
            else
            {
              Communicator.PrintAboveMaxCutError(itemName);
              if (currentItem != null) { currentItem.Result = PricingResult.UndercutTooDeep; currentItem.PriceChangePercent = cutPercentage; }
            }
          }

          ECommons.Automation.Callback.Fire(&retainerSell->AtkUnitBase, true, 0); // confirm
          ui->Close(true);

          return true;
        }
        else
        {
          switch (_newPrice)
          {
            case -3: // below minimum listing price
            case -2: // below price floor
              if (IsHawkRun && Plugin.Configuration.AutoVendorSellOnPriceCheckFail)
              {
                if (currentItem != null) currentItem.Result = PricingResult.VendorSell;
                Svc.Log.Debug($"[HawkRun] Price check failed — will vendor-sell");
              }
              else
              {
                if (_newPrice == -3)
                {
                  Communicator.PrintBelowMinimumListingPriceError(itemName);
                  if (currentItem != null) currentItem.Result = PricingResult.BelowMinimum;
                }
                else
                {
                  Communicator.PrintBelowPriceFloorError(itemName);
                  if (currentItem != null) currentItem.Result = PricingResult.BelowFloor;
                }
              }
              break;

            case -1: //no MB listings found
            case null:
            default:
              Svc.Log.Warning("SetNewPrice: No price to set");
              Communicator.PrintNoPriceToSetError(itemName);
              if (currentItem != null) currentItem.Result = PricingResult.NoData;
              break;
          }

          ECommons.Automation.Callback.Fire(&retainerSell->AtkUnitBase, true, 1); // cancel
          ui->Close(true);
          return true;
        }
      }
      else
        return false;
    }
    finally
    {
      var result = Plugin.CurrentRun?.CurrentItem?.Result ?? PricingResult.Pending;

      // Track listing value for run summary (before clearing state)
      // Don't track vendor-pending items as listings
      if (!_skipCurrentItem && result != PricingResult.VendorSell)
      {
        var listingValue = (_newPrice.HasValue && _newPrice > 0) ? _newPrice.Value : _oldPrice ?? 0;
        if (listingValue > 0)
        {
          Plugin.PinchRunLog.AddListingValue(listingValue * listingQuantity);
          // RecordFinalPrice updates existing listing rows — skip during hawk runs
          // (new listings aren't in the DB yet; they'll appear in the next pinch snapshot)
          if (!IsHawkRun && Plugin.Configuration.EnableGilTracking && itemPayload != null)
            GilTracker.RecordFinalPrice(itemPayload.ItemId, listingValue, listingQuantity);
        }
      }

      _oldPrice = null;
      _newPrice = null;
      _skipCurrentItem = false;

      // Don't increment for vendor-pending items — TrackVendorSale owns that
      if (result != PricingResult.VendorSell)
        Plugin.PinchRunLog.IncrementProcessed();
    }
  }

  private void MBHandler_NewPriceReceived(object? sender, NewPriceEventArgs e)
  {
    Svc.Log.Debug($"New price received: {e.NewPrice}");
    _newPrice = e.NewPrice;

    // Mirror onto PricingItem
    var item = Plugin.CurrentRun?.CurrentItem;
    if (item != null)
      item.MbPrice = e.NewPrice > 0 ? e.NewPrice : null;
  }
}
