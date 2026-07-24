using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Scrooge.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Utility;

namespace Scrooge;

/// <summary>
/// Orchestrates desynth runs over the SalvageItemSelector addon.
/// Mirrors HawkRunOrchestrator: TaskManager-driven, queue-based, addon-aware.
///
/// Pacing is intentionally slow. See spec's "Pacing and humanization" section.
/// </summary>
internal sealed class DesynthOrchestrator : IDisposable
{
  /// <summary>
  /// Conservative free-slot floor before we'll start a run. Each desynth can
  /// produce multiple yield types, and a player who runs out of inventory
  /// mid-run gets a stuck dialog and a half-completed run. Bail before we
  /// start rather than mid-run.
  /// </summary>
  private const int MinFreeInventorySlots = 5;

  /// <summary>
  /// The melt's declared expected state (spine). Two facets, in report order:
  /// the salvage window must be open, and the player must not be occupied - the
  /// game refuses Desynthesize otherwise. Both are dead ends the advisor cannot
  /// self-clear (it does not walk you to Mutamix, and it will not close your
  /// bell for you), so both refuse loudly, naming the gap.
  /// </summary>
  internal static readonly ExpectedState MeltExpected = new("melt",
    new SpineExpectation(Spine.Facet.View, "the desynthesis window open", Spine.Rung.Refuse),
    new SpineExpectation(Spine.Facet.Occupancy, "to be un-occupied", Spine.Rung.Refuse));

  /// <summary>Reads the melt's expected-state facets from live game sensors.</summary>
  private static System.Collections.Generic.List<FacetReading> ReadMeltState() => new()
  {
    SpineSensors.AddonReady("SalvageItemSelector", "it isn't open (talk to Mutamix first)"),
    SpineSensors.Unoccupied(),
  };

  private readonly TaskManager _taskManager;
  private readonly Random _random = new();

  private Queue<DesynthItem>? _queue;
  private int _processed;
  private int _itemsUntilNextLongPause;

  /// <summary>
  /// True when the run may auto-continue: the player took everything eligible
  /// (Select All intent), so a window that repopulates after the queue drains
  /// (the game's agent list truncates large inventories — run 74 melted
  /// exactly 100 with a known eligible left over) refills the queue instead
  /// of announcing a dishonest plain success. Hand-picked subsets never
  /// auto-continue; they get a leftover report instead.
  /// </summary>
  private bool _autoContinue;

  /// <summary>
  /// Slots already attempted this run. A rescan only feeds the queue slots we
  /// have not touched — a stuck item must not produce an infinite
  /// rescan-melt-rescan loop.
  /// </summary>
  private readonly HashSet<(InventoryType Container, int Slot)> _attempted = new();

  /// <summary>_processed at the start of the current round; a continuation
  /// round that makes no progress ends the run loudly instead of rescanning.</summary>
  private int _roundStartProcessed;

  /// <summary>
  /// Per-run monotonic counter incremented at the head of every act (including
  /// stack continuations). Yields from one act share the same value, used as
  /// desynth_yields.attempt_seq for grouping.
  /// </summary>
  private int _currentAttemptSeq;

  /// <summary>Read by DesynthYieldTracker to stamp the attempt_seq on incoming yield rows.</summary>
  internal int CurrentAttemptSeq => _currentAttemptSeq;

  /// <summary>True while a desynth run is in progress.</summary>
  internal bool IsRunning { get; private set; }

  /// <summary>
  /// Incremented every time a run closes WITHOUT completing (user abort, addon
  /// timeout, watchdog). The sweep deck polls this to un-mark its Desynth stage -
  /// a stage marked done at fire time must not stay "done" over a run that died.
  /// </summary>
  internal int AbortEpoch { get; private set; }

