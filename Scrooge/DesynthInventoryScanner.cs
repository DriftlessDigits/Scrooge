using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// Snapshots the items currently visible in AgentSalvage.ItemList into a list
/// of <see cref="DesynthItem"/>, cross-referenced with inventory state and
/// gearset membership.
///
/// Read-only. No mutations to game state.
/// </summary>
internal static class DesynthInventoryScanner
{
  // The DoH class job ids that can desynthesize. Mirror of CRP..CUL contiguous
  // block. Internal: the sitrep's desynth-skill line reads the same map.
  internal static readonly Dictionary<byte, string> ClassJobAbbrev = new()
  {
    [8]  = "CRP",
    [9]  = "BSM",
    [10] = "ARM",
    [11] = "GSM",
    [12] = "LTW",
    [13] = "WVR",
    [14] = "ALC",
    [15] = "CUL",
  };

  /// <summary>
  /// Scans current desynth state. Returns an empty list if AgentSalvage is null
  /// or has no items. Caller is responsible for any UI refresh logic.
  /// </summary>
  internal static unsafe List<DesynthItem> Scan()
  {
    var result = new List<DesynthItem>();

    var agent = AgentSalvage.Instance();
    if (agent == null) return result;

    // Snapshot of gearset item ids (HQ-aware: stored as itemId+1M for HQ)
    var gearsetIds = SnapshotGearsetItemIds();

    var itemSheet = Svc.Data.GetExcelSheet<Item>();
    var im = InventoryManager.Instance();
    if (im == null) return result;

    int count = (int)agent->ItemCount;
    for (int i = 0; i < count; i++)
    {
      var entry = agent->ItemList[i];

      // SalvageListItem.ItemId is a game-internal ID that doesn't map to the
      // Lumina Item sheet directly (e.g. 60170 for "Augmented Crystarium
      // Greatsword"). Source the canonical Item.RowId from the actual
      // InventoryItem at the slot the agent points to.
      var slotContainer = im->GetInventoryContainer(entry.InventoryType);
      if (slotContainer == null) continue;
      var slot = slotContainer->GetInventorySlot((int)entry.InventorySlot);
      if (slot == null || slot->ItemId == 0) continue;

      if (BuildItem(slot, entry.InventoryType, (int)entry.InventorySlot,
            itemSheet, gearsetIds, hidden: false) is { } item)
        result.Add(item);
    }

    return result;
  }

  /// <summary>
  /// The hidden half of the pile (decision walk, 2026-08-30): pile variants that
  /// live in the bags but NOT in the salvage agent's current list - the window's
  /// filter shows one category at a time, and melt-routed furniture hides outside
  /// the default. Read straight from InventoryManager (bag containers only; melt
  /// is bags-only), through the same builder as the visible scan, so protections,
  /// the ban list, and the color law hold identically. Rows come back with
  /// <see cref="DesynthItem.Hidden"/> set - the preview renders them checkable
  /// and the run's category walk melts the checked ones.
  /// </summary>
  internal static unsafe List<DesynthItem> ScanHiddenPile(
    HashSet<(uint ItemId, bool IsHq)> pile, List<DesynthItem> visible)
  {
    var result = new List<DesynthItem>();
    if (pile.Count == 0) return result;

    var im = InventoryManager.Instance();
    if (im == null) return result;
    var itemSheet = Svc.Data.GetExcelSheet<Item>();
    var gearsetIds = SnapshotGearsetItemIds();

    var visibleSlots = new HashSet<(InventoryType, int)>();
    foreach (var v in visible)
      visibleSlots.Add((v.Container, v.SlotIndex));

    InventoryType[] bags =
      [InventoryType.Inventory1, InventoryType.Inventory2,
       InventoryType.Inventory3, InventoryType.Inventory4];
    foreach (var bag in bags)
    {
      var container = im->GetInventoryContainer(bag);
      if (container == null) continue;
      for (int s = 0; s < container->Size; s++)
      {
        var slot = container->GetInventorySlot(s);
        if (slot == null || slot->ItemId == 0) continue;
        if (visibleSlots.Contains((bag, s))) continue;
        bool isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        if (!pile.Contains((slot->ItemId, isHq))) continue;

        if (BuildItem(slot, bag, s, itemSheet, gearsetIds, hidden: true) is { } item)
          result.Add(item);
      }
    }

    return result;
  }

