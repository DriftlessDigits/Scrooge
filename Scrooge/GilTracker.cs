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
  private static HashSet<string>? _mappedCategories;

  // Run-level State
  private static readonly List<ListingRecord> _runListings = [];
  private static string _runSource = "full";
  private static int _finalItemCount;
  private static long _finalListingValue;

  /// <summary>Current retainer name — set by AutoPinch, read by the hook.</summary>
  public static string CurrentRetainerName { get; set; } = String.Empty;

  // --- Lumina Helpers ---

  public static string GetItemCategory(uint itemID)
  {
    var category = _items.GetRow(itemID).ItemUICategory.ValueNullable?.Name.ToString() ?? "Unknown";
    _mappedCategories ??= GilStorage.GetMappedCategories();
    if (!_mappedCategories.Contains(category))
      Svc.Log.Warning($"[GilTrack] Unmapped category: \"{category}\" (itemID={itemID}, name={GetItemName(itemID)})");
    return category;
  }

  public static string GetItemName(uint itemID)
  {
    return _items.GetRow(itemID).Name.ToString();
  }

  // --- Pinch Run Integration ---

  /// <summary>Called at pinch run start. Clears run-level state.</summary>
  /// <param name="source">"full" for all-retainer runs, or the retainer name for single-retainer runs.</param>
  public static void StartRun(string source = "full")
  {
    _runListings.Clear();
    CurrentRetainerName = String.Empty;
    _runSource = source;
    _finalItemCount = 0;
    _finalListingValue = 0;
    _mappedCategories ??= GilStorage.GetMappedCategories();
  }

  /// <summary>
  /// Records the final (post-adjustment) price for an item and updates the listing in the DB.
  /// Called from AutoPinch.SetNewPrice for every item after pricing.
  /// </summary>
  public static void RecordFinalPrice(uint itemId, int unitPrice, int quantity)
  {
    _finalItemCount++;
    _finalListingValue += (long)unitPrice * quantity;
    GilStorage.UpdateListingPrice(CurrentRetainerName, itemId, unitPrice);
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

    // Step 0: Remember when everything PREVIOUSLY listed here was first seen.
    // Listings that disappear in this snapshot most likely sold — their
    // first_seen feeds sold_after_days when ProcessSaleHistory reconciles
    // the sale moments later (the snapshot delete would otherwise erase it).
    var previousAges = GilStorage.GetRetainerListingAges(CurrentRetainerName);

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
      var priceDigits = new string(priceStr.Where(char.IsDigit).ToArray());
      if (!int.TryParse(priceDigits, out var pricePerUnit))
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

      previousAges.Remove((itemID, isHQ));

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

    // Whatever survived in previousAges disappeared from the sell list —
    // stash for sale reconciliation (sold_after_days). Keyed per retainer;
    // replaced wholesale each snapshot so stale entries never accumulate.
    _recentDelistings[CurrentRetainerName] = previousAges;

    Svc.Log.Debug($"[GilTrack] Snapshotted {itemCount} listings for {CurrentRetainerName}");
  }

  /// <summary>
  /// Listings that vanished in the latest snapshot per retainer, with their
  /// first_seen — the sit-time evidence for sales reconciled right after.
  /// In-memory only: a restart between snapshot and reconcile just means
  /// sold_after_days stays null for that sale (evidence lost, never wrong).
  /// </summary>
  private static readonly Dictionary<string, Dictionary<(uint ItemId, bool IsHq), long>> _recentDelistings = [];

  /// <summary>
  /// Called at pinch run end. Captures gil balances, updates current listings,
  /// cleans up first-seen timestamps for items no longer listed, and saves.
  /// </summary>
  public static void FinalizeRun()
  {
    if (GameSafe.PlayerGil() is not long playerGil)
    {
      Svc.Log.Warning("[GilTrack] InventoryManager unavailable at run end — skipping final snapshot");
      return;
    }

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Gil snapshot → DB
    var snapshotId = GilStorage.InsertGilSnapshot(now, playerGil, "pinch_run");
    foreach (var (name, gil) in GameSafe.RetainerBalances())
      GilStorage.InsertRetainerSnapshot(snapshotId, name, gil);

    // Market snapshot → DB (post-adjustment values from RecordFinalPrice)
    var avgAgeDays = _runListings.Count > 0
        ? _runListings.Average(l => (now - l.FirstSeenTimestamp) / 86400.0)
        : 0.0;
    GilStorage.InsertMarketSnapshot(now, _finalItemCount, _finalListingValue, avgAgeDays, _runSource);

    Svc.Log.Info($"[GilTrack] Run complete: {_finalItemCount} listings, " +
        $"{_finalListingValue:N0}g on market, {playerGil:N0}g player");

    GilGoals.Evaluate();
  }

  // --- Passive Balance Snapshots (called from GilTrackEventListener) ---

  /// <summary>
  /// Takes a player-only balance snapshot if tracking is enabled and dedup passes.
  /// Retainer balances are NOT captured (only available at the summoning bell).
  /// </summary>
  public static void TakeBalanceSnapshot(string source)
  {
    if (!Plugin.Configuration.EnableGilTracking) return;
    if (GameSafe.PlayerGil() is not long playerGil) return;

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Dedup: skip if last snapshot < 60s ago and balance unchanged
    var last = GilStorage.GetLatestPlayerGilAndTimestamp();
    if (last.HasValue && (now - last.Value.Timestamp) < 60 && last.Value.Gil == playerGil)
      return;

    GilStorage.InsertGilSnapshot(now, playerGil, source);
    Svc.Log.Debug($"[GilTrack] Balance snapshot ({source}): {playerGil:N0}g");

    GilGoals.Evaluate();
  }

  // --- Sale History Processing (called from hook) ---

  /// <summary>
  /// Process sale history entries from the RetainerHistory hook.
  /// For each entry:
  /// - If already finalized, drop any duplicate pending twin (chat-captured row
  ///   left orphaned by a race or prior bug) and move on.
  /// - Else try to promote a matching pending row (from chat parsing).
  /// - Else insert fresh.
  /// UpsertLastSalePrice runs on the non-skip paths since both produce authoritative
  /// unit_price + timestamp data.
  /// </summary>
  public static void ProcessSaleHistory(List<RetainerHistoryHook.RetainerHistoryData> entries, string retainerName)
  {
    var newCount = 0;
    var promotedCount = 0;
    var dedupedCount = 0;

    foreach (var entry in entries)
    {
      // UnitPrice from the hook is the TOTAL sale price, not per-unit.
      var totalGil = (long)entry.UnitPrice;
      var realUnitPrice = entry.Quantity > 0 ? (int)(entry.UnitPrice / entry.Quantity) : (int)entry.UnitPrice;

      if (GilStorage.TransactionExists(entry.ItemID, entry.UnixTimeSeconds, retainerName))
      {
        if (GilStorage.DeleteDuplicatePendingSale(entry.ItemID, (int)entry.Quantity, totalGil))
          dedupedCount++;
        continue;
      }

      var promoted = GilStorage.TryPromotePendingSale(
        entry.ItemID,
        (int)entry.Quantity,
        totalGil,
        (long)entry.UnixTimeSeconds,
        retainerName,
        entry.BuyerName);

      if (promoted)
      {
        promotedCount++;
      }
      else
      {
        GilStorage.InsertTransaction(
          (long)entry.UnixTimeSeconds,
          "earned",
          "retainer_sale",
          totalGil,
          entry.ItemID,
          GetItemName(entry.ItemID),
          GetItemCategory(entry.ItemID),
          (int)entry.Quantity,
          realUnitPrice,
          entry.IsHQ,
          retainerName,
          entry.BuyerName);

        newCount++;
      }

      // Sit time: if this item vanished from the sell list in the snapshot
      // that just ran, its first_seen tells how long it sat before selling.
      int? soldAfterDays = null;
      if (_recentDelistings.TryGetValue(retainerName, out var delistings)
          && delistings.TryGetValue((entry.ItemID, entry.IsHQ), out var firstSeen)
          && (long)entry.UnixTimeSeconds >= firstSeen)
        soldAfterDays = (int)(((long)entry.UnixTimeSeconds - firstSeen) / 86400);

      GilStorage.UpsertLastSalePrice(entry.ItemID, entry.IsHQ, realUnitPrice, (long)entry.UnixTimeSeconds, soldAfterDays);
    }

    if (newCount > 0 || promotedCount > 0 || dedupedCount > 0)
      Svc.Log.Info($"[GilTrack] {newCount} new / {promotedCount} reconciled / {dedupedCount} deduped sale(s) from {retainerName}");
  }
}