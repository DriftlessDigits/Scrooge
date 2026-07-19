using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using Scrooge.Windows;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Scrooge;

/// <summary>
/// The fifth executor: GC Expert Delivery turn-in. Consumes the routing
/// window's Turn In pile while the player stands at their GC's personnel
/// officer with the Expert Delivery tab open.
///
/// Model: WALK THE DISPLAYED LIST, not a private queue (Sam's call,
/// 2026-07-12, after the queue-order model lost two rounds to list
/// rebuilds). The approved pile is a checklist; each pass scans the
/// displayed rows top-down and turns in the first row still on the
/// checklist - rows the router said keep are walked past. Click-what-
/// you-see makes list reshuffles and reload windows harmless by
/// construction. Every step still fails closed: reward dialog must name
/// the clicked item; wallet must grow; unapproved HQ twins get No; a
/// full scan with no checklist rows ends the run, reporting any items
/// that never appeared. The batch confirm in the routing window is the
/// safety rail (BP4 Q4) - this just executes.
/// </summary>
internal sealed class GcTurnInOrchestrator
{
  /// <summary>One confirmed turn-in-pile item. SealReward from the sheet at queue time.</summary>
  internal sealed record GcTurnInItem(uint ItemId, bool IsHq, string Name, int SealReward);

  private readonly TaskManager _taskManager;
  private readonly System.Random _random = new();

  // The checklist: approved entries by item id, ticked off as deliveries
  // verify. _passedOver = ids we deliberately walk past for the rest of
  // the run (HQ row offered, no HQ approval).
  private Dictionary<uint, List<GcTurnInItem>>? _approved;
  private readonly HashSet<uint> _passedOver = [];
  private int _remaining;
  private bool _pendingHqDelivery;

  private uint _sealsBefore;
  private int _itemsUntilLongPause;

  /// <summary>
  /// The shared run-host lifecycle: state, progress (done/total), value (seals),
  /// self-calibrating ETA, and the stall terminal. This orchestrator was the
  /// accidental prototype of the contract (the 0129f13 Progress tuple); it now
  /// drives the generalized core instead of hand-rolling its own bookkeeping.
  /// </summary>
  private readonly RunLifecycle _run = new(System.TimeSpan.FromSeconds(30));

  internal bool IsRunning => _run.IsRunning;

