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
  private static readonly HashSet<string> _runRetainers = [];       // track which retainers were scanned

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
    _runRetainers.Clear();
    CurrentRetainerName = String.Empty;
  }

  /// <summary>Set the current retainer context (called per retainer in the run).</summary>
  public static void SetRetainer(string name)
  {
    CurrentRetainerName = name;
    _runRetainers.Add(name);
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

    var itemCount = addon->AtkValues[9].Int;              // header index 9 = item count
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    for (int i  = 0; i < itemCount; i++)
    {
      var baseIdx = 10 + (i * 13);
      var iconId = addon->AtkValues[baseIdx].Int;
      var itemName = addon->AtkValues[baseIdx + 1].GetValueAsString();
      var quantity = addon->AtkValues[baseIdx + 2].Int;
      var priceStr = addon->AtkValues[baseIdx + 3].GetValueAsString();
      var slotIndex = addon->AtkValues[baseIdx + 5].Int;

      // HQ detection: icon texture ID >= 1000000 means HQ
      var isHQ = iconId >= 1000000;

      // Parse price from formatted string (e.g., "2,025" → 2025)
      if (!int.TryParse(priceStr.Replace(",", ""), out var pricePerUnit))
        continue;

      // Resolve item ID from name via Lumina
      var cleanName = Communicator.CleanItemName(itemName, out _);
      var payload = Communicator.RawItemNameToItemPayload(itemName);
      var itemID = payload?.ItemId ?? 0u;
      if (itemID == 0) continue;

      // First-seen tracking: key = retainerName|slotIndex|itemId
      var key = $"{CurrentRetainerName}|{slotIndex}|{itemID}";
      if (!GilStorage.Data.FirstSeenTimestamps.TryGetValue(key, out var firstSeen))
      {
        firstSeen = now;
        GilStorage.Data.FirstSeenTimestamps[key] = firstSeen;
      }

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

    Svc.Log.Debug($"[GilTrack] Snapshotted {itemCount} listings for {CurrentRetainerName}");
  }

  /// <summary>
  /// Called at pinch run end. Captures gil balances, updates current listings,
  /// cleans up first-seen timestamps for items no longer listed, and saves.
  /// </summary>
  public static unsafe void FinalizeRun()
  {
    // Capture player gil
    var playerGil = (long)InventoryManager.Instance()->GetGil();

    // Capture retainer gil from Retainer Manager
    var retainerGil = new Dictionary<string, long>();
    var rm = RetainerManager.Instance();

    for (uint i = 0; i < (rm->GetRetainerCount()); i++)
    {
      var retainer = rm->GetRetainerBySortedIndex(i);
      var name = retainer->NameString;
      if (!string.IsNullOrEmpty(name))
      {
        retainerGil[name] = retainer->Gil;
      }
    }

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Record gil snapshot
    GilStorage.Data.GilHistory.Add(new GilSnapshot
    {
      Timestamp = now,
      PlayerGil = playerGil,
      RetainerGil = retainerGil,
    });

    // Record market snapshot
    var totalListingValue = _runListings.Sum(l => (long)l.UnitPrice * l.Quantity);
    var avgAgeDays = _runListings.Count > 0 ? (_runListings.Average(l => (now - l.FirstSeenTimestamp) / 86400.0)) : 0.0;

    GilStorage.Data.MarketHistory.Add(new MarketSnapshot
    {
      Timestamp = now,
      ItemCount = _runListings.Count,
      TotalListingValue = totalListingValue,
      AverageListingAgeDays = avgAgeDays,
    });

    // Update current listings
    GilStorage.Data.CurrentListings = [.. _runListings];

    // Clean up first-seen timestamps for items no longer listed
    // ONLY for retainers that were actually scanned in this run
    // Key format: "retainerName|slotIndex|itemId"
    var activeKeys = _runListings
        .Select(l => $"{l.RetainerName}|{l.SlotIndex}|{l.ItemId}")
        .ToHashSet();

    var staleKeys = GilStorage.Data.FirstSeenTimestamps.Keys
        .Where(k =>
        {
          // Only clean up keys belonging to retainers we scanned
          var parts = k.Split('|');
          if (parts.Length < 3) return false;
          var retainerName = parts[0];
          return _runRetainers.Contains(retainerName) && !activeKeys.Contains(k);
        })
        .ToList();

    foreach (var key in staleKeys)
      GilStorage.Data.FirstSeenTimestamps.Remove(key);

    // Save everything
    GilStorage.Save();
    Svc.Log.Info($"[GilTrack] Run complete: {_runListings.Count} listings, " + $"{totalListingValue:N0}g on market, {playerGil:N0}g player");
  }

  // --- Sale History Processing (called from hook) ---

  /// <summary>
  /// Process sale history entries from the RetainerHistory hook.
  /// Deduplicates against existing sales and appends new ones.
  /// </summary>
  public static void ProcessSaleHistory(
    List<RetainerHistoryHook.RetainerHistoryData> entries, string retainerName)
  {
    var newSales = new List<SaleRecord>();

    foreach (var entry in entries)
    {
      // Dedup: check if we already have this exact sale
      var isDuplicate = GilStorage.Data.Sales.Any(s =>
        s.ItemId == entry.ItemID &&
        s.SaleTimestamp == entry.UnixTimeSeconds &&
        s.RetainerName == retainerName);

      if (isDuplicate) continue;

      newSales.Add(new SaleRecord
      {
        ItemId = entry.ItemID,
        ItemName = GetItemName(entry.ItemID),
        Category = GetItemCategory(entry.ItemID),
        UnitPrice = (int)entry.UnitPrice,  // UnitPrice confirmed via debug — no division needed
        Quantity = (int)entry.Quantity,
        IsHQ = entry.IsHQ,
        RetainerName = retainerName,
        BuyerName = entry.BuyerName,
        SaleTimestamp = entry.UnixTimeSeconds
      });
    }

    if (newSales.Count > 0)
    {
      GilStorage.Data.Sales.AddRange(newSales);
      GilStorage.Save();
      Svc.Log.Info($"[GilTrack] {newSales.Count} new sale(s) from {retainerName}");
    }
  }
}