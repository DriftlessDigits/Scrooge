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
using Scrooge.Windows;
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
internal sealed class StandingOrchestrator : IDisposable
{
  private readonly TaskManager _taskManager;

  /// <summary>The buyback-dialog guard the triage executor arms while it vendors.</summary>
  private readonly VendorDismissGuard _vendorDismiss = new("Standing");

  private Queue<PricingItem>? _standingQueue;
  private List<PricingItem>? _repriceQueue;
  private RunData? _repriceRun; // temp run owned by QueueReprice - cleared on cleanup
  private IDisposable? _catchallBlock;
  private string? _currentRetainer;
  private int _vendorSoldCount;
  private long _vendorSoldGil;
  private int _pulledCount;

  /// <summary>
  /// The triage batch's run state, on the shared lifecycle rather than a hand-rolled
  /// bool (orchestrators item 15). The terminal latch is what it buys: the batch tail
  /// and the reprice tail and the wedge all reach an exit funnel, and only the first
  /// arrival may close the run (S18).
  /// </summary>
  private readonly RunLifecycle _run = new(TimeSpan.FromSeconds(45));

  /// <summary>The lifecycle, for the round's rail and the completion summary.</summary>
  internal RunLifecycle Run => _run;

  /// <summary>True while a triage run is in progress.</summary>
  internal bool IsRunning => _run.IsRunning;

  /// <summary>
  /// Detects a batch that died without cleanup - the TaskManager's AbortOnTimeout
  /// clears the queue but tells nobody (finding #14: wedged "Standing listings in
  /// progress...", leaked catchall block suppressing gil tracking). A healthy run
  /// always has tasks queued until StandingEnd runs, so IsRunning with an idle
  /// TaskManager can only mean an abort.
  ///
  /// <para><b>It runs on the framework tick now, not the Accountant's Draw</b>
  /// (stability sweep, 2026-08-16). This was the only wedge watchdog in the plugin
  /// whose recovery depended on a WINDOW BEING OPEN: a batch that died with the
  /// Accountant closed stayed latched - IsRunning up, catchall block held, every
  /// queueing door refusing - until the player happened to open the window again.
  /// The other four executors were already ticking; this one now ticks with them.</para>
  /// </summary>
  private readonly QueueWedgeWatchdog _wedge;

  internal StandingOrchestrator()
  {
    _taskManager = new TaskManager
    {
      TimeLimitMS = 10000,
      AbortOnTimeout = true,
    };
    // isLive reads the LIFECYCLE, not a bool the executor kept in step by hand: the
    // truer signal, because it cannot still be true after a terminal transition.
    _wedge = new QueueWedgeWatchdog(
      isLive: () => _run.IsRunning,
      isBusy: () => _taskManager.IsBusy,
      onWedged: () =>
      {
        Svc.Log.Warning("[Standing] Batch died mid-flight (task timeout) — recovering state.");
        Communicator.PrintStandingSummary(_vendorSoldCount, _vendorSoldGil, _pulledCount);
        CleanupRunState("a task timed out and the batch's queue died", stalled: true);
      });
  }

  public void Dispose()
  {
    _wedge.Disarm();
    _taskManager.Abort();
    _catchallBlock?.Dispose();
    _catchallBlock = null;
    RemoveTalkListeners();
  }

  /// <summary>Queue a single item for pull + vendor. Returns false if rejected.</summary>
  internal bool QueueSingle(PricingItem item) => QueueAll([item]);

  /// <summary>
  /// Where the player is standing, as both queueing doors read it: sell list &gt;
  /// retainer menu &gt; retainer list &gt; error. Inside a retainer the sell list may
  /// already be open (pull immediately) or not (open it first); at the roster there
  /// is no active retainer yet and both outs are the walk's business.
  ///
  /// <para>False means the state was refused and the player has already been told
  /// why - either they are nowhere near a bell, or the retainer is unreadable.</para>
  /// </summary>
  private static unsafe bool TryReadRetainerState(out string? activeRetainer, out bool sellListOpen)
  {
    activeRetainer = null;
    sellListOpen = false;

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

    return true;
  }

