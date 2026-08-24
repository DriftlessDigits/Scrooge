using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// Our own shelf: <c>listings</c>, <c>last_sale_prices</c> and <c>own_listing_writes</c>.
///
/// <para><c>listings</c> is a mirror of the retainer windows as last seen, keyed by
/// slot; <c>last_sale_prices</c> is the memory of what each item actually cleared at;
/// <c>own_listing_writes</c> is the record of prices <em>we</em> put on the board, so a
/// later scan can tell our own hand from a competitor's.</para>
/// </summary>
internal static partial class GilStorage
{
  /// <summary>
  /// Looks up the first_seen timestamp for a listing. Returns null if not found.
  /// Called from GilTracker.SnapshotListings() to preserve existing timestamps.
  /// </summary>
  internal static long? GetFirstSeen(string retainerName, int slotIndex, uint itemId)
  {
    using var cmd = new SqliteCommand(
      @"SELECT first_seen FROM listings
      WHERE retainer_name = @ret AND slot_index = @slot AND item_id = @iid",
      _connection);
    cmd.Parameters.AddWithValue("@ret", retainerName);
    cmd.Parameters.AddWithValue("@slot", slotIndex);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    var result = cmd.ExecuteScalar();
    return result != null ? (long)result : null;
  }

  /// <summary>
  /// Inserts or replaces a listing. All columns must be specified —
  /// INSERT OR REPLACE deletes the old row first, so missing columns
  /// would get default values instead of the old data.
  /// </summary>
  internal static void UpsertListing(string retainerName, int slotIndex, uint itemId,
      string itemName, string category, int unitPrice, int quantity, bool isHq,
      long firstSeen, long lastUpdated, SqliteTransaction? transaction = null)
  {
    using var cmd = new SqliteCommand(
      @"INSERT OR REPLACE INTO listings
      (retainer_name, slot_index, item_id, item_name, category, unit_price, quantity, is_hq, first_seen, last_updated)
      VALUES (@ret, @slot, @iid, @iname, @cat, @up, @qty, @hq, @fs, @lu)",
      _connection);
    cmd.Transaction = transaction;
    cmd.Parameters.AddWithValue("@ret", retainerName);
    cmd.Parameters.AddWithValue("@slot", slotIndex);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@iname", itemName);
    cmd.Parameters.AddWithValue("@cat", category);
    cmd.Parameters.AddWithValue("@up", unitPrice);
    cmd.Parameters.AddWithValue("@qty", quantity);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@fs", firstSeen);
    cmd.Parameters.AddWithValue("@lu", lastUpdated);
    cmd.ExecuteNonQuery();
  }

