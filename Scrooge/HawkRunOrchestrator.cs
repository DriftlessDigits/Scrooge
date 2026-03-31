using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Scrooge.Windows;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Scrooge;

/// <summary>
/// Orchestrates hawk runs: navigating to the sell view, processing items
/// one at a time, and swapping retainers when full.
/// </summary>
internal sealed class HawkRunOrchestrator
{
  private readonly TaskManager _taskManager;
  private readonly ItemPricingPipeline _pricing;
  private readonly Func<int, int> _applyJitter;
  private readonly IAddonLifecycle.AddonEventDelegate _skipRetainerDialog;
  private readonly Action _removeTalkListeners;

  private Queue<HawkWindow.HawkItem>? _hawkQueue;
  private int _hawkRetainerSlotsUsed;

  /// <summary>True while a hawk run is in progress.</summary>
  internal bool IsRunning { get; private set; }

  internal HawkRunOrchestrator(
    TaskManager taskManager,
    ItemPricingPipeline pricing,
    Func<int, int> applyJitter,
    IAddonLifecycle.AddonEventDelegate skipRetainerDialog,
    Action removeTalkListeners)
  {
    _taskManager = taskManager;
    _pricing = pricing;
    _applyJitter = applyJitter;
    _skipRetainerDialog = skipRetainerDialog;
    _removeTalkListeners = removeTalkListeners;
  }

  /// <summary>Resets hawk state on error/abort.</summary>
  internal void Abort()
  {
    IsRunning = false;
    _hawkQueue = null;
    Plugin.PinchRunLog.CancelRun();
    Plugin.CurrentRun = null;
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", AutoConfirmVendorDismiss);
  }

  /// <summary>
  /// Finds the first retainer with open sell slots, navigates to their
  /// "Sell items in your inventory on the market" view, then opens the HawkWindow.
  /// </summary>
  internal unsafe void OpenHawkView()
  {
    if (_taskManager.IsBusy)
      return;

    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !GenericHelpers.IsAddonReady(addon))
      return;

    var retainerList = new AddonMaster.RetainerList(addon);
    var retainers = retainerList.Retainers;

    // Count total available slots across all retainers, find first with space
    int targetIndex = -1;
    int totalAvailableSlots = 0;
    for (int i = 0; i < retainers.Length; i++)
    {
      var count = GameNavigation.GetRetainerListingCount(addon, i);
      totalAvailableSlots += (20 - count);
      if (targetIndex < 0 && count < 20)
        targetIndex = i;
    }

    if (targetIndex < 0)
    {
      Svc.Chat.PrintError("[Scrooge] All retainers have full sell lists (20/20).");
      return;
    }

