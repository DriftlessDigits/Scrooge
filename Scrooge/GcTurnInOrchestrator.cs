using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;

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
  private Queue<GcTurnInItem>? _queue;
  private uint _sealsBefore;
  private long _sealsEarned;
  private int _turnedIn;

  internal bool IsRunning { get; private set; }

  public GcTurnInOrchestrator()
  {
    _taskManager = new TaskManager
    {
      TimeLimitMS = 10000,
      AbortOnTimeout = true,
    };
  }

  internal void Abort() => Finish("cancelled");

  private void Finish(string how)
  {
    _taskManager.Abort();
    _queue = null;
    if (IsRunning)
      Svc.Chat.Print($"[Scrooge] Churn run {how}: {_turnedIn} items, {_sealsEarned:N0} seals.");
    IsRunning = false;
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
    IsRunning = true;
    Svc.Chat.Print($"[Scrooge] Churning {items.Count} items ({seals.Current:N0}/{seals.Max:N0} seals).");
    _taskManager.Enqueue(ProcessNext, "GcProcessNext");
  }

  /// <summary>
  /// One item per pass: check wallet room, find the item in the delivery
  /// list, click it, then hand off to the reward-dialog steps.
  /// </summary>
  private unsafe bool? ProcessNext()
  {
    if (_queue == null || _queue.Count == 0)
    {
      Finish("complete");
      return true;
    }

    var item = _queue.Peek();

    // Wallet room - stop before the game starts eating the overflow.
    if (GameSafe.CompanySeals() is not { } seals)
    {
      Finish("aborted (seal wallet unreadable mid-run)");
      return true;
    }
    if (seals.Current + (uint)item.SealReward > seals.Max)
    {
      Finish($"stopped - seal wallet nearly full ({seals.Current:N0}/{seals.Max:N0}), {_queue.Count} items left");
      return true;
    }
    _sealsBefore = seals.Current;

    // Re-resolve the item in the agent's delivery list every pass - the
    // list reshuffles after each turn-in.
    var index = FindDeliveryIndex(item);
    if (index < 0)
    {
      // Not in the list (already gone, or ineligible after all) - skip, don't guess.
      Svc.Chat.Print($"[Scrooge] Skipped {item.Name} - not in the delivery list.");
      _queue.Dequeue();
      _taskManager.Enqueue(ProcessNext, "GcProcessNext");
      return true;
    }

    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon)
        || !GenericHelpers.IsAddonReady(addon))
      return null; // retry until timeout

    // Select the list row -> pops the reward dialog.
    // VERIFY in-game: (1, index) as the row-select event on this addon.
    ECommons.Automation.Callback.Fire(addon, true, 1, index);

    _taskManager.Enqueue(() => ConfirmReward(item), $"GcConfirm_{item.Name}");
    _taskManager.DelayNext(300);
    _taskManager.Enqueue(() => VerifyAndAdvance(item), $"GcVerify_{item.Name}");
    _taskManager.DelayNext(400);
    _taskManager.Enqueue(ProcessNext, "GcProcessNext");
    return true;
  }

  /// <summary>
  /// Finds the queued item in AgentGrandCompanySupply's current list.
  /// Match = item id + HQ state of the actual inventory slot the agent
  /// entry points at. Returns -1 when absent.
  /// </summary>
  private static unsafe int FindDeliveryIndex(GcTurnInItem item)
  {
    var agent = AgentGrandCompanySupply.Instance();
    if (agent == null || agent->ItemArray == null) return -1;

    var im = InventoryManager.Instance();
    if (im == null) return -1;

    for (int i = 0; i < agent->NumItems; i++)
    {
      var entry = agent->ItemArray[i];
      if (entry.ItemId != item.ItemId) continue;

      var container = im->GetInventoryContainer(entry.Inventory);
      if (container == null) continue;
      var slot = container->GetInventorySlot(entry.Slot);
      if (slot == null || slot->ItemId != item.ItemId) continue;

      var isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
      if (isHq == item.IsHq)
        return i;
    }
    return -1;
  }

  /// <summary>
  /// The reward dialog is up - verify it shows the item we clicked before
  /// touching Deliver. A mismatch means the list event did something we
  /// didn't intend, and the only safe move is a full stop.
  /// </summary>
  private unsafe bool? ConfirmReward(GcTurnInItem item)
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyReward", out var addon)
        || !GenericHelpers.IsAddonReady(addon))
      return null; // retry until timeout

    if (!RewardDialogShows(addon, item.Name))
    {
      addon->Close(true);
      Finish($"ABORTED - reward dialog didn't show {item.Name}. Nothing delivered for it");
      return true;
    }

    new AddonMaster.GrandCompanySupplyReward((nint)addon).Deliver();
    return true;
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
  private bool? VerifyAndAdvance(GcTurnInItem item)
  {
    if (GameSafe.CompanySeals() is not { } seals)
    {
      Finish("aborted (seal wallet unreadable mid-run)");
      return true;
    }

    if (seals.Current <= _sealsBefore)
      return null; // not landed yet - retry until timeout

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
