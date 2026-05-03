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
      uint baseItemId = entry.ItemId;
      if (baseItemId == 0) continue;

      // AgentSalvage stores +1M for HQ in some builds; defensively normalize.
      bool isHq = baseItemId >= 1_000_000u;
      uint nqId = isHq ? baseItemId - 1_000_000u : baseItemId;

      var luminaItem = itemSheet.GetRow(nqId);

      // Find the inventory slot that matches and read the fields we need
      // for protection flags. Returned as primitives — InventoryItem* can't
      // cross tuple/return boundaries cleanly.
      var hit = FindInventorySlot(im, nqId, isHq);

      // SB100 and equipped materia are skipped here as protections, but
      // they're also gil opportunities — see "Materia harvest sibling" in
      // the spec's Out-of-scope section. v1 protects only; a future
      // sibling feature extracts.
      //
      // Spiritbond is stored as 0..10000 in SpiritbondOrCollectability.
      // For non-collectable equipment this field IS spiritbond.
      bool sb100 = hit.Found && hit.SpiritbondOrCollectability >= 10000;
      bool hasMateria = hit.Found && hit.HasAnyMateria;

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
        Quantity = hit.Found ? hit.Quantity : 1,
        IsHq = isHq,
        ClassJobId = classJob,
        ClassAbbrev = abbrev,
        ItemLevel = itemLevel,
        Color = color,
        IsInGearset = isInGearset,
        IsSpiritbond100 = sb100,
        HasMateria = hasMateria,
        RequiresUntradableConfirm = requiresUntradableConfirm,
        Container = hit.Container,
        SlotIndex = hit.Found ? hit.SlotIndex : -1,
        Selected = false,
      });
    }

    return result;
  }

  /// <summary>
  /// Snapshot of an inventory slot's relevant fields. Used instead of
  /// returning an <c>InventoryItem*</c> because pointer types can't appear
  /// as tuple element types or as method return types in non-unsafe contexts.
  /// </summary>
  private struct InventoryHit
  {
    public bool Found;
    public InventoryType Container;
    public int SlotIndex;
    public int Quantity;
    public ushort SpiritbondOrCollectability;
    public bool HasAnyMateria;
  }

  /// <summary>
  /// Locates the first inventory slot matching (itemId, isHq) across the four
  /// main inventory pages and snapshots the fields we need for protection
  /// flags (spiritbond, materia) and quantity. Returns an empty hit
  /// (<c>Found = false</c>) if no match.
  /// </summary>
  private static unsafe InventoryHit FindInventorySlot(InventoryManager* im, uint itemId, bool isHq)
  {
    var containers = new[]
    {
      InventoryType.Inventory1,
      InventoryType.Inventory2,
      InventoryType.Inventory3,
      InventoryType.Inventory4,
    };

    foreach (var ct in containers)
    {
      var c = im->GetInventoryContainer(ct);
      if (c == null) continue;

      for (int i = 0; i < c->Size; i++)
      {
        var s = c->GetInventorySlot(i);
        if (s == null || s->ItemId != itemId) continue;

        bool slotHq = (s->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        if (slotHq != isHq) continue;

        bool hasAnyMateria = false;
        for (int m = 0; m < 5; m++)
        {
          if (s->Materia[m] != 0) { hasAnyMateria = true; break; }
        }

        return new InventoryHit
        {
          Found = true,
          Container = ct,
          SlotIndex = i,
          Quantity = s->Quantity,
          SpiritbondOrCollectability = s->SpiritbondOrCollectability,
          HasAnyMateria = hasAnyMateria,
        };
      }
    }

    return default;
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
