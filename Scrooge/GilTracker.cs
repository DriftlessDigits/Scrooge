using System;
using System.Collections.Generic;
using System.Linq;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Scrooge;

/// <summary>
/// Coordinates gil tracking: processes sale history from the hook,
/// captures listing snapshots during pinch runs, tracks gil balances,
/// and manages first-seen timestamps for listing duration.
/// </summary>
internal static class GilTracker
{
  private static readonly Lumina.Excel.ExcelSheet<Item> _items = Svc.Data.GetExcelSheet<Item>();

  // Run-level State
  private static readonly List<ListingRecord> _runListings = [];

  /// <summary>Current retainer name — set by AutoPinch, read by the hook.</summary>
  public static string CurrentRetainerName { get; set; } = String.Empty;

  // --- Lumina Helpers ---

  public static string GetItemCategory(uint itemID)
  {
    return _items.GetRow(itemID).ItemUICategory.ValueNullable?.Name.ToString() ?? "Unknown";
  }

  public static string GetItemName(uint itemID)
  {
    return _items.GetRow(itemID).Name.ToString();
  }

  // --- Pinch Run Integration ---

  /// <summary>Called at pinch run start. Clears run-level state.</summary>
  public static void StartRun()
  {
    _runListings.Clear();
    CurrentRetainerName = String.Empty;
  }

  /// <summary>Set the current retainer context (called per retainer in the run).</summary>
  public static void SetRetainer(string name)
  {
    CurrentRetainerName = name;
  }