  /// <summary>
  /// One inventory slot into one preview row - the shared half of both scans.
  /// Null = not a row (not in the Item sheet, or banned).
  /// </summary>
  private static unsafe DesynthItem? BuildItem(InventoryItem* slot,
    InventoryType container, int slotIndex,
    Lumina.Excel.ExcelSheet<Item> itemSheet, HashSet<uint> gearsetIds, bool hidden)
  {
    uint nqId = slot->ItemId;
    bool isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;

    // Some inventory entries (event items, collectables, special tokens)
    // aren't in the regular Item sheet — skip rather than crash.
    if (!itemSheet.HasRow(nqId)) return null;
    var luminaItem = itemSheet.GetRow(nqId);

    // SB100 and equipped materia are skipped here as protections, but
    // they're also gil opportunities — see "Materia harvest sibling" in
    // the spec's Out-of-scope section. v1 protects only; a future
    // sibling feature extracts.
    //
    // Spiritbond is stored as 0..10000 in SpiritbondOrCollectability.
    // For non-collectable equipment this field IS spiritbond.
    bool sb100 = slot->SpiritbondOrCollectability >= 10000;
    bool hasMateria = false;
    for (int m = 0; m < 5; m++)
    {
      if (slot->Materia[m] != 0) { hasMateria = true; break; }
    }

    // Gearset check: by id+HQ. Conservative — flags both copies if you have
    // two of the same item, even though only one is in a gearset.
    uint gearsetKey = isHq ? nqId + 1_000_000u : nqId;
    bool isInGearset = gearsetIds.Contains(gearsetKey);

    // THE BAN LIST HOLDS HERE TOO (ruled 2026-08-23). "Leave it alone" covered
    // listing, repricing, and the GC, but the salvage scan had no filter - a
    // banned item was selectable and meltable, and Select All would take it.
    // Same id+HQ key convention as the ban list itself.
    if (Plugin.Configuration.BannedItemIds.Contains(gearsetKey)) return null;

    byte classJob = (byte)luminaItem.ClassJobRepair.RowId;
    string abbrev = ClassJobAbbrev.TryGetValue(classJob, out var a) ? a : "ALL";

    float playerLevel = GameSafe.GetDesynthLevel(classJob);
    int itemLevel = (int)luminaItem.LevelItem.RowId;
    var color = DesynthSkillup.Classify(playerLevel, itemLevel, GameSafe.MaxDesynthLevel());

    // Untradable / Unique items require the in-game "Desynthesize
    // unique/untradable item" checkbox to be ticked before the dialog's
    // Desynthesize button enables. Per-dialog (not sticky), so we record
    // the requirement at scan time and let the orchestrator's chain
    // auto-click it during the run.
    bool requiresUntradableConfirm = luminaItem.IsUntradable || luminaItem.IsUnique;

    return new DesynthItem
    {
      ItemId = nqId,
      Name = luminaItem.Name.ToString(),
      Quantity = slot->Quantity,
      IsHq = isHq,
      ClassJobId = classJob,
      ClassAbbrev = abbrev,
      ItemLevel = itemLevel,
      Color = color,
      IsInGearset = isInGearset,
      IsSpiritbond100 = sb100,
      HasMateria = hasMateria,
      RequiresUntradableConfirm = requiresUntradableConfirm,
      Container = container,
      SlotIndex = slotIndex,
      Selected = false,
      Hidden = hidden,
    };
  }

  /// <summary>
  /// Returns the set of (itemId+1M for HQ, itemId for NQ) ids referenced by
  /// any saved gearset. Conservative match key — see scanner notes.
  /// Shared with the routing window's protection scan.
  /// </summary>
  internal static unsafe HashSet<uint> SnapshotGearsetItemIds()
  {
    var ids = new HashSet<uint>();
    var module = RaptureGearsetModule.Instance();
    if (module == null) return ids;

    for (int i = 0; i < 100; i++) // gearset count cap
    {
      var entry = module->GetGearset(i);
      if (entry == null || !entry->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
        continue;

      foreach (var slot in entry->Items)
      {
        if (slot.ItemId == 0) continue;
        ids.Add(slot.ItemId);
      }
    }

    return ids;
  }
}