  /// <summary>
  /// Reprices a cap-blocked or undercut item by running it through the normal pricing
  /// pipeline with price guards bypassed. Requires being at the item's retainer sell list.
  /// </summary>
  internal unsafe bool QueueReprice(PricingItem item)
  {
    if (IsRunning) return false;

    // Must be at the retainer sell list for this item's retainer
    if (!TryReadRetainerState(out var activeRetainer, out var sellListOpen)) return false;

    // Reset item state for repricing. QueuedAction is the Skipped() contract:
    // without a real action stamped here, every chained step no-ops.
    item.QueuedAction = StandingAction.Reprice;
    item.BypassPriceGuards = true;
    var priorResult = item.Result;

    // THE CONFIRM WRITES WHAT WAS PROPOSED (the crasher-guard ruling, 2026-08-21). A
    // deep-cut warning banked the exact price it was about to write, and the press is
    // the player answering "competition or crasher?" about THAT number - so the
    // pipeline posts it rather than re-deriving one. A cap-blocked row carries no such
    // agreement (the clamp already wrote a price), so it keeps the full re-price.
    item.ConfirmedPrice = priorResult == PricingResult.UndercutTooDeep
      ? item.RejectedPrice
      : null;
    // The ask BEFORE this reprice - captured here because the pipeline overwrites
    // CurrentListingPrice as it works, and the standing book needs the operand the
    // move started from (the same read-after-clobber class the narrators hit).
    var priorListingPrice = item.CurrentListingPrice ?? 0;
    item.Result = PricingResult.Pending;
    item.FinalPrice = null;

    // The run the pipeline reads CurrentItem and Mode from. A chained reprice JOINS
    // the batch's live run rather than replacing it - a fresh RunData here would
    // throw away everything the vendor/pull half just narrated, which is how the
    // errand ended up "half narrated, half invisible". Mode stays Pinch: that is
    // the pricing path a reprice must take (ItemPricingPipeline.IsPinchRun).
    var tempRun = Plugin.CurrentRun is { IsStandingRun: true } live
      ? live
      // This item plus whatever else is still queued behind it - a reprice-only
      // batch starts here, so declaring a total of 1 would under-count the errand.
      : new RunData
      {
        Mode = RunMode.Pinch,
        IsStandingRun = true,
        TotalItems = 1 + (_repriceQueue?.Count ?? 0),
      };
    tempRun.CurrentItem = item;
    var joinedLiveRun = ReferenceEquals(tempRun, Plugin.CurrentRun);
    Plugin.CurrentRun = tempRun;
    _repriceRun = tempRun;
    if (!joinedLiveRun)
      Plugin.Ledger.StartNewRun();

    // A reprice is its own short run on the lifecycle: chained reprices each Start
    // fresh after the previous one Completed, which is exactly what the hand-rolled
    // bool was doing when it flipped false-then-true between links.
    _run.Start(tempRun.TotalItems, RunValueUnit.Gil, DateTime.UtcNow,
      $"Reprice {tempRun.TotalItems} listing{(tempRun.TotalItems == 1 ? "" : "s")}");
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
    var pricing = Plugin.PinchHost.Pricing;
    _taskManager.Enqueue(() => SellListRowSteps.OpenRow(item, isReprice: true),
      $"RepriceRightClick_{item.ItemName}");
    _taskManager.DelayNext(500);
    _taskManager.Enqueue(() => SellListRowSteps.Skipped(item) ? true : RetainerPanelActions.ClickAdjustPrice(), $"RepriceAdjust_{item.ItemName}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(() => SellListRowSteps.Skipped(item) ? true : pricing.Board.ClickComparePrice(), $"RepriceCompare_{item.ItemName}");
    _taskManager.DelayNext(Plugin.Configuration.MarketBoardKeepOpenMS);

    // The MB response lands via an async event that fills item.FinalPrice -
    // a flat keep-open delay lost that race once (Carbuncle Chair, Pending).
    // Wait for the price (or a sentinel result) with a hard deadline, then
    // let SetNewPrice run either way - its history/own-sales fallbacks handle
    // a truly silent market.
    var mbDeadline = DateTime.MinValue;
    _taskManager.Enqueue(() =>
    {
      if (SellListRowSteps.Skipped(item)) return true;
      if (item.FinalPrice is > 0 || item.Result != PricingResult.Pending) return true;
      if (mbDeadline == DateTime.MinValue)
        mbDeadline = DateTime.UtcNow.AddMilliseconds(7000);
      return DateTime.UtcNow >= mbDeadline;
    }, $"RepriceAwaitMb_{item.ItemName}");
    _taskManager.Enqueue(() => SellListRowSteps.Skipped(item) ? true : pricing.SetNewPrice(), $"RepriceSetPrice_{item.ItemName}");

    // Cleanup: check result and update triage, chain next reprice if any
    _taskManager.Enqueue(() =>
    {
      // The compare-prices window closes when this item is DONE (ruled 08-23), not
      // when the next item happens to open its own - on the last reprice, or in the
      // gap while the flow navigates, a stale board for a finished item stayed up.
      RetainerPanelActions.CloseItemSearchResult();
      RemoveTalkListeners();
      var success = item.Result == PricingResult.Applied;
      // One item's beat, then this reprice link closes. StartNextReprice (below)
      // starts the next one; FinishStandingRun closes the errand when none is left.
      _run.RecordProgress(1, 0, DateTime.UtcNow);
      _run.Complete(DateTime.UtcNow);
      Plugin.Ledger.IncrementProcessed();

      if (success)
      {
        // The ask MOVED on a listing that was already standing, and it moved AFTER
        // the pinch that set the baseline - so only the delta is the book's news.
        StandingBookFeed.Repriced(item.ItemId, item.IsHq, item.RetainerName,
          item.FinalPrice ?? 0, priorListingPrice, Math.Max(1, item.Quantity));
        Plugin.Accountant.RemoveItem(item);
      }
      else if (item.Result == PricingResult.Skipped)
        Plugin.Accountant.RemoveItem(item); // no longer listed - row is moot
      else
      {
        Svc.Chat.PrintError($"[Scrooge] Reprice failed for {item.ItemName}: {item.Result}");
        // Restore the pre-reprice verdict so the triage row keeps its real
        // reason (and its Reprc button) instead of degrading to Unknown.
        item.Result = priorResult;
      }

      // Chain next reprice if batch has more. CurrentRun deliberately survives -
      // the next reprice joins this same run so one errand is one log entry.
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
      // No flash (ruled 08-15) - the completion handler owns the taskbar.
      _taskManager.Enqueue(() => { FinishStandingRun(); return true; }, "RepriceAllDone");
      return true;
    }, "RepriceEnd");

    _wedge.Arm();
    return true;
  }

  /// <summary>
  /// Processes a batch of triage decisions. Vendor/Pull items are batched together
  /// (grouped by retainer, descending slot order). Reprices chain sequentially after.
  /// </summary>
  internal unsafe bool QueueStandingBatch(Dictionary<PricingItem, StandingAction> actions)
  {
    if (IsRunning || actions.Count == 0) return false;

    // Melt and Gc are pull-for-X (walk ruling 2, 08-02): the retrieve is this
    // errand's, the exit fires at its own stop next round. The intent that
    // carries the ruling across the retainer->bag crossing is banked on PULL
    // COMPLETION, not here - an intent may only exist when an item is actually
    // inbound, or a failed retrieve leaves a stale ruling waiting to misfire.
    var vendorPull = actions
      .Where(a => a.Value is StandingAction.Vendor or StandingAction.Pull
        or StandingAction.Melt or StandingAction.Gc)
      .Select(a => { a.Key.QueuedAction = a.Value; return a.Key; })
      .ToList();

    var reprices = actions
      .Where(a => a.Value == StandingAction.Reprice)
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

    if (!TryReadRetainerState(out var activeRetainer, out var sellListOpen)) return false;

    // Group by retainer, descending slot index (bottom-first prevents index shifting)
    var sorted = items
      .OrderBy(i => i.RetainerName)
      .ThenByDescending(i => i.SlotIndex)
      .ToList();

    _standingQueue = new Queue<PricingItem>(sorted);
    _currentRetainer = activeRetainer;
    _vendorSoldCount = 0;
    _vendorSoldGil = 0;
    _pulledCount = 0;
    _run.Start(sorted.Count, RunValueUnit.Gil, DateTime.UtcNow,
      $"Work {sorted.Count} triage row{(sorted.Count == 1 ? "" : "s")}");

    // The triage run now NARRATES like every other run (WALK unit 6). On 07-24 a
    // Go run of 5 vendors and 1 reprice cleared three sections of the Ledger with
    // nothing in the run log to show for it. Reprices chained after this batch join
    // this same run, so one errand reads as one run from start to summary.
    Plugin.CurrentRun = new RunData
    {
      Mode = RunMode.Pinch, // the pricing path a reprice takes; see QueueReprice
      IsStandingRun = true,
      TotalItems = items.Count + (_repriceQueue?.Count ?? 0),
    };
    Plugin.Ledger.StartNewRun();

    // If we're inside a retainer but sell list isn't open, open it first
    if (activeRetainer != null && !sellListOpen)
    {
      _taskManager.Enqueue(GameNavigation.ClickSellItems, "StandingOpenSellList");
      _taskManager.DelayNext(500);
    }

    // Auto-dismiss retainer greeting dialogs
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);

    // Auto-confirm "unable to process item buyback requests" on retainer dismiss
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", _vendorDismiss.Confirm);

    _taskManager.Enqueue(ProcessNext, "StandingProcessNext");
    _wedge.Arm();
    return true;
  }

