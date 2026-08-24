using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// THE PINCH RUN: the board pass that walks every enabled retainer's sell list and
/// re-asks the market what each listing should cost. Two doors into the same chain -
/// the whole bell roster (<see cref="PinchAllRetainers"/>) and one open sell list
/// (<see cref="PinchAllRetainerItems"/>) - and one per-item ladder underneath both.
///
/// <para>An executor of the same shape as every other one in this plugin: its own
/// spine gate, its own queue-wedge watchdog, its own listener registrations, and one
/// cancel door that tears all of it down. It borrows the host's task manager, which
/// is what makes "another run is already working" true across the pinch, the bell and
/// recon without a second busy flag - and it borrows the vendor rider, which drains
/// its rows inside this run's retainer visits rather than as an errand of its own.</para>
/// </summary>
internal sealed class PinchRunExecutor : IDisposable
{
  /// <summary>
  /// The pinch's declared expected state (spine): the bell roster (RetainerList)
  /// must be open. Unmet is a WalkThere gap - the round deck is what names the
  /// walk. The bell-roster pinch keeps its historical SILENT no-op when it is
  /// not at the roster (this routes the same check through the one evaluator and
  /// gives the executor a declared contract; it does not add a chat line).
  /// </summary>
  internal static readonly ExpectedState PinchExpected = new("pinch",
    new SpineExpectation(Spine.Facet.Place, "to be at a retainer bell", Spine.Rung.WalkThere));

  private static List<FacetReading> ReadPinchState() => new()
  {
    SpineSensors.AddonReady("RetainerList", "you're not at a retainer bell"),
  };

  private readonly TaskManager _taskManager;
  private readonly ItemPricingPipeline _pricing;
  private readonly VendorRiderExecutor _rider;
  private readonly Func<int, int> _applyJitter;

  /// <summary>
  /// Wedge watchdog for the plain pinch (shake finding SF1, 2026-08-12): the ECommons
  /// TaskManager's TimeLimitMS clears its queue WITHOUT telling us - the 23:05 pinch
  /// died 3 items into 102 and nothing reported, so the round sailed on over an
  /// unpinched board.
  ///
  /// <para>The pinch was the one executor with no flag AND no watchdog, because its
  /// only "running" state IS the queue the timeout just emptied. So its liveness is
  /// read off the run itself: a live pinch's queue is never empty (every step through
  /// EndRunLog is enqueued up front, and EndRunLog nulls CurrentRun from inside its
  /// own task), which makes a plain-pinch CurrentRun over an idle TaskManager mean the
  /// queue died and nothing else. Standing runs are excluded - they are a pinch by
  /// pricing mode, not by errand, and their own orchestrator watches them.</para>
  ///
  /// <para>Armed at pinch start; it retires itself the moment the run ends by any door
  /// (completion, button cancel, Draw catch - all of them null CurrentRun).</para>
  /// </summary>
  private readonly QueueWedgeWatchdog _wedge;

  internal PinchRunExecutor(TaskManager taskManager, ItemPricingPipeline pricing,
    VendorRiderExecutor rider, Func<int, int> applyJitter)
  {
    _taskManager = taskManager;
    _pricing = pricing;
    _rider = rider;
    _applyJitter = applyJitter;
    _wedge = new QueueWedgeWatchdog(
      isLive: () => Plugin.CurrentRun is { Mode: RunMode.Pinch, IsStandingRun: false },
      isBusy: () => _taskManager.IsBusy,
      onWedged: OnPinchWedged);

    // NAMED, NOT PROMISCUOUS (ruled 2026-08-23): unfiltered, this fired on EVERY
    // addon's PostSetup - held SHIFT plus any window opening sent the idle queue on
    // a ~10s dead errand (ClickComparePrice with no sell panel to click). The handler
    // was always written for the sell-price panel; now the subscription says so.
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSell", RetainerSellPostSetup);
  }

