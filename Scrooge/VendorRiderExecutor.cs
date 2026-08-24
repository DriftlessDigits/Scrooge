using Dalamud.Game.Addon.Lifecycle;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using Scrooge.Windows;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// THE VENDOR RIDER (WALK unit 3), as a real executor. The unanimous Pull &amp; Vendor
/// rows ride THIS pinch, grouped by the retainer whose visit drains them: snapshotted
/// at pinch start and woven into each retainer's own task chain, never a separate
/// errand. Recomputed rows, not queued state - an abort just leaves the un-drained
/// ones in the pile, because a row clears only on a completed vendor.
///
/// <para>The selection (which rows ride, per retainer) is the pure
/// <see cref="VendorRider"/> core, linked into Scrooge.Tests. What is here is the
/// EXECUTION: the same pull-then-vendor steps the manual triage executor runs
/// (through the shared <see cref="SellListRowSteps"/> contract), reporting into the
/// pinch run log and stamping the routing receipt exactly as the Hawk vendor path
/// does, so assent-clears-dissent registers the act.</para>
///
/// <para>It lived inside the pinch window as a scatter of private fields and
/// <c>Rider*</c>-prefixed methods; every other executor in this plugin is a class
/// that owns its own fields, its own listener registration and its own teardown, and
/// now so is this one. The pinch calls three doors: <see cref="Snapshot"/> at start,
/// <see cref="Dispatch"/> inside each retainer's visit, <see cref="Cleanup"/> at
/// every exit.</para>
/// </summary>
internal sealed class VendorRiderExecutor
{
  private readonly TaskManager _taskManager;

  /// <summary>This pinch's riding rows, keyed by the retainer that will drain them.</summary>
  private Dictionary<string, List<PricingItem>> _byRetainer = new(StringComparer.Ordinal);

  private IDisposable? _catchall;
  private bool _listenerActive;

  /// <summary>The buyback-dialog guard the rider arms while it vendors.</summary>
  private readonly VendorDismissGuard _vendorDismiss = new("Rider");

  internal VendorRiderExecutor(TaskManager taskManager) => _taskManager = taskManager;

  /// <summary>
  /// Snapshots the unanimous Pull &amp; Vendor rows the rider will drain this pinch,
  /// grouped by retainer, and arms the buyback-dismiss listener if any ride. Silent
  /// (empty map, no listener) when the rider is disabled or nothing qualifies - the
  /// dialog only appears once we have actually vendored, so the listener is armed
  /// only when rows are riding. Marks each riding row's queued action so the
  /// per-item steps have the skip contract they share with the manual triage
  /// executor.
  /// </summary>
  internal void Snapshot()
  {
    _byRetainer.Clear();
    if (!Plugin.Configuration.PinchVendorRider) return;

    // A staged verb (stamped by the candidate builder) survives; rows the pile
    // volunteered without a human verb default to Vendor, exactly as before.
    var candidates = Plugin.Accountant.PullVendorRiderCandidates();

    // THE DOMAN DESTINY (ruled 2026-08-21). A row the floor turned down under the
    // Enclave mode is worth twice vendor at the Enclave - the rider must never realize
    // half of that automatically. A row the PLAYER explicitly staged to Vendor still
    // rides: that is a human answering the question, not the machine answering it for
    // him. The hold pile is advice, and this is the door that keeps it advice.
    if (Plugin.Configuration.PriceFloorMode == PriceFloorMode.DomanEnclave)
      candidates.RemoveAll(c => !c.PlayerResolved && c.Item.Result == PricingResult.BelowFloor);

    foreach (var item in VendorRider.Riders(candidates))
      if (item.QueuedAction == StandingAction.None)
        item.QueuedAction = StandingAction.Vendor;
    _byRetainer = VendorRider.ByRetainer(candidates, i => i.RetainerName);

    if (_byRetainer.Count > 0)
    {
      Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", _vendorDismiss.Confirm);
      _listenerActive = true;
    }
  }

  /// <summary>
  /// Inserts this retainer's rider steps ahead of the queued sell-list close, so
  /// they run while the list is still open (Insert prepends - build back-to-front:
  /// last item first, each item's steps reversed, so execution is forward order).
  /// A no-op when nothing rides here.
  /// </summary>
  internal void Dispatch(string retainerName)
  {
    if (!Plugin.Configuration.PinchVendorRider) return;
    if (!_byRetainer.TryGetValue(retainerName, out var riders) || riders.Count == 0) return;

    for (int r = riders.Count - 1; r >= 0; r--)
      InsertRiderItem(riders[r]);
  }

