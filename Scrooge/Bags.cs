using System;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Scrooge;

/// <summary>
/// THE FOUR MAIN BAGS, walked once. Every scanner in this plugin that means
/// "the player's carryable inventory" means these four pages, and each of them
/// used to spell out the same array, the same null checks and the same
/// two-deep loop — three copies of the container list, four copies of the walk.
///
/// <para>This is <see cref="GameSafe"/>'s discipline applied to a shape instead
/// of a single read: the manager and every container and every slot are read
/// through null guards, and an unreadable page is skipped rather than thrown
/// over. A caller that needs to know it could not look at all asks a method that
/// says so (<see cref="TryForEachSlot"/> returns false); a caller that just wants
/// a number gets 0, which is the "can't verify, don't start" answer the melt and
/// the coffer guards already wanted.</para>
/// </summary>
internal static unsafe class Bags
{
  /// <summary>The four main inventory pages, in order. The one definition.</summary>
  internal static readonly InventoryType[] MainPages =
  {
    InventoryType.Inventory1,
    InventoryType.Inventory2,
    InventoryType.Inventory3,
    InventoryType.Inventory4,
  };

  /// <summary>
  /// Calls <paramref name="visit"/> for every slot of the four main pages,
  /// including empty ones (a slot with <c>ItemId == 0</c> is what "free" means,
  /// so the free-slot count is just a consumer of this walk). Silently does
  /// nothing when the inventory manager is unavailable.
  /// </summary>
  internal static void ForEachSlot(Action<InventoryItemView> visit)
    => TryForEachSlot(visit);

  /// <summary>
  /// <see cref="ForEachSlot"/>, but reports whether the walk happened at all:
  /// false means the inventory manager was unreadable and NOTHING was visited,
  /// which is different from "walked and found nothing". Callers whose answer
  /// changes meaning under an incomplete read (the hawk's bag observation, which
  /// closes lane_held flags on absence) must use this door.
  /// </summary>
  internal static bool TryForEachSlot(Action<InventoryItemView> visit)
  {
    var im = InventoryManager.Instance();
    if (im == null) return false;

    foreach (var page in MainPages)
    {
      var container = im->GetInventoryContainer(page);
      if (container == null) continue;
      for (int i = 0; i < container->Size; i++)
      {
        var slot = container->GetInventorySlot(i);
        if (slot == null) continue;
        visit(new InventoryItemView(slot->ItemId, (int)slot->Quantity,
          (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0));
      }
    }
    return true;
  }

  /// <summary>
  /// Empty slots across the four main pages. 0 when the manager is unavailable —
  /// every caller reads that as "can't verify, don't start", which is the same
  /// fail-closed answer each hand-rolled copy already returned.
  /// </summary>
  internal static int FreeSlots()
  {
    int free = 0;
    ForEachSlot(s => { if (s.ItemId == 0) free++; });
    return free;
  }

  /// <summary>
  /// Total quantity across the four main pages of every item
  /// <paramref name="match"/> accepts. 0 when the manager is unavailable.
  /// </summary>
  internal static int TotalQuantityWhere(Func<uint, bool> match)
  {
    int total = 0;
    ForEachSlot(s => { if (s.ItemId != 0 && match(s.ItemId)) total += s.Quantity; });
    return total;
  }

  /// <summary>
  /// One slot as plain data, lifted out of native memory before any caller sees
  /// it — the walk hands over values, not pointers, so a consumer lambda can
  /// never outlive the read it came from. <c>ItemId == 0</c> is an empty slot.
  /// </summary>
  internal readonly record struct InventoryItemView(uint ItemId, int Quantity, bool IsHq);
}