  public void Dispose()
  {
    _wedge.Disarm();
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSell", RetainerSellPostSetup);
  }

  /// <summary>Whether the pinch task chain is mid-flight (round deck's busy gate).</summary>
  internal bool Busy => _taskManager.IsBusy;

  /// <summary>
  /// The pinch's refusal, said once - the instant path and the grace window's expiry
  /// both end here.
  ///
  /// <para>A REFUSAL IS A RUN THAT ENDED (07-26). The pinch is the round's FIRST stage,
  /// marked done the moment it fires; a refusal nobody reported left the round walking
  /// straight past the board to the melt with nothing pinched and nothing said. It now
  /// reports exactly like the bell run's RefuseList, so the round halts and the rail
  /// names the gap.</para>
  ///
  /// <para><b>The player's press is untouched.</b> Outside a round this is a human
  /// leaning on the overlay button, and the pinch has ALWAYS no-opped silently there -
  /// there is no round to halt and no run he asked to be told about.
  /// <see cref="GracePlan.ShouldReport"/> is the one place that distinction lives.</para>
  /// </summary>
  private static void RefusePinch(SpineEvaluation eval, bool playerPressed)
  {
    if (!GracePlan.ShouldReport(playerPressed)) return;
    Svc.Chat.PrintError($"[Scrooge] {eval.Message}");
    // No run behind this one - the pinch never started. RunFacts.None is the truth,
    // not a gap: nothing was processed.
    RunFlow.ReportDied(RunKind.Pinch, eval.Message, run: null);
  }

  /// <summary>
  /// Entry point for "pinch all retainers": iterates every enabled retainer,
  /// opens their sell list, and queues price adjustments for all their items.
  /// Registers Talk dialog listeners to auto-dismiss retainer greeting dialogs.
  /// </summary>
  internal unsafe void PinchAllRetainers()
  {
    if (_taskManager.IsBusy)
      return;

    // Spine gate: the pinch expects the bell roster. Routed through the one
    // evaluator so the contract is declared and honest; the body's addon fetch
    // below is now just the pointer read (the same condition the spine checked).
    // Read ONCE, up top: the same fact decides whether the refusal waits and whether
    // it reports (see GracePlan).
    var playerPressed = !Plugin.Accountant.RoundActive;

    var eval = SpineEvaluator.Evaluate(PinchExpected, ReadPinchState());
    if (!eval.CanFire)
    {
      // The stage-boundary grace (2026-07-26). The pinch is the round's FIRST stage
      // and it fires the moment the roster opens - which is exactly the moment
      // AutoRetainer is most likely to be mid-summon and the roster least likely to
      // be ready. Wait the window out inside a round; outside one this is a human's
      // overlay press and the silent no-op is unchanged.
      if (GracePlan.ShouldWaitOut(eval, playerPressed)
          && SpineGrace.Hold("the retainer roster", GracePlan.AutoFireGraceMs,
               met: () => SpineEvaluator.Evaluate(PinchExpected, ReadPinchState()).CanFire,
               onReturned: PinchAllRetainers,
               onExpired: () => RefusePinch(eval, playerPressed)))
        return;

      RefusePinch(eval, playerPressed);
      return;
    }

    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      // Auto-dismiss the "Talk" dialog that appears when opening each retainer
      Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
      Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);

      // we cache the number of retainers because AddonMaster will be disposed once the RetainerList addon is closed.
      var retainerList = new AddonMaster.RetainerList(addon);
      var retainers = retainerList.Retainers;
      var num = retainers.Length;

      // Check if all are disabled (sentinel present)
      bool allDisabled = Plugin.Configuration.EnabledRetainerNames.Contains(Configuration.ALL_DISABLED_SENTINEL);

