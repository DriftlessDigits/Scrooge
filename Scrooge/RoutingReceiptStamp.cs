using ECommons.DalamudServices;
using System;

namespace Scrooge;

/// <summary>
/// The two writes an executor owes the routing receipt when an exit actually
/// fires, with the one guard-and-log policy both of them run under.
///
/// <para>Six executors stamped the executed act and three closed the never-cleared
/// receipts, and every one of them spelled the write as a bare try/catch swallow -
/// so a stamp that failed took assent-clears-dissent (v2.17) down with it silently,
/// and nothing in /xllog said which item or which act. The guard was written in
/// two of the nine places and missing from the other seven.</para>
/// </summary>
internal static class RoutingReceiptStamp
{
  /// <summary>
  /// Stamps the newest unexecuted routing receipt with the act that fired
  /// (Listed / Vendored / Desynthed / TurnedIn) - the stamp assent-clears-dissent
  /// keys off. A failure is a lost pardon, not a lost run, so it is logged and
  /// the executor carries on.
  /// </summary>
  internal static void Executed(uint itemId, bool isHq, string action)
  {
    if (!Plugin.Configuration.EnableGilTracking || itemId == 0) return;
    try { GilStorage.MarkRoutingReceiptExecuted(itemId, isHq, action); }
    catch (Exception ex) { Svc.Log.Warning($"[Receipt] {action} stamp failed for {itemId}: {ex.Message}"); }
  }

  /// <summary>
  /// Outcome join (M4): a pull/vendor evicts the listing from the board WITHOUT an MB
  /// sale, so the item's open decision receipts close as never-cleared - the forecast
  /// never got its test, and that absence is the finding (design Section 4). A GilTrack
  /// MB sale, by contrast, fills them cleared; the two paths never collide because a
  /// sold item is skipped before it reaches a pull.
  /// </summary>
  internal static void NeverCleared(uint itemId, bool isHq)
  {
    if (!Plugin.Configuration.EnableGilTracking || itemId == 0) return;
    try { GilStorage.CloseReceiptsNeverCleared(itemId, isHq); }
    catch (Exception ex) { Svc.Log.Warning($"[Receipt] never-cleared close failed for {itemId}: {ex.Message}"); }
  }
}
