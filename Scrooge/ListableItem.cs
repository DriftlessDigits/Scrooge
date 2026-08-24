using FFXIVClientStructs.FFXIV.Client.Game;

namespace Scrooge;

/// <summary>
/// One bag row the market board would accept: an item with a search category, no
/// untradeable flag, and no place on the ban list. Snapshot of the gate's answer -
/// see <see cref="ListableInventoryScanner.Scan"/> for who produces it and why three
/// separate runs read the same rows.
///
/// <para>Index fields are valid only for the snapshot in which they were captured.
/// The run orchestrators re-resolve the slot at action time to tolerate inventory
/// shifts mid-run.</para>
/// </summary>
internal sealed class ListableItem
{
  /// <summary>Item id as the inventory slot reports it. NQ id (no +1M offset).</summary>
  public uint ItemId { get; init; }

  /// <summary>Display name (Lumina lookup at scan time).</summary>
  public string Name { get; init; } = "";

  /// <summary>Stack quantity at scan time.</summary>
  public int Quantity { get; init; }

  /// <summary>HQ marker.</summary>
  public bool IsHq { get; init; }

  /// <summary>UI-state: checkbox state in the hawk checklist.</summary>
  public bool Selected { get; set; }

  /// <summary>The inventory bag this item lives in (for the orchestrators).</summary>
  public InventoryType Container { get; init; }

  /// <summary>Inventory slot index within <see cref="Container"/>.</summary>
  public int SlotIndex { get; init; }

  /// <summary>What you last sold one of these for, 0 when the tape never saw one.</summary>
  public int LastSalePrice { get; init; }

  /// <summary>True when that last sale is older than the configured stale window.</summary>
  public bool LastSaleStale { get; init; }

  /// <summary>True when the item is on the Always Vendor list - it rides every run, unchecked.</summary>
  public bool IsAlwaysVendor { get; init; }

  /// <summary>
  /// True when the ROUTER sent this run's copy to the vendor - no better exit in
  /// evidence. Rides the same direct-sell path as Always Vendor, but it is a
  /// verdict, not a standing rule, and the spoken reason must say which.
  /// </summary>
  public bool RoutedVendor { get; init; }

  /// <summary>The router's verdict for this row, as the Route column's tag. Verdict.None for rows the map has no opinion on.</summary>
  public RouteTagMap.Result RouteTag { get; init; }

  /// <summary>True once an override for this item was recorded this window session - write once, not per click.</summary>
  public bool OverrideRecorded { get; set; }
}
