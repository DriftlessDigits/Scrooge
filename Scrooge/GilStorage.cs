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

  /// <summary>The shared SQLite connection. Borrowers must not Dispose it.</summary>
  internal static SqliteConnection Connection =>
    _connection ?? throw new InvalidOperationException(
      "GilStorage is not initialized — Initialize() failed or Dispose() already ran");

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
    // Derive the drop list from the live schema — a hardcoded list goes stale
    // every migration, leaving orphan tables at user_version 0 that make the
    // re-run migrations throw.
    var tables = new List<string>();
    using (var listCmd = new SqliteCommand(
      "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'", _connection))
    using (var reader = listCmd.ExecuteReader())
    {
      while (reader.Read())
        tables.Add(reader.GetString(0));
    }

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
    _categoryGroupCache = null;
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
      SqliteTransaction? transaction = null, int? ventureTokens = null)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO gil_snapshots (timestamp, player_gil, source, venture_tokens)
      VALUES (@ts, @gil, @src, @vt);
      SELECT last_insert_rowid();",
      _connection);
    cmd.Transaction = transaction;
    cmd.Parameters.AddWithValue("@ts", timestamp);
    cmd.Parameters.AddWithValue("@gil", playerGil);
    cmd.Parameters.AddWithValue("@src", source);
    cmd.Parameters.AddWithValue("@vt", (object?)ventureTokens ?? DBNull.Value);
    return (long)cmd.ExecuteScalar()!;
  }

  /// <summary>Records one collected venture result (V15).</summary>
  internal static void InsertVentureReturn(long capturedAt, string retainerName,
      uint itemId, int quantity, bool isHq)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO venture_returns (captured_at, retainer_name, item_id, quantity, is_hq)
      VALUES (@ts, @ret, @item, @qty, @hq)",
      _connection);
    cmd.Parameters.AddWithValue("@ts", capturedAt);
    cmd.Parameters.AddWithValue("@ret", retainerName);
    cmd.Parameters.AddWithValue("@item", itemId);
    cmd.Parameters.AddWithValue("@qty", quantity);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.ExecuteNonQuery();
  }

  /// <summary>Venture returns captured in the last N days, newest first.</summary>
  internal static List<(long CapturedAt, string Retainer, uint ItemId, int Quantity, bool IsHq)>
      GetVentureReturns(int sinceDays)
  {
    var rows = new List<(long, string, uint, int, bool)>();
    var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sinceDays * 86400L;
    using var cmd = new SqliteCommand(
      @"SELECT captured_at, retainer_name, item_id, quantity, is_hq
        FROM venture_returns WHERE captured_at >= @cutoff
        ORDER BY captured_at DESC",
      _connection);
    cmd.Parameters.AddWithValue("@cutoff", cutoff);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      rows.Add((reader.GetInt64(0), reader.GetString(1), (uint)reader.GetInt64(2),
        reader.GetInt32(3), reader.GetInt32(4) != 0));
    return rows;
  }

  /// <summary>
  /// Oldest and newest venture-token stock readings in the last N days
  /// (bell-snapshot piggyback), or null when fewer than two readings exist.
  /// Burn/acquire rate = the delta over the window.
  /// </summary>
  internal static ((long Ts, int Tokens) First, (long Ts, int Tokens) Last)? GetVentureTokenSpan(int sinceDays)
  {
    var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sinceDays * 86400L;
    using var cmd = new SqliteCommand(
      @"SELECT timestamp, venture_tokens FROM gil_snapshots
        WHERE venture_tokens IS NOT NULL AND timestamp >= @cutoff
        ORDER BY timestamp ASC",
      _connection);
    cmd.Parameters.AddWithValue("@cutoff", cutoff);
    using var reader = cmd.ExecuteReader();
    (long, int)? first = null, last = null;
    while (reader.Read())
    {
      var row = (reader.GetInt64(0), reader.GetInt32(1));
      first ??= row;
      last = row;
    }
    return first is { } f && last is { } l && f.Item1 != l.Item1
      ? ((f.Item1, f.Item2), (l.Item1, l.Item2))
      : null;
  }

  /// <summary>
  /// Upserts one Universalis fetch round for a world (V16). Framework thread
  /// only — callers marshal here from the fetch worker.
  /// </summary>
  internal static void UpsertUniversalisStats(uint worldId, List<UniversalisStat> stats, long fetchedAt)
  {
    using var tx = Connection.BeginTransaction();
    using var cmd = new SqliteCommand(
      @"INSERT INTO universalis_stats (item_id, world_id, nq_velocity, hq_velocity, last_sale_at, last_upload_at, fetched_at)
      VALUES (@item, @world, @nq, @hq, @sale, @upload, @fetched)
      ON CONFLICT (item_id, world_id) DO UPDATE SET
        nq_velocity = @nq, hq_velocity = @hq, last_sale_at = @sale,
        last_upload_at = @upload, fetched_at = @fetched",
      _connection, tx);
    var pItem = cmd.Parameters.Add("@item", Microsoft.Data.Sqlite.SqliteType.Integer);
    var pWorld = cmd.Parameters.Add("@world", Microsoft.Data.Sqlite.SqliteType.Integer);
    var pNq = cmd.Parameters.Add("@nq", Microsoft.Data.Sqlite.SqliteType.Real);
    var pHq = cmd.Parameters.Add("@hq", Microsoft.Data.Sqlite.SqliteType.Real);
    var pSale = cmd.Parameters.Add("@sale", Microsoft.Data.Sqlite.SqliteType.Integer);
    var pUpload = cmd.Parameters.Add("@upload", Microsoft.Data.Sqlite.SqliteType.Integer);
    var pFetched = cmd.Parameters.Add("@fetched", Microsoft.Data.Sqlite.SqliteType.Integer);

    pWorld.Value = worldId;
    pFetched.Value = fetchedAt;
    foreach (var stat in stats)
    {
      pItem.Value = stat.ItemId;
      pNq.Value = stat.NqVelocity;
      pHq.Value = stat.HqVelocity;
      pSale.Value = (object?)stat.LastSaleAt ?? DBNull.Value;
      pUpload.Value = (object?)stat.LastUploadAt ?? DBNull.Value;
      cmd.ExecuteNonQuery();
    }
    tx.Commit();
  }

  /// <summary>All cached Universalis rows for one world, keyed by item id.</summary>
  internal static Dictionary<uint, (UniversalisStat Stat, long FetchedAt)> GetUniversalisStats(uint worldId)
  {
    var rows = new Dictionary<uint, (UniversalisStat, long)>();
    using var cmd = new SqliteCommand(
      @"SELECT item_id, nq_velocity, hq_velocity, last_sale_at, last_upload_at, fetched_at
        FROM universalis_stats WHERE world_id = @world",
      _connection);
    cmd.Parameters.AddWithValue("@world", worldId);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      var itemId = (uint)reader.GetInt64(0);
      rows[itemId] = (new UniversalisStat(
        itemId,
        reader.GetDouble(1),
        reader.GetDouble(2),
        reader.IsDBNull(3) ? null : reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4)),
        reader.GetInt64(5));
    }
    return rows;
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
      SqliteTransaction? transaction = null, bool isPending = false)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO transactions (timestamp, direction, source, amount, item_id, item_name, category, quantity, unit_price, is_hq, retainer_name, counterparty, is_pending)
      VALUES (@ts, @dir, @src, @amt, @iid, @iname, @cat, @qty, @up, @hq, @ret, @cpty, @pending)",
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
    cmd.Parameters.AddWithValue("@pending", isPending ? 1 : 0);
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
  /// Deletes one pending retainer_sale row that duplicates an already-finalized sale.
  /// Called when a hook entry matches an existing finalized row — the oldest pending
  /// row with the same (item_id, quantity, amount) is the chat-captured twin of that
  /// finalized sale and should be dropped rather than left as an orphan.
  /// FIFO to match TryPromotePendingSale's 1:1 semantics: one hook entry resolves
  /// exactly one pending row, whether by promotion or deduplication. Returns true
  /// if a row was removed.
  /// </summary>
  internal static bool DeleteDuplicatePendingSale(uint itemId, int quantity, long amount)
  {
    using var cmd = new SqliteCommand(
      @"DELETE FROM transactions
        WHERE id = (
          SELECT id FROM transactions
          WHERE is_pending = 1
            AND direction  = 'earned'
            AND source     = 'retainer_sale'
            AND item_id    = @iid
            AND quantity   = @qty
            AND amount     = @amt
          ORDER BY timestamp ASC
          LIMIT 1
        )",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@qty", quantity);
    cmd.Parameters.AddWithValue("@amt", amount);
    return cmd.ExecuteNonQuery() > 0;
  }

  /// <summary>
  /// Tries to promote a pending retainer_sale row (inserted by the chat parser)
  /// to a finalized row using authoritative data from RetainerHistoryHook.
  /// Match key: (item_id, quantity, amount). FIFO on collision — oldest pending
  /// promotes first. Returns true if a pending row was promoted, false if none matched.
  /// </summary>
  internal static bool TryPromotePendingSale(
    uint itemId, int quantity, long amount,
    long serverTimestamp, string retainerName, string buyerName)
  {
    using var cmd = new SqliteCommand(
      @"UPDATE transactions
        SET timestamp     = @ts,
            retainer_name = @ret,
            counterparty  = @buyer,
            is_pending    = 0
        WHERE id = (
          SELECT id FROM transactions
          WHERE is_pending = 1
            AND item_id  = @iid
            AND quantity = @qty
            AND amount   = @amt
          ORDER BY timestamp ASC
          LIMIT 1
        )",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@qty", quantity);
    cmd.Parameters.AddWithValue("@amt", amount);
    cmd.Parameters.AddWithValue("@ts", serverTimestamp);
    cmd.Parameters.AddWithValue("@ret", retainerName);
    cmd.Parameters.AddWithValue("@buyer", buyerName);
    return cmd.ExecuteNonQuery() > 0;
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

  /// <summary>Inserts aggregate market stats for a pinch run.</summary>
  internal static void InsertMarketSnapshot(long timestamp, int itemCount,
      long totalValue, double avgAge, string source, SqliteTransaction? transaction = null)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO market_snapshots (timestamp, item_count, total_listing_value, avg_listing_age_days, source)
      VALUES (@ts, @cnt, @val, @avg, @src)",
      _connection);
    cmd.Transaction = transaction;
    cmd.Parameters.AddWithValue("@ts", timestamp);
    cmd.Parameters.AddWithValue("@cnt", itemCount);
    cmd.Parameters.AddWithValue("@val", totalValue);
    cmd.Parameters.AddWithValue("@avg", avgAge);
    cmd.Parameters.AddWithValue("@src", source);
    cmd.ExecuteNonQuery();
  }

  // =========================================================================
  // Gil Tracking — Reads (called from GilWindow)
  // =========================================================================

  /// <summary>
  /// Returns the latest snapshot for the Portfolio summary: latest known player gil
  /// paired with the latest known retainer balances. Player gil comes from the most
  /// recent gil_snapshots row (including zone_change captures). Retainer balances come
  /// from the most recent snapshot that actually has retainer_snapshots rows — zone
  /// changes only record player gil, so the latest snapshot and the latest retainer
  /// snapshot are not always the same row.
  /// </summary>
  internal static GilSnapshot? GetLatestSnapshot()
  {
    // Latest snapshot (any source) — may be a zone_change row with no retainer data.
    long timestamp, playerGil;
    using (var snapCmd = new SqliteCommand(
      "SELECT timestamp, player_gil FROM gil_snapshots ORDER BY timestamp DESC LIMIT 1",
      _connection))
    using (var snapReader = snapCmd.ExecuteReader())
    {
      if (!snapReader.Read()) return null;
      timestamp = snapReader.GetInt64(0);
      playerGil = snapReader.GetInt64(1);
    } // snapReader disposed here before opening retReader

    // Retainer balances from the most recent snapshot that has retainer rows.
    var retainerGil = new Dictionary<string, long>();
    using var retCmd = new SqliteCommand(
      @"SELECT retainer_name, gil FROM retainer_snapshots
        WHERE snapshot_id = (
          SELECT s.id FROM gil_snapshots s
          WHERE EXISTS (SELECT 1 FROM retainer_snapshots r WHERE r.snapshot_id = s.id)
          ORDER BY s.timestamp DESC LIMIT 1
        )",
      _connection);
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

  /// <summary>Gets the most recent snapshot's timestamp and player gil for dedup checks.</summary>
  internal static (long Timestamp, long Gil)? GetLatestPlayerGilAndTimestamp()
  {
    using var cmd = new SqliteCommand(
      "SELECT timestamp, player_gil FROM gil_snapshots ORDER BY timestamp DESC LIMIT 1",
      _connection);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return null;
    return (reader.GetInt64(0), reader.GetInt64(1));
  }

  /// <summary>
  /// Returns per-snapshot player gil and (optional) retainer gil sum, ordered by timestamp ascending.
  /// RetainerGil is null when the snapshot has no retainer_snapshots rows (e.g. zone_change captures).
  /// Caller decides how to handle gaps (carry-forward, filter, etc.).
  /// </summary>
  internal static IReadOnlyList<(long Timestamp, long PlayerGil, long? RetainerGil)> GetTotalGilHistory()
  {
    var rows = new List<(long, long, long?)>();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var cmd = new SqliteCommand(
      @"SELECT s.timestamp, s.player_gil, SUM(r.gil) AS retainer_gil
        FROM gil_snapshots s
        LEFT JOIN retainer_snapshots r ON r.snapshot_id = s.id
        GROUP BY s.id
        ORDER BY s.timestamp ASC",
      _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      long? retainer = reader.IsDBNull(2) ? null : reader.GetInt64(2);
      rows.Add((reader.GetInt64(0), reader.GetInt64(1), retainer));
    }
    sw.Stop();
    Svc.Log.Verbose($"[GilHistory] GetTotalGilHistory returned {rows.Count} rows in {sw.ElapsedMilliseconds}ms");
    return rows;
  }

  /// <summary>Gets the N most recent retainer sales as SaleRecord objects.</summary>
  internal static List<SaleRecord> GetRecentSales(int limit)
  {
    var sales = new List<SaleRecord>();
    using var cmd = new SqliteCommand(
      @"SELECT item_id, item_name, category, unit_price, quantity, is_hq,
      retainer_name, counterparty, timestamp, is_pending
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
        SaleTimestamp = reader.GetInt64(8),
        IsPending = reader.GetInt32(9) != 0
      });
    }
    return sales;
  }

  /// <summary>
  /// Count of retainer_sale rows still marked pending (not yet reconciled).
  /// Safe to call from draw loops: returns 0 if the connection isn't open yet
  /// (plugin startup race) or has been closed (teardown).
  /// </summary>
  internal static int GetPendingSaleCount()
  {
    if (_connection is null || _connection.State != System.Data.ConnectionState.Open)
      return 0;
    using var cmd = new SqliteCommand(
      @"SELECT COUNT(*) FROM transactions
        WHERE direction = 'earned' AND source = 'retainer_sale' AND is_pending = 1",
      _connection);
    return Convert.ToInt32(cmd.ExecuteScalar());
  }

  /// <summary>
  /// Returns all transactions with optional direction/source filters, most recent first.
  /// </summary>
  internal static List<TransactionRecord> GetTransactions(string? direction = null, string? source = null,
      long? since = null, int limit = -1, int offset = 0)
  {
    var results = new List<TransactionRecord>();
    var where = BuildTransactionWhere(direction, source, since);
    var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
    var limitClause = limit > 0 ? $"LIMIT {limit} OFFSET {offset}" : "";

    using var cmd = new SqliteCommand(
      $@"SELECT timestamp, direction, source, amount, item_name, category, quantity, unit_price
         FROM transactions {whereClause}
         ORDER BY timestamp DESC
         {limitClause}",
      _connection);
    AddTransactionParams(cmd, direction, source, since);

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      results.Add(new TransactionRecord
      {
        Timestamp = reader.GetInt64(0),
        Direction = reader.GetString(1),
        Source = reader.GetString(2),
        Amount = reader.GetInt64(3),
        ItemName = reader.GetString(4),
        Category = reader.GetString(5),
        Quantity = reader.GetInt32(6),
        UnitPrice = reader.GetInt32(7),
      });
    }
    return results;
  }

  /// <summary>Returns total count of transactions matching the given filters.</summary>
  internal static int GetTransactionCount(string? direction = null, string? source = null, long? since = null)
  {
    var where = BuildTransactionWhere(direction, source, since);
    var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

    using var cmd = new SqliteCommand(
      $"SELECT COUNT(*) FROM transactions {whereClause}", _connection);
    AddTransactionParams(cmd, direction, source, since);
    return Convert.ToInt32(cmd.ExecuteScalar());
  }

  private static List<string> BuildTransactionWhere(string? direction, string? source, long? since)
  {
    var where = new List<string>();
    if (direction != null) where.Add("direction = @dir");
    if (source != null) where.Add("source = @src");
    if (since != null) where.Add("timestamp >= @since");
    return where;
  }

  private static void AddTransactionParams(SqliteCommand cmd, string? direction, string? source, long? since)
  {
    if (direction != null) cmd.Parameters.AddWithValue("@dir", direction);
    if (source != null) cmd.Parameters.AddWithValue("@src", source);
    if (since != null) cmd.Parameters.AddWithValue("@since", since.Value);
  }

  /// <summary>
  /// Returns earned/spent totals grouped by source for a given time range.
  /// </summary>
  internal static List<(string Direction, string Source, long Total, int Count)>
      GetEarnedVsSpent(long? since = null)
  {
    var results = new List<(string, string, long, int)>();
    var whereClause = since.HasValue ? "WHERE timestamp >= @since" : "";

    using var cmd = new SqliteCommand(
      $@"SELECT direction, source, SUM(amount) AS total, COUNT(*) AS cnt
         FROM transactions {whereClause}
         GROUP BY direction, source
         ORDER BY direction, total DESC",
      _connection);
    if (since.HasValue) cmd.Parameters.AddWithValue("@since", since.Value);

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      results.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3)));
    return results;
  }

  /// <summary>
  /// Returns daily gil totals (player + retainers) from the last snapshot of each day.
  /// Retainer balances carried forward from the most recent snapshot that has them.
  /// </summary>
  internal static List<(string Date, long TotalGil, long Delta)> GetDailyChanges()
  {
    var rows = GetTotalGilHistory();
    var daily = new List<(string Date, long TotalGil, long Delta)>();
    if (rows.Count == 0) return daily;

    // Walk through snapshots, carry forward retainer balance, track last total per day
    int firstWithRetainer = -1;
    for (int i = 0; i < rows.Count; i++)
      if (rows[i].RetainerGil.HasValue) { firstWithRetainer = i; break; }
    if (firstWithRetainer < 0) return daily;

    long lastRetainer = rows[firstWithRetainer].RetainerGil!.Value;
    string? currentDay = null;
    long dayTotal = 0;

    for (int i = firstWithRetainer; i < rows.Count; i++)
    {
      var r = rows[i];
      if (r.RetainerGil.HasValue) lastRetainer = r.RetainerGil.Value;
      var total = r.PlayerGil + lastRetainer;
      var day = DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).LocalDateTime.ToString("yyyy-MM-dd");

      if (day != currentDay)
      {
        if (currentDay != null)
          daily.Add((currentDay, dayTotal, 0));
        currentDay = day;
      }
      dayTotal = total;
    }
    if (currentDay != null)
      daily.Add((currentDay, dayTotal, 0));

    // Compute deltas
    for (int i = daily.Count - 1; i > 0; i--)
      daily[i] = (daily[i].Date, daily[i].TotalGil, daily[i].TotalGil - daily[i - 1].TotalGil);

    return daily;
  }

  /// <summary>
  /// Computes untracked gil changes over a time range. Compares total snapshot diffs
  /// against sum of tracked transactions. The gap is what we can't account for.
  /// Returns (earned untracked, spent untracked) — both positive values.
  /// </summary>
  internal static (long UntrackedEarned, long UntrackedSpent) GetUntrackedDeltas(long? since = null)
  {
    // Get first and last snapshots in the range (player_gil + retainer carry-forward)
    var history = GetTotalGilHistory();
    if (history.Count < 2) return (0, 0);

    // Find range boundaries
    int startIdx = 0;
    if (since.HasValue)
    {
      for (int i = 0; i < history.Count; i++)
        if (history[i].Timestamp >= since.Value) { startIdx = i; break; }
    }

    // Walk through with carry-forward to get first and last total
    long lastRetainer = 0;
    bool hasRetainer = false;
    long firstTotal = 0, lastTotal = 0;
    long firstTotalTs = 0;
    bool firstSet = false;

    for (int i = 0; i < history.Count; i++)
    {
      if (history[i].RetainerGil.HasValue)
      {
        lastRetainer = history[i].RetainerGil!.Value;
        hasRetainer = true;
      }
      if (!hasRetainer) continue;

      var total = history[i].PlayerGil + lastRetainer;
      if (i >= startIdx && !firstSet)
      {
        firstTotal = total;
        firstTotalTs = history[i].Timestamp;
        firstSet = true;
      }
      lastTotal = total;
    }

    if (!firstSet) return (0, 0);
    var snapshotDelta = lastTotal - firstTotal;

    // Sum tracked transactions within the snapshot window only.
    // Using the user's `since` would include transactions before the first snapshot,
    // whose gil is already baked into firstTotal — causing phantom untracked spend.
    using var cmd = new SqliteCommand(
      @"SELECT
           COALESCE(SUM(CASE WHEN direction = 'earned' THEN amount ELSE 0 END), 0),
           COALESCE(SUM(CASE WHEN direction = 'spent' THEN amount ELSE 0 END), 0)
         FROM transactions WHERE timestamp >= @since",
      _connection);
    cmd.Parameters.AddWithValue("@since", firstTotalTs);
    using var reader = cmd.ExecuteReader();
    reader.Read();
    var trackedEarned = reader.GetInt64(0);
    var trackedSpent = reader.GetInt64(1);

    var trackedNet = trackedEarned - trackedSpent;
    var untracked = snapshotDelta - trackedNet;

    if (untracked > 0)
      return (untracked, 0);
    if (untracked < 0)
      return (0, -untracked);
    return (0, 0);
  }

  /// <summary>
  /// Computes untracked delta between the two most recent snapshots.
  /// Used for debug logging on zone change.
  /// </summary>
  internal static (long SnapshotDiff, long TrackedNet, long Untracked)? GetLatestSnapshotGap()
  {
    var history = GetTotalGilHistory();
    if (history.Count < 2) return null;

    // Find the last two snapshots with retainer data (carry-forward)
    long lastRetainer = 0;
    bool hasRetainer = false;
    long prevTotal = 0, currentTotal = 0;
    long prevTs = 0;

    for (int i = 0; i < history.Count; i++)
    {
      if (history[i].RetainerGil.HasValue)
      {
        lastRetainer = history[i].RetainerGil!.Value;
        hasRetainer = true;
      }
      if (!hasRetainer) continue;

      prevTotal = currentTotal;
      prevTs = i > 0 ? history[i - 1].Timestamp : history[i].Timestamp;
      currentTotal = history[i].PlayerGil + lastRetainer;
    }

    if (prevTotal == 0) return null;
    var snapshotDiff = currentTotal - prevTotal;
    if (snapshotDiff == 0) return null;

    // Sum tracked transactions between the two most recent snapshots
    var lastTs = history[history.Count - 1].Timestamp;
    var secondLastTs = history[history.Count - 2].Timestamp;

    using var cmd = new SqliteCommand(
      @"SELECT
          COALESCE(SUM(CASE WHEN direction = 'earned' THEN amount ELSE 0 END), 0) -
          COALESCE(SUM(CASE WHEN direction = 'spent' THEN amount ELSE 0 END), 0)
        FROM transactions WHERE timestamp >= @from AND timestamp <= @to",
      _connection);
    cmd.Parameters.AddWithValue("@from", secondLastTs);
    cmd.Parameters.AddWithValue("@to", lastTs);
    using var reader = cmd.ExecuteReader();
    reader.Read();
    var trackedNet = reader.GetInt64(0);

    return (snapshotDiff, trackedNet, snapshotDiff - trackedNet);
  }

  /// <summary>Returns the timestamp of the earliest transaction, or null if none exist.</summary>
  internal static long? GetEarliestTransactionTimestamp()
  {
    using var cmd = new SqliteCommand(
      "SELECT MIN(timestamp) FROM transactions", _connection);
    var result = cmd.ExecuteScalar();
    return result is DBNull or null ? null : (long)result;
  }

  /// <summary>Returns distinct source values from the transactions table.</summary>
  internal static List<string> GetDistinctSources()
  {
    var sources = new List<string>();
    using var cmd = new SqliteCommand("SELECT DISTINCT source FROM transactions ORDER BY source", _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      sources.Add(reader.GetString(0));
    return sources;
  }

  /// <summary>
  /// Gets sales grouped by macro_group, display_group, and raw ui_category
  /// for the 3-level category tree in the Gil Dashboard.
  /// </summary>
  internal static List<(string MacroGroup, string MainGroup, string Category, int Count, long Gil)>
      GetCategoryTree(long sinceTimestamp)
  {
    var results = new List<(string MacroGroup, string MainGroup, string Category, int Count, long Gil)>();
    using var cmd = new SqliteCommand(
      @"SELECT COALESCE(cg.macro_group, '') as macro,
      COALESCE(cg.display_group, t.category) as main,
      t.category as micro,
      COUNT(*) as cnt, SUM(t.amount) as gil
      FROM transactions t
      LEFT JOIN category_groups cg ON t.category = cg.ui_category
      WHERE t.direction = 'earned' AND t.source = 'retainer_sale' AND t.timestamp > @since
      GROUP BY macro, main, micro
      ORDER BY macro, gil DESC",
      _connection);
    cmd.Parameters.AddWithValue("@since", sinceTimestamp);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      results.Add((
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        reader.GetInt64(4)));
    }
    return results;
  }

  /// <summary>
  /// Gets per-retainer summary: last sale, sale count, total gil, and average listing age.
  /// Sources retainer names from both transactions and listings so retainers appear
  /// regardless of which table has data.
  /// </summary>
  internal static List<RetainerSummary> GetRetainerSummary(long sinceTimestamp)
  {
    var results = new List<RetainerSummary>();
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Pending rows (is_pending=1) have no retainer attribution yet — exclude them
    // from the per-retainer summary so they don't show up under a blank row.
    using var cmd = new SqliteCommand(
      @"SELECT
      r.retainer_name,
      COALESCE(t.last_sale, 0) as last_sale,
      COALESCE(t.sale_count, 0) as sale_count,
      COALESCE(t.total_gil, 0) as total_gil,
      COALESCE(la.avg_age, 0) as avg_age_days
      FROM (
        SELECT retainer_name FROM transactions
          WHERE direction = 'earned' AND source = 'retainer_sale' AND is_pending = 0
        UNION
        SELECT retainer_name FROM listings
      ) r
      LEFT JOIN (
        SELECT retainer_name,
          MAX(timestamp) as last_sale,
          COUNT(*) as sale_count,
          SUM(amount) as total_gil
        FROM transactions
        WHERE direction = 'earned' AND source = 'retainer_sale' AND is_pending = 0 AND timestamp > @since
        GROUP BY retainer_name
      ) t ON r.retainer_name = t.retainer_name
      LEFT JOIN (
        SELECT retainer_name, AVG((@now - first_seen) / 86400.0) as avg_age
        FROM listings
        GROUP BY retainer_name
      ) la ON r.retainer_name = la.retainer_name
      ORDER BY COALESCE(t.total_gil, 0) DESC",
      _connection);
    cmd.Parameters.AddWithValue("@since", sinceTimestamp);
    cmd.Parameters.AddWithValue("@now", now);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      results.Add(new RetainerSummary
      {
        RetainerName = reader.GetString(0),
        LastSaleTimestamp = reader.GetInt64(1),
        SaleCount = reader.GetInt32(2),
        TotalGil = reader.GetInt64(3),
        AvgListingAgeDays = reader.GetDouble(4),
      });
    }
    return results;
  }

  /// <summary>Returns the set of all ui_category values that have a mapping in category_groups.</summary>
  internal static HashSet<string> GetMappedCategories()
  {
    var categories = new HashSet<string>();
    using var cmd = new SqliteCommand("SELECT ui_category FROM category_groups", _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      categories.Add(reader.GetString(0));
    return categories;
  }

  private static Dictionary<string, (string Macro, string Display)>? _categoryGroupCache;

  /// <summary>
  /// Returns the (macro_group, display_group) pair for a given raw ui_category,
  /// or null if the category has no mapping. Cached on first access; cleared only
  /// on database reset.
  /// </summary>
  internal static (string Macro, string Display)? GetCategoryGroup(string uiCategory)
  {
    if (_categoryGroupCache == null)
    {
      var map = new Dictionary<string, (string Macro, string Display)>();
      using var cmd = new SqliteCommand(
        "SELECT ui_category, macro_group, display_group FROM category_groups",
        _connection);
      using var reader = cmd.ExecuteReader();
      while (reader.Read())
        map[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
      _categoryGroupCache = map;
    }

    return _categoryGroupCache.TryGetValue(uiCategory, out var group) ? group : null;
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

  // =========================================================================
  // Triage Flags (V12) - persistent until acted on or dismissed
  // =========================================================================

  /// <summary>
  /// Inserts a triage flag, or refreshes the existing OPEN flag for the same
  /// (item, hq, retainer, reason) - re-flagging updates detail/prices/created_at
  /// instead of stacking duplicates.
  ///
  /// When <paramref name="evidence"/> is supplied, the flag is EVIDENCE-KEYED
  /// (decision memory): the stored snapshot is compared to the live one and the
  /// row is only touched when the world actually moved (TriageMemory.DecideUpsert).
  /// An unchanged world - or a bare manual reprice - is a silent no-op, so a
  /// stuck-thin item does not reset its "held since" clock every pinch. Passing
  /// null keeps the legacy always-refresh behavior (slow_evict and friends).
  /// </summary>
  internal static void UpsertTriageFlag(uint itemId, bool isHq, string retainerName, int slotIndex,
      string reason, string detail, int oldPrice, int flaggedPrice,
      TriageMemory.EvidenceSnapshot? evidence = null, int minHistorySamples = 0,
      string scope = "")
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Evidence-keyed path: read the stored snapshot, let the pure core decide.
    var action = TriageMemory.FlagAction.Refresh; // legacy default: always UPDATE-then-INSERT
    var evidenceStr = "";
    if (evidence is TriageMemory.EvidenceSnapshot snap)
    {
      evidenceStr = snap.Serialize();
      action = TriageMemory.DecideUpsert(ReadOpenFlagEvidence(itemId, isHq, retainerName, reason), snap, minHistorySamples);
      Svc.Log.Debug($"[Triage] {reason} item {itemId}{(isHq ? " HQ" : "")} @ {retainerName}: {action} (ev {evidenceStr})");
      if (action == TriageMemory.FlagAction.Silent)
        return; // same unanswered question - don't churn the row or the clock
    }

    if (action != TriageMemory.FlagAction.Insert)
    {
      using var update = new SqliteCommand(
        @"UPDATE triage_flags
          SET detail = @detail, old_price = @old, flagged_price = @flagged,
              slot_index = @slot, created_at = @now, evidence = @evidence,
              scope = CASE WHEN @scope <> '' THEN @scope ELSE scope END
          WHERE item_id = @iid AND is_hq = @hq AND retainer_name = @ret
            AND reason = @reason AND status = 'open'",
        _connection);
      update.Parameters.AddWithValue("@detail", detail);
      update.Parameters.AddWithValue("@old", oldPrice);
      update.Parameters.AddWithValue("@flagged", flaggedPrice);
      update.Parameters.AddWithValue("@slot", slotIndex);
      update.Parameters.AddWithValue("@now", now);
      update.Parameters.AddWithValue("@evidence", evidenceStr);
      update.Parameters.AddWithValue("@scope", scope);
      update.Parameters.AddWithValue("@iid", (long)itemId);
      update.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      update.Parameters.AddWithValue("@ret", retainerName);
      update.Parameters.AddWithValue("@reason", reason);
      if (update.ExecuteNonQuery() > 0) return;
    }

    using var insert = new SqliteCommand(
      @"INSERT INTO triage_flags
          (created_at, item_id, is_hq, retainer_name, slot_index, reason, detail, old_price, flagged_price, evidence, scope)
        VALUES (@now, @iid, @hq, @ret, @slot, @reason, @detail, @old, @flagged, @evidence, @scope)",
      _connection);
    insert.Parameters.AddWithValue("@now", now);
    insert.Parameters.AddWithValue("@iid", (long)itemId);
    insert.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    insert.Parameters.AddWithValue("@ret", retainerName);
    insert.Parameters.AddWithValue("@slot", slotIndex);
    insert.Parameters.AddWithValue("@reason", reason);
    insert.Parameters.AddWithValue("@detail", detail);
    insert.Parameters.AddWithValue("@old", oldPrice);
    insert.Parameters.AddWithValue("@flagged", flaggedPrice);
    insert.Parameters.AddWithValue("@evidence", evidenceStr);
    insert.Parameters.AddWithValue("@scope", scope);
    insert.ExecuteNonQuery();
  }

  /// <summary>
  /// Item ids with an open lane_held flag — the standing thin-history set.
  /// Feeds the community-history prefetch at run start (the triage table IS
  /// the persistent list of items that will hold thin again next pinch).
  /// </summary>
  internal static List<uint> GetOpenLaneHeldItemIds()
  {
    var ids = new List<uint>();
    using var cmd = new SqliteCommand(
      "SELECT DISTINCT item_id FROM triage_flags WHERE status = 'open' AND reason = 'lane_held'",
      _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      ids.Add((uint)reader.GetInt64(0));
    return ids;
  }

  /// <summary>
  /// The evidence snapshot stored on the open flag for a key. NULL means no
  /// open row exists; an empty string means a row exists WITHOUT a snapshot
  /// (pre-V17 legacy) — DecideUpsert adopts that row rather than duplicating
  /// it. Deterministic pick: prefer an evidenced row, then the newest, so a
  /// not-yet-deduped pair never feeds a legacy '' to the gate.
  /// </summary>
  private static string? ReadOpenFlagEvidence(uint itemId, bool isHq, string retainerName, string reason)
  {
    using var cmd = new SqliteCommand(
      @"SELECT evidence FROM triage_flags
        WHERE item_id = @iid AND is_hq = @hq AND retainer_name = @ret
          AND reason = @reason AND status = 'open'
        ORDER BY (evidence <> '') DESC, created_at DESC, id DESC LIMIT 1",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@ret", retainerName);
    cmd.Parameters.AddWithValue("@reason", reason);
    var result = cmd.ExecuteScalar();
    return result == null ? null
      : result == DBNull.Value ? "" // row exists, no snapshot — adopt, don't duplicate
      : (string)result;
  }

  /// <summary>
  /// Self-heal (M2): closes every open flag on this (item, hq, retainer) whose
  /// reason did NOT re-fire this pass - the resolved live rules and the dead-
  /// producer strays (upward_held/outlier_warn) alike. TriageMemory.FlagsToClose
  /// makes the pick (pure/tested); this method just reads the rows and stamps
  /// the closes as 'resolved'. Returns the number of flags healed.
  /// </summary>
  internal static int SelfHealTriageFlags(uint itemId, bool isHq, string retainerName,
      IReadOnlySet<string> raisedThisPass)
  {
    var open = new List<(long Id, string Reason)>();
    using (var cmd = new SqliteCommand(
      @"SELECT id, reason FROM triage_flags
        WHERE item_id = @iid AND is_hq = @hq AND retainer_name = @ret AND status = 'open'",
      _connection))
    {
      cmd.Parameters.AddWithValue("@iid", (long)itemId);
      cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      cmd.Parameters.AddWithValue("@ret", retainerName);
      using var reader = cmd.ExecuteReader();
      while (reader.Read())
        open.Add((reader.GetInt64(0), reader.GetString(1)));
    }

    var toClose = TriageMemory.FlagsToClose(open, raisedThisPass);
    foreach (var id in toClose)
      SetTriageFlagStatus(id, "resolved");
    return toClose.Count;
  }

  /// <summary>Open triage flags, newest first. Loaded by the TriageWindow alongside the current run's items.</summary>
  internal static List<TriageFlag> GetOpenTriageFlags()
  {
    var flags = new List<TriageFlag>();
    using var cmd = new SqliteCommand(
      @"SELECT id, created_at, item_id, is_hq, retainer_name, slot_index,
               reason, detail, old_price, flagged_price, status
        FROM triage_flags WHERE status = 'open'
        ORDER BY created_at DESC",
      _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      flags.Add(new TriageFlag
      {
        Id = reader.GetInt64(0),
        CreatedAt = reader.GetInt64(1),
        ItemId = (uint)reader.GetInt64(2),
        IsHq = reader.GetInt32(3) != 0,
        RetainerName = reader.GetString(4),
        SlotIndex = reader.GetInt32(5),
        Reason = reader.GetString(6),
        Detail = reader.GetString(7),
        OldPrice = reader.GetInt32(8),
        FlaggedPrice = reader.GetInt32(9),
        Status = reader.GetString(10),
      });
    }
    return flags;
  }

  /// <summary>Closes a flag: status = 'dismissed' or 'actioned', stamps acted_at.</summary>
  internal static void SetTriageFlagStatus(long flagId, string status)
  {
    using var cmd = new SqliteCommand(
      "UPDATE triage_flags SET status = @status, acted_at = @now WHERE id = @id",
      _connection);
    cmd.Parameters.AddWithValue("@status", status);
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    cmd.Parameters.AddWithValue("@id", flagId);
    cmd.ExecuteNonQuery();
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

  /// <summary>
  /// Records a routing override: the router said one thing, the player did
  /// another. Written by the Hawk window when a gated item is checked anyway.
  /// </summary>
  internal static void InsertRoutingOverride(uint itemId, bool isHq, int ilvl,
      string routerVerdict, string routerReason, string playerVerdict)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO routing_overrides
          (created_at, item_id, is_hq, ilvl, router_verdict, router_reason, player_verdict)
        VALUES (@now, @iid, @hq, @ilvl, @rv, @reason, @pv)",
      _connection);
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@ilvl", ilvl);
    cmd.Parameters.AddWithValue("@rv", routerVerdict);
    cmd.Parameters.AddWithValue("@reason", routerReason);
    cmd.Parameters.AddWithValue("@pv", playerVerdict);
    cmd.ExecuteNonQuery();
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
  // Market memory (V19) — append-diff events + current-board snapshot
  // =========================================================================

  private static string EventKindTag(MarketEvents.EventKind k) => k switch
  {
    MarketEvents.EventKind.Appeared => "appeared",
    MarketEvents.EventKind.Disappeared => "disappeared",
    _ => "price_moved",
  };

  private static string ResolutionTag(MarketEvents.DisappearResolution r) => r switch
  {
    MarketEvents.DisappearResolution.Sold => "sold",
    MarketEvents.DisappearResolution.Pulled => "pulled",
    _ => "gone",
  };

  /// <summary>
  /// The stored current-board snapshot for one item — the prior scan the next diff
  /// compares against. Returns the listings plus the timestamp of the scan that
  /// produced them (0 when the item has never been scanned), which becomes the
  /// next event's seen_after (the window's start).
  /// </summary>
  internal static (List<MarketEvents.BoardListing> Prior, long PriorScanAt) GetBoardSnapshot(uint itemId)
  {
    var prior = new List<MarketEvents.BoardListing>();
    long priorScanAt = 0;
    using var cmd = new SqliteCommand(
      @"SELECT retainer_name, quantity, is_hq, unit_price, is_own, seen_at
        FROM market_board_snapshot WHERE item_id = @iid",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      prior.Add(new MarketEvents.BoardListing(
        reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2) != 0,
        reader.GetInt64(3), reader.GetInt32(4) != 0));
      priorScanAt = Math.Max(priorScanAt, reader.GetInt64(5));
    }
    return (prior, priorScanAt);
  }

  /// <summary>
  /// The ONE write path for market memory (design Section 3): diffs the incoming
  /// board for an item against the stored snapshot, APPENDS the resulting events, and
  /// REPLACES the snapshot with the new board — atomically. Every board packet that
  /// reaches a pricing door runs this, even for items the pricer skips (the
  /// observation was made, record it). Foreign timing is the (seen_after, seen_by)
  /// window only; own disappearances land as 'pulled'/observed, upgradeable to
  /// 'sold'/confirmed by a later GilTrack confirm. Returns the number of events appended.
  /// </summary>
  internal static int ApplyBoardScan(uint itemId, IReadOnlyList<MarketEvents.BoardListing> currentBoard,
      long scanAt, uint worldId = 0, MarketEvents.Observer observer = MarketEvents.Observer.OwnScan)
  {
    var (prior, priorScanAt) = GetBoardSnapshot(itemId);
    var events = MarketEvents.Diff(prior, currentBoard);
    var window = new MarketEvents.ObservationWindow(priorScanAt, scanAt);
    var observerTag = observer == MarketEvents.Observer.Community ? "community" : "own_scan";

    using var tx = Connection.BeginTransaction();

    foreach (var ev in events)
    {
      var row = MarketEvents.ToRow(ev, window, observer);
      using var insert = new SqliteCommand(
        @"INSERT INTO market_events
            (item_id, is_hq, retainer_name, quantity, kind, old_price, new_price,
             is_own, observer, certainty, resolution, ambiguous_match, seen_after, seen_by, world_id)
          VALUES (@iid, @hq, @ret, @qty, @kind, @old, @new, @own, @observer, @certainty,
                  @resolution, @amb, @after, @by, @world)",
        _connection, tx);
      insert.Parameters.AddWithValue("@iid", (long)itemId);
      insert.Parameters.AddWithValue("@hq", ev.IsHq ? 1 : 0);
      insert.Parameters.AddWithValue("@ret", ev.Retainer);
      insert.Parameters.AddWithValue("@qty", ev.Quantity);
      insert.Parameters.AddWithValue("@kind", EventKindTag(ev.Kind));
      insert.Parameters.AddWithValue("@old", (object?)ev.OldPrice ?? DBNull.Value);
      insert.Parameters.AddWithValue("@new", (object?)ev.NewPrice ?? DBNull.Value);
      insert.Parameters.AddWithValue("@own", ev.IsOwn ? 1 : 0);
      insert.Parameters.AddWithValue("@observer", observerTag);
      insert.Parameters.AddWithValue("@certainty", row.Certainty == MarketEvents.Certainty.Confirmed ? "confirmed" : "observed");
      insert.Parameters.AddWithValue("@resolution", ev.Resolution is MarketEvents.DisappearResolution r ? ResolutionTag(r) : (object)DBNull.Value);
      insert.Parameters.AddWithValue("@amb", ev.Ambiguous ? 1 : 0);
      insert.Parameters.AddWithValue("@after", window.SeenAfter);
      insert.Parameters.AddWithValue("@by", window.SeenBy);
      insert.Parameters.AddWithValue("@world", worldId);
      insert.ExecuteNonQuery();
    }

    // Replace the snapshot: the current board IS the new read model.
    using (var del = new SqliteCommand("DELETE FROM market_board_snapshot WHERE item_id = @iid", _connection, tx))
    {
      del.Parameters.AddWithValue("@iid", (long)itemId);
      del.ExecuteNonQuery();
    }
    foreach (var l in currentBoard)
    {
      using var ins = new SqliteCommand(
        @"INSERT OR REPLACE INTO market_board_snapshot
            (item_id, is_hq, retainer_name, quantity, unit_price, is_own, world_id, observer, seen_at)
          VALUES (@iid, @hq, @ret, @qty, @price, @own, @world, @observer, @seen)",
        _connection, tx);
      ins.Parameters.AddWithValue("@iid", (long)itemId);
      ins.Parameters.AddWithValue("@hq", l.IsHq ? 1 : 0);
      ins.Parameters.AddWithValue("@ret", l.Retainer);
      ins.Parameters.AddWithValue("@qty", l.Quantity);
      ins.Parameters.AddWithValue("@price", l.UnitPrice);
      ins.Parameters.AddWithValue("@own", l.IsOwn ? 1 : 0);
      ins.Parameters.AddWithValue("@world", worldId);
      ins.Parameters.AddWithValue("@observer", observerTag);
      ins.Parameters.AddWithValue("@seen", scanAt);
      ins.ExecuteNonQuery();
    }

    tx.Commit();
    return events.Count;
  }

  // =========================================================================
  // Decision receipts (V19) — one row per pricing decision, relative coords
  // =========================================================================

  /// <summary>
  /// Writes one decision receipt (design Section 4) and prunes the item back to its
  /// most-recent <paramref name="keepPerItem"/> receipts (bounded by inventory, not
  /// time). All coordinates are relative and item-agnostic; arm_id + item_category +
  /// stack coords ride from day one. The outcome join (time_to_clear / outcome_state)
  /// is left open — a GilTrack confirm or an evict fills it later, never here.
  /// </summary>
  internal static void InsertDecisionReceipt(uint itemId, bool isHq, string retainerName,
      string itemCategory, string? armId, DecisionReceipts.ReceiptCoordinates c,
      string outcome, string evidence, int keepPerItem = 3)
  {
    using var tx = Connection.BeginTransaction();

    using (var insert = new SqliteCommand(
      @"INSERT INTO decision_receipts
          (created_at, item_id, is_hq, retainer_name, item_category, arm_id,
           position_in_lane, board_depth, undercut_target_ratio, velocity_per_day,
           lane_n, lane_spread, weighted_lane_age_days, forecast_clearing_days,
           quantity, lane_stack_norm, outcome, evidence, outcome_state)
        VALUES (@now, @iid, @hq, @ret, @cat, @arm,
                @pos, @depth, @undercut, @vel,
                @n, @spread, @age, @forecast,
                @qty, @stacknorm, @outcome, @evidence, 'open')",
      _connection, tx))
    {
      insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
      insert.Parameters.AddWithValue("@iid", (long)itemId);
      insert.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      insert.Parameters.AddWithValue("@ret", retainerName);
      insert.Parameters.AddWithValue("@cat", itemCategory);
      insert.Parameters.AddWithValue("@arm", (object?)armId ?? DBNull.Value);
      insert.Parameters.AddWithValue("@pos", (object?)c.PositionInLane ?? DBNull.Value);
      insert.Parameters.AddWithValue("@depth", c.BoardDepth);
      insert.Parameters.AddWithValue("@undercut", (object?)c.UndercutTargetRatio ?? DBNull.Value);
      insert.Parameters.AddWithValue("@vel", (object?)c.VelocityPerDay ?? DBNull.Value);
      insert.Parameters.AddWithValue("@n", c.LaneSampleCount);
      insert.Parameters.AddWithValue("@spread", c.LaneSpread);
      insert.Parameters.AddWithValue("@age", c.LaneWeightedAgeDays);
      insert.Parameters.AddWithValue("@forecast", (object?)c.ForecastClearingDays ?? DBNull.Value);
      insert.Parameters.AddWithValue("@qty", c.Quantity);
      insert.Parameters.AddWithValue("@stacknorm", (object?)c.LaneStackNorm ?? DBNull.Value);
      insert.Parameters.AddWithValue("@outcome", outcome);
      insert.Parameters.AddWithValue("@evidence", evidence);
      insert.ExecuteNonQuery();
    }

    // Retention: keep the newest N for this (item, quality); prune the rest.
    var ids = new List<long>();
    using (var read = new SqliteCommand(
      @"SELECT id FROM decision_receipts WHERE item_id = @iid AND is_hq = @hq
        ORDER BY created_at DESC, id DESC",
      _connection, tx))
    {
      read.Parameters.AddWithValue("@iid", (long)itemId);
      read.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      using var reader = read.ExecuteReader();
      while (reader.Read()) ids.Add(reader.GetInt64(0));
    }
    foreach (var id in DecisionReceipts.ReceiptsToPrune(ids, keepPerItem))
    {
      using var del = new SqliteCommand("DELETE FROM decision_receipts WHERE id = @id", _connection, tx);
      del.Parameters.AddWithValue("@id", id);
      del.ExecuteNonQuery();
    }

    tx.Commit();
  }

  /// <summary>
  /// Outcome join on a GilTrack sale confirm (design Section 4): fills time_to_clear
  /// on every OPEN receipt for the sold (item, quality) and marks it cleared. The V13
  /// sold_after_days capture is the wiring model — the confirm is the only thing that
  /// closes a receipt as sold. Also upgrades the item's own market_events disappearance
  /// rows to sold/confirmed (same certainty-tier discipline; foreign rows never move).
  /// Returns the number of receipts cleared.
  /// </summary>
  internal static int FillReceiptOutcomeOnSale(uint itemId, bool isHq, long soldAtUnix)
  {
    // Read open receipts, compute per-row time_to_clear from their created_at.
    var open = new List<(long Id, long CreatedAt)>();
    using (var read = new SqliteCommand(
      @"SELECT id, created_at FROM decision_receipts
        WHERE item_id = @iid AND is_hq = @hq AND outcome_state = 'open'",
      _connection))
    {
      read.Parameters.AddWithValue("@iid", (long)itemId);
      read.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      using var reader = read.ExecuteReader();
      while (reader.Read()) open.Add((reader.GetInt64(0), reader.GetInt64(1)));
    }

    foreach (var (id, createdAt) in open)
    {
      using var upd = new SqliteCommand(
        @"UPDATE decision_receipts SET time_to_clear_days = @days, outcome_state = 'cleared'
          WHERE id = @id",
        _connection);
      upd.Parameters.AddWithValue("@days", DecisionReceipts.TimeToClearDays(createdAt, soldAtUnix));
      upd.Parameters.AddWithValue("@id", id);
      upd.ExecuteNonQuery();
    }

    // Upgrade own disappearance events to confirmed-sold (foreign rows untouched).
    using (var evUpd = new SqliteCommand(
      @"UPDATE market_events SET resolution = 'sold', certainty = 'confirmed'
        WHERE item_id = @iid AND is_hq = @hq AND kind = 'disappeared'
          AND is_own = 1 AND resolution = 'pulled'",
      _connection))
    {
      evUpd.Parameters.AddWithValue("@iid", (long)itemId);
      evUpd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      evUpd.ExecuteNonQuery();
    }

    return open.Count;
  }

  /// <summary>
  /// Closes every OPEN receipt for a pulled/evicted (item, quality) as never-cleared
  /// (design Section 4): the listing left the board before it sold, so the forecast
  /// never got its test — that absence is the finding, not a gap to leave open.
  /// </summary>
  internal static int CloseReceiptsNeverCleared(uint itemId, bool isHq)
  {
    using var cmd = new SqliteCommand(
      @"UPDATE decision_receipts SET outcome_state = 'never_cleared'
        WHERE item_id = @iid AND is_hq = @hq AND outcome_state = 'open'",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    return cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Half-life scorekeeping seed (design Section 5 / the M4 brief): a single aggregate
  /// line — for cleared receipts, how realized time-to-clear compares to the forecast,
  /// split by whether the lane leaned on old evidence (weighted age >= agedDays). A
  /// debug/dashboard readout is enough for v0; the resolver upgrade is post-3.0.
  /// </summary>
  internal static List<(bool OldEvidence, int N, double AvgForecastDays, double AvgActualDays)>
      GetClearingVsForecast(double agedDays = 30.0)
  {
    var rows = new List<(bool, int, double, double)>();
    using var cmd = new SqliteCommand(
      @"SELECT weighted_lane_age_days >= @aged AS old_evidence,
               COUNT(*) AS n,
               AVG(forecast_clearing_days) AS avg_forecast,
               AVG(time_to_clear_days) AS avg_actual
        FROM decision_receipts
        WHERE outcome_state = 'cleared' AND time_to_clear_days IS NOT NULL
          AND forecast_clearing_days IS NOT NULL
        GROUP BY old_evidence",
      _connection);
    cmd.Parameters.AddWithValue("@aged", agedDays);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      rows.Add((reader.GetInt64(0) != 0, reader.GetInt32(1),
        reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
        reader.IsDBNull(3) ? 0 : reader.GetDouble(3)));
    return rows;
  }

  // =========================================================================
  // Zombie lane_held flag heal (V19) — container-scoped close
  // =========================================================================

  /// <summary>
  /// The zombie sweep (M4): closes open lane_held flags whose item has left the
  /// container this run observed — but ONLY the container this run type can prove.
  /// A pinch passes FlagScope.Board with the sell-list item ids it saw; a Hawk run
  /// passes FlagScope.Inventory with the inventory item ids it saw. TriageMemory makes
  /// the pick (pure/tested); a flag pointing at the other container, or an Unknown-
  /// scope legacy flag, is left OPEN (fail toward open). Closes as 'item_gone' — a
  /// later GilTrack confirm may relabel it 'sold'. Returns the number closed.
  /// </summary>
  internal static int ZombieSweepLaneHeldFlags(string retainerName,
      TriageMemory.FlagScope runScope, IReadOnlySet<uint> observedItemIds)
  {
    var open = new List<TriageMemory.ZombieFlagRow>();
    using (var cmd = new SqliteCommand(
      @"SELECT id, item_id, scope FROM triage_flags
        WHERE retainer_name = @ret AND reason = 'lane_held' AND status = 'open'",
      _connection))
    {
      cmd.Parameters.AddWithValue("@ret", retainerName);
      using var reader = cmd.ExecuteReader();
      while (reader.Read())
        open.Add(new TriageMemory.ZombieFlagRow(
          reader.GetInt64(0), (uint)reader.GetInt64(1),
          TriageMemory.ParseScope(reader.IsDBNull(2) ? "" : reader.GetString(2))));
    }

    var toClose = TriageMemory.ZombieFlagsToClose(runScope, observedItemIds, open);
    foreach (var id in toClose)
      SetTriageFlagStatus(id, "item_gone");
    return toClose.Count;
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

    // Thin old gil snapshots: keep one per day for entries older than 30 days.
    // Delete child retainer_snapshots first — FK constraint blocks parent delete otherwise.
    const string thinFilter = @"timestamp < @cutoff
      AND id NOT IN (
        SELECT MAX(id) FROM gil_snapshots
        WHERE timestamp < @cutoff
        GROUP BY DATE(timestamp, 'unixepoch')
      )";

    using (var cmd = new SqliteCommand(
      $@"DELETE FROM retainer_snapshots
      WHERE snapshot_id IN (SELECT id FROM gil_snapshots WHERE {thinFilter})",
      _connection))
    {
      cmd.Parameters.AddWithValue("@cutoff", thirtyDays);
      cmd.ExecuteNonQuery();
    }

    using (var cmd = new SqliteCommand(
      $"DELETE FROM gil_snapshots WHERE {thinFilter}",
      _connection))
    {
      cmd.Parameters.AddWithValue("@cutoff", thirtyDays);
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