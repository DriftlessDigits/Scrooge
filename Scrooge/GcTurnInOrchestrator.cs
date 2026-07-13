using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Scrooge;

/// <summary>
/// The fifth executor: GC Expert Delivery turn-in. Consumes the routing
/// window's Churn pile while the player stands at their GC's personnel
/// officer with the Expert Delivery tab open. Every step fails closed:
/// item not in the delivery list -> skipped and reported; reward dialog
/// shows a different item than expected -> run aborts; seal wallet about
/// to overflow -> run stops with the remainder counted. The batch confirm
/// in the routing window is the safety rail (BP4 Q4) - this just executes.
/// </summary>
internal sealed class GcTurnInOrchestrator
{
  /// <summary>One confirmed churn-pile item. SealReward from the sheet at queue time.</summary>
  internal sealed record GcTurnInItem(uint ItemId, bool IsHq, string Name, int SealReward);

  private readonly TaskManager _taskManager;
  private readonly System.Random _random = new();
  private Queue<GcTurnInItem>? _queue;
  private uint _sealsBefore;
  private long _sealsEarned;
  private int _turnedIn;
  private int _itemsUntilLongPause;

  internal bool IsRunning { get; private set; }

  public GcTurnInOrchestrator()
  {
    _taskManager = new TaskManager
    {
      TimeLimitMS = 10000,
      AbortOnTimeout = true,
    };
  }

  /// <summary>
  /// External cancel (Cancel button, plugin dispose). Never call from inside
  /// a task - tasks end their run via FinishState and a return value instead
  /// (the LegacyTaskManager abort invariant).
  /// </summary>
  internal void Abort()
  {
    _taskManager.Abort();
    FinishState("cancelled");
  }

  /// <summary>
  /// Clears run state without touching the task manager, so it is safe from
  /// inside a task. Already-queued follow-up tasks no-op on the IsRunning
  /// guard at their top. Non-complete finishes close a still-open reward
  /// dialog so an abort never leaves the game mid-prompt.
  /// </summary>
  private unsafe void FinishState(string how)
  {
    _queue = null;
    if (IsRunning)
    {
      Svc.Framework.Update -= OnFrameworkUpdate;
      if (how != "complete")
        CloseRewardDialog();
      Svc.Chat.Print($"[Scrooge] Turn-in run {how}: {_turnedIn} items, {_sealsEarned:N0} seals.");
    }
    IsRunning = false;
  }

  /// <summary>
  /// Wedge watchdog. A task timeout clears the queue inside ECommons without
  /// notifying us - IsRunning with an idle task manager is exactly that state
  /// and nothing else. Finish so the run can't jam StartRun (and the Cancel
  /// button) until a manual cancel.
  /// </summary>
  private void OnFrameworkUpdate(IFramework framework)
  {
    if (IsRunning && !_taskManager.IsBusy)
      FinishState("timed out (the game stopped responding)");
  }