  /// <summary>Aborts the triage run and cleans up listeners.</summary>
  internal void Abort()
  {
    _taskManager.Abort();
    CleanupRunState("you cancelled the batch");
  }

  /// <summary>
  /// Closes the narrated triage run at its natural end: the log gets its End and
  /// summary lines, CurrentRun clears, and the completion event fires so the Ledger
  /// re-reads what this errand just changed. Idempotent - the vendor/pull tail and
  /// the reprice tail both land here and only the first one has a run to close.
  /// </summary>
  private void FinishStandingRun()
  {
    _wedge.Disarm();
    if (Plugin.CurrentRun is not { IsStandingRun: true }) return;
    var standing = Plugin.CurrentRun;
    Plugin.Ledger.EndRun();
    Plugin.CurrentRun = null;
    _repriceRun = null;
    // The run itself rides the completion (RunFacts): the handler is a tick late and
    // has nothing left to ask by the time it runs.
    RunFlow.ReportDone(RunKind.Standing, standing);
  }

  /// <summary>Shared teardown for abort/stall paths - every resource a run holds.</summary>
  private void CleanupRunState(string reason = "the batch stopped", bool stalled = false)
  {
    _wedge.Disarm();
    var now = DateTime.UtcNow;
    if (stalled) _run.Stall(now); else _run.Cancel(now);
    _standingQueue = null;
    _repriceQueue = null;
    _catchallBlock?.Dispose();
    _catchallBlock = null;
    var narrating = Plugin.CurrentRun is { IsStandingRun: true };
    var standing = narrating ? Plugin.CurrentRun : null;
    if (narrating)
    {
      Plugin.Ledger.CancelRun();
      Plugin.CurrentRun = null;
    }
    else if (_repriceRun != null && ReferenceEquals(Plugin.CurrentRun, _repriceRun))
      Plugin.CurrentRun = null;
    _repriceRun = null;
    RemoveTalkListeners();
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", _vendorDismiss.Confirm);
    if (narrating)
      RunFlow.ReportDied(RunKind.Standing, reason, standing);
  }

