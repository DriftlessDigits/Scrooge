using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
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
  private IDisposable? _catchallBlock;
  private int _hawkRetainerSlotsUsed;

  /// <summary>The shared run-host lifecycle (state, progress, value, stall terminal).</summary>
  private readonly RunLifecycle _run = new(TimeSpan.FromSeconds(45));

  // M4 inventory-scope zombie sweep: the full tradeable-bag container observed at
  // run start, and the retainers this run attributed lane_held flags to. The sweep
  // fires ONLY at natural completion (full observation); every partial exit
  // (retainers filled, cancel, dispose) leaves the flags open - fail toward open.
  private HashSet<uint>? _observedInventoryIds;
  private HashSet<string>? _visitedRetainers;

  /// <summary>True while a hawk run is in progress.</summary>
  internal bool IsRunning => _run.IsRunning;

  /// <summary>The live run, for the standard progress readout (see RunHostRender).</summary>
  internal RunLifecycle Run => _run;

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

  /// <summary>
  /// Fail-closed teardown on error/abort. A cancel is a PARTIAL exit - it never runs
  /// the inventory-scope zombie sweep (the container was not fully worked), so open
  /// lane_held flags stay open.
  /// </summary>
  internal void Abort()
  {
    _run.Cancel(DateTime.UtcNow);
    _hawkQueue = null;
    _catchallBlock?.Dispose();
    _catchallBlock = null;
    Plugin.PinchRunLog.CancelRun();
    Plugin.CurrentRun = null;
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", AutoConfirmVendorDismiss);
  }

  /// <summary>
  /// Scans the player's tradeable bags for the item ids present right now - the full
  /// container an inventory-scope lane_held flag points at, as this run first sees it.
  /// Null when the inventory manager is unreadable (the sweep then treats observation
  /// as incomplete and closes nothing). Base item ids only (HQ is a slot flag); a
  /// superset is safe here - more ids present means fewer closes, i.e. fail toward open.
  /// </summary>
  private static unsafe HashSet<uint>? ScanBagInventoryItemIds()
  {
    var im = InventoryManager.Instance();
    if (im == null) return null;

    var ids = new HashSet<uint>();
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
        ids.Add(slot->ItemId);
      }
    }
    return ids;
  }

  /// <summary>
  /// The Hawk completion boundary's M4 sweep: close inventory-scope lane_held flags
  /// whose item has left the bags this run observed. Gated by RunLifecycle so it fires
  /// only on FULL observation (natural completion with a readable start-of-run bag
  /// scan); a partial run produces no inputs and closes nothing. Mirrors the pinch
  /// board sweep wired in SnapshotListings, but scoped to the inventory container.
  /// </summary>
  private void HawkZombieSweep(bool fullyObserved)
  {
    if (!Plugin.Configuration.EnableGilTracking) return;
    if (_observedInventoryIds is not { } observed || _visitedRetainers == null) return;

    var inputs = RunLifecycle.HawkSweepInputs(fullyObserved, observed);
    if (inputs == null) return;

    foreach (var retainer in _visitedRetainers)
    {
      try
      {
        GilStorage.ZombieSweepLaneHeldFlags(retainer, TriageMemory.FlagScope.Inventory, inputs);
      }
      catch (Exception ex)
      {
        Svc.Log.Warning($"[Triage] Hawk zombie lane_held sweep failed for {retainer}: {ex.Message}");
      }
    }
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

    // Read before mutating any run state — fail closed if the retainer
    // can't be resolved (avoids a half-started run).
    var retainerName = GameSafe.ActiveRetainerName();
    if (retainerName == null)
    {
      Svc.Chat.PrintError("[Scrooge] Couldn't read the active retainer — try reopening the sell list.");
      return;
    }

    _pricing.ClearState();
    _run.Start(items.Count, RunValueUnit.Gil, DateTime.UtcNow, $"List {items.Count} items");
    Plugin.CurrentRun = new RunData { Mode = RunMode.Hawk };
    _hawkQueue = new Queue<HawkWindow.HawkItem>(items);
    _hawkRetainerSlotsUsed = 0;

    // M4 sweep prep: snapshot the full tradeable-bag container this run observes,
    // and seed the visited-retainer set. Captured at START (before listing removes
    // items from bags), so a still-held thin item reads present and its flag stays.
    _observedInventoryIds = ScanBagInventoryItemIds();
    _visitedRetainers = new HashSet<string> { retainerName };

    Plugin.PinchRunLog.StartNewRun(isHawkRun: true);
    Plugin.PinchRunLog.SetTotalItems(items.Count);

    // Set retainer name for log grouping (ClickRetainer doesn't fire when already inside a retainer)
    Plugin.PinchRunLog.SetCurrentRetainer(retainerName);

    // Read current retainer's listing count from RetainerSellList
    _hawkRetainerSlotsUsed = GameSafe.RetainerSellListLength() ?? 0;

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
        _run.Complete(DateTime.UtcNow);
        // Natural completion: the full inventory container was worked. Close any
        // inventory-scope lane_held flag whose item has left the bags (M4 sweep).
        HawkZombieSweep(fullyObserved: true);
        Plugin.CurrentRun = null;
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
      _taskManager.Enqueue(() => { _catchallBlock?.Dispose(); _catchallBlock = GilTrackingState.Block("hawk_vendor"); return true; }, $"HawkBlockCatchall_{item.Name}");
      _taskManager.Enqueue(_pricing.ClickHaveRetainerSellItems, $"HawkVendorSell_{item.Name}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(() => { TrackVendorSale(item); return true; }, $"HawkTrackVendor_{item.Name}");
      _taskManager.Enqueue(() => { _catchallBlock?.Dispose(); _catchallBlock = null; return true; }, $"HawkUnblockCatchall_{item.Name}");
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
      _taskManager.Enqueue(() => { _catchallBlock?.Dispose(); _catchallBlock = GilTrackingState.Block("hawk_vendor"); return true; }, $"HawkBlockCatchall_{item.Name}");
      _taskManager.Enqueue(_pricing.ClickHaveRetainerSellItems, $"HawkVendorSell_{item.Name}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(() => { TrackVendorSale(item); return true; }, $"HawkTrackVendor_{item.Name}");
      _taskManager.Enqueue(() => { _catchallBlock?.Dispose(); _catchallBlock = null; return true; }, $"HawkUnblockCatchall_{item.Name}");
      _taskManager.Enqueue(HawkProcessNext, "HawkProcessNext");
      return true;
    }

    if (result == PricingResult.Listed)
    {
      _hawkRetainerSlotsUsed++;
    }

    // One item processed (listed, or held/skipped) - advance the lifecycle and reset
    // its stall watchdog. Listing value is tracked by PinchRunLog; vendor value is
    // recorded on the vendor paths (TrackVendorSale).
    _run.RecordProgress(1, 0, DateTime.UtcNow);
    _taskManager.Enqueue(HawkProcessNext, "HawkProcessNext");
    return true;
  }

  /// <summary>Tracks a vendor sale for summary, log, and chat output.</summary>
  private void TrackVendorSale(HawkWindow.HawkItem item)
  {
    // Don't track if vendor sell failed (e.g., non-vendorable item)
    if (Plugin.CurrentRun?.CurrentItem?.Result == PricingResult.Skipped)
      return;

    var vendorPrice = (int)Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(item.ItemId).PriceLow;
    var totalGil = vendorPrice * item.Quantity;
    Plugin.PinchRunLog.AddVendorSale(totalGil);
    Plugin.PinchRunLog.IncrementProcessed();
    _run.RecordProgress(1, totalGil, DateTime.UtcNow); // one item done + gil earned

    if (Plugin.Configuration.EnableGilTracking)
    {
      var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      GilStorage.InsertTransaction(now, "earned", "vendor_sale", totalGil,
        item.ItemId, item.Name, GilTracker.GetItemCategory(item.ItemId),
        item.Quantity, vendorPrice, item.IsHq, "", "NPC Vendor");
    }

    var reason = item.IsAlwaysVendor
      ? "Always Vendor"
      : Plugin.CurrentRun?.CurrentItem?.Result switch
        {
          PricingResult.BelowFloor => "Below floor",
          PricingResult.BelowMinimum => "Below minimum",
          _ => "Price check failed"
        };
    Plugin.PinchRunLog.AddEntry(ItemOutcome.VendorSold, item.Name,
      $"{reason} — {totalGil:N0} gil ({vendorPrice:N0} × {item.Quantity})");

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
        _visitedRetainers?.Add(retainers[i].Name); // sweep this retainer too at completion

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
      // PARTIAL exit (retainers filled before the queue drained) - fail toward open
      // flags: cancel, no zombie sweep. The container was not fully worked.
      _run.Cancel(DateTime.UtcNow);
      Plugin.CurrentRun = null;
      _hawkQueue = null;
      Util.FlashWindow();
      return true;
    }, "HawkRunEnd");
    return true;
  }
}
