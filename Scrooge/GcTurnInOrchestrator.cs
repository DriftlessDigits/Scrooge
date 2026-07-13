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
    _approved = null;
    _passedOver.Clear();
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

    _approved = [];
    foreach (var it in items)
    {
      if (!_approved.TryGetValue(it.ItemId, out var list))
        _approved[it.ItemId] = list = [];
      list.Add(it);
    }
    _remaining = items.Count;
    _passedOver.Clear();
    _sealsEarned = 0;
    _turnedIn = 0;
    _itemsUntilLongPause = _random.Next(8, 16);
    IsRunning = true;
    Svc.Framework.Update += OnFrameworkUpdate;
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
      FinishState("complete");
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
      FinishState(_remaining > 0
        ? $"complete - {_remaining} approved {(_remaining == 1 ? "item" : "items")} never appeared in the delivery list"
        : "complete");
      return true;
    }

    // Wallet room - stop before the game starts eating the overflow.
    if (GameSafe.CompanySeals() is not { } seals)
    {
      FinishState("aborted (seal wallet unreadable mid-run)");
      return true;
    }
    if (seals.Current + (uint)item.SealReward > seals.Max)
    {
      FinishState($"stopped - seal wallet nearly full ({seals.Current:N0}/{seals.Max:N0}), {_remaining} items left");
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

    // The HQ confirm normally fires on row-select (handled in
    // ConfirmReward), but answer it here too if it surfaces late.
    if (AnswerHqConfirm(item) == HqAnswer.PassedOver)
      return true;

    if (GameSafe.CompanySeals() is not { } seals)
    {
      FinishState("aborted (seal wallet unreadable mid-run)");
      return true;
    }

    if (seals.Current <= _sealsBefore)
      return false; // not landed yet - keep polling (seals lag the Deliver packet)

    _sealsEarned += seals.Current - _sealsBefore;
    _turnedIn++;
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