  /// <summary>Processes the next item in the triage queue.</summary>
  private unsafe bool? ProcessNext()
  {
    if (_standingQueue == null || _standingQueue.Count == 0)
    {
      var hasReprices = _repriceQueue != null && _repriceQueue.Count > 0;

      // Only close retainer if no reprices pending — reprices need the sell list open
      if (_currentRetainer != null && !hasReprices)
      {
        _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "StandingCloseSellList");
        _taskManager.DelayNext(500);
        _taskManager.Enqueue(GameNavigation.CloseRetainer, "StandingCloseRetainer");
        _taskManager.DelayNext(500);
      }

      _taskManager.Enqueue(() =>
      {
        Communicator.PrintStandingSummary(_vendorSoldCount, _vendorSoldGil, _pulledCount);
        // The batch's own leg is finished. A chained reprice Starts a fresh link
        // below; otherwise FinishStandingRun closes the errand.
        _run.Complete(DateTime.UtcNow);
        _standingQueue = null;

        // Chain reprices if any are pending — keep listeners alive
        if (hasReprices)
        {
          StartNextReprice();
          return true;
        }

        // Fully done — clean up listeners
        RemoveTalkListeners();
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", _vendorDismiss.Confirm);
        _repriceQueue = null;
        FinishStandingRun();
        // No flash (ruled 08-15) - the completion handler owns the taskbar.
        return true;
      }, "StandingEnd");
      return true;
    }