  /// <summary>
  /// The game refuses the Desynthesize command outright while the player is in
  /// any occupied state - the 07-22 sweep lap died exactly this way (desynth
  /// fired over an open retainer bell; SalvageDialog force-opened fine, then
  /// the server answered "Unable to execute command while occupied" and the run
  /// timed out). Force-openable UI is not permission: check BEFORE starting.
  /// </summary>
  internal static bool PlayerOccupied(out string why)
  {
    if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
    {
      why = "the retainer bell is open";
      return true;
    }
    if (Svc.Condition[ConditionFlag.OccupiedInEvent]
        || Svc.Condition[ConditionFlag.OccupiedInQuestEvent]
        || Svc.Condition[ConditionFlag.Occupied]
        || Svc.Condition[ConditionFlag.Occupied30]
        || Svc.Condition[ConditionFlag.Occupied33]
        || Svc.Condition[ConditionFlag.Occupied38]
        || Svc.Condition[ConditionFlag.Occupied39])
    {
      why = "you're occupied (an NPC or window has you)";
      return true;
    }
    why = "";
    return false;
  }

  internal DesynthOrchestrator()
  {
    _taskManager = new TaskManager
    {
      TimeLimitMS = 10000,
      AbortOnTimeout = true,
    };
  }

  /// <summary>
  /// Per-action humanizer. Owns its own randomness; deliberately independent of
  /// `Configuration.EnableJitter` (which is an AutoPinch-scoped knob and defaults
  /// off). Pacing is "non-negotiable" per the spec — it must not be coupled to a
  /// plugin-wide toggle a player might flip for unrelated reasons. Bands match
  /// the spec's "Pacing and humanization" table per call site.
  /// </summary>
  private int Jitter(int baseMs, int band)
  {
    var offset = (int)(((_random.NextDouble() * 2.0) - 1.0) * band);
    return Math.Max(1, baseMs + offset);
  }

  public void Dispose()
  {
    _taskManager.Abort();
    Svc.Framework.Update -= WatchdogTick;
  }

  /// <summary>Resets state on error/abort.</summary>
  internal void Abort()
  {
    _taskManager.Abort();
    CloseRunAborted("user-initiated abort or addon closed mid-run");
  }

  /// <summary>
  /// The ONE way a run dies: every abort path (user, addon timeout, watchdog)
  /// funnels here so the run row is stamped aborted, the busy flag drops, and
  /// the sweep deck learns the stage did not finish. The 07-22 leak was this
  /// hygiene existing in Abort() but not in the timeout paths - the TaskManager's
  /// own TimeLimitMS cleared the queue and left IsRunning true forever.
  /// </summary>
  private void CloseRunAborted(string reason)
  {
    IsRunning = false;
    _queue = null;
    Plugin.PinchRunLog.CancelRun();
    if (Plugin.CurrentRun != null && Plugin.CurrentRun.Mode == RunMode.Desynth)
    {
      if (Plugin.CurrentRun.DesynthRunId is long runId)
      {
        try
        {
          Plugin.DesynthYieldStore?.AbortRun(runId, DateTimeOffset.UtcNow, reason);
        }
        catch (Exception ex)
        {
          Svc.Log.Error(ex, "Failed to update desynth_runs row on abort");
        }
      }
      Plugin.CurrentRun = null;
    }
    AbortEpoch++;
  }

  /// <summary>
  /// Backstop for the backstop: if the ECommons TaskManager's TimeLimitMS ever
  /// fires (it clears the queue WITHOUT telling us), the run would otherwise
  /// stay "in progress" forever and wedge every busy-gate in the plugin. A live
  /// run's queue is never empty (each task enqueues its successor), so
  /// IsRunning && !IsBusy can only mean the queue died. Subscribed at run
  /// start; unsubscribes itself when the run is over.
  /// </summary>
  private void WatchdogTick(Dalamud.Plugin.Services.IFramework _)
  {
    if (!IsRunning)
    {
      Svc.Framework.Update -= WatchdogTick;
      return;
    }
    if (_taskManager.IsBusy) return;

    Svc.Framework.Update -= WatchdogTick;
    Svc.Chat.PrintError(
      "[Scrooge] Desynth run stalled (task queue died without finishing) - run closed. " +
      "The melt pile is untouched; run it again when you're clear.");
    CloseRunAborted("watchdog: task queue died without run end");
  }

