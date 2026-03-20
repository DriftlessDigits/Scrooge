using System;
using System.Collections.Generic;
using System.IO;
using ECommons.DalamudServices;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// SQLite-backed persistent storage for gil tracking data and quotes.
/// Replaces the old JSON-based GilData approach. All database access
/// goes through this class — no other file touches SQL.
/// </summary>
internal static class GilStorage
{
  private static SqliteConnection? _connection;
  internal static string DbPath => Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "scrooge.db");

  // =========================================================================
  // Lifecycle
  // =========================================================================

  /// <summary>
  /// Opens the database, runs bootstrap (tables, migration, seeds), and prunes.
  /// Called once from Plugin constructor.
  /// </summary>
  internal static void Initialize()
  {
    Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
    _connection = new SqliteConnection($"Data Source={DbPath}");
    _connection.Open();

    // WAL mode prevents SQLITE_BUSY when the hook writes on the game thread while the UI reads on the draw thread
    using (var walCmd = new SqliteCommand("PRAGMA journal_mode=WAL;", _connection))
      walCmd.ExecuteNonQuery();

    // All first-time setup lives in the bootstrap file
    GilStorageBootstrap.Run(_connection);

    Prune();
  }

  /// <summary>Begins a transaction. Caller must Commit() or Dispose() to rollback.</summary>
  internal static SqliteTransaction BeginTransaction() => _connection!.BeginTransaction();

  /// <summary>Drops all tables, restores JSON backup if available, and re-runs bootstrap. Debug only.</summary>
  internal static void ResetDatabase()
  {
    var tables = new[] { "transactions", "retainer_snapshots", "gil_snapshots",
        "market_snapshots", "listings", "category_groups", "quotes" };
    foreach (var table in tables)
    {
      using var cmd = new SqliteCommand($"DROP TABLE IF EXISTS {table}", _connection);
      cmd.ExecuteNonQuery();
    }
    using var pragma = new SqliteCommand("PRAGMA user_version = 0;", _connection);
    pragma.ExecuteNonQuery();

    // Restore JSON backup so migration can re-run
    var jsonPath = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "gil_data.json");
    var bakPath = jsonPath + ".bak";
    if (!File.Exists(jsonPath) && File.Exists(bakPath))
      File.Move(bakPath, jsonPath);

    GilStorageBootstrap.Run(_connection!);
    Prune();
  }

  /// <summary>Closes the database connection. Called from Plugin.Dispose().</summary>
  internal static void Dispose()
  {
    _connection?.Close();
    _connection?.Dispose();
    _connection = null;
  }


  // =========================================================================
  // Gil Tracking — Writes (called from GilTracker)
  // =========================================================================

  /// <summary>
  /// Inserts a gil balance snapshot. Returns the new row ID so retainer
  /// snapshots can be linked to it.
  /// </summary>
  internal static long InsertGilSnapshot(long timestamp, long playerGil, string source,
      SqliteTransaction? transaction = null)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO gil_snapshots (timestamp, player_gil, source)
      VALUES (@ts, @gil, @src);
      SELECT last_insert_rowid();",
      _connection);
    cmd.Transaction = transaction;
    cmd.Parameters.AddWithValue("@ts", timestamp);
    cmd.Parameters.AddWithValue("@gil", playerGil);
    cmd.Parameters.AddWithValue("@src", source);
    return (long)cmd.ExecuteScalar()!;
  }

  /// <summary>Inserts a single retainer's gil balance, linked to a snapshot.</summary>
  internal static void InsertRetainerSnapshot(long snapshotId, string retainerName, long gil,
      SqliteTransaction? transaction = null)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO retainer_snapshots (snapshot_id, retainer_name, gil)
      VALUES (@sid, @name, @gil)",
      _connection);
    cmd.Transaction = transaction;
    cmd.Parameters.AddWithValue("@sid", snapshotId);
    cmd.Parameters.AddWithValue("@name", retainerName);
    cmd.Parameters.AddWithValue("@gil", gil);
    cmd.ExecuteNonQuery();
  }

  /// <summary>Inserts a gil transaction (sale, purchase, etc.).</summary>
  internal static void InsertTransaction(long timestamp, string direction, string source,
      long amount, uint itemId, string itemName, string category, int quantity,
      int unitPrice, bool isHq, string retainerName, string counterparty,
      SqliteTransaction? transaction = null)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO transactions (timestamp, direction, source, amount, item_id, item_name, category, quantity, unit_price, is_hq, retainer_name, counterparty)
      VALUES (@ts, @dir, @src, @amt, @iid, @iname, @cat, @qty, @up, @hq, @ret, @cpty)",
      _connection);
    cmd.Transaction = transaction;
    cmd.Parameters.AddWithValue("@ts", timestamp);
    cmd.Parameters.AddWithValue("@dir", direction);
    cmd.Parameters.AddWithValue("@src", source);
    cmd.Parameters.AddWithValue("@amt", amount);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@iname", itemName);
    cmd.Parameters.AddWithValue("@cat", category);
    cmd.Parameters.AddWithValue("@qty", quantity);
    cmd.Parameters.AddWithValue("@up", unitPrice);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@ret", retainerName);
    cmd.Parameters.AddWithValue("@cpty", counterparty);
    cmd.ExecuteNonQuery();
  }

  /// <summary>Checks if a transaction already exists (for deduplication).</summary>
  internal static bool TransactionExists(uint itemId, long timestamp, string retainerName)
  {
    using var cmd = new SqliteCommand(
      @"SELECT COUNT(*) FROM transactions
      WHERE item_id = @iid AND timestamp = @ts AND retainer_name = @ret",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@ts", timestamp);
    cmd.Parameters.AddWithValue("@ret", retainerName);
    return (long)cmd.ExecuteScalar()! > 0;
  }

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

  /// <summary>Inserts aggregate market stats for a pinch run.</summary>
  internal static void InsertMarketSnapshot(long timestamp, int itemCount,
      long totalValue, double avgAge, SqliteTransaction? transaction = null)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO market_snapshots (timestamp, item_count, total_listing_value, avg_listing_age_days)
      VALUES (@ts, @cnt, @val, @avg)",
      _connection);
    cmd.Transaction = transaction;
    cmd.Parameters.AddWithValue("@ts", timestamp);
    cmd.Parameters.AddWithValue("@cnt", itemCount);
    cmd.Parameters.AddWithValue("@val", totalValue);
    cmd.Parameters.AddWithValue("@avg", avgAge);
    cmd.ExecuteNonQuery();
  }

  // =========================================================================
  // Gil Tracking — Reads (called from GilWindow)
  // =========================================================================

  /// <summary>
  /// Gets the most recent gil snapshot with per-retainer balances.
  /// Reconstructs the GilSnapshot record including the RetainerGil dictionary
  /// by joining gil_snapshots + retainer_snapshots.
  /// </summary>
  internal static GilSnapshot? GetLatestSnapshot()
  {
    // Get the latest snapshot row — read values and close reader
    // before opening a second one (only one active reader per connection)
    long snapshotId, timestamp, playerGil;
    using (var snapCmd = new SqliteCommand(
      "SELECT id, timestamp, player_gil FROM gil_snapshots ORDER BY timestamp DESC LIMIT 1",
      _connection))
    using (var snapReader = snapCmd.ExecuteReader())
    {
      if (!snapReader.Read()) return null;
      snapshotId = snapReader.GetInt64(0);
      timestamp = snapReader.GetInt64(1);
      playerGil = snapReader.GetInt64(2);
    } // snapReader disposed here before opening retReader

    // Get retainer balances for this snapshot
    var retainerGil = new Dictionary<string, long>();
    using var retCmd = new SqliteCommand(
      "SELECT retainer_name, gil FROM retainer_snapshots WHERE snapshot_id = @id",
      _connection);
    retCmd.Parameters.AddWithValue("@id", snapshotId);
    using var retReader = retCmd.ExecuteReader();
    while (retReader.Read())
    {
      retainerGil[retReader.GetString(0)] = retReader.GetInt64(1);
    }

    return new GilSnapshot
    {
      Timestamp = timestamp,
      PlayerGil = playerGil,
      RetainerGil = retainerGil
    };
  }

  /// <summary>Gets the N most recent retainer sales as SaleRecord objects.</summary>
  internal static List<SaleRecord> GetRecentSales(int limit)
  {
    var sales = new List<SaleRecord>();
    using var cmd = new SqliteCommand(
      @"SELECT item_id, item_name, category, unit_price, quantity, is_hq,
      retainer_name, counterparty, timestamp
      FROM transactions
      WHERE direction = 'earned' AND source = 'retainer_sale'
      ORDER BY timestamp DESC
      LIMIT @limit", 
      _connection);
    cmd.Parameters.AddWithValue("@limit", limit);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      sales.Add(new SaleRecord
      {
        ItemId = (uint)reader.GetInt64(0),
        ItemName = reader.GetString(1),
        Category = reader.GetString(2),
        UnitPrice = reader.GetInt32(3),
        Quantity = reader.GetInt32(4),
        IsHQ = reader.GetInt32(5) != 0,
        RetainerName = reader.GetString(6),
        BuyerName = reader.GetString(7),
        SaleTimestamp = reader.GetInt64(8)
      });
    }
    return sales;
  }

  /// <summary>
  /// Gets sales grouped by display group since a timestamp.
  /// JOINs with category_groups to roll up granular UI categories
  /// (e.g., "Gladiator's Arm" → "Weapons"). Unmapped categories
  /// fall back to their raw ItemUICategory name.
  /// </summary>
  internal static List<(string Category, int Count, long Gil)> GetCategorySales(
      long sinceTimestamp)
  {
    var results = new List<(string Category, int Count, long Gil)>();
    using var cmd = new SqliteCommand(
      @"SELECT COALESCE(cg.display_group, t.category) as grp,
      COUNT(*) as cnt, SUM(t.amount) as gil
      FROM transactions t
      LEFT JOIN category_groups cg ON t.category = cg.ui_category
      WHERE t.direction = 'earned' AND t.source = 'retainer_sale' AND t.timestamp > @since
      GROUP BY grp
      ORDER BY gil DESC",
      _connection);
    cmd.Parameters.AddWithValue("@since", sinceTimestamp);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      results.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
    }
    return results;
  }

  /// <summary>
  /// Returns the count of distinct categories in transactions
  /// that have no mapping in category_groups.
  /// </summary>
  internal static int GetUnmappedCategoryCount()
  {
    using var cmd = new SqliteCommand(
      @"SELECT COUNT(DISTINCT t.category) FROM transactions t
      LEFT JOIN category_groups cg ON t.category = cg.ui_category
      WHERE cg.ui_category IS NULL AND t.category != ''",
      _connection);
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

  // =========================================================================
  // Quotes
  // =========================================================================

  /// <summary>
  /// Picks a random quote from the 10 least-recently-displayed,
  /// updates its last_displayed timestamp, and returns it.
  /// </summary>
  internal static QuoteRecord? GetRandomQuote()
  {
    var candidates = new List<QuoteRecord>();
    using var cmd = new SqliteCommand(
      "SELECT id, text, author FROM quotes ORDER BY last_displayed ASC LIMIT 10",
      _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      candidates.Add(new QuoteRecord
      {
        Id = reader.GetInt32(0),
        Text = reader.GetString(1),
        Author = reader.GetString(2)
      });
    }

    if (candidates.Count == 0) return null;

    var pick = candidates[Random.Shared.Next(candidates.Count)];

    // Update last_displayed so this quote moves to the back of the queue
    using var updateCmd = new SqliteCommand(
      "UPDATE quotes SET last_displayed = @now WHERE id = @id",
      _connection);
    updateCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    updateCmd.Parameters.AddWithValue("@id", pick.Id);
    updateCmd.ExecuteNonQuery();

    return pick;
  }

  // =========================================================================
  // Maintenance — Pruning (runs on every startup)
  // =========================================================================

  /// <summary>
  /// Prunes old data on startup:
  /// - Transactions older than 90 days: deleted
  /// - Gil snapshots older than 30 days: thinned to one per day
  /// - Market snapshots older than 30 days: thinned to one per day
  /// - Orphaned retainer snapshots: deleted
  /// </summary>
  private static void Prune()
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var ninetyDays = now - (90L * 24 * 3600);
    var thirtyDays = now - (30L * 24 * 3600);

    // Delete old transactions
    using (var cmd = new SqliteCommand(
      "DELETE FROM transactions WHERE timestamp < @cutoff",
      _connection))
    {
      cmd.Parameters.AddWithValue("@cutoff", ninetyDays);
      cmd.ExecuteNonQuery();
    }

    // Thin old gil snapshots: keep one per day for entries older than 30 days
    using (var cmd = new SqliteCommand(
      @"DELETE FROM gil_snapshots
      WHERE timestamp < @cutoff
      AND id NOT IN (
        SELECT MAX(id) FROM gil_snapshots
        WHERE timestamp < @cutoff
        GROUP BY DATE(timestamp, 'unixepoch')
      )",
      _connection))
    {
      cmd.Parameters.AddWithValue("@cutoff", thirtyDays);
      cmd.ExecuteNonQuery();
    }

    // Delete orphaned retainer snapshots
    using (var cmd = new SqliteCommand(
      @"DELETE FROM retainer_snapshots
      WHERE snapshot_id NOT IN (SELECT id FROM gil_snapshots)",
      _connection))
    {
      cmd.ExecuteNonQuery();
    }

    // Thin old market snapshots: same pattern
    using (var cmd = new SqliteCommand(
      @"DELETE FROM market_snapshots
      WHERE timestamp < @cutoff
      AND id NOT IN (
        SELECT MAX(id) FROM market_snapshots
        WHERE timestamp < @cutoff
        GROUP BY DATE(timestamp, 'unixepoch')
      )",
      _connection))
    {
      cmd.Parameters.AddWithValue("@cutoff", thirtyDays);
      cmd.ExecuteNonQuery();
    }
  }
}