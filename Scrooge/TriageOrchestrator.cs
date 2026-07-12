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
  private List<PricingItem>? _repriceQueue;
  private RunData? _repriceRun; // temp run owned by QueueReprice - cleared on cleanup
  private IDisposable? _catchallBlock;
  private string? _currentRetainer;
  private int _vendorSoldCount;
  private long _vendorSoldGil;
  private int _pulledCount;

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
    _catchallBlock?.Dispose();
    _catchallBlock = null;
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

    var insideRetainer = false;
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _))
    {
      activeRetainer = GameSafe.ActiveRetainerName();
      insideRetainer = true;
      sellListOpen = true;
    }
    else if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out _))
    {
      activeRetainer = GameSafe.ActiveRetainerName();
      insideRetainer = true;
    }
    else if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out _))
    {
      Svc.Chat.PrintError("[Scrooge] Talk to a retainer or open the retainer list first.");
      return false;
    }

    if (insideRetainer && activeRetainer == null)
    {
      Svc.Chat.PrintError("[Scrooge] Couldn't read the active retainer — try reopening the retainer.");
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
    _repriceRun = tempRun;

    IsRunning = true;
    _currentRetainer = activeRetainer;

    // Navigate to retainer + sell list if needed
    if (activeRetainer != null && item.RetainerName != activeRetainer)
    {
      // Wrong retainer — close and navigate
      if (sellListOpen)
      {
        _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "RepriceCloseSellList");
        _taskManager.DelayNext(500);
      }
      _taskManager.Enqueue(GameNavigation.CloseRetainer, "RepriceCloseRetainer");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(() => NavigateToRetainer(item.RetainerName), $"RepriceNav_{item.RetainerName}");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(GameNavigation.ClickSellItems, "RepriceOpenSellList");
      _taskManager.DelayNext(1000);
      _currentRetainer = item.RetainerName;
    }
    else if (activeRetainer == null)
    {
      // At retainer list — navigate to the correct one
      _taskManager.Enqueue(() => NavigateToRetainer(item.RetainerName), $"RepriceNav_{item.RetainerName}");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(GameNavigation.ClickSellItems, "RepriceOpenSellList");
      _taskManager.DelayNext(1000);
      _currentRetainer = item.RetainerName;
    }
    else if (!sellListOpen)
    {
      // Right retainer, but sell list not open
      _taskManager.Enqueue(GameNavigation.ClickSellItems, "RepriceOpenSellList");
      _taskManager.DelayNext(1000);
    }

    // Auto-dismiss retainer greeting dialogs
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);

    // Standard pinch item flow: right-click → adjust price → compare prices → set price.
    // Row re-resolved by id at execution (see OpenSellListRow); a no-longer-
    // listed item marks itself Skipped and the remaining steps no-op.
    var pricing = Plugin.AutoPinch.Pricing;
    _taskManager.Enqueue(() => OpenSellListRow(item, isReprice: true),
      $"RepriceRightClick_{item.ItemName}");
    _taskManager.DelayNext(500);
    _taskManager.Enqueue(() => Skipped(item) ? true : pricing.ClickAdjustPrice(), $"RepriceAdjust_{item.ItemName}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(() => Skipped(item) ? true : pricing.ClickComparePrice(), $"RepriceCompare_{item.ItemName}");
    _taskManager.DelayNext(Plugin.Configuration.MarketBoardKeepOpenMS);

    // The MB response lands via an async event that fills item.FinalPrice -
    // a flat keep-open delay lost that race once (Carbuncle Chair, Pending).
    // Wait for the price (or a sentinel result) with a hard deadline, then
    // let SetNewPrice run either way - its history/own-sales fallbacks handle
    // a truly silent market.
    var mbDeadline = DateTime.MinValue;
    _taskManager.Enqueue(() =>
    {
      if (Skipped(item)) return true;
      if (item.FinalPrice is > 0 || item.Result != PricingResult.Pending) return true;
      if (mbDeadline == DateTime.MinValue)
        mbDeadline = DateTime.UtcNow.AddMilliseconds(7000);
      return DateTime.UtcNow >= mbDeadline;
    }, $"RepriceAwaitMb_{item.ItemName}");
    _taskManager.Enqueue(() => Skipped(item) ? true : pricing.SetNewPrice(), $"RepriceSetPrice_{item.ItemName}");

    // Cleanup: check result and update triage, chain next reprice if any
    _taskManager.Enqueue(() =>
    {
      RemoveTalkListeners();
      var success = item.Result == PricingResult.Applied;
      Plugin.CurrentRun = null;
      _repriceRun = null;
      IsRunning = false;

      if (success)
        Plugin.TriageWindow.RemoveItem(item);
      else if (item.Result == PricingResult.Skipped)
        Plugin.TriageWindow.RemoveItem(item); // no longer listed - row is moot
      else
        Svc.Chat.PrintError($"[Scrooge] Reprice failed for {item.ItemName}: {item.Result}");

      // Chain next reprice if batch has more
      if (_repriceQueue != null && _repriceQueue.Count > 0)
      {
        StartNextReprice();
        return true;
      }

      // Last reprice done - close up shop like the vendor/pull path does
      // (the sell list only needed to stay open BETWEEN reprices).
      _repriceQueue = null;
      _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "RepriceFinalCloseSellList");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(GameNavigation.CloseRetainer, "RepriceFinalCloseRetainer");
      _taskManager.Enqueue(() => { Util.FlashWindow(); return true; }, "RepriceAllDone");
      return true;
    }, "RepriceEnd");

    return true;
  }

  /// <summary>
  /// Processes a batch of triage decisions. Vendor/Pull items are batched together
  /// (grouped by retainer, descending slot order). Reprices chain sequentially after.
  /// </summary>
  internal unsafe bool QueueTriageBatch(Dictionary<PricingItem, TriageAction> actions)
  {
    if (IsRunning || actions.Count == 0) return false;

    var vendorPull = actions
      .Where(a => a.Value == TriageAction.Vendor || a.Value == TriageAction.Pull)
      .Select(a => { a.Key.QueuedAction = a.Value; return a.Key; })
      .ToList();

    var reprices = actions
      .Where(a => a.Value == TriageAction.Reprice)
      .Select(a => a.Key)
      .ToList();

    _repriceQueue = reprices.Count > 0 ? reprices : null;

    if (vendorPull.Count > 0)
      return QueueAll(vendorPull);

    // No vendor/pull items — start reprices directly
    if (_repriceQueue != null && _repriceQueue.Count > 0)
      return StartNextReprice();

    return false;
  }

  /// <summary>Starts the next reprice from the reprice queue.</summary>
  private bool StartNextReprice()
  {
    if (_repriceQueue == null || _repriceQueue.Count == 0)
    {
      _repriceQueue = null;
      return false;
    }

    var item = _repriceQueue[0];
    _repriceQueue.RemoveAt(0);
    return QueueReprice(item);
  }

  /// <summary>
  /// Queue multiple items for pull + vendor. Groups by retainer,
  /// sorts by descending slot index to avoid index shifting.
  /// Returns false if the queue was rejected (not at retainer list, no space, etc.).
  /// </summary>
  internal unsafe bool QueueAll(List<PricingItem> items)
  {
    if (IsRunning || items.Count == 0) return false;

    // Need at least 1 inventory slot to hold the pulled item.
    // Null manager reads as 0 free slots — can't verify, don't start.
    var im = InventoryManager.Instance();
    var freeSlots = im == null ? 0 : im->GetEmptySlotsInBag();
    if (freeSlots < 1)
    {
      Svc.Chat.PrintError("[Scrooge] No inventory space to pull items from MB.");
      return false;
    }

    // Detect current state: sell list > retainer menu > retainer list > error
    string? activeRetainer = null;
    bool sellListOpen = false;

    var insideRetainer = false;
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _))
    {
      // Inside a retainer with sell list open — ready to pull immediately
      activeRetainer = GameSafe.ActiveRetainerName();
      insideRetainer = true;
      sellListOpen = true;
    }
    else if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out _))
    {
      // Inside a retainer at the menu — need to open sell list
      activeRetainer = GameSafe.ActiveRetainerName();
      insideRetainer = true;
    }
    else if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out _))
    {
      Svc.Chat.PrintError("[Scrooge] Talk to a retainer or open the retainer list first.");
      return false;
    }

    if (insideRetainer && activeRetainer == null)
    {
      Svc.Chat.PrintError("[Scrooge] Couldn't read the active retainer — try reopening the retainer.");
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
    _pulledCount = 0;
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
    CleanupRunState();
  }

  /// <summary>
  /// Detects a batch that died without cleanup - the TaskManager's
  /// AbortOnTimeout clears the queue but tells nobody (finding #14: wedged
  /// "Triage in progress...", leaked catchall block suppressing gil
  /// tracking). A healthy run always has tasks queued until TriageEnd runs,
  /// so IsRunning with an idle TaskManager can only mean an abort.
  /// Called from the Triage window's Draw.
  /// </summary>
  internal void RecoverIfStalled()
  {
    if (!IsRunning || _taskManager.IsBusy) return;
    Svc.Log.Warning("[Triage] Batch died mid-flight (task timeout) — recovering state.");
    Communicator.PrintTriageSummary(_vendorSoldCount, _vendorSoldGil, _pulledCount);
    CleanupRunState();
  }

  /// <summary>Shared teardown for abort/stall paths - every resource a run holds.</summary>
  private void CleanupRunState()
  {
    IsRunning = false;
    _triageQueue = null;
    _repriceQueue = null;
    _catchallBlock?.Dispose();
    _catchallBlock = null;
    if (_repriceRun != null && ReferenceEquals(Plugin.CurrentRun, _repriceRun))
      Plugin.CurrentRun = null;
    _repriceRun = null;
    RemoveTalkListeners();
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", AutoConfirmVendorDismiss);
  }

  /// <summary>Processes the next item in the triage queue.</summary>
  private unsafe bool? ProcessNext()
  {
    if (_triageQueue == null || _triageQueue.Count == 0)
    {
      var hasReprices = _repriceQueue != null && _repriceQueue.Count > 0;

      // Only close retainer if no reprices pending — reprices need the sell list open
      if (_currentRetainer != null && !hasReprices)
      {
        _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "TriageCloseSellList");
        _taskManager.DelayNext(500);
        _taskManager.Enqueue(GameNavigation.CloseRetainer, "TriageCloseRetainer");
        _taskManager.DelayNext(500);
      }

      _taskManager.Enqueue(() =>
      {
        Communicator.PrintTriageSummary(_vendorSoldCount, _vendorSoldGil, _pulledCount);
        IsRunning = false;
        _triageQueue = null;

        // Chain reprices if any are pending — keep listeners alive
        if (hasReprices)
        {
          StartNextReprice();
          return true;
        }

        // Fully done — clean up listeners
        RemoveTalkListeners();
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", AutoConfirmVendorDismiss);
        _repriceQueue = null;
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
        _taskManager.DelayNext(500);
        _taskManager.Enqueue(GameNavigation.CloseRetainer, "TriageCloseRetainer");
        _taskManager.DelayNext(500);
      }

      _taskManager.Enqueue(() => NavigateToRetainer(item.RetainerName), $"TriageNav_{item.RetainerName}");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(GameNavigation.ClickSellItems, "TriageClickSellItems");
      _taskManager.DelayNext(1000);
      _currentRetainer = item.RetainerName;
    }

    // Pull: right-click item in sell list → "Return Items to Inventory".
    // The row is re-resolved by item id at execution time - recorded slot
    // indexes go stale the moment anything sells, and held-flag rows can be
    // days old. Not listed anymore = it sold; skip, never pull blind.
    _taskManager.Enqueue(() => OpenSellListRow(item, isReprice: false),
      $"TriageRightClick_{item.ItemName}");
    _taskManager.DelayNext(500);
    _taskManager.Enqueue(() => Skipped(item) ? true : FailClosed(item, GameNavigation.ClickReturnToInventory()),
      $"TriageReturn_{item.ItemName}");
    _taskManager.DelayNext(1000);

    if (item.QueuedAction != TriageAction.Pull)
    {
      // Vendor: wait for the pulled item to land in bags (server round-trip,
      // variable latency), then right-click → "Have Retainer Sell Items".
      // Returning false retries each tick; a hard deadline marks the ITEM
      // skipped so one slow arrival costs one item, never the batch. The
      // item stays in the player's bags either way - nothing is lost.
      var arrivalDeadline = DateTime.MinValue;
      _taskManager.Enqueue(() =>
      {
        if (Skipped(item)) return true;
        if (arrivalDeadline == DateTime.MinValue)
          arrivalDeadline = DateTime.UtcNow.AddMilliseconds(5000);
        if (GameNavigation.TryClickInventoryItemById(item.ItemId, item.IsHq))
          return true;
        if (DateTime.UtcNow < arrivalDeadline)
          return false; // not in bags yet - retry
        Svc.Chat.Print($"[Scrooge] {item.ItemName} never arrived in bags — left unvendored (check inventory).");
        item.QueuedAction = TriageAction.None;
        return true;
      }, $"TriageClickInv_{item.ItemName}");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(() => { if (!Skipped(item)) { _catchallBlock?.Dispose(); _catchallBlock = GilTrackingState.Block("triage_vendor"); } return true; },
        $"TriageBlockCatchall_{item.ItemName}");
      _taskManager.Enqueue(() => Skipped(item) ? true : FailClosed(item, GameNavigation.ClickVendorSellItem()),
        $"TriageVendor_{item.ItemName}");
      _taskManager.DelayNext(500);

      // Track the sale and retire the row (flags close on COMPLETION, not queue)
      _taskManager.Enqueue(() => { if (!Skipped(item)) { TrackVendorSale(item); Plugin.TriageWindow.RemoveItem(item); } return true; },
        $"TriageTrack_{item.ItemName}");
      _taskManager.Enqueue(() => { _catchallBlock?.Dispose(); _catchallBlock = null; return true; },
        $"TriageUnblockCatchall_{item.ItemName}");
    }
    else
    {
      // Pull only — track the pull and retire the row on completion
      _taskManager.Enqueue(() => { if (!Skipped(item)) { _pulledCount++; Plugin.TriageWindow.RemoveItem(item); } return true; },
        $"TriagePulled_{item.ItemName}");
    }

    // Continue to next item
    _taskManager.Enqueue(ProcessNext, "TriageProcessNext");
    return true;
  }

  /// <summary>An item marked skipped mid-batch (no longer listed) - queued follow-up tasks no-op.</summary>
  private static bool Skipped(PricingItem item)
    => item.QueuedAction == TriageAction.None || item.Result == PricingResult.Skipped;

  /// <summary>
  /// Translates a context-menu clicker's tri-state for the TaskManager: a
  /// null (entry missing - fail closed) becomes a per-item skip, because a
  /// raw null return tells the TaskManager to abort the ENTIRE queue. One
  /// bad menu costs one item, never the batch.
  /// </summary>
  private static bool? FailClosed(PricingItem item, bool? clickResult)
  {
    if (clickResult != null) return clickResult;
    Svc.Chat.Print($"[Scrooge] {item.ItemName} — expected menu entry missing; item skipped (see /xllog).");
    item.QueuedAction = TriageAction.None;
    return true;
  }

  /// <summary>
  /// Opens the sell-list context menu for the item, re-resolving its row by
  /// (item id, HQ) against the sell list's DISPLAYED rows - recorded slot
  /// indexes are stale hints, never targeting data, and the RetainerMarket
  /// container orders slots differently than the display (finding #16). An
  /// item that is no longer listed (sold since flagging) marks itself
  /// skipped and closes its row; the batch moves on.
  /// </summary>
  private unsafe bool? OpenSellListRow(PricingItem item, bool isReprice)
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon)
        || !GenericHelpers.IsAddonReady(addon))
      return false; // navigation still in flight - retry

    if (GameSafe.SellListRow(item.ItemId, item.IsHq) is not { } row)
    {
      Svc.Chat.Print($"[Scrooge] {item.ItemName} is no longer listed on {item.RetainerName} — skipped (likely sold).");
      if (isReprice) item.Result = PricingResult.Skipped;
      else item.QueuedAction = TriageAction.None;
      Plugin.TriageWindow.RemoveItem(item); // row is moot - close its flags
      return true;
    }

    item.Quantity = row.Quantity; // live stack size (flag rows start unknown)
    return GameNavigation.OpenItemContextMenu(row.RowIndex);
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

    if (Plugin.Configuration.EnableGilTracking)
    {
      var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      GilStorage.InsertTransaction(now, "earned", "vendor_sale", totalGil,
        item.ItemId, item.ItemName, GilTracker.GetItemCategory(item.ItemId),
        item.Quantity, vendorPrice, item.IsHq, "", "NPC Vendor");
    }
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