    var item = _standingQueue.Dequeue();

    // Navigate to the correct retainer if needed
    if (item.RetainerName != _currentRetainer)
    {
      if (_currentRetainer != null)
      {
        _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "StandingCloseSellList");
        _taskManager.DelayNext(500);
        _taskManager.Enqueue(GameNavigation.CloseRetainer, "StandingCloseRetainer");
        _taskManager.DelayNext(500);
      }

      _taskManager.Enqueue(() => NavigateToRetainer(item.RetainerName), $"StandingNav_{item.RetainerName}");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(GameNavigation.ClickSellItems, "StandingClickSellItems");
      _taskManager.DelayNext(1000);
      _currentRetainer = item.RetainerName;
    }

    // Pull: right-click item in sell list → "Return Items to Inventory".
    // The row is re-resolved by item id at execution time - recorded slot
    // indexes go stale the moment anything sells, and held-flag rows can be
    // days old. Not listed anymore = it sold; skip, never pull blind.
    _taskManager.Enqueue(() => SellListRowSteps.OpenRow(item, isReprice: false),
      $"StandingRightClick_{item.ItemName}");
    _taskManager.DelayNext(500);
    _taskManager.Enqueue(() => SellListRowSteps.Skipped(item) ? true : SellListRowSteps.FailClosed(item, GameNavigation.ClickReturnToInventory()),
      $"StandingReturn_{item.ItemName}");
    _taskManager.DelayNext(1000);

    if (item.QueuedAction == StandingAction.Vendor)
    {
      // Vendor: wait for the pulled item to land in bags (server round-trip,
      // variable latency), then right-click → "Have Retainer Sell Items".
      // Returning false retries each tick; a hard deadline marks the ITEM
      // skipped so one slow arrival costs one item, never the batch. The
      // item stays in the player's bags either way - nothing is lost.
      _taskManager.Enqueue(SellListRowSteps.BagArrivalWait(item), $"StandingClickInv_{item.ItemName}");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(() => { if (!SellListRowSteps.Skipped(item)) { _catchallBlock?.Dispose(); _catchallBlock = GilTrackingState.Block("triage_vendor"); } return true; },
        $"StandingBlockCatchall_{item.ItemName}");
      _taskManager.Enqueue(() => SellListRowSteps.Skipped(item) ? true : SellListRowSteps.FailClosed(item, GameNavigation.ClickVendorSellItem()),
        $"StandingVendor_{item.ItemName}");
      _taskManager.DelayNext(500);

      // Track the sale and retire the row (flags close on COMPLETION, not queue)
      _taskManager.Enqueue(() => { if (!SellListRowSteps.Skipped(item)) { TrackVendorSale(item); RoutingReceiptStamp.NeverCleared(item.ItemId, item.IsHq); Plugin.Accountant.RemoveItem(item); } return true; },
        $"StandingTrack_{item.ItemName}");
      _taskManager.Enqueue(() => { _catchallBlock?.Dispose(); _catchallBlock = null; return true; },
        $"StandingUnblockCatchall_{item.ItemName}");
    }
    else
    {
      // Pull, and pull-for-X (Melt / Gc, unit 5) — the retrieve is identical;
      // the intent banked here on completion is what makes the destination
      // fire at its own stop, and makes the router never re-ask.
      _taskManager.Enqueue(() =>
      {
        if (!SellListRowSteps.Skipped(item))
        {
          _pulledCount++; _run.RecordProgress(1, 0, DateTime.UtcNow); NarratePull(item); BookRemoval(item, "triage");
          BankPullIntent(item);
          RoutingReceiptStamp.NeverCleared(item.ItemId, item.IsHq); Plugin.Accountant.RemoveItem(item);
        }
        return true;
      }, $"StandingPulled_{item.ItemName}");
    }

    // Continue to next item
    _taskManager.Enqueue(ProcessNext, "StandingProcessNext");
    return true;
  }

  /// <summary>
  /// Banks the pull-for-X ruling once the retrieve has actually happened (walk
  /// ruling 2). Plain pulls bank nothing - they answered "off the board", not
  /// "where next". Best-effort with a warning: a lost intent costs one re-ask,
  /// never the pull.
  /// </summary>
  private static void BankPullIntent(PricingItem item)
  {
    if (item.QueuedAction is not (StandingAction.Melt or StandingAction.Gc)) return;
    try { GilStorage.UpsertPullIntent(item.ItemId, item.IsHq, item.QueuedAction.ToString()); }
    catch (Exception ex) { Svc.Log.Warning($"[Standing] Pull intent not banked for {item.ItemName}: {ex.Message}"); }
  }

  /// <summary>
  /// Narrates a pull - the listing came off the board and the item is in the bags.
  /// Rendered as a Banned-blue "observed, not sold" line rather than a green earn,
  /// because a pull moves an item without earning a coin.
  /// </summary>
  private static void NarratePull(PricingItem item)
  {
    Plugin.Ledger.SetCurrentRetainer(item.RetainerName);
    var ask = item.CurrentListingPrice is int p && p > 0 ? $" (was listed at {p:N0})" : "";
    Plugin.Ledger.AddEntry(ItemOutcome.Banned, item.ItemName,
      $"Pulled to inventory{ask}");
    Plugin.Ledger.IncrementProcessed();
  }

  /// <summary>
  /// Books a listing leaving the board at the ask it was standing at (WALK unit 6).
  /// A row with no known listing price contributes nothing rather than a guessed
  /// zero-value removal - an unknown operand is silence, not a number.
  /// </summary>
  private static void BookRemoval(PricingItem item, string source)
  {
    if (item.CurrentListingPrice is not int ask || ask <= 0) return;
    StandingBookFeed.Removed(item.ItemId, item.IsHq, item.RetainerName, ask,
      Math.Max(1, item.Quantity), source);
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

    Svc.Log.Warning($"[Standing] Retainer '{retainerName}' not found — skipping");
    return true;
  }

  /// <summary>Tracks a vendor sale for the triage summary.</summary>
  private void TrackVendorSale(PricingItem item)
  {
    // The listing left the board at its ASK - that is what leaves the standing
    // book. The vendor price below is what we then got for the item in the bags;
    // it is a gil event, not a board event, and must not be confused for one.
    BookRemoval(item, "triage");

    // Narrate it. This is the line that did not exist on 07-24, when five vendored
    // listings left the board with nothing in the run log to show for them.
    Plugin.Ledger.SetCurrentRetainer(item.RetainerName);

    var totalGil = VendorSaleBook.Record(item.ItemId, item.ItemName, item.IsHq,
      item.Quantity, RunLogVoice.Reasons.BelowWhatTheBoardPays);

    _vendorSoldCount++;
    _vendorSoldGil += totalGil;
    _run.RecordProgress(1, totalGil, DateTime.UtcNow); // one row done + gil earned
    Plugin.Ledger.IncrementProcessed();
  }

  /// <summary>Auto-clicks the retainer greeting dialog.</summary>
  private unsafe void SkipRetainerDialog(AddonEvent type, AddonArgs args)
  {
    if (!_taskManager.IsBusy)
      RemoveTalkListeners();
    else if (((AtkUnitBase*)args.Addon.Address)->IsVisible)
      new AddonMaster.Talk(args.Addon).Click();
  }

  private void RemoveTalkListeners()
  {
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);
  }
}