    // Auto-dismiss retainer greeting dialog
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", _skipRetainerDialog);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", _skipRetainerDialog);

    // Navigate: click retainer → sell view → open HawkWindow
    _taskManager.Enqueue(() => GameNavigation.ClickRetainer(targetIndex), "HawkClickRetainer");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(GameNavigation.ClickSellItems, "HawkClickSellItems");
    _taskManager.DelayNext(500);
    _taskManager.Enqueue(() => {
      _removeTalkListeners();
      Plugin.HawkWindow.SetAvailableSlots(totalAvailableSlots);
      Plugin.HawkWindow.RefreshInventory();
      Plugin.HawkWindow.IsOpen = true;
      return true;
    }, "HawkOpenWindow");
  }

  /// <summary>
  /// Entry point for hawk runs. Called by HawkWindow when user clicks Go.
  /// Assumes we're already in the retainer's sell view (OpenHawkView navigated there).
  /// Processes items one at a time, swapping retainers when full.
  /// </summary>
  internal unsafe void StartHawkRun(List<HawkWindow.HawkItem> items)
  {
    if (_taskManager.IsBusy || items.Count == 0)
      return;

    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _))
    {
      Svc.Chat.PrintError("[Scrooge] Not in retainer sell view. Click Hawk Wares first.");
      return;
    }

    _pricing.ClearState();
    IsRunning = true;
    Plugin.CurrentRun = new RunData { Mode = RunMode.Hawk };
    _hawkQueue = new Queue<HawkWindow.HawkItem>(items);
    _hawkRetainerSlotsUsed = 0;

    Plugin.PinchRunLog.StartNewRun(isHawkRun: true);
    Plugin.PinchRunLog.SetTotalItems(items.Count);

    // Read current retainer's listing count from RetainerSellList
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var sellList) && GenericHelpers.IsAddonReady(sellList))
    {
      var listNode = (AtkComponentNode*)sellList->UldManager.NodeList[10];
      var listComponent = (AtkComponentList*)listNode->Component;
      _hawkRetainerSlotsUsed = listComponent->ListLength;
    }

    // Auto-dismiss retainer greeting dialogs (needed for retainer swaps)
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", _skipRetainerDialog);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", _skipRetainerDialog);

    // Auto-confirm vendor dismiss dialog
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", AutoConfirmVendorDismiss);

    // Start processing
    _taskManager.Enqueue(HawkProcessNext, "HawkProcessNext");
  }

  /// <summary>
  /// Processes the next item in the hawk queue. If the current retainer is full,
  /// swaps to the next retainer with space before continuing.
  /// </summary>
  private unsafe bool? HawkProcessNext()
  {
    if (_hawkQueue == null || _hawkQueue.Count == 0)
    {
      // All done — cleanup
      _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "HawkCloseSellList");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(GameNavigation.CloseRetainer, "HawkCloseRetainer");
      _taskManager.Enqueue(() => { _removeTalkListeners(); return true; });
      _taskManager.Enqueue(() => {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", AutoConfirmVendorDismiss);
        Plugin.PinchRunLog.EndRun();
        Plugin.CurrentRun = null;
        IsRunning = false;
        _hawkQueue = null;
        Util.FlashWindow();
        return true;
      }, "HawkRunEnd");
      return true;
    }

    // Peek first — route Always Vendor items before checking slot capacity
    var item = _hawkQueue.Peek();

    // Path A: Always Vendor — direct vendor sell, no MB, no retainer slot
    if (item.IsAlwaysVendor)
    {
      _hawkQueue.Dequeue();
      _taskManager.Enqueue(() => { if (Plugin.CurrentRun != null) Plugin.CurrentRun.CurrentItem = new PricingItem { ItemId = item.ItemId }; return true; }, $"HawkInitItem_{item.Name}");
      _taskManager.Enqueue(() => _pricing.ClickInventoryItem(item), $"HawkClickItem_{item.Name}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(_pricing.ClickHaveRetainerSellItems, $"HawkVendorSell_{item.Name}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(() => { TrackVendorSale(item); return true; }, $"HawkTrackVendor_{item.Name}");
      _taskManager.Enqueue(HawkProcessNext, "HawkProcessNext");
      return true;
    }

    // Slot capacity check — only for non-vendor items
    if (_hawkRetainerSlotsUsed >= 20)
    {
      // Swap to next retainer (item stays in queue since we only peeked)
      _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "HawkSwapCloseSellList");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(GameNavigation.CloseRetainer, "HawkSwapCloseRetainer");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(HawkFindNextRetainer, "HawkFindNextRetainer");
      return true;
    }

    // Path C: Normal MB flow — safe to dequeue now
    _hawkQueue.Dequeue();
    _taskManager.Enqueue(() => { if (Plugin.CurrentRun != null) Plugin.CurrentRun.CurrentItem = new PricingItem { ItemId = item.ItemId }; return true; }, $"HawkInitItem_{item.Name}");
    _taskManager.Enqueue(() => _pricing.ClickInventoryItem(item), $"HawkClickItem_{item.Name}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(_pricing.ClickPutUpForSale, $"HawkPutUpForSale_{item.Name}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(_pricing.DelayMarketBoard, $"HawkDelayMB_{item.Name}");
    _taskManager.Enqueue(_pricing.ClickComparePrice, $"HawkComparePrice_{item.Name}");
    _taskManager.DelayNext(_applyJitter(Plugin.Configuration.MarketBoardKeepOpenMS));
    _taskManager.Enqueue(_pricing.SetNewPrice, $"HawkSetPrice_{item.Name}");
    _taskManager.Enqueue(() => HandlePostPrice(item), $"HawkPostPrice_{item.Name}");
    // NOTE: HawkProcessNext is NOT enqueued here — HandlePostPrice owns it

    return true;
  }

  /// <summary>
  /// After SetNewPrice completes, decides whether to increment retainer slots
  /// (item was listed on MB) or vendor-sell (price check failed, auto-vendor enabled).
  /// HandlePostPrice is the single gateway to the next item — every path ends by
  /// enqueuing HawkProcessNext.
  /// </summary>
  private unsafe bool? HandlePostPrice(HawkWindow.HawkItem item)
  {
    var result = Plugin.CurrentRun?.CurrentItem?.Result ?? PricingResult.Pending;

    if (result == PricingResult.VendorSell)
    {
      // Path B: price check failed → vendor sell instead
      _taskManager.Enqueue(() => _pricing.ClickInventoryItem(item), $"HawkReClickItem_{item.Name}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(_pricing.ClickHaveRetainerSellItems, $"HawkVendorSell_{item.Name}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(() => { TrackVendorSale(item); return true; }, $"HawkTrackVendor_{item.Name}");
      _taskManager.Enqueue(HawkProcessNext, "HawkProcessNext");
      return true;
    }

    if (result == PricingResult.Listed)
    {
      _hawkRetainerSlotsUsed++;
    }

    _taskManager.Enqueue(HawkProcessNext, "HawkProcessNext");
    return true;
  }

  /// <summary>Tracks a vendor sale for summary and chat output.</summary>
  private void TrackVendorSale(HawkWindow.HawkItem item)
  {
    // Don't track if vendor sell failed (e.g., non-vendorable item)
    if (Plugin.CurrentRun?.CurrentItem?.Result == PricingResult.Skipped)
      return;

    var vendorPrice = (int)Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(item.ItemId).PriceLow;
    var totalGil = vendorPrice * item.Quantity;
    Plugin.PinchRunLog.AddVendorSale(totalGil);
    Plugin.PinchRunLog.IncrementProcessed();
    Communicator.PrintVendorSold(item.Name, vendorPrice, item.Quantity);
  }

  /// <summary>
  /// Auto-confirms the "unable to process item buyback requests" dialog
  /// that appears when dismissing a retainer after vendor-selling items.
  /// Only confirms if the dialog text matches — ignores unexpected dialogs.
  /// </summary>
  private unsafe void AutoConfirmVendorDismiss(AddonEvent type, AddonArgs args)
  {
    var addon = new AddonMaster.SelectYesno(args.Addon);
    // NOTE: English-only text match. Non-English clients will not auto-confirm
    // and the dialog will block the run. Acceptable for now — Scrooge targets EN.
    if (addon.Text.Contains("unable to process item buyback requests"))
    {
      Svc.Log.Debug("[HawkRun] Auto-confirming vendor dismiss dialog");
      addon.Yes();
    }
  }

  /// <summary>
  /// Finds the next retainer with available sell slots and navigates to their sell view.
  /// Called when the current retainer hits 20/20 mid-run.
  /// </summary>
  private unsafe bool? HawkFindNextRetainer()
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !GenericHelpers.IsAddonReady(addon))
      return false;

    var retainerList = new AddonMaster.RetainerList(addon);
    var retainers = retainerList.Retainers;

    for (int i = 0; i < retainers.Length; i++)
    {
      var count = GameNavigation.GetRetainerListingCount(addon, i);
      if (count < 20)
      {
        _hawkRetainerSlotsUsed = count;

        _taskManager.Enqueue(() => GameNavigation.ClickRetainer(i), "HawkSwapClickRetainer");
        _taskManager.DelayNext(100);
        _taskManager.Enqueue(GameNavigation.ClickSellItems, "HawkSwapClickSellItems");
        _taskManager.DelayNext(500);
        _taskManager.Enqueue(HawkProcessNext, "HawkProcessNext");
        return true;
      }
    }

    // No retainers with space — abort remaining items
    Svc.Chat.PrintError($"[Scrooge] All retainers full. {_hawkQueue?.Count ?? 0} items could not be listed.");
    _taskManager.Enqueue(() => { _removeTalkListeners(); return true; });
    _taskManager.Enqueue(() => {
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", AutoConfirmVendorDismiss);
      Plugin.PinchRunLog.EndRun();
      Plugin.CurrentRun = null;
      IsRunning = false;
      _hawkQueue = null;
      Util.FlashWindow();
      return true;
    }, "HawkRunEnd");
    return true;
  }
}
