using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
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
  /// Per-item chain head. Task 11 replaces the no-op body with the real
  /// item-select / dialog-confirm / result-dismiss sequence.
  /// </summary>
  private unsafe bool? ProcessNext()
  {
    if (_queue == null || _queue.Count == 0)
    {
      EndRun();
      return true;
    }

    var item = _queue.Dequeue();
    Svc.Chat.Print($"[Scrooge DEBUG] Would desynth: {item.Name}");
    _processed++;

    // Inter-item beat: 1500±400 ms band per spec.
    _taskManager.DelayNext(Jitter(Plugin.Configuration.DesynthPerActionBaseMs, 400));
    _taskManager.Enqueue(ProcessNext, "DesynthProcessNext");
    return true;
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