  /// <summary>Updates the unit_price for a listing after price adjustment.</summary>
  internal static void UpdateListingPrice(string retainerName, uint itemId, int newPrice)
  {
    using var cmd = new SqliteCommand(
      @"UPDATE listings SET unit_price = @price, last_updated = @now
      WHERE retainer_name = @ret AND item_id = @iid",
      _connection);
    cmd.Parameters.AddWithValue("@price", newPrice);
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    cmd.Parameters.AddWithValue("@ret", retainerName);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Deletes all listings for a retainer. Called BEFORE UpsertListing
  /// calls in SnapshotListings — the upserts re-insert current items.
  /// first_seen is preserved because SnapshotListings reads it via
  /// GetFirstSeen() before this delete runs.
  /// </summary>
  internal static void DeleteRetainerListings(string retainerName, SqliteTransaction? transaction = null)
  {
    using var cmd = new SqliteCommand(
      "DELETE FROM listings WHERE retainer_name = @ret", _connection);
    cmd.Transaction = transaction;
    cmd.Parameters.AddWithValue("@ret", retainerName);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Gets the last sale for each (item, quality) ever sold via retainer.
  /// Reads from the last_sale_prices table (survives transaction pruning).
  /// SoldAfterDays is how long the listing sat before selling — null for
  /// sales reconciled before V13 started capturing it.
  /// </summary>
  internal static Dictionary<(uint ItemId, bool IsHq), (int Price, long Timestamp, int? SoldAfterDays)> GetLastSalePrices()
  {
    var prices = new Dictionary<(uint, bool), (int, long, int?)>();
    using var cmd = new SqliteCommand(
      "SELECT item_id, is_hq, unit_price, timestamp, sold_after_days FROM last_sale_prices",
      _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      prices[((uint)reader.GetInt64(0), reader.GetInt32(1) != 0)] =
        (reader.GetInt32(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt32(4));
    return prices;
  }

  /// <summary>
  /// Last sale price for one item at one quality, or null when that variant
  /// has never sold via retainer. NQ and HQ are separate evidence — an NQ
  /// sale says nothing about the HQ price.
  /// </summary>
  internal static int? GetLastSalePrice(uint itemId, bool isHq)
  {
    using var cmd = new SqliteCommand(
      "SELECT unit_price FROM last_sale_prices WHERE item_id = @iid AND is_hq = @hq",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    var result = cmd.ExecuteScalar();
    return result == null || result == DBNull.Value ? null : Convert.ToInt32(result);
  }

  /// <summary>
  /// Last sale price AND timestamp for one item at one quality - the own-sales
  /// pricing fallback needs both (staleness gate + "sold Nd ago" label).
  /// </summary>
  internal static (int Price, long Timestamp)? GetLastSalePriceWithTime(uint itemId, bool isHq)
  {
    using var cmd = new SqliteCommand(
      "SELECT unit_price, timestamp FROM last_sale_prices WHERE item_id = @iid AND is_hq = @hq",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return null;
    return (reader.GetInt32(0), reader.GetInt64(1));
  }

  /// <summary>
  /// Upserts the last sale price for an item variant. Called on every retainer
  /// sale. soldAfterDays (listing sit time, when known) only overwrites when
  /// provided — a sale without sit-time evidence keeps the previous value.
  /// </summary>
  internal static void UpsertLastSalePrice(uint itemId, bool isHq, int unitPrice, long timestamp, int? soldAfterDays = null)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO last_sale_prices (item_id, is_hq, unit_price, timestamp, sold_after_days)
        VALUES (@iid, @hq, @price, @ts, @days)
        ON CONFLICT(item_id, is_hq) DO UPDATE SET
          unit_price = @price, timestamp = @ts,
          sold_after_days = COALESCE(@days, sold_after_days)",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@price", unitPrice);
    cmd.Parameters.AddWithValue("@ts", timestamp);
    cmd.Parameters.AddWithValue("@days", (object?)soldAfterDays ?? DBNull.Value);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// first_seen per (item, quality) currently listed on a retainer — oldest
  /// wins when the same variant is listed in multiple slots. Read by
  /// GilTracker.SnapshotListings BEFORE the delete/re-insert so disappeared
  /// (= likely sold) listings can carry their sit time to sale reconciliation.
  /// </summary>
  internal static Dictionary<(uint ItemId, bool IsHq), long> GetRetainerListingAges(string retainerName)
  {
    var ages = new Dictionary<(uint, bool), long>();
    using var cmd = new SqliteCommand(
      @"SELECT item_id, is_hq, MIN(first_seen) FROM listings
        WHERE retainer_name = @ret GROUP BY item_id, is_hq",
      _connection);
    cmd.Parameters.AddWithValue("@ret", retainerName);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      ages[((uint)reader.GetInt64(0), reader.GetInt32(1) != 0)] = reader.GetInt64(2);
    return ages;
  }

  // =========================================================================
  // The standing book (V21) - the write side of our own board presence
  // =========================================================================

  /// <summary>
  /// Appends the own writes one finished run performed, in a single transaction -
  /// a run's write-side effect lands whole or not at all, so a crash mid-flush can
  /// never leave the book counting half a Hawk run. Returns the row count the
  /// database actually took, which the caller compares against what it handed over:
  /// a short bank is a reportable event, not a shrug.
  /// </summary>
  internal static int InsertOwnWrites(IReadOnlyList<OwnWrite> writes)
  {
    if (writes.Count == 0) return 0;
    using var transaction = _connection!.BeginTransaction();
    var inserted = StandingBookSchema.InsertOwnWrites(_connection, transaction, writes);
    transaction.Commit();
    return inserted;
  }

  /// <summary>
  /// Own writes performed after the given time - the delta layered on the last
  /// ground-truth board read. Passing the last full scan's timestamp is what makes
  /// the resync structural: everything older is already reflected in the listings
  /// table, so it simply falls outside the window.
  /// </summary>
  internal static List<OwnWrite> GetOwnWritesSince(long since)
  {
    var writes = new List<OwnWrite>();
    using var cmd = new SqliteCommand(
      @"SELECT written_at, kind, item_id, is_hq, retainer_name, unit_price, prior_price, quantity, source
      FROM own_listing_writes WHERE written_at > @since ORDER BY written_at",
      _connection);
    cmd.Parameters.AddWithValue("@since", since);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      if (!Enum.TryParse<WriteKind>(reader.GetString(1), ignoreCase: true, out var kind))
        continue; // an unknown kind is a future writer's row - skip, never guess
      writes.Add(new OwnWrite(
        reader.GetInt64(0), kind, (uint)reader.GetInt64(2), reader.GetInt32(3) != 0,
        reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt32(7),
        reader.GetString(8)));
    }
    return writes;
  }

  /// <summary>
  /// Gil that SOLD off our board after the given time. Chat capture sees these
  /// live, so the book deflates as listings clear instead of waiting for the next
  /// pinch to notice they are gone. Pending (chat-parsed) rows count - they are
  /// promoted in place when the sale history reconciles, never duplicated.
  /// </summary>
  internal static long GetRetainerSalesGilSince(long since)
  {
    using var cmd = new SqliteCommand(
      @"SELECT COALESCE(SUM(amount), 0) FROM transactions
      WHERE source = 'retainer_sale' AND timestamp > @since",
      _connection);
    cmd.Parameters.AddWithValue("@since", since);
    return Convert.ToInt64(cmd.ExecuteScalar());
  }

  /// <summary>
  /// How many listings SOLD off our board after the given time - the count sibling
  /// of <see cref="GetRetainerSalesGilSince"/>, one row per cleared listing. Feeds
  /// the book-kept listing count the same way the gil sum feeds the book's value.
  /// </summary>
  internal static int GetRetainerSalesCountSince(long since)
  {
    using var cmd = new SqliteCommand(
      @"SELECT COUNT(*) FROM transactions
      WHERE source = 'retainer_sale' AND timestamp > @since",
      _connection);
    cmd.Parameters.AddWithValue("@since", since);
    return Convert.ToInt32(cmd.ExecuteScalar());
  }

  /// <summary>Gets listings older than the cutoff timestamp (slow movers).</summary>
  internal static List<ListingRecord> GetSlowMovers(long olderThan)
  {
    var listings = new List<ListingRecord>();
    using var cmd = new SqliteCommand(
      @"SELECT retainer_name, slot_index, item_id, item_name, category,
      unit_price, quantity, is_hq, first_seen, last_updated
      FROM listings
      WHERE first_seen < @cutoff
      ORDER BY first_seen ASC",
      _connection);
    cmd.Parameters.AddWithValue("@cutoff", olderThan);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      listings.Add(new ListingRecord
      {
        RetainerName = reader.GetString(0),
        SlotIndex = reader.GetInt32(1),
        ItemId = (uint)reader.GetInt64(2),
        ItemName = reader.GetString(3),
        Category = reader.GetString(4),
        UnitPrice = reader.GetInt32(5),
        Quantity = reader.GetInt32(6),
        IsHQ = reader.GetInt32(7) != 0,
        FirstSeenTimestamp = reader.GetInt64(8),
        LastUpdatedTimestamp = reader.GetInt64(9)
      });
    }
    return listings;
  }

  /// <summary>
  /// All own standing listings from the captured board state (the listings
  /// table), for the Ledger's Listed pile. READ-only - the listings-table
  /// tripwire forbids ad hoc WRITES, not reads. Richest-first by unit price.
  /// </summary>
  internal static List<ListingRecord> GetAllCurrentListings()
  {
    var listings = new List<ListingRecord>();
    using var cmd = new SqliteCommand(
      @"SELECT retainer_name, slot_index, item_id, item_name, category,
      unit_price, quantity, is_hq, first_seen, last_updated
      FROM listings
      ORDER BY unit_price DESC",
      _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      listings.Add(new ListingRecord
      {
        RetainerName = reader.GetString(0),
        SlotIndex = reader.GetInt32(1),
        ItemId = (uint)reader.GetInt64(2),
        ItemName = reader.GetString(3),
        Category = reader.GetString(4),
        UnitPrice = reader.GetInt32(5),
        Quantity = reader.GetInt32(6),
        IsHQ = reader.GetInt32(7) != 0,
        FirstSeenTimestamp = reader.GetInt64(8),
        LastUpdatedTimestamp = reader.GetInt64(9)
      });
    }
    return listings;
  }
}
