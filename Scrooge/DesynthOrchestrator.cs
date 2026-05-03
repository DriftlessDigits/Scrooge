using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Scrooge.Windows;
using System;
using System.Collections.Generic;
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

  private readonly TaskManager _taskManager;
  private readonly Random _random = new();

  private Queue<DesynthItem>? _queue;
  private int _processed;
  private int _itemsUntilNextLongPause;

  /// <summary>True while a desynth run is in progress.</summary>
  internal bool IsRunning { get; private set; }

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
  }

  /// <summary>Resets state on error/abort.</summary>
  internal void Abort()
  {
    _taskManager.Abort();
    IsRunning = false;
    _queue = null;
    Plugin.PinchRunLog.CancelRun();
    if (Plugin.CurrentRun != null && Plugin.CurrentRun.Mode == RunMode.Desynth)
      Plugin.CurrentRun = null;
  }

  /// <summary>Entry point. Called by DesynthPreviewWindow when user clicks Run.</summary>
  internal unsafe void StartRun(List<DesynthItem> items)
  {
    if (IsRunning || _taskManager.IsBusy || items.Count == 0)
      return;

    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SalvageItemSelector", out _))
    {
      Svc.Chat.PrintError("[Scrooge] SalvageItemSelector not open. Talk to Mutamix first.");
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
    // PinchRunLog renders LogEntry rows only under an open RetainerHeader tree.
    // Set a synthetic "retainer" name so per-item rows render. Without this,
    // the run log shows summary lines but no per-item entries.
    Plugin.PinchRunLog.SetCurrentRetainer("Desynth");
    _queue = new Queue<DesynthItem>(items);
    _processed = 0;
    _itemsUntilNextLongPause = NextPauseInterval();

    Plugin.PinchRunLog.StartNewRun(); // Task 13 widens the signature to take a desynth flag
    Plugin.PinchRunLog.SetTotalItems(items.Count);

    _taskManager.Enqueue(ProcessNext, "DesynthProcessNext");
  }

  /// <summary>
  /// Per-item chain head. Fresh queue items enter through here. Mid-stack
  /// continuations re-enter via <see cref="EnqueueActChain"/> directly with
  /// <c>isContinuation: true</c>.
  /// </summary>
  private unsafe bool? ProcessNext()
  {
    if (_queue == null || _queue.Count == 0)
    {
      EndRun();
      return true;
    }

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

    EnqueueActChain(item, agentIndex, isContinuation: false);
    return true;
  }

  // --- Agent / addon helpers ---

  /// <summary>
  /// Finds the current index of the given DesynthItem in AgentSalvage.ItemList.
  /// Returns -1 if not present (player switched filter, item disappeared, etc.).
  /// </summary>
  private static unsafe int FindAgentIndex(DesynthItem item)
  {
    var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentSalvage.Instance();
    if (agent == null) return -1;

    for (int i = 0; i < agent->ItemCount; i++)
    {
      var entry = agent->ItemList[i];
      if (entry.ItemId == item.ItemId) return i;
      if (entry.ItemId == item.ItemId + 1_000_000u && item.IsHq) return i;
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
  /// Toggles the "Desynthesize unique/untradable item" checkbox on SalvageDialog.
  /// Per-dialog (not sticky) — must be called for every untradable/unique item
  /// before FireSalvageDialogConfirm, or the Desynthesize button stays disabled.
  /// </summary>
  /// <remarks>
  /// Callback index TBD at smoke. Likely value index 1 or 2 (after the
  /// confirm/cancel slots). If 1 doesn't work, try 2 then 3 and check chat
  /// log for callback errors.
  /// </remarks>
  private static unsafe void FireUntradableCheckbox()
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SalvageDialog", out var addon)
        || !GenericHelpers.IsAddonReady(addon)) return;

    // PLACEHOLDER — verify callback index at smoke.
    var values = stackalloc AtkValue[1];
    values[0] = new AtkValue { Type = AtkValueType.Int, Int = 1 }; // try 1, then 2
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
  /// Callback index TBD at smoke. Most likely Int=0 (primary action), with
  /// Close being a different value. If 0 closes instead of retrying, try 1.
  /// </remarks>
  private static unsafe void FireRetryCallback()
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SalvageResult", out var addon)
        || !GenericHelpers.IsAddonReady(addon)) return;

    // PLACEHOLDER — verify callback index at smoke.
    var values = stackalloc AtkValue[1];
    values[0] = new AtkValue { Type = AtkValueType.Int, Int = 0 }; // try 0, then 1
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
    // Track for the run log + chat-yield attribution. Set at the start of
    // every act so v2.6.1.0's E-chat parser can attribute yields to the
    // current item.
    _taskManager.Enqueue(() =>
    {
      if (Plugin.CurrentRun != null)
        Plugin.CurrentRun.CurrentItem = new PricingItem { ItemId = item.ItemId };
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

    // If the item requires the unique/untradable checkbox, click it before
    // confirm. Per-dialog (not sticky) — fires every act on every applicable
    // item. The "Guarantee NQ item results" checkbox is never touched.
    if (item.RequiresUntradableConfirm)
    {
      _taskManager.Enqueue(() => { FireUntradableCheckbox(); return true; },
        $"DesynthUntradable_{item.Name}");
      // Brief settle after checkbox toggle so the dialog state propagates
      // before we fire confirm.
      _taskManager.DelayNext(Jitter(200, 100));
    }

    _taskManager.Enqueue(() => { FireSalvageDialogConfirm(); return true; },
      $"DesynthConfirm_{item.Name}");

    // Wait for SalvageResult — strict, abort-on-timeout. Per Q1 (2026-05-03
    // in-game observation), SalvageResult always fires after every desynth.
    // Absence within the 4000ms ceiling means a stale UI state we shouldn't
    // continue from.
    _taskManager.DelayNext(Jitter(600, 200));
    _taskManager.Enqueue(WaitForAddon("SalvageResult", 4000),
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
    // Log the act. ItemOutcome.Desynthed gets plain-text render in Task 13.
    _processed++;
    Plugin.PinchRunLog.AddEntry(ItemOutcome.Desynthed, item.Name,
      $"desynthed{(item.IsHq ? " (HQ)" : "")}");
    Plugin.PinchRunLog.IncrementProcessed();

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
        Abort();
        return true; // unblock the queue; Abort cleared it
      }
      return false; // keep polling
    };
  }

  private void EndRun()
  {
    Plugin.PinchRunLog.EndRun();
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
