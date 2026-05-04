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
  // The DoH class job ids that can desynthesize. Mirror of CRP..CUL contiguous block.
  private static readonly Dictionary<byte, string> ClassJobAbbrev = new()
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

      uint nqId = slot->ItemId;
      bool isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;

      // Some inventory entries (event items, collectables, special tokens)
      // aren't in the regular Item sheet — skip rather than crash.
      if (!itemSheet.HasRow(nqId)) continue;
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

      byte classJob = (byte)luminaItem.ClassJobRepair.RowId;
      string abbrev = ClassJobAbbrev.TryGetValue(classJob, out var a) ? a : "ALL";

      int playerLevel = DesynthSkillup.GetDesynthLevel(classJob);
      int itemLevel = (int)luminaItem.LevelItem.RowId;
      var color = DesynthSkillup.Classify(playerLevel, itemLevel);

      // Untradable / Unique items require the in-game "Desynthesize
      // unique/untradable item" checkbox to be ticked before the dialog's
      // Desynthesize button enables. Per-dialog (not sticky), so we record
      // the requirement at scan time and let the orchestrator's chain
      // auto-click it during the run.
      bool requiresUntradableConfirm = luminaItem.IsUntradable || luminaItem.IsUnique;

      result.Add(new DesynthItem
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
        Container = entry.InventoryType,
        SlotIndex = (int)entry.InventorySlot,
        Selected = false,
      });
    }

    return result;
  }

  /// <summary>
  /// Returns the set of (itemId+1M for HQ, itemId for NQ) ids referenced by
  /// any saved gearset. Conservative match key — see scanner notes.
  /// </summary>
  private static unsafe HashSet<uint> SnapshotGearsetItemIds()
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