  /// <summary>
  /// Entry point. Called by DesynthPreviewWindow when user clicks Run.
  /// <paramref name="allEligibleSelected"/> = the player took every
  /// non-protected item in the scan (Select All intent) — the run may
  /// auto-continue if the window repopulates after the queue drains.
  /// </summary>
  internal unsafe void StartRun(List<DesynthItem> items, bool allEligibleSelected = false)
  {
    if (IsRunning || _taskManager.IsBusy || items.Count == 0)
      return;

    // The salvage window must be open and the player un-occupied (the game
    // refuses Desynthesize otherwise - the desynth windows still force-open,
    // which is the trap). Both flow through the one spine evaluation now, so
    // the refusal names the gap in the same vocabulary every executor uses,
    // instead of timing out ten silent seconds into the run.
    var eval = SpineEvaluator.Evaluate(MeltExpected, ReadMeltState());
    if (!eval.CanFire)
    {
      Svc.Chat.PrintError($"[Scrooge] {eval.Message}");
      return;
    }

    int freeSlots = CountFreeInventorySlots();
    if (freeSlots < MinFreeInventorySlots)
    {
      Svc.Chat.PrintError(
        $"[Scrooge] Only {freeSlots} free inventory slot(s) — need at least {MinFreeInventorySlots} before starting a desynth run. " +
        $"Yields go into your inventory; running out mid-run leaves the desynth dialog stuck.");
      return;
    }

    IsRunning = true;
    Plugin.CurrentRun = new RunData { Mode = RunMode.Desynth };

    // Mode inference: any red/yellow row tags Skillup; otherwise Burn. Imperfect
    // (a player running Burn over a red-eligible inventory would be tagged Skillup).
    // If/when DesynthPreviewWindow surfaces the preset, thread that through for accuracy.
    var modeLabel = items.Any(i =>
        i.Color == DesynthSkillupColor.Red || i.Color == DesynthSkillupColor.Yellow)
      ? "Skillup" : "Burn";

    try
    {
      Plugin.CurrentRun.DesynthRunId =
        Plugin.DesynthYieldStore?.StartRun(modeLabel, items.Count, DateTimeOffset.UtcNow);
    }
    catch (Exception ex)
    {
      Svc.Log.Error(ex, "Failed to insert desynth_runs row — yield capture for this run will be unattributed");
    }
    _currentAttemptSeq = 0;

    // PinchRunLog renders LogEntry rows only under an open RetainerHeader tree.
    // Set a synthetic "retainer" name so per-item rows render. Without this,
    // the run log shows summary lines but no per-item entries.
    Plugin.PinchRunLog.SetCurrentRetainer("Desynth");
    _queue = new Queue<DesynthItem>(items);
    _processed = 0;
    _autoContinue = allEligibleSelected;
    _attempted.Clear();
    _roundStartProcessed = 0;
    _itemsUntilNextLongPause = NextPauseInterval();

    Plugin.PinchRunLog.StartNewRun(isDesynthRun: true);
    Plugin.PinchRunLog.SetTotalItems(items.Count);

    // The TaskManager's own timeout must never RACE a task's internal deadline:
    // on 07-22 both sat at 10s, TimeLimitMS won by milliseconds, and its queue
    // wipe bypassed WaitForAddon's cleanup entirely. Keep the manager's limit
    // comfortably above the longest per-task ceiling so the graceful path
    // always fires first; the watchdog below catches anything that slips.
    _taskManager.TimeLimitMS =
      Math.Max(15000, Plugin.Configuration.ServerRoundTripCeilingMs + 5000);

    Svc.Framework.Update -= WatchdogTick; // defensive: never double-subscribe
    Svc.Framework.Update += WatchdogTick;

    _taskManager.Enqueue(ProcessNext, "DesynthProcessNext");
  }

