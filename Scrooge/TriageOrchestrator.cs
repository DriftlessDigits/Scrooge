using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// Orchestrates triage actions: pulling items off the MB and vendor-selling them.
/// Groups items by retainer to minimize retainer swaps.
/// Processes items in descending slot order within each retainer (bottom-first
/// so higher indices aren't shifted when lower items are removed).
/// </summary>
internal sealed class TriageOrchestrator : IDisposable
{
  private readonly TaskManager _taskManager;

  private Queue<PricingItem>? _triageQueue;
  private string? _currentRetainer;
  private int _vendorSoldCount;
  private long _vendorSoldGil;

  /// <summary>True while a triage run is in progress.</summary>
  internal bool IsRunning { get; private set; }

  internal TriageOrchestrator()
  {
    _taskManager = new TaskManager
    {
      TimeLimitMS = 10000,
      AbortOnTimeout = true,
    };
  }

  public void Dispose()
  {
    _taskManager.Abort();
    RemoveTalkListeners();
  }

  /// <summary>Queue a single item for pull + vendor. Returns false if rejected.</summary>
  internal bool QueueSingle(PricingItem item) => QueueAll([item]);

  /// <summary>
  /// Reprices a cap-blocked or undercut item by running it through the normal pricing
  /// pipeline with price guards bypassed. Requires being at the item's retainer sell list.
  /// </summary>
  internal unsafe bool QueueReprice(PricingItem item)
  {
    if (IsRunning) return false;

    // Must be at the retainer sell list for this item's retainer
    string? activeRetainer = null;
    bool sellListOpen = false;

    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _))
    {
      var rm = RetainerManager.Instance();
      activeRetainer = rm->GetActiveRetainer()->NameString;
      sellListOpen = true;
    }
    else if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out _))
    {
      var rm = RetainerManager.Instance();
      activeRetainer = rm->GetActiveRetainer()->NameString;
    }
    else if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out _))
    {
      Svc.Chat.PrintError("[Scrooge] Talk to a retainer or open the retainer list first.");
      return false;
    }

    // Reset item state for repricing
    item.BypassPriceGuards = true;
    item.Result = PricingResult.Pending;
    item.FinalPrice = null;

    // Create a temporary run so the pipeline can find CurrentItem and mode
    var tempRun = new RunData { Mode = RunMode.Pinch, TotalItems = 1 };
    tempRun.CurrentItem = item;
    Plugin.CurrentRun = tempRun;

    IsRunning = true;
    _currentRetainer = activeRetainer;

    // Navigate to retainer + sell list if needed
    if (activeRetainer != null && item.RetainerName != activeRetainer)
    {
      // Wrong retainer — close and navigate
      if (sellListOpen)
      {
        _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "RepriceCloseSellList");
        _taskManager.DelayNext(100);
      }
      _taskManager.Enqueue(GameNavigation.CloseRetainer, "RepriceCloseRetainer");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(() => NavigateToRetainer(item.RetainerName), $"RepriceNav_{item.RetainerName}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(GameNavigation.ClickSellItems, "RepriceOpenSellList");
      _taskManager.DelayNext(500);
      _currentRetainer = item.RetainerName;
    }
    else if (activeRetainer == null)
    {
      // At retainer list — navigate to the correct one
      _taskManager.Enqueue(() => NavigateToRetainer(item.RetainerName), $"RepriceNav_{item.RetainerName}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(GameNavigation.ClickSellItems, "RepriceOpenSellList");
      _taskManager.DelayNext(500);
      _currentRetainer = item.RetainerName;
    }
    else if (!sellListOpen)
    {
      // Right retainer, but sell list not open
      _taskManager.Enqueue(GameNavigation.ClickSellItems, "RepriceOpenSellList");
      _taskManager.DelayNext(500);
    }

    // Auto-dismiss retainer greeting dialogs
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);

    // Standard pinch item flow: right-click → adjust price → compare prices → set price
    var pricing = Plugin.AutoPinch.Pricing;
    _taskManager.Enqueue(() => GameNavigation.OpenItemContextMenu(item.SlotIndex),
      $"RepriceRightClick_{item.ItemName}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(pricing.ClickAdjustPrice, $"RepriceAdjust_{item.ItemName}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(pricing.ClickComparePrice, $"RepriceCompare_{item.ItemName}");
    _taskManager.DelayNext(Plugin.Configuration.MarketBoardKeepOpenMS);
    _taskManager.Enqueue(pricing.SetNewPrice, $"RepriceSetPrice_{item.ItemName}");

    // Cleanup: check result and update triage
    _taskManager.Enqueue(() =>
    {
      RemoveTalkListeners();
      var success = item.Result == PricingResult.Applied;
      Plugin.CurrentRun = null;
      IsRunning = false;

      if (success)
        Plugin.TriageWindow.RemoveItem(item);
      else
        Svc.Chat.PrintError($"[Scrooge] Reprice failed for {item.ItemName}: {item.Result}");

      Util.FlashWindow();
      return true;
    }, "RepriceEnd");

    return true;
  }

  /// <summary>
  /// Queue multiple items for pull + vendor. Groups by retainer,
  /// sorts by descending slot index to avoid index shifting.
  /// Returns false if the queue was rejected (not at retainer list, no space, etc.).
  /// </summary>
  internal unsafe bool QueueAll(List<PricingItem> items)
  {
    if (IsRunning || items.Count == 0) return false;

    // Need at least 1 inventory slot to hold the pulled item
    var freeSlots = InventoryManager.Instance()->GetEmptySlotsInBag();
    if (freeSlots < 1)
    {
      Svc.Chat.PrintError("[Scrooge] No inventory space to pull items from MB.");
      return false;
    }

    // Detect current state: sell list > retainer menu > retainer list > error
    string? activeRetainer = null;
    bool sellListOpen = false;

    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _))
    {
      // Inside a retainer with sell list open — ready to pull immediately
      var rm = RetainerManager.Instance();
      activeRetainer = rm->GetActiveRetainer()->NameString;
      sellListOpen = true;
    }
    else if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out _))
    {
      // Inside a retainer at the menu — need to open sell list
      var rm = RetainerManager.Instance();
      activeRetainer = rm->GetActiveRetainer()->NameString;
    }
    else if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out _))
    {
      Svc.Chat.PrintError("[Scrooge] Talk to a retainer or open the retainer list first.");
      return false;
    }

    // Group by retainer, descending slot index (bottom-first prevents index shifting)
    var sorted = items
      .OrderBy(i => i.RetainerName)
      .ThenByDescending(i => i.SlotIndex)
      .ToList();

    _triageQueue = new Queue<PricingItem>(sorted);
    _currentRetainer = activeRetainer;
    _vendorSoldCount = 0;
    _vendorSoldGil = 0;
    IsRunning = true;

    // If we're inside a retainer but sell list isn't open, open it first
    if (activeRetainer != null && !sellListOpen)
    {
      _taskManager.Enqueue(GameNavigation.ClickSellItems, "TriageOpenSellList");
      _taskManager.DelayNext(500);
    }

    // Auto-dismiss retainer greeting dialogs
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);

    // Auto-confirm "unable to process item buyback requests" on retainer dismiss
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", AutoConfirmVendorDismiss);

    _taskManager.Enqueue(ProcessNext, "TriageProcessNext");
    return true;
  }

  /// <summary>Aborts the triage run and cleans up listeners.</summary>
  internal void Abort()
  {
    _taskManager.Abort();
    IsRunning = false;
    _triageQueue = null;
    RemoveTalkListeners();
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", AutoConfirmVendorDismiss);
  }

  /// <summary>Processes the next item in the triage queue.</summary>
  private unsafe bool? ProcessNext()
  {
    if (_triageQueue == null || _triageQueue.Count == 0)
    {
      // Done — close retainer and print summary
      if (_currentRetainer != null)
      {
        _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "TriageCloseSellList");
        _taskManager.DelayNext(100);
        _taskManager.Enqueue(GameNavigation.CloseRetainer, "TriageCloseRetainer");
        _taskManager.DelayNext(100);
      }

      _taskManager.Enqueue(() =>
      {
        RemoveTalkListeners();
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", AutoConfirmVendorDismiss);
        Communicator.PrintTriageSummary(_vendorSoldCount, _vendorSoldGil);
        IsRunning = false;
        _triageQueue = null;
        Util.FlashWindow();
        return true;
      }, "TriageEnd");
      return true;
    }

    var item = _triageQueue.Dequeue();

    // Navigate to the correct retainer if needed
    if (item.RetainerName != _currentRetainer)
    {
      if (_currentRetainer != null)
      {
        _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "TriageCloseSellList");
        _taskManager.DelayNext(100);
        _taskManager.Enqueue(GameNavigation.CloseRetainer, "TriageCloseRetainer");
        _taskManager.DelayNext(100);
      }

      _taskManager.Enqueue(() => NavigateToRetainer(item.RetainerName), $"TriageNav_{item.RetainerName}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(GameNavigation.ClickSellItems, "TriageClickSellItems");
      _taskManager.DelayNext(500);
      _currentRetainer = item.RetainerName;
    }

    // Pull: right-click item in sell list → "Return Items to Inventory"
    _taskManager.Enqueue(() => GameNavigation.OpenItemContextMenu(item.SlotIndex),
      $"TriageRightClick_{item.ItemName}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(GameNavigation.ClickReturnToInventory,
      $"TriageReturn_{item.ItemName}");
    _taskManager.DelayNext(500);

    // Vendor: find item in inventory → right-click → "Have Retainer Sell Items"
    _taskManager.Enqueue(() => GameNavigation.ClickInventoryItemById(item.ItemId, item.IsHq),
      $"TriageClickInv_{item.ItemName}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(GameNavigation.ClickVendorSellItem,
      $"TriageVendor_{item.ItemName}");
    _taskManager.DelayNext(100);

    // Track the sale
    _taskManager.Enqueue(() => { TrackVendorSale(item); return true; },
      $"TriageTrack_{item.ItemName}");

    // Continue to next item
    _taskManager.Enqueue(ProcessNext, "TriageProcessNext");
    return true;
  }

  /// <summary>Finds a retainer by name in the RetainerList and clicks them.</summary>
  private unsafe bool? NavigateToRetainer(string retainerName)
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon)
        || !GenericHelpers.IsAddonReady(addon))
      return false;

    var retainerList = new AddonMaster.RetainerList(addon);
    var retainers = retainerList.Retainers;

    for (int i = 0; i < retainers.Length; i++)
    {
      if (retainers[i].Name == retainerName)
        return GameNavigation.ClickRetainer(i);
    }

    Svc.Log.Warning($"[Triage] Retainer '{retainerName}' not found — skipping");
    return true;
  }

  /// <summary>Tracks a vendor sale for the triage summary.</summary>
  private void TrackVendorSale(PricingItem item)
  {
    var vendorPrice = (int)Svc.Data.GetExcelSheet<Item>().GetRow(item.ItemId).PriceLow;
    var totalGil = vendorPrice * item.Quantity;
    _vendorSoldCount++;
    _vendorSoldGil += totalGil;
    Communicator.PrintVendorSold(item.ItemName, vendorPrice, item.Quantity);
  }

  /// <summary>Auto-clicks the retainer greeting dialog.</summary>
  private unsafe void SkipRetainerDialog(AddonEvent type, AddonArgs args)
  {
    if (!_taskManager.IsBusy)
      RemoveTalkListeners();
    else if (((AtkUnitBase*)args.Addon.Address)->IsVisible)
      new AddonMaster.Talk(args.Addon).Click();
  }

  /// <summary>
  /// Auto-confirms the "unable to process item buyback requests" dialog
  /// that appears when dismissing a retainer after vendor-selling items.
  /// </summary>
  private unsafe void AutoConfirmVendorDismiss(AddonEvent type, AddonArgs args)
  {
    var addon = new AddonMaster.SelectYesno(args.Addon);
    if (addon.Text.Contains("unable to process item buyback requests"))
    {
      Svc.Log.Debug("[Triage] Auto-confirming vendor dismiss dialog");
      addon.Yes();
    }
  }

  private void RemoveTalkListeners()
  {
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);
  }
}