  /// <summary>
  /// Snapshot all listings from the RetainerSellList addon.
  /// Called once per retainer, after the sell list is open.
  /// Reads AtkValues directly: base 10, stride 13, slot index at offset 5.
  /// See: RetainerSellList Addon - AtkValue Map.md
  /// </summary>
  public static unsafe void SnapshotListings()
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) || !GenericHelpers.IsAddonReady(addon))
      return;

    var itemCount = addon->AtkValues[9].Int;
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Step 1: Read existing first_seen values before we delete
    // (so we can preserve them for items that are still listed)
    var existingFirstSeen = new Dictionary<string, long>();
    for (int i = 0; i < itemCount; i++)
    {
      var baseIdx = 10 + (i * 13);
      var slotIndex = addon->AtkValues[baseIdx + 5].Int;
      var itemName = addon->AtkValues[baseIdx + 1].GetValueAsString();
      var payload = Communicator.RawItemNameToItemPayload(itemName);
      var itemID = payload?.ItemId ?? 0u;
      if (itemID == 0) continue;

      var fs = GilStorage.GetFirstSeen(CurrentRetainerName, slotIndex, itemID);
      if (fs.HasValue)
        existingFirstSeen[$"{slotIndex}|{itemID}"] = fs.Value;
    }

    // Step 2+3: Delete and re-insert in a transaction.
    // Without this, a cancelled run would leave partial/empty listings in the DB.
    using var transaction = GilStorage.BeginTransaction();

    GilStorage.DeleteRetainerListings(CurrentRetainerName, transaction);

    // Re-insert current items + build in-memory list
    for (int i = 0; i < itemCount; i++)
    {
      var baseIdx = 10 + (i * 13);
      var iconId = addon->AtkValues[baseIdx].Int;
      var itemName = addon->AtkValues[baseIdx + 1].GetValueAsString();
      var quantity = addon->AtkValues[baseIdx + 2].Int;
      var priceStr = addon->AtkValues[baseIdx + 3].GetValueAsString();
      var slotIndex = addon->AtkValues[baseIdx + 5].Int;

      var isHQ = iconId >= 1000000;
      if (!int.TryParse(priceStr.Replace(",", ""), out var pricePerUnit))
        continue;

      var cleanName = Communicator.CleanItemName(itemName, out _);
      var payload = Communicator.RawItemNameToItemPayload(itemName);
      var itemID = payload?.ItemId ?? 0u;
      if (itemID == 0) continue;

      // Preserve existing first_seen, or use now for new listings
      var key = $"{slotIndex}|{itemID}";
      var firstSeen = existingFirstSeen.TryGetValue(key, out var fs) ? fs : now;

      // Write to DB
      GilStorage.UpsertListing(CurrentRetainerName, slotIndex, itemID,
          cleanName, GetItemCategory(itemID), pricePerUnit, quantity,
          isHQ, firstSeen, now, transaction);

      // Keep in-memory for FinalizeRun calculations
      _runListings.Add(new ListingRecord
      {
        ItemId = itemID,
        ItemName = cleanName,
        Category = GetItemCategory(itemID),
        UnitPrice = pricePerUnit,
        Quantity = quantity,
        IsHQ = isHQ,
        RetainerName = CurrentRetainerName,
        SlotIndex = slotIndex,
        FirstSeenTimestamp = firstSeen,
        LastUpdatedTimestamp = now
      });
    }

    transaction.Commit();
    Svc.Log.Debug($"[GilTrack] Snapshotted {itemCount} listings for {CurrentRetainerName}");
  }

  /// <summary>
  /// Called at pinch run end. Captures gil balances, updates current listings,
  /// cleans up first-seen timestamps for items no longer listed, and saves.
  /// </summary>
  public static unsafe void FinalizeRun()
  {
    var playerGil = (long)InventoryManager.Instance()->GetGil();

    // Capture retainer gil
    var retainerGil = new Dictionary<string, long>();
    var rm = RetainerManager.Instance();
    for (uint i = 0; i < rm->GetRetainerCount(); i++)
    {
      var retainer = rm->GetRetainerBySortedIndex(i);
      var name = retainer->NameString;
      if (!string.IsNullOrEmpty(name))
        retainerGil[name] = retainer->Gil;
    }

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Gil snapshot → DB
    var snapshotId = GilStorage.InsertGilSnapshot(now, playerGil, "pinch_run");
    foreach (var (name, gil) in retainerGil)
      GilStorage.InsertRetainerSnapshot(snapshotId, name, gil);

    // Market snapshot → DB (computed from _runListings, same as before)
    var totalListingValue = _runListings.Sum(l => (long)l.UnitPrice * l.Quantity);
    var avgAgeDays = _runListings.Count > 0
        ? _runListings.Average(l => (now - l.FirstSeenTimestamp) / 86400.0)
        : 0.0;
    GilStorage.InsertMarketSnapshot(now, _runListings.Count, totalListingValue, avgAgeDays);

    Svc.Log.Info($"[GilTrack] Run complete: {_runListings.Count} listings, " +
        $"{totalListingValue:N0}g on market, {playerGil:N0}g player");
  }

  // --- Sale History Processing (called from hook) ---

  /// <summary>
  /// Process sale history entries from the RetainerHistory hook.
  /// Deduplicates against existing sales and appends new ones.
  /// </summary>
  public static void ProcessSaleHistory(List<RetainerHistoryHook.RetainerHistoryData> entries, string retainerName)
  {
    var newCount = 0;

    foreach (var entry in entries)
    {
      if (GilStorage.TransactionExists(entry.ItemID, entry.UnixTimeSeconds, retainerName))
        continue;

      GilStorage.InsertTransaction(
        (long)entry.UnixTimeSeconds,
        "earned",
        "retainer_sale",
        (long)entry.UnitPrice * entry.Quantity,  // amount = total gil
        entry.ItemID,
        GetItemName(entry.ItemID),
        GetItemCategory(entry.ItemID),
        (int)entry.Quantity,
        (int)entry.UnitPrice,
        entry.IsHQ,
        retainerName, 
        entry.BuyerName);

      newCount++;
    }

    if (newCount > 0)
      Svc.Log.Info($"[GilTrack] {newCount} new sale(s) from {retainerName}");
  }
}