  /// <summary>
  /// Per-item chain head. Fresh queue items enter through here. Mid-stack
  /// continuations re-enter via <see cref="EnqueueActChain"/> directly with
  /// <c>isContinuation: true</c>.
  /// </summary>
  private unsafe bool? ProcessNext()
  {
    if (!IsRunning) return true;
    if (_queue == null)
    {
      EndRun();
      return true;
    }
    if (_queue.Count == 0)
      return TryContinueOrEnd();

    // Verify the addon is still open. If the player walked away from Mutamix
    // mid-run, abort cleanly.
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SalvageItemSelector", out _))
    {
      Svc.Chat.PrintError("[Scrooge] Desynth window closed mid-run. Aborting.");
      EndRun();
      return true;
    }

    var item = _queue.Peek();
    int agentIndex = FindAgentIndex(item);
    if (agentIndex < 0)
    {
      // Item not in the agent list (player switched filter mid-run, or stack
      // depleted between enqueue and now). Skip and continue.
      _queue.Dequeue();
      Svc.Chat.Print($"[Scrooge] Skipped \"{item.Name}\" — no longer in list.");
      _taskManager.Enqueue(ProcessNext, "DesynthProcessNext");
      return true;
    }

    _attempted.Add((item.Container, item.SlotIndex));
    EnqueueActChain(item, agentIndex, isContinuation: false);
    return true;
  }

  /// <summary>
  /// Queue drained. Rescan the window before declaring success: the game's
  /// agent list truncates large inventories, so a drained queue does not mean
  /// a drained inventory. All-eligible runs refill and continue (progress
  /// guard: a round that melted nothing ends loudly rather than rescanning);
  /// hand-picked runs report the leftovers — fail loud even on success.
  /// </summary>
  private bool? TryContinueOrEnd()
  {
    var leftovers = DesynthInventoryScanner.Scan()
      .FindAll(i => !i.IsProtected && !_attempted.Contains((i.Container, i.SlotIndex)));

    if (leftovers.Count == 0)
    {
      EndRun();
      return true;
    }

    if (!_autoContinue)
    {
      Svc.Chat.Print(
        $"[Scrooge] Desynth run complete — {leftovers.Count} more eligible item(s) still in the window.");
      EndRun();
      return true;
    }

    if (_processed <= _roundStartProcessed)
    {
      Svc.Chat.PrintError(
        $"[Scrooge] Desynth window still shows {leftovers.Count} eligible item(s) but the last round made no progress. Stopping.");
      EndRun();
      return true;
    }

    _roundStartProcessed = _processed;
    _queue = new Queue<DesynthItem>(leftovers);
    Plugin.PinchRunLog.SetTotalItems(_processed + leftovers.Count);
    if (Plugin.CurrentRun?.DesynthRunId is long continuedRunId)
    {
      try
      {
        Plugin.DesynthYieldStore?.UpdateTotalItems(continuedRunId, _processed + leftovers.Count);
      }
      catch (Exception ex)
      {
        Svc.Log.Error(ex, "Failed to update desynth_runs total_items on auto-continue");
      }
    }
    Svc.Chat.Print(
      $"[Scrooge] Desynth window refilled — continuing with {leftovers.Count} more item(s).");

    _taskManager.Enqueue(ProcessNext, "DesynthProcessNext");
    return true;
  }

  // --- Agent / addon helpers ---

  /// <summary>
  /// Finds the current index of the given DesynthItem in AgentSalvage.ItemList.
  /// Returns -1 if not present (player switched filter, item disappeared, etc.).
  ///
  /// Matches by (InventoryType, InventorySlot) — SalvageListItem.ItemId is a
  /// game-internal ID that doesn't map to Lumina Item.RowId, so we use the
  /// slot identity that stays stable across the run.
  /// </summary>
  private static unsafe int FindAgentIndex(DesynthItem item)
  {
    var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentSalvage.Instance();
    if (agent == null) return -1;

    for (int i = 0; i < agent->ItemCount; i++)
    {
      var entry = agent->ItemList[i];
      if (entry.InventoryType == item.Container && (int)entry.InventorySlot == item.SlotIndex)
        return i;
    }
    return -1;
  }

