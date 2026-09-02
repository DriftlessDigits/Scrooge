using ECommons.DalamudServices;
using Scrooge.Windows;
using System;

namespace Scrooge;

/// <summary>
/// BOOKING A VENDOR SALE, once. Three executors sell to the NPC vendor — the hawk's
/// price-check fallback, the standing triage's manual pull-and-vendor, and the pinch
/// rider — and all three then wrote the same six facts by hand: look up the vendor
/// price, multiply by the stack, add it to the run's vendor total, narrate a run-log
/// line, insert the gil-tracking transaction, and stamp the routing receipt that
/// assent-clears-dissent keys off.
///
/// <para>Six facts times three copies is six ways to drift, and it had already
/// drifted: only the rider floored the stack at 1, so a row whose quantity was still
/// unknown (a flag row, which starts at 0) booked a <b>zero-gil</b> vendor sale on the
/// other two paths — a real sale, in the ledger and in the transaction table, worth
/// nothing. The floor applies uniformly here.</para>
///
/// <para>What stays with each caller is what is genuinely its own: the hawk's
/// lifecycle progress beat, the triage's tally and its book removal, the rider's
/// never-cleared receipt and its ledger-row retirement. This owns the part that was
/// never supposed to differ.</para>
/// </summary>
internal static class VendorSaleBook
{
  /// <summary>
  /// Books one vendor sale and returns the total gil it earned.
  ///
  /// <para><paramref name="quantity"/> is floored at 1: every caller reaches here
  /// holding a real item it just sold, so a 0 (an unread stack size) means "we could
  /// not see how many", not "none" — and one is the honest minimum for a sale that
  /// demonstrably happened.</para>
  ///
  /// <para><paramref name="reason"/> is the run-log's "why this went to the vendor"
  /// clause; each executor knows its own (a floor refusal, a no-data fallback, an
  /// always-vendor rule) and none of them can be derived here.</para>
  /// </summary>
  internal static int Record(uint itemId, string itemName, bool isHq, int quantity, string reason)
  {
    var qty = quantity > 0 ? quantity : 1;

    var vendorPrice = 0;
    try
    {
      vendorPrice = (int)Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(itemId).PriceLow;
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[VendorSaleBook] Vendor price unreadable for {itemName}: {ex.Message}");
    }

    var totalGil = vendorPrice * qty;

    Plugin.Ledger.AddVendorSale(totalGil);
    Plugin.Ledger.AddEntry(ItemOutcome.VendorSold, RunLogVoice.Name(itemName, isHq),
      RunLogVoice.Vendor(qty, totalGil, reason));

    Communicator.PrintVendorSold(itemName, vendorPrice, qty);

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    GilStorage.InsertTransaction(now, "earned", "vendor_sale", totalGil,
      itemId, itemName, GilTracker.GetItemCategory(itemId),
      qty, vendorPrice, isHq, "", "NPC Vendor");

    // The item's Vendor exit executed and nobody overrode it — the stamp
    // assent-clears-dissent (v2.17) reads.
    RoutingReceiptStamp.Executed(itemId, isHq, "Vendored");

    return totalGil;
  }
}