  /// <summary>The live run, for the standard progress readout (see RunHostRender).</summary>
  internal RunLifecycle Run => _run;

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
    FinishState(RunState.Cancelled, "cancelled");
  }

  /// <summary>
  /// Fail-closed teardown: drives the lifecycle to a terminal state and cleans up
  /// every resource the run holds, on EVERY exit path. Safe from inside a task -
  /// it never touches the task manager. Already-queued follow-ups no-op on the
  /// IsRunning guard at their top. A watchdog Stall may have ended the run before
  /// this call, in which case only the cleanup runs (once). Non-complete finishes
  /// close a still-open reward dialog so an abort never leaves the game mid-prompt.
  /// </summary>
  private unsafe void FinishState(RunState outcome, string how)
  {
    _approved = null;
    _passedOver.Clear();
    var now = System.DateTime.UtcNow;
    var justEnded = _run.IsRunning
      && (outcome == RunState.Complete ? _run.Complete(now)
          : outcome == RunState.Stalled ? _run.Stall(now)
          : _run.Cancel(now));
    if (justEnded)
    {
      Svc.Framework.Update -= OnFrameworkUpdate;
      if (outcome != RunState.Complete)
        CloseRewardDialog();

      // Close out the run window (ruling 9). Only when CurrentRun is OUR GC run -
      // never touch a live pinch/hawk run's log. The chat summary is preserved as
      // it was; the run window gets the seals total as its summary line.
      if (Plugin.CurrentRun?.Mode == RunMode.Gc)
      {
        Plugin.CurrentRun.AddRunEntry(RunEvent.Summary, $"{_run.Value:N0} seals earned");
        Plugin.PinchRunLog.EndRun();
        Plugin.CurrentRun = null;
      }

      Svc.Chat.Print($"[Scrooge] Turn-in run {how}: {_run.Done} items, {_run.Value:N0} seals.");
    }
  }

  /// <summary>
  /// Wedge watchdog. A task timeout clears the queue inside ECommons without
  /// notifying us - IsRunning with an idle task manager is exactly that state
  /// and nothing else. Stall the run (fail closed) so it can't jam StartRun (and
  /// the Cancel button) until a manual cancel.
  /// </summary>
  private void OnFrameworkUpdate(IFramework framework)
  {
    if (_run.IsRunning && !_taskManager.IsBusy)
      FinishState(RunState.Stalled, "timed out (the game stopped responding)");
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

    _approved = [];
    foreach (var it in items)
    {
      if (!_approved.TryGetValue(it.ItemId, out var list))
        _approved[it.ItemId] = list = [];
      list.Add(it);
    }
    _remaining = items.Count;
    _passedOver.Clear();
    _itemsUntilLongPause = _random.Next(8, 16);
    _run.Start(items.Count, RunValueUnit.Seals, System.DateTime.UtcNow, $"Turn in {items.Count} items");
    Svc.Framework.Update += OnFrameworkUpdate;

    // Ruling 9: GC joins the one run window. The run log carries the full view
    // (start / per-item / summary); the orchestrator's own RunLifecycle (_run)
    // stays the authority on seals/value/ETA/stall for the inline Ledger glance.
    Plugin.CurrentRun = new RunData { Mode = RunMode.Gc };
    Plugin.PinchRunLog.StartNewRun(isGcRun: true);
    Plugin.PinchRunLog.SetCurrentRetainer("Grand Company");
    Plugin.PinchRunLog.SetTotalItems(items.Count);

    Svc.Chat.Print($"[Scrooge] Turning in {items.Count} items ({seals.Current:N0}/{seals.Max:N0} seals).");
    _taskManager.Enqueue(ProcessNext, "GcProcessNext");
  }

  /// <summary>
  /// One delivery per pass: scan the DISPLAYED list top-down, click the
  /// first row still on the checklist, hand off to the reward-dialog
  /// steps. No checklist row in a loaded list = the run is done.
  /// </summary>
  private unsafe bool? ProcessNext()
  {
    if (!IsRunning)
      return true; // run ended from an earlier task - queued follow-ups no-op

    if (_approved == null || _remaining == 0)
    {
      FinishState(RunState.Complete, "complete");
      return true;
    }

    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon)
        || !GenericHelpers.IsAddonReady(addon))
      return false; // false = keep polling until timeout (null would signal ABORT)

    // The displayed list REBUILDS after every delivery. While it reloads,
    // this pass is not-ready - poll, never conclude (live receipt
    // 2026-07-12: treating "loading" as "absent" burned queue items into
    // 'not in the delivery list' skips during each reload window).
    if (addon->AtkValues[0].UInt != 2)
      return false;
    var entries = *(DisplayEntry**)((nint)addon + 648);
    if (entries == null)
      return false;

    // Walk the displayed rows - the callback's index space - and take the
    // first one still on the checklist. Rows the router said keep (and ids
    // passed over for HQ mismatch) are walked past.
    var count = (int)addon->AtkValues[6].UInt;
    var index = -1;
    GcTurnInItem? item = null;
    for (var i = 0; i < count; i++)
    {
      var id = entries[i].ItemId;
      if (_passedOver.Contains(id)) continue;
      if (_approved.TryGetValue(id, out var list) && list.Count > 0)
      {
        index = i;
        // NQ-preferred candidate: an un-confirmed delivery is an NQ trade,
        // so tick NQ entries first and let the HQ confirm identify HQ rows.
        item = list.Find(e => !e.IsHq) ?? list[0];
        break;
      }
    }
    if (index < 0 || item == null)
    {
      // Loaded list, no checklist rows left - done. Anything unticked never
      // appeared (filtered out, already gone, or ineligible) - say so.
      FinishState(RunState.Complete, _remaining > 0
        ? $"complete - {_remaining} approved {(_remaining == 1 ? "item" : "items")} never appeared in the delivery list"
        : "complete");
      return true;
    }

    // Wallet room - stop before the game starts eating the overflow.
    if (GameSafe.CompanySeals() is not { } seals)
    {
      FinishState(RunState.Cancelled, "aborted (seal wallet unreadable mid-run)");
      return true;
    }
    if (seals.Current + (uint)item.SealReward > seals.Max)
    {
      FinishState(RunState.Cancelled, $"stopped - seal wallet nearly full ({seals.Current:N0}/{seals.Max:N0}), {_remaining} items left");
      return true;
    }
    _sealsBefore = seals.Current;
    _pendingHqDelivery = false;

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

  /// <summary>Outcome of an HQ-confirm check: no dialog, answered Yes, or refused.</summary>
  private enum HqAnswer { None, AnsweredYes, PassedOver }

  /// <summary>
  /// Answers the "really trade a high-quality item?" confirm wherever it
  /// appears in the flow. Live receipt 2026-07-12: for HQ rows it pops on
  /// ROW-SELECT, before the reward dialog opens (it blocked ConfirmReward's
  /// poll into a timeout twice) - so both ConfirmReward and VerifyAndAdvance
  /// call this. HQ approval on the checklist -> Yes and the HQ entry gets
  /// ticked on delivery. No HQ approval -> No, and the id is walked past
  /// for the rest of the run rather than donate the wrong variant.
  /// </summary>
  private unsafe HqAnswer AnswerHqConfirm(GcTurnInItem item)
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesno)
        || !GenericHelpers.IsAddonReady(yesno))
      return HqAnswer.None;

    if (_approved?.GetValueOrDefault(item.ItemId)?.Exists(e => e.IsHq) == true)
    {
      _pendingHqDelivery = true;
      new AddonMaster.SelectYesno((nint)yesno).Yes();
      return HqAnswer.AnsweredYes;
    }

    new AddonMaster.SelectYesno((nint)yesno).No();
    CloseRewardDialog();
    _passedOver.Add(item.ItemId);
    Svc.Chat.Print($"[Scrooge] Passed over {item.Name} - the list offered the HQ copy but only the NQ one was approved.");
    return HqAnswer.PassedOver;
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

    // The HQ confirm can pop on row-select and modally block the reward
    // dialog - answer it here or the poll below times the run out.
    if (AnswerHqConfirm(item) == HqAnswer.PassedOver)
      return true; // skipped; the queued ProcessNext moves on

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
      FinishState(RunState.Cancelled, $"ABORTED - reward dialog didn't show {item.Name}. Nothing delivered for it");
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

    // The HQ confirm normally fires on row-select (handled in
    // ConfirmReward), but answer it here too if it surfaces late.
    if (AnswerHqConfirm(item) == HqAnswer.PassedOver)
      return true;

    if (GameSafe.CompanySeals() is not { } seals)
    {
      FinishState(RunState.Cancelled, "aborted (seal wallet unreadable mid-run)");
      return true;
    }

    if (seals.Current <= _sealsBefore)
      return false; // not landed yet - keep polling (seals lag the Deliver packet)

    // One delivery landed: advance done + accrue the seal delta, resetting the
    // stall watchdog. The lifecycle owns the progress/value/ETA bookkeeping now.
    var sealDelta = seals.Current - _sealsBefore;
    _run.RecordProgress(1, sealDelta, System.DateTime.UtcNow);

    // Mirror the delivery into the one run window (ruling 9).
    Plugin.PinchRunLog.AddEntry(ItemOutcome.TurnedIn, item.Name, $"turned in for {sealDelta:N0} seals");
    Plugin.PinchRunLog.IncrementProcessed();

    TickOff(item.ItemId);
    return true;
  }

  /// <summary>
  /// Removes one checklist entry for a delivered id - the HQ entry when the
  /// HQ confirm fired, otherwise an NQ entry first (an unconfirmed delivery
  /// is an NQ trade).
  /// </summary>
  private void TickOff(uint itemId)
  {
    if (_approved?.GetValueOrDefault(itemId) is not { Count: > 0 } list)
      return;
    var entry = _pendingHqDelivery
      ? list.Find(e => e.IsHq) ?? list[0]
      : list.Find(e => !e.IsHq) ?? list[0];
    list.Remove(entry);
    _remaining--;
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