  /// <summary>
  /// The per-item pull-then-vendor chain, inserted in reverse. Mirrors the manual
  /// triage executor's vendor path: re-resolve the row by id at the open sell list
  /// (a no-longer-listed row marks itself skipped and the chain no-ops), pull it to
  /// inventory, wait for it to land, then vendor it. Bookkeeping is pinch-flavored:
  /// the run log, not a triage summary, and the routing-receipt stamp the pull path
  /// otherwise omits.
  /// </summary>
  private void InsertRiderItem(PricingItem item)
  {
    if (item.QueuedAction == StandingAction.Vendor)
    {
      _taskManager.Insert(() => { _catchall?.Dispose(); _catchall = null; return true; },
        $"RiderUnblock_{item.ItemName}");
      _taskManager.Insert(() => { if (!SellListRowSteps.Skipped(item)) BookSale(item); return true; },
        $"RiderTrack_{item.ItemName}");
      _taskManager.InsertDelayNext(500);
      _taskManager.Insert(() => SellListRowSteps.Skipped(item) ? true : SellListRowSteps.FailClosed(item, GameNavigation.ClickVendorSellItem()),
        $"RiderVendor_{item.ItemName}");
      _taskManager.Insert(() => { if (!SellListRowSteps.Skipped(item)) { _catchall?.Dispose(); _catchall = GilTrackingState.Block("pinch_vendor_rider"); } return true; },
        $"RiderBlock_{item.ItemName}");
      _taskManager.InsertDelayNext(500);

      // Wait for the pulled item to reach the bags (server round-trip, variable
      // latency); a hard deadline marks the ITEM skipped so one slow arrival costs
      // one item, never the whole pinch. The item stays in the bags either way.
      _taskManager.Insert(SellListRowSteps.BagArrivalWait(item), $"RiderClickInv_{item.ItemName}");
      _taskManager.InsertDelayNext(1000);
    }
    else
    {
      // Pull-for-X (unit 5): the rider learned the retrieve and only the
      // retrieve - no vendor leg, no bag-arrival wait. The booking banks the
      // intent that fires the exit at its own stop.
      _taskManager.Insert(() => { if (!SellListRowSteps.Skipped(item)) BookPull(item); return true; },
        $"RiderPulled_{item.ItemName}");
      _taskManager.InsertDelayNext(800);
    }

    _taskManager.Insert(() => SellListRowSteps.Skipped(item) ? true : SellListRowSteps.FailClosed(item, GameNavigation.ClickReturnToInventory()),
      $"RiderReturn_{item.ItemName}");
    _taskManager.InsertDelayNext(500);
    _taskManager.Insert(() => SellListRowSteps.OpenRow(item), $"RiderOpenRow_{item.ItemName}");
  }

  /// <summary>
  /// Books a completed rider PULL: the standing book loses the ask, the open
  /// receipts close never-cleared (the listing was evicted without a market
  /// sale, so the spot it took never got its test), and - for pull-for-melt/GC
  /// - the intent that carries the
  /// ruling across the retainer-&gt;bag crossing is banked, on completion only.
  /// A plain pull banks nothing: it answered "off the board", not "where next".
  /// </summary>
  private static void BookPull(PricingItem item)
  {
    var qty = item.Quantity > 0 ? item.Quantity : 1;
    if (item.CurrentListingPrice is int ask && ask > 0)
      StandingBookFeed.Removed(item.ItemId, item.IsHq, item.RetainerName, ask, qty, "rider");

    Plugin.Ledger.SetCurrentRetainer(item.RetainerName);
    var dest = item.QueuedAction switch
    {
      StandingAction.Melt => "melt",
      StandingAction.Gc => "GC turn-in",
      _ => "inventory",
    };
    var was = item.CurrentListingPrice is int p && p > 0 ? $" (was listed at {p:N0})" : "";
    Plugin.Ledger.AddEntry(ItemOutcome.Banned, item.ItemName, $"Pulled for {dest}{was}");

    if (item.QueuedAction is StandingAction.Melt or StandingAction.Gc)
      try { GilStorage.UpsertPullIntent(item.ItemId, item.IsHq, item.QueuedAction.ToString()); }
      catch (Exception ex) { Svc.Log.Warning($"[Rider] Pull intent not banked for {item.ItemName}: {ex.Message}"); }

    RoutingReceiptStamp.NeverCleared(item.ItemId, item.IsHq);

    Plugin.Accountant.RemoveItem(item);
  }

  /// <summary>
  /// Books a completed rider vendor sale. The sale itself - price, gil, ledger line,
  /// chat line, transaction row, executed receipt - is
  /// <see cref="VendorSaleBook.Record"/>, the same booking the hawk and the manual
  /// triage make. What is the rider's own is on either side of it: the standing book
  /// loses the ASK the listing was standing at (the vendor gil is what the item then
  /// fetched in the bags, a different fact entirely, and an unknown ask books nothing
  /// rather than a guessed zero), the open receipts close never-cleared because the
  /// pull evicted the listing without a market sale, and the ledger row retires.
  /// </summary>
  private static void BookSale(PricingItem item)
  {
    var qty = item.Quantity > 0 ? item.Quantity : 1;

    if (item.CurrentListingPrice is int ask && ask > 0)
      StandingBookFeed.Removed(item.ItemId, item.IsHq, item.RetainerName, ask, qty, "rider");

    VendorSaleBook.Record(item.ItemId, item.ItemName, item.IsHq, qty,
      RunLogVoice.Reasons.BelowWhatTheBoardPays);

    // Pull evicted the listing without an MB sale - the spot it took never got its test.
    RoutingReceiptStamp.NeverCleared(item.ItemId, item.IsHq);

    Plugin.Accountant.RemoveItem(item);
  }

  /// <summary>Tears down the rider's per-run state: the buyback listener and any live catchall block.</summary>
  internal void Cleanup()
  {
    if (_listenerActive)
    {
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", _vendorDismiss.Confirm);
      _listenerActive = false;
    }
    _catchall?.Dispose();
    _catchall = null;
    _byRetainer.Clear();
  }
}