  // --- Callback helpers ---

  private static unsafe void FireSelectorCallback(int itemIndex)
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SalvageItemSelector", out var addon)
        || !GenericHelpers.IsAddonReady(addon)) return;

    var values = stackalloc AtkValue[2];
    values[0] = new AtkValue { Type = AtkValueType.Int, Int = 12 };
    values[1] = new AtkValue { Type = AtkValueType.Int, Int = itemIndex };
    addon->FireCallback(2, values, true);
  }

  /// <summary>
  /// If a SelectYesno popup is open (typically the untradable/unique
  /// confirmation that fires after Confirm on SalvageDialog), click Yes.
  /// No-op if no popup is up. Int=0 = Yes per FFXIV addon convention.
  /// </summary>
  private static unsafe void TryConfirmSelectYesno()
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon)
        || !GenericHelpers.IsAddonReady(addon)) return;

    var values = stackalloc AtkValue[1];
    values[0] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
    addon->FireCallback(1, values, true);
  }

  private static unsafe void FireSalvageDialogConfirm()
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SalvageDialog", out var addon)
        || !GenericHelpers.IsAddonReady(addon)) return;

    var values = stackalloc AtkValue[1];
    values[0] = new AtkValue { Type = AtkValueType.Int, Int = 0 }; // 0 = confirm
    addon->FireCallback(1, values, true);
  }

  /// <summary>
  /// Clicks Retry on SalvageResult: dismisses the result and re-opens
  /// SalvageDialog with the same stack item (quantity decremented). Used to
  /// iterate a stack one act at a time without re-firing FireSelectorCallback.
  /// </summary>
  /// <remarks>
  /// Int=0 verified in live use (2026-07-11): 50-84 item runs complete
  /// hands-off, stacks iterate (consecutive same-source attempt_seq rows).
  /// </remarks>
  private static unsafe void FireRetryCallback()
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SalvageResult", out var addon)
        || !GenericHelpers.IsAddonReady(addon)) return;

    var values = stackalloc AtkValue[1];
    values[0] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
    addon->FireCallback(1, values, true);
  }

  private static unsafe void DismissSalvageResult()
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SalvageResult", out var addon)
        || !GenericHelpers.IsAddonReady(addon)) return;

    // SalvageResult requires explicit close (no auto-dismiss). Close(false)
    // fires the close-callback path equivalent to clicking Close.
    addon->Close(false);
  }

  // --- Chain ---

  /// <summary>
  /// Enqueues one act of the desynth chain. <paramref name="isContinuation"/>
  /// distinguishes fresh-queue-item entry (uses FireSelectorCallback) from
  /// mid-stack continuation (uses FireRetryCallback on the still-open
  /// SalvageResult to re-open SalvageDialog).
  /// </summary>
  private unsafe void EnqueueActChain(DesynthItem item, int agentIndex, bool isContinuation)
  {
    _currentAttemptSeq++;

    // Track for the run log + chat-yield attribution. Set at the start of
    // every act so v2.6.1.0's E-chat parser can attribute yields to the
    // current item.
    _taskManager.Enqueue(() =>
    {
      if (Plugin.CurrentRun != null)
        Plugin.CurrentRun.CurrentItem = new PricingItem { ItemId = item.ItemId, IsHq = item.IsHq };
      return true;
    }, $"DesynthInit_{item.Name}");

    if (!isContinuation)
    {
      // Fresh entry — click the item in the selector to open SalvageDialog.
      _taskManager.Enqueue(() => { FireSelectorCallback(agentIndex); return true; },
        $"DesynthSelect_{item.Name}");

      // Wait for SalvageDialog (lag-absorption ceiling: 4000ms).
      _taskManager.DelayNext(Jitter(800, 200));
      _taskManager.Enqueue(WaitForAddon("SalvageDialog", 4000),
        $"DesynthWaitDialog_{item.Name}");
    }
    else
    {
      // Continuation — click Retry on the still-open SalvageResult. Retry
      // dismisses the result and re-opens SalvageDialog with the stack
      // quantity decremented.
      _taskManager.Enqueue(() => { FireRetryCallback(); return true; },
        $"DesynthRetry_{item.Name}");

      // Same wait band — Retry path also produces SalvageDialog.
      _taskManager.DelayNext(Jitter(800, 200));
      _taskManager.Enqueue(WaitForAddon("SalvageDialog", 4000),
        $"DesynthWaitDialog_{item.Name}");
    }

    // "Read the dialog" beat — humanized 300±150ms before firing confirm.
    _taskManager.DelayNext(Jitter(300, 150));

    // Note: per AddonSalvageDialog (FFXIVClientStructs), the only checkbox
    // exposed is BulkDesynchCheckboxNode (entire-stack), which we never touch.
    // Untradable/unique items do NOT need a pre-Confirm checkbox tick — if
    // they pop a SelectYesno confirmation after Confirm, that's handled
    // separately. This was a misread of the spec; chat-log "I have to check
    // the box each time" referred to a different setup, not this dialog.
    _taskManager.Enqueue(() => { FireSalvageDialogConfirm(); return true; },
      $"DesynthConfirm_{item.Name}");

    // If a SelectYesno popup appears (untradable/unique confirmation), click
    // Yes. Non-blocking — if the popup never shows, we move on to wait for
    // SalvageResult below.
    if (item.RequiresUntradableConfirm)
    {
      _taskManager.DelayNext(Jitter(400, 150));
      _taskManager.Enqueue(() => { TryConfirmSelectYesno(); return true; },
        $"DesynthYesno_{item.Name}");
    }

    // Wait for SalvageResult — strict, abort-on-timeout. Per Q1 (2026-05-03
    // in-game observation), SalvageResult always fires after every desynth.
    // This is the act's server round trip, so it draws the shared ceiling
    // (ServerRoundTripCeilingMs), not the UI-local 4000ms band above.
    _taskManager.DelayNext(Jitter(600, 200));
    _taskManager.Enqueue(WaitForAddon("SalvageResult", Plugin.Configuration.ServerRoundTripCeilingMs),
      $"DesynthWaitResult_{item.Name}");

    // PostActDecision logs the act and decides Retry-vs-Close.
    _taskManager.Enqueue(() => PostActDecision(item), $"DesynthPostAct_{item.Name}");
  }

  /// <summary>
  /// Runs after `WaitForAddon("SalvageResult")` succeeds. Logs the act, runs
  /// long-pause-injection bookkeeping, then decides whether to iterate the
  /// stack (Retry) or advance to the next queue item (Close + dequeue).
  /// </summary>
  private unsafe bool? PostActDecision(DesynthItem item)
  {
    // Bail if a prior task aborted the run (e.g. WaitForAddon timeout).
    if (!IsRunning || _queue == null) return true;

    // Log the act. ItemOutcome.Desynthed gets plain-text render in Task 13.
    _processed++;
    Plugin.PinchRunLog.AddEntry(ItemOutcome.Desynthed, item.Name,
      $"desynthed{(item.IsHq ? " (HQ)" : "")}");
    Plugin.PinchRunLog.IncrementProcessed();

    // V20: stamp the standing routing receipt - the item's Desynth exit
    // executed. Only the newest unexecuted receipt takes the stamp, so stack
    // continuations after the first act are no-ops.
    try { GilStorage.MarkRoutingReceiptExecuted(item.ItemId, item.IsHq, "Desynthed"); } catch { }

    // Long-pause injection — counted per-act, not per-queue-item, so a stack
    // of 99 contributes 99 acts toward the next pause. Decrement first; if
    // <= 0, inject the pause AFTER advancing the chain (so the pause sits
    // between this act's dismiss and the next act's start).
    bool injectLongPause = (--_itemsUntilNextLongPause <= 0)
      && Plugin.Configuration.DesynthHumanPauses;
    if (injectLongPause)
      _itemsUntilNextLongPause = NextPauseInterval();

    // Re-check the agent list. If the item is still there, the stack has
    // more units; iterate via Retry. If it's gone, the stack is depleted
    // (or the player did something weird mid-run); close and advance queue.
    int nextAgentIndex = FindAgentIndex(item);
    bool stackHasMore = nextAgentIndex >= 0;

    // Inter-act beat — humanized base + jitter, plus optional long pause.
    int interActMs = Jitter(Plugin.Configuration.DesynthPerActionBaseMs, 400);
    if (injectLongPause)
      interActMs += _random.Next(3000, 8001);
    _taskManager.DelayNext(interActMs);

    if (stackHasMore)
    {
      // Continue the stack. Retry on the still-open SalvageResult.
      EnqueueActChain(item, nextAgentIndex, isContinuation: true);
    }
    else
    {
      // Stack depleted. Close SalvageResult and advance queue.
      _taskManager.Enqueue(() => { DismissSalvageResult(); return true; },
        $"DesynthClose_{item.Name}");
      _queue!.Dequeue();
      _taskManager.Enqueue(ProcessNext, "DesynthProcessNext");
    }

    return true;
  }

  /// <summary>
  /// Returns a task that polls until the named addon is ready or the timeout
  /// elapses. Aborts the run if the timeout hits — a stale UI state means
  /// we'd misclick.
  ///
  /// Deadline is captured lazily on first poll (not at enqueue time), so the
  /// `DelayNext` that precedes us doesn't eat into our timeout budget.
  /// </summary>
  private System.Func<bool?> WaitForAddon(string addonName, int timeoutMs)
  {
    long? deadline = null;
    return () =>
    {
      deadline ??= Environment.TickCount64 + timeoutMs;
      unsafe
      {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(addonName, out var a)
            && GenericHelpers.IsAddonReady(a))
          return true;
      }
      if (Environment.TickCount64 > deadline)
      {
        Svc.Chat.PrintError($"[Scrooge] Timeout waiting for {addonName}. Aborting.");
        // Don't call _taskManager.Abort() from inside a task — corrupts the
        // tick loop. CloseRunAborted only clears state (and stamps the run row
        // aborted, which the old inline cleanup forgot); the remaining enqueued
        // tasks see IsRunning == false and bail at their guard.
        CloseRunAborted($"timeout waiting for {addonName}");
        return true;
      }
      return false; // keep polling
    };
  }

  private void EndRun()
  {
    Plugin.PinchRunLog.EndRun();
    if (Plugin.CurrentRun?.DesynthRunId is long runId)
    {
      try
      {
        Plugin.DesynthYieldStore?.EndRun(runId, DateTimeOffset.UtcNow);
      }
      catch (Exception ex)
      {
        Svc.Log.Error(ex, "Failed to update desynth_runs row on end");
      }
    }
    Plugin.CurrentRun = null;
    IsRunning = false;
    _queue = null;
    Util.FlashWindow();
    Svc.Chat.Print($"[Scrooge] Desynthed {_processed} items.");
  }

  /// <summary>Random 8..15 — items between long-pause injections.</summary>
  private int NextPauseInterval() => _random.Next(8, 16);

  /// <summary>
  /// Counts empty (ItemId == 0) slots across the four main inventory pages.
  /// Returns 0 if InventoryManager isn't available (defensive — treat as
  /// "can't verify, don't start").
  /// </summary>
  private static unsafe int CountFreeInventorySlots()
  {
    var im = InventoryManager.Instance();
    if (im == null) return 0;

    var containers = new[]
    {
      InventoryType.Inventory1,
      InventoryType.Inventory2,
      InventoryType.Inventory3,
      InventoryType.Inventory4,
    };

    int free = 0;
    foreach (var ct in containers)
    {
      var c = im->GetInventoryContainer(ct);
      if (c == null) continue;
      for (int i = 0; i < c->Size; i++)
      {
        var s = c->GetInventorySlot(i);
        if (s != null && s->ItemId == 0) free++;
      }
    }
    return free;
  }
}
