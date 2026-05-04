using FFXIVClientStructs.FFXIV.Client.Game;

namespace Scrooge;

/// <summary>
/// A single row in the desynth preview. Snapshot of one item visible in the
/// SalvageItemSelector addon, with all the state needed to classify, color,
/// flag, and (after Run) act on it.
///
/// Index fields are valid only for the snapshot in which they were captured.
/// The orchestrator re-resolves item position via AgentSalvage at action time
/// to tolerate inventory shifts mid-run.
/// </summary>
internal sealed class DesynthItem
{
  /// <summary>Item id from AgentSalvage.ItemList. NQ id (no +1M offset).</summary>
  public uint ItemId { get; init; }

  /// <summary>Display name (Lumina lookup at scan time).</summary>
  public string Name { get; init; } = "";

  /// <summary>Stack quantity at scan time.</summary>
  public int Quantity { get; init; }

  /// <summary>HQ marker.</summary>
  public bool IsHq { get; init; }

  /// <summary>The DoH class that desynthesizes this item (e.g. 8 = CRP).</summary>
  public byte ClassJobId { get; init; }

  /// <summary>Display label for the class column ("CRP", "BSM", "ALL", ...).</summary>
  public string ClassAbbrev { get; init; } = "";

  /// <summary>Item level (LevelItem.RowId in Lumina).</summary>
  public int ItemLevel { get; init; }

  /// <summary>Skillup color from <see cref="DesynthSkillup.Classify"/>.</summary>
  public DesynthSkillupColor Color { get; init; }

  /// <summary>True if any saved gearset references this item by id+HQ.</summary>
  public bool IsInGearset { get; init; }

  /// <summary>True if the item's spiritbond is at 100%.</summary>
  public bool IsSpiritbond100 { get; init; }

  /// <summary>True if the item has any equipped materia.</summary>
  public bool HasMateria { get; init; }

  /// <summary>
  /// True if the in-game SalvageDialog will require the "Desynthesize
  /// unique/untradable item" checkbox to be ticked before its Desynthesize
  /// button enables. Set when Lumina <c>Item.IsUntradable</c> or
  /// <c>Item.IsUnique</c> is true. The orchestrator auto-clicks the checkbox
  /// when this is set — not a protection flag, just a ceremony the player
  /// shouldn't have to manage during a bulk run.
  /// </summary>
  public bool RequiresUntradableConfirm { get; init; }

  /// <summary>The inventory slot this item lives in (for the orchestrator).</summary>
  public InventoryType Container { get; init; }

  /// <summary>Inventory slot index within <see cref="Container"/>. Reserved for future precision-gearset matching.</summary>
  public int SlotIndex { get; init; }

  /// <summary>UI-state: checkbox state in the preview window.</summary>
  public bool Selected { get; set; }

  /// <summary>True if any protection flag is set (gearset / SB100 / materia).</summary>
  public bool IsProtected => IsInGearset || IsSpiritbond100 || HasMateria;
}