  private static unsafe void CloseRewardDialog()
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyReward", out var addon)
        && GenericHelpers.IsAddonReady(addon))
      addon->Close(true);
  }

  /// <summary>
  /// Entry point. Requires the Expert Delivery tab of the GC supply window
  /// to be open (the routing window's Churn button checks the same thing).
  /// </summary>
  internal unsafe void StartRun(List<GcTurnInItem> items)
  {
    if (_taskManager.IsBusy || IsRunning || items.Count == 0)
      return;

    if (!AtExpertDelivery())
    {
      Svc.Chat.PrintError("[Scrooge] Open your GC's Expert Delivery window first (personnel officer).");
      return;
    }

    if (GameSafe.CompanySeals() is not { } seals)
    {
      Svc.Chat.PrintError("[Scrooge] Couldn't read your seal wallet - not starting.");
      return;
    }

    _queue = new Queue<GcTurnInItem>(items);
    _sealsEarned = 0;
    _turnedIn = 0;
    _itemsUntilLongPause = _random.Next(8, 16);
    IsRunning = true;
    Svc.Framework.Update += OnFrameworkUpdate;
    Svc.Chat.Print($"[Scrooge] Turning in {items.Count} items ({seals.Current:N0}/{seals.Max:N0} seals).");
    _taskManager.Enqueue(ProcessNext, "GcProcessNext");
  }

  /// <summary>
  /// One item per pass: check wallet room, find the item in the delivery
  /// list, click it, then hand off to the reward-dialog steps.
  /// </summary>
  private unsafe bool? ProcessNext()
  {
    if (!IsRunning)
      return true; // run ended from an earlier task - queued follow-ups no-op

    if (_queue == null || _queue.Count == 0)
    {
      FinishState("complete");
      return true;
    }

    var item = _queue.Peek();

    // Wallet room - stop before the game starts eating the overflow.
    if (GameSafe.CompanySeals() is not { } seals)
    {
      FinishState("aborted (seal wallet unreadable mid-run)");
      return true;
    }
    if (seals.Current + (uint)item.SealReward > seals.Max)
    {
      FinishState($"stopped - seal wallet nearly full ({seals.Current:N0}/{seals.Max:N0}), {_queue.Count} items left");
      return true;
    }
    _sealsBefore = seals.Current;

    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon)
        || !GenericHelpers.IsAddonReady(addon))
      return false; // false = keep polling until timeout (null would signal ABORT)

    // Re-resolve the item's DISPLAYED row every pass - the list reshuffles
    // after each turn-in, and the row-select callback's index space is
    // display rows, not agent-array positions (live receipt 2026-07-12:
    // agent index selected the wrong row, finding #16's bug class at the
    // GC counter; the reward name check aborted the run as designed).
    var index = FindDisplayIndex(addon, item);
    if (index < 0)
    {
      // Not in the displayed list (already gone, filtered out, or
      // ineligible after all) - skip, don't guess.
      Svc.Chat.Print($"[Scrooge] Skipped {item.Name} - not in the delivery list.");
      _queue.Dequeue();
      _taskManager.Enqueue(ProcessNext, "GcProcessNext");
      return true;
    }

    // Select the display row -> pops the reward dialog. The trailing zero
    // AtkValue matches the event signature the game expects.
    ECommons.Automation.Callback.Fire(addon, true, 1, index, ECommons.Automation.Callback.ZeroAtkValue);

    _taskManager.Enqueue(() => ConfirmReward(item), $"GcConfirm_{item.Name}");
    _taskManager.DelayNext(Jitter(350, 150));
    _taskManager.Enqueue(() => VerifyAndAdvance(item), $"GcVerify_{item.Name}");

    // Inter-item beat - humanized base + jitter, plus an occasional longer
    // pause, mirroring the desynth orchestrator's pacing grammar.
    var interItemMs = Jitter(900, 400);
    if (--_itemsUntilLongPause <= 0)
    {
      interItemMs += _random.Next(3000, 8001);
      _itemsUntilLongPause = _random.Next(8, 16);
    }
    _taskManager.DelayNext(interItemMs);
    _taskManager.Enqueue(ProcessNext, "GcProcessNext");
    return true;
  }

  /// <summary>Base +- uniform jitter, floored at 1ms (desynth's pacing helper).</summary>
  private int Jitter(int baseMs, int band)
  {
    var offset = (int)(((_random.NextDouble() * 2.0) - 1.0) * band);
    return System.Math.Max(1, baseMs + offset);
  }

  /// <summary>
  /// One row of the supply list's DISPLAYED item array, which hangs off the
  /// addon at +648 in display order - sorted and filtered exactly as the
  /// player sees it, and the index space the row-select callback expects.
  /// Layout per AutoRetainer's GCExpectEntry (production-proven).
  /// </summary>
  [StructLayout(LayoutKind.Explicit, Size = 152)]
  private struct DisplayEntry
  {
    [FieldOffset(120)] public uint Seals;
    [FieldOffset(132)] public uint ItemId;
  }

  /// <summary>
  /// Finds the queued item's displayed row. Match is by item id only - the
  /// display array carries no HQ flag, so when NQ and HQ copies coexist the
  /// row picked may be either variant (same item, same name; the reward
  /// check passes and the HQ confirm is answered either way). Returns -1
  /// when absent or the list isn't loaded.
  /// </summary>
  private static unsafe int FindDisplayIndex(AtkUnitBase* addon, GcTurnInItem item)
  {
    if (addon->AtkValues[0].UInt != 2) return -1; // list still loading
    var count = (int)addon->AtkValues[6].UInt;
    var entries = *(DisplayEntry**)((nint)addon + 648);
    if (entries == null) return -1;

    for (var i = 0; i < count; i++)
      if (entries[i].ItemId == item.ItemId)
        return i;
    return -1;
  }

  /// <summary>
  /// The reward dialog is up - verify it shows the item we clicked before
  /// touching Deliver. A mismatch means the list event did something we
  /// didn't intend, and the only safe move is a full stop.
  /// </summary>
  private unsafe bool? ConfirmReward(GcTurnInItem item)
  {
    if (!IsRunning)
      return true;

    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyReward", out var addon)
        || !GenericHelpers.IsAddonReady(addon))
      return false; // false = keep polling until timeout (null would signal ABORT)

    if (!RewardDialogShows(addon, item.Name))
    {
      // Evidence before the abort: what DID the dialog say? Settles whether
      // the wrong row was clicked (different item's strings) or the dialog
      // doesn't carry names in AtkValues at all (RetainerTaskResult disease).
      DumpAddonStrings("GrandCompanySupplyReward", addon);
      addon->Close(true);
      FinishState($"ABORTED - reward dialog didn't show {item.Name}. Nothing delivered for it");
      return true;
    }

    new AddonMaster.GrandCompanySupplyReward((nint)addon).Deliver();
    return true;
  }

  /// <summary>Dumps every non-empty string AtkValue of an addon (mismatch evidence).</summary>
  private static unsafe void DumpAddonStrings(string name, AtkUnitBase* addon)
  {
    var sb = new System.Text.StringBuilder($"[GcTurnIn] {name} strings: ");
    for (var i = 0; i < addon->AtkValuesCount; i++)
    {
      var text = addon->AtkValues[i].GetValueAsString();
      if (!string.IsNullOrEmpty(text))
        sb.Append($"[{i}]={text} ");
    }
    Svc.Log.Info(sb.ToString());
  }

  /// <summary>
  /// Scans the reward dialog's string AtkValues for the expected item name.
  /// Layout is unverified in this codebase, so the check is a containment
  /// scan over every string value - broad on purpose. If the name genuinely
  /// isn't anywhere in the dialog, we have the wrong dialog.
  /// </summary>
  private static unsafe bool RewardDialogShows(AtkUnitBase* addon, string itemName)
  {
    for (var i = 0; i < addon->AtkValuesCount; i++)
    {
      var text = addon->AtkValues[i].GetValueAsString();
      if (!string.IsNullOrEmpty(text)
          && text.Contains(itemName, System.StringComparison.OrdinalIgnoreCase))
        return true;
    }
    return false;
  }

  /// <summary>
  /// Post-Deliver bookkeeping: the wallet must have grown. No growth after
  /// the retry window = the hand-in didn't happen - stop rather than churn
  /// blind (a stuck dialog or a full wallet both land here).
  /// </summary>
  private unsafe bool? VerifyAndAdvance(GcTurnInItem item)
  {
    if (!IsRunning)
      return true;

    // HQ hand-ins pop a confirm ("really trade a high-quality item?") after
    // Deliver. Queued HQ -> intended, answer Yes. Queued NQ -> the id-only
    // display match landed on the item's HQ twin (the array carries no HQ
    // flag); answer No and skip rather than donate the wrong variant.
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesno)
        && GenericHelpers.IsAddonReady(yesno))
    {
      if (item.IsHq)
      {
        new AddonMaster.SelectYesno((nint)yesno).Yes();
      }
      else
      {
        new AddonMaster.SelectYesno((nint)yesno).No();
        CloseRewardDialog();
        Svc.Chat.Print($"[Scrooge] Skipped {item.Name} - the list offered the HQ copy but the NQ one was queued.");
        _queue?.Dequeue();
        return true;
      }
    }

    if (GameSafe.CompanySeals() is not { } seals)
    {
      FinishState("aborted (seal wallet unreadable mid-run)");
      return true;
    }

    if (seals.Current <= _sealsBefore)
      return false; // not landed yet - keep polling (seals lag the Deliver packet)

    _sealsEarned += seals.Current - _sealsBefore;
    _turnedIn++;
    _queue?.Dequeue();
    return true;
  }

  internal static unsafe bool AtExpertDelivery()
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon)
        || !GenericHelpers.IsAddonReady(addon))
      return false;
    var agent = AgentGrandCompanySupply.Instance();
    // Tab 2 = Expert Delivery (0 supply, 1 provisioning).
    return agent != null && agent->SelectedTab == 2;
  }
}
