using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The Venture Coffer rider's PURE core (WALK, Sam's 2026-07-23 ruling: "if there
/// is a Venture Coffer in the inventory, it needs to be used to unlock an item").
/// A coffer is hidden routable inventory - itself no exit, its contents all of
/// them - so the sweep opens ALL coffers at the FRONT of disposition (before the
/// melt), and the unlocked items land in the bags for the next Refresh to route.
///
/// This file is the Dalamud-free half: the coffer identity, the free-slot guard
/// DECISION, and the per-open set-diff attribution. Kept out of the game layer on
/// purpose so it links into the test project - a game/config static leaking in
/// here breaks that compile. The opening loop, the item-use call, and the run-log
/// narration are the Dalamud half in <see cref="CofferOrchestrator"/>.
/// </summary>
internal static class CofferLogic
{
  /// <summary>
  /// The Lumina Item sheet row for "Venture Coffer" (verified against the live
  /// Item sheet 2026-07-23: RowId 32161, exact name "Venture Coffer",
  /// StackSize 999, PriceLow 0 - the retainer-venture reward coffer).
  /// </summary>
  internal const uint VentureCofferItemId = 32161;

  /// <summary>
  /// Conservative free-slot floor before we'll open a coffer. Mirrors
  /// <c>DesynthOrchestrator.MinFreeInventorySlots</c> (5): the unlocked item lands
  /// in the bags, and opening against a full inventory risks a lost item or a
  /// stuck state. Bail before the open rather than gamble on the game's rules.
  /// </summary>
  internal const int MinFreeInventorySlots = 5;

  /// <summary>The coffer detection predicate - the one identity the rider keys off.</summary>
  internal static bool IsVentureCoffer(uint itemId) => itemId == VentureCofferItemId;

  /// <summary>
  /// The free-slot guard's decision: safe to open a coffer only when free slots are
  /// at or above the floor. Pure so the threshold is pinned by a test, not by an
  /// eyeballed constant buried in the loop.
  /// </summary>
  internal static bool CanOpen(int freeSlots) => freeSlots >= MinFreeInventorySlots;

  /// <summary>
  /// Set-diff attribution: the items that newly APPEARED or grew between a
  /// before/after bag snapshot (keyed by item id + HQ, valued by total quantity).
  /// Only positive deltas count, and the coffer itself is excluded (its own stack
  /// shrinks by one on each open, a negative delta we never narrate). Used both
  /// per-open (attribute the one coffer's yield) and rider-wide (the end summary of
  /// everything that appeared during the run) - if a single open's diff comes back
  /// empty or ambiguous, the caller narrates honestly and leans on the wider diff.
  /// </summary>
  internal static List<CofferYield> NewItems(
    IReadOnlyDictionary<(uint ItemId, bool Hq), int> before,
    IReadOnlyDictionary<(uint ItemId, bool Hq), int> after)
  {
    var result = new List<CofferYield>();
    foreach (var kv in after)
    {
      if (IsVentureCoffer(kv.Key.ItemId)) continue;
      before.TryGetValue(kv.Key, out var was);
      var delta = kv.Value - was;
      if (delta > 0)
        result.Add(new CofferYield(kv.Key.ItemId, kv.Key.Hq, delta));
    }
    return result;
  }
}

/// <summary>One item unlocked from a coffer: its identity and how many appeared.</summary>
internal sealed record CofferYield(uint ItemId, bool IsHq, int Qty);
