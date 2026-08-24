using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace Scrooge;

/// <summary>
/// THE SELL-LIST ROW CONTRACT, game side. Two executors walk a retainer's sell list
/// row by row — the manual standing triage and the pinch's vendor rider — and both
/// obey the same four rules, because both are batches where one bad row must cost one
/// item and never the queue:
///
/// <list type="bullet">
/// <item>a row is targeted by (item id, HQ) against the DISPLAYED rows, never by a
/// remembered slot index, which goes stale the moment anything sells;</item>
/// <item>a row that is no longer listed marks itself skipped, closes its ledger row,
/// and the chain walks past it;</item>
/// <item>a missing context-menu entry is a per-item skip, not a null return — a raw
/// null tells the TaskManager to abort the ENTIRE queue;</item>
/// <item>a pulled item gets a bounded wait to reach the bags, and a hard deadline
/// skips the item rather than wedging the run.</item>
/// </list>
///
/// <para>The PURE half of this contract — "is this item skipped?" — already lives in
/// <see cref="StandingMemory.ItemSkipped"/> and is pinned in Scrooge.Tests. What is
/// here is the half that touches the game: the addon reads, the chat lines, the
/// clicks. Both chains now call these; before, each carried its own identically-bodied
/// copy under its own prefix (<c>Skipped</c>/<c>FailClosed</c> and
/// <c>RiderSkipped</c>/<c>RiderFailClosed</c>).</para>
/// </summary>
internal static unsafe class SellListRowSteps
{
  /// <summary>
  /// An item marked skipped mid-batch (no longer listed, or a missing menu entry) —
  /// every queued follow-up step for it no-ops. The decision itself is pure and
  /// tested; this is the one-argument spelling the chains read best.
  /// </summary>
  internal static bool Skipped(PricingItem item)
    => StandingMemory.ItemSkipped(item.QueuedAction, item.Result);

  /// <summary>
  /// Translates a context-menu clicker's tri-state for the TaskManager: a null
  /// (entry missing — fail closed) becomes a per-item skip, because a raw null
  /// return tells the TaskManager to abort the whole queue. One bad menu costs one
  /// item, never the batch.
  /// </summary>
  internal static bool? FailClosed(PricingItem item, bool? clickResult)
  {
    if (clickResult != null) return clickResult;
    Svc.Chat.Print($"[Scrooge] {item.ItemName} — expected menu entry missing; item skipped (see /xllog).");
    item.QueuedAction = StandingAction.None;
    return true;
  }

  /// <summary>
  /// Opens the sell-list context menu for the item, re-resolving its row by
  /// (item id, HQ) against the sell list's DISPLAYED rows — recorded slot indexes
  /// are stale hints, never targeting data, and the RetainerMarket container orders
  /// slots differently than the display (finding #16). An item that is no longer
  /// listed (sold since flagging) marks itself skipped and closes its row; the
  /// batch moves on.
  ///
  /// <para><paramref name="isReprice"/> picks WHICH skip mark a vanished row gets: a
  /// reprice pass is walking items it means to price, so the pass result goes
  /// Skipped; a pull pass is walking queued actions, so the action goes None. Both
  /// read as skipped afterwards.</para>
  /// </summary>
  internal static bool? OpenRow(PricingItem item, bool isReprice = false)
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon)
        || !GenericHelpers.IsAddonReady(addon))
      return false; // navigation still in flight - retry

    if (GameSafe.SellListRow(item.ItemId, item.IsHq) is not { } row)
    {
      Svc.Chat.Print($"[Scrooge] {item.ItemName} is no longer listed on {item.RetainerName} — skipped (likely sold).");
      if (isReprice) item.Result = PricingResult.Skipped;
      else item.QueuedAction = StandingAction.None;
      Plugin.Accountant.RemoveItem(item); // row is moot - close its flags
      return true;
    }

    item.Quantity = row.Quantity; // live stack size (flag rows start unknown)
    return GameNavigation.OpenItemContextMenu(row.RowIndex);
  }

  /// <summary>
  /// Builds the "wait for the pulled item to land in the bags" step: a server
  /// round-trip of variable latency, so the step retries each tick and clicks the
  /// item the moment it appears. A hard 5s deadline marks the ITEM skipped, so one
  /// slow arrival costs one item and never the run — the item stays in the player's
  /// bags either way, so nothing is lost by giving up on it.
  ///
  /// <para>Returns the step instead of enqueuing it, because the two callers put it
  /// on the queue differently (the triage <c>Enqueue</c>s forward, the rider
  /// <c>Insert</c>s in reverse) and each owns its own task name. The deadline is
  /// closed over per call, so two items in flight never share one clock.</para>
  /// </summary>
  internal static Func<bool?> BagArrivalWait(PricingItem item)
  {
    var arrivalDeadline = DateTime.MinValue;
    return () =>
    {
      if (Skipped(item)) return true;
      if (arrivalDeadline == DateTime.MinValue)
        arrivalDeadline = DateTime.UtcNow.AddMilliseconds(5000);
      if (GameNavigation.TryClickInventoryItemById(item.ItemId, item.IsHq))
        return true;
      if (DateTime.UtcNow < arrivalDeadline)
        return false; // not in bags yet - retry
      Svc.Chat.Print($"[Scrooge] {item.ItemName} never arrived in bags — left unvendored (check inventory).");
      item.QueuedAction = StandingAction.None;
      return true;
    };
  }
}