      // If all are disabled, skip all retainers and notify user. A REFUSAL IS A RUN
      // THAT ENDED (07-26): inside a round this must report like every other refusal,
      // or the fired stage waits forever on a completion that is never coming (SF2's
      // in-flight latch holds the flow until the stage speaks). The player's own
      // press keeps the historical chat line and nothing more.
      if (allDisabled)
      {
        Communicator.PrintAllRetainersDisabled();
        if (GracePlan.ShouldReport(playerPressed))
          RunFlow.ReportDied(RunKind.Pinch, "every retainer is disabled in the pinch settings", run: null);
        return;
      }

      _pricing.ClearState();
      Plugin.CurrentRun = new RunData { Mode = RunMode.Pinch };
      Plugin.Ledger.StartNewRun();
      if (Plugin.Configuration.EnableGilTracking)
        GilTracker.StartRun();

      // Vendor rider (WALK unit 3): snapshot the unanimous Pull & Vendor rows now,
      // grouped by retainer, and drain each retainer's set inside its own visit
      // below. The snapshot arms its own buyback-dismiss listener when rows ride.
      _rider.Snapshot();

      // If no retainers are explicitly enabled, enable all by default
      bool allEnabled = Plugin.Configuration.EnabledRetainerNames.Count == 0;

      // Pre-calculate total items from RetainerList addon AtkValues
      // Layout: base offset 3, 10 values per retainer, offset 6 = "Selling X items" text
      // See: RetainerList Addon - AtkValue Map.md
      int preRunTotal = 0;
      // Fleet capacity, banked from the SAME roster read (gate 9b): every
      // retainer counts here - a disabled retainer's slots are still slots the
      // hawk could fill, so the capacity walk ignores the pinch filter.
      int fleetFree = 0;
      for (int i = 0; i < num; i++)
      {
        var retainerName = retainers[i].Name;
        var selling = GameNavigation.GetRetainerListingCount(addon, i);
        fleetFree += 20 - selling;
        if (!allEnabled && !Plugin.Configuration.EnabledRetainerNames.Contains(retainerName))
          continue;

        preRunTotal += selling;
      }
      Plugin.Ledger.SetTotalItems(preRunTotal);
      FleetCapacity.Observe(fleetFree, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

      for (int i = 0; i < num; i++)
      {
        var retainerName = retainers[i].Name;

        // Skip retainers that are excluded in configuration
        if (!allEnabled && !Plugin.Configuration.EnabledRetainerNames.Contains(retainerName))
        {
          Svc.Log.Debug($"Skipping retainer '{retainerName}' (excluded by user configuration)");
          continue;
        }
        EnqueueSingleRetainer(i, retainerName);
      }

      _taskManager.Enqueue(RemoveTalkAddonListeners);
      _taskManager.Enqueue(() => { _rider.Cleanup(); return true; }, "RiderCleanup");
      if (Plugin.Configuration.TTSWhenAllDone)
        _taskManager.Enqueue(() => TtsAnnouncer.Speak(Plugin.Configuration.TTSWhenAllDoneMsg), "SpeakTTSAll");

      _taskManager.Enqueue(() => {
        Plugin.Ledger.EndRun();
        var pinched = Plugin.CurrentRun;
        Plugin.CurrentRun = null;
        if (Plugin.Configuration.EnableGilTracking)
          GilTracker.FinalizeRun();
        // No flash here (ruled 08-15): the completion handler owns the taskbar -
        // it flashes standalone runs and stays quiet when the round will chain.
        // The board was just re-read end to end - this is the ground truth the
        // Ledger's header, its listings, and the standing book all resync to. The
        // run rides along so the rail can say how much it read (RunFacts).
        RunFlow.ReportDone(RunKind.Pinch, pinched);
        return true;
      }, "EndRunLog");

      _wedge.Arm();
    }
  }

  /// <summary>
  /// Queues the full sequence for one retainer: click retainer → open sell list →
  /// process all items → close sell list → close retainer.
  /// </summary>
  /// <param name="index">Retainer index in the RetainerList addon (0-based).</param>
  /// <param name="retainerName">The retainer's name - the key its vendor riders were grouped under.</param>
  private void EnqueueSingleRetainer(int index, string retainerName)
  {
    _taskManager.Enqueue(() => GameNavigation.ClickRetainer(index), $"ClickRetainer{index}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(GameNavigation.ClickSellItems, $"ClickSellItems{index}");
    _taskManager.DelayNext(500);

    // Gil tracking: set retainer context and snapshot all listings from the sell list
    if (Plugin.Configuration.EnableGilTracking)
    {
      _taskManager.Enqueue(() => {
        var name = GameSafe.ActiveRetainerName();
        if (name == null)
        {
          Svc.Log.Warning("[GilTrack] Couldn't read active retainer — skipping listing snapshot");
          return true;
        }
        GilTracker.SetRetainer(name);
        GilTracker.SnapshotListings();
        return true;
      }, $"SnapshotListings{index}");
    }

    _taskManager.Enqueue(() => EnqueueAllRetainerItems(InsertSingleItem, true), $"EnqueueAllRetainerItems{index}");
    _taskManager.DelayNext(500);

    // Vendor rider: with the sell list still open and the reprice pass done, pull
    // and vendor this retainer's unanimous Pull & Vendor rows (WALK unit 3). Same
    // window, same retainer - no separate errand. Dispatched (not pre-built)
    // because the reprice pass inserts its steps at runtime; the dispatch inserts
    // the rider's steps ahead of the close below, so they run while the list is up.
    _taskManager.Enqueue(() => { _rider.Dispatch(retainerName); return true; }, $"RiderDispatch{index}");

    _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, $"CloseRetainerSellList{index}");
    _taskManager.DelayNext(100);

    // Gil tracking: view sale history to capture sales via hook
    if (Plugin.Configuration.EnableGilTracking)
    {
      _taskManager.Enqueue(GameNavigation.ClickSaleHistory, $"ClickSaleHistory{index}");
      _taskManager.DelayNext(1500); // wait for server response + hook to fire
      _taskManager.Enqueue(GameNavigation.CloseSaleHistory, $"CloseSaleHistory{index}");
      _taskManager.DelayNext(100);
    }

    _taskManager.Enqueue(GameNavigation.CloseRetainer, $"CloseRetainer{index}");
    _taskManager.DelayNext(100);
  }

  /// <summary>
  /// Entry point for "pinch the retainer whose sell list is already open" - the
  /// overlay button on RetainerSellList, and the post-pinch hotkey's sibling.
  /// </summary>
  internal void PinchAllRetainerItems()
  {
    if (_taskManager.IsBusy)
      return;

    // Read before mutating any run state — fail closed if the retainer
    // can't be resolved (avoids a half-started run).
    var retainerName = GameSafe.ActiveRetainerName();
    if (retainerName == null)
    {
      Svc.Chat.PrintError("[Scrooge] Couldn't read the active retainer — try reopening the sell list.");
      return;
    }

    _pricing.ClearState();
    Plugin.CurrentRun = new RunData { Mode = RunMode.Pinch };
    Plugin.Ledger.StartNewRun();

    // Get total items from the sell list
    if (GameSafe.RetainerSellListLength() is int totalItems)
      Plugin.Ledger.SetTotalItems(totalItems);

    // Set retainer name for log grouping (ClickRetainer doesn't fire for single-retainer runs)
    Plugin.Ledger.SetCurrentRetainer(retainerName);

    // Gil tracking: start run, set retainer, snapshot
    if (Plugin.Configuration.EnableGilTracking)
    {
      GilTracker.StartRun(retainerName);
      GilTracker.SetRetainer(retainerName);
      _taskManager.Enqueue(() => { GilTracker.SnapshotListings(); return true; }, "SnapshotListings");
    }

    EnqueueAllRetainerItems(EnqueueSingleItem, false);

    // Gil tracking: close sell list → view sale history → reopen sell list
    if (Plugin.Configuration.EnableGilTracking)
    {
      _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "GilTrack_CloseSellList");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(GameNavigation.ClickSaleHistory, "GilTrack_ClickSaleHistory");
      _taskManager.DelayNext(1500);
      _taskManager.Enqueue(GameNavigation.CloseSaleHistory, "GilTrack_CloseSaleHistory");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(GameNavigation.ClickSellItems, "GilTrack_ReopenSellList");
      _taskManager.DelayNext(100);
    }

    _taskManager.Enqueue(() => {
      Plugin.Ledger.EndRun();
      var pinched = Plugin.CurrentRun;
      Plugin.CurrentRun = null;
      if (Plugin.Configuration.EnableGilTracking)
        GilTracker.FinalizeRun();
      // No flash here (ruled 08-15) - the completion handler owns the taskbar.
      RunFlow.ReportDone(RunKind.Pinch, pinched);
      return true;
    }, "EndRunLog");

    _wedge.Arm();
  }

  /// <summary>The pinch's teardown when its queue dies under it - see <see cref="_wedge"/>.</summary>
  private void OnPinchWedged()
  {
    Svc.Chat.PrintError(
      "[Scrooge] Pinch stopped early - a step timed out and its queue died. Run closed; items already pinched kept their new prices.");
    RemoveTalkAddonListeners();
    _rider.Cleanup();
    Plugin.Ledger.CancelRun();
    var died = Plugin.CurrentRun;
    Plugin.CurrentRun = null;
    RunFlow.ReportDied(RunKind.Pinch, "a step timed out and the task queue died", died);
  }

  /// <summary>
  /// Cancels a live pinch run: queue dropped, listeners and riders torn down, the
  /// death reported with the facts the run had banked. ONE definition for the
  /// overlay's Cancel button and the round's Abandon (SF3-N: on 08-12 an abandoned
  /// round's pinch kept walking the boards for minutes after its round was gone).
  /// The run hands over its facts before it is torn down (RunFacts) - a cancel
  /// halfway through a board pass still processed everything up to the press.
  /// </summary>
  internal void CancelPinchRun(string reason)
  {
    _taskManager.Abort();
    RemoveTalkAddonListeners();
    _rider.Cleanup();
    Plugin.Ledger.CancelRun();
    var cancelled = Plugin.CurrentRun;
    Plugin.CurrentRun = null;
    RunFlow.ReportDied(RunKind.Pinch, reason, cancelled);
  }

  /// <summary>Iterates all items in the current retainer's sell list and queues them for processing.</summary>
  /// <param name="enqueueFunc">Function to queue each item (EnqueueSingleItem or InsertSingleItem).</param>
  /// <param name="reverseOrder">If true, process items bottom-to-top (needed for Insert-based queuing).</param>
  private bool? EnqueueAllRetainerItems(Action<int> enqueueFunc, bool reverseOrder)
  {
    if (GameSafe.RetainerSellListLength() is int num)
    {
      if (reverseOrder)
      {
        for (int i = num - 1; i >= 0; i--)
        {
          enqueueFunc(i);
        }
      }
      else
      {
        for (int i = 0; i < num; i++)
        {
          enqueueFunc(i);
        }
      }
      if (Plugin.Configuration.TTSWhenEachDone)
        _taskManager.Enqueue(() => TtsAnnouncer.Speak(Plugin.Configuration.TTSWhenEachDoneMsg), "SpeakTTSEach");

      return true;
    }
    else
      return false;
  }

  /// <summary>Queues the price adjustment steps for a single item (forward order).</summary>
  /// <param name="index">Item index in the RetainerSellList addon (0-based).</param>
  private void EnqueueSingleItem(int index)
  {
    _taskManager.Enqueue(() => { if (Plugin.CurrentRun != null) Plugin.CurrentRun.CurrentItem = new PricingItem { SlotIndex = index }; return true; }, $"InitItem{index}");
    _taskManager.Enqueue(() => GameNavigation.OpenItemContextMenu(index), $"OpenItemContextMenu{index}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(RetainerPanelActions.ClickAdjustPrice, $"ClickAdjustPrice{index}");
    _taskManager.DelayNext(100);
    // Ask the board and wait for the WHOLE of it: DelayMB -> ClickComparePrice ->
    // four escalating await windows, re-firing the request between them, before
    // SetNewPrice holds on a genuine no-response. Each window early-outs the instant
    // data lands. Spelled once, in BoardReadLadder - the pinch used to hand-roll it
    // here and again in InsertSingleItem, which was three copies of one chain and
    // three places for a window count to drift. The task names are unchanged: the
    // pinch's naming rides in as Labels.ForIndex.
    _pricing.Board.EnqueueBoardRead(BoardReadLadder.Labels.ForIndex(index));
    _taskManager.Enqueue(_pricing.SetNewPrice, $"SetNewPrice{index}");
  }

  /// <summary>
  /// Same as EnqueueSingleItem but uses Insert (prepend) instead of Enqueue (append).
  /// Steps are added in reverse order because Insert pushes to the front of the queue.
  /// Used when processing items within the PinchAllRetainers flow.
  /// </summary>
  /// <param name="index">Item index in the RetainerSellList addon (0-based).</param>
  private void InsertSingleItem(int index)
  {
    _taskManager.Insert(_pricing.SetNewPrice, $"SetNewPrice{index}");
    // The same board read as EnqueueSingleItem, pushed to the front instead of the
    // back - the ladder emits its steps in reverse so they come out in order. Same
    // Labels, so the task names are byte-identical to the enqueue path's.
    _pricing.Board.InsertBoardRead(BoardReadLadder.Labels.ForIndex(index));
    _taskManager.InsertDelayNext(100);
    _taskManager.Insert(RetainerPanelActions.ClickAdjustPrice, $"ClickAdjustPrice{index}");
    _taskManager.InsertDelayNext(100);
    _taskManager.Insert(() => GameNavigation.OpenItemContextMenu(index), $"OpenItemContextMenu{index}");
    _taskManager.Insert(() => { if (Plugin.CurrentRun != null) Plugin.CurrentRun.CurrentItem = new PricingItem { SlotIndex = index }; return true; }, $"InitItem{index}");
  }

  /// <summary>
  /// Auto-clicks the retainer greeting dialog. Handed to the bell and recon
  /// orchestrators too - all three walk retainers, and all three meet the same Talk
  /// window on the way in.
  /// </summary>
  internal unsafe void SkipRetainerDialog(AddonEvent type, AddonArgs args)
  {
    // fallback for when something was improperly cleaned up
    if (!_taskManager.IsBusy)
      RemoveTalkAddonListeners();
    else
    {
      if (((AtkUnitBase*)args.Addon.Address)->IsVisible)
        new AddonMaster.Talk(args.Addon).Click();
    }
  }

  /// <summary>
  /// Triggered when posting a new item to the MB. If the post-pinch hotkey
  /// is held, automatically fetches the lowest price and undercuts it.
  /// </summary>
  private void RetainerSellPostSetup(AddonEvent type, AddonArgs args)
  {
    if (_taskManager.IsBusy)
      return;

    if (Plugin.Configuration.EnablePostPinchkey && Plugin.KeyState[Plugin.Configuration.PostPinchKey])
    {
      _taskManager.Enqueue(_pricing.Board.ClickComparePrice, $"ClickComparePricePosted");
      _taskManager.DelayNext(_applyJitter(Plugin.Configuration.MarketBoardKeepOpenMS));
      _taskManager.Enqueue(_pricing.SetNewPrice, $"SetNewPricePosted");
    }
  }

  /// <summary>Drops the Talk-dialog listeners. Shared with the bell and recon runs.</summary>
  internal void RemoveTalkAddonListeners()
  {
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);
  }
}
