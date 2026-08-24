using System;
using System.Collections.Generic;
using System.IO;
using ECommons.DalamudServices;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// SQLite-backed persistent storage for gil tracking data and quotes.
/// Replaces the old JSON-based GilData approach. All database access
/// goes through this class — no other file touches SQL. The class is split
/// across <c>GilStorage.*.cs</c> partials, one per table family; this file
/// holds the connection itself and the lifecycle that opens, resets, prunes
/// and closes it.
/// </summary>
internal static partial class GilStorage
{
  private static SqliteConnection? _connection;

  internal static string DbPath => Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "scrooge.db");

  /// <summary>The shared SQLite connection. Borrowers must not Dispose it.</summary>
  internal static SqliteConnection Connection =>
    _connection ?? throw new InvalidOperationException(
      "GilStorage is not initialized — Initialize() failed or Dispose() already ran");

  /// <summary>
  /// Whether storage is open and climbed. False before Initialize(), after a failed
  /// Initialize(), and after Dispose() — the one thing a caller can ASK instead of
  /// finding out by exception.
  ///
  /// <para>It exists because "gil tracking disabled" used to be a log line and
  /// nothing else: a migration that threw left the connection assigned and every
  /// caller happily querying a half-climbed database. Now a failure nulls the
  /// connection, so <see cref="Connection"/> throws honestly everywhere and this
  /// flag lets the startup path skip the dependents it knows about rather than
  /// building them against a corpse.</para>
  /// </summary>
  internal static bool StorageAvailable => _connection != null;

  // =========================================================================
  // Lifecycle
  // =========================================================================

  /// <summary>
  /// Opens the database, runs bootstrap (tables, migration, seeds), and prunes.
  /// Called once from Plugin constructor.
  ///
  /// <para>FAIL CLOSED: if the climb or the prune throws, the connection is disposed
  /// and dropped before the exception leaves. A half-migrated database is not a
  /// degraded database to read around — it is one whose shape no longer matches what
  /// the queries believe, and every read off it is a wrong answer wearing numbers.</para>
  /// </summary>
  internal static void Initialize()
  {
    Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
    _connection = new SqliteConnection($"Data Source={DbPath}");

    try
    {
      _connection.Open();

      // WAL mode prevents SQLITE_BUSY when the hook writes on the game thread while the UI reads on the draw thread
      using (var walCmd = new SqliteCommand("PRAGMA journal_mode=WAL;", _connection))
        walCmd.ExecuteNonQuery();

      // All first-time setup lives in the bootstrap file. The ladder is
      // Dalamud-free (linked-source tested); the real sinks are wired here,
      // once, before the climb.
      GilStorageBootstrap.LogInfo = m => Svc.Log.Info(m);
      GilStorageBootstrap.LogDebug = m => Svc.Log.Debug(m);
      GilStorageBootstrap.LogError = (m, ex) => Svc.Log.Error(ex, m);
      GilStorageBootstrap.JsonDirProvider = () => Plugin.PluginInterface.GetPluginConfigDirectory();
      GilStorageBootstrap.JsonImporter = ImportLegacyJson;
      GilStorageBootstrap.Run(_connection);

      Prune();
    }
    catch
    {
      try { _connection.Dispose(); } catch { /* the failure that matters is the one on its way out */ }
      _connection = null;
      throw;
    }
  }

  /// <summary>
  /// THE V1 LEGACY-JSON IMPORT (moved here from the bootstrap when the ladder went
  /// Dalamud-free, 08-22): reads gil_data.json and writes it through this class's
  /// own insert methods. The ladder calls it through the JsonImporter seam and owns
  /// the transaction, the .bak rename, and the retry-on-failure semantics.
  /// </summary>
  private sealed class LegacyGilData
  {
    public System.Collections.Generic.List<SaleRecord> Sales { get; set; } = [];
    public System.Collections.Generic.List<GilSnapshot> GilHistory { get; set; } = [];
    public System.Collections.Generic.List<MarketSnapshot> MarketHistory { get; set; } = [];
    public System.Collections.Generic.List<ListingRecord> CurrentListings { get; set; } = [];
  }

  private static bool ImportLegacyJson(string jsonPath)
  {
    var json = File.ReadAllText(jsonPath);
    var data = System.Text.Json.JsonSerializer.Deserialize<LegacyGilData>(json);
    if (data == null) return false; // Corrupt/empty — fail, retry next time

    foreach (var sale in data.Sales)
      InsertTransaction(sale.SaleTimestamp, "earned", "retainer_sale",
          sale.TotalGil, sale.ItemId, sale.ItemName, sale.Category,
          sale.Quantity, sale.UnitPrice, sale.IsHQ, sale.RetainerName,
          sale.BuyerName);

    foreach (var snap in data.GilHistory)
    {
      var snapshotId = InsertGilSnapshot(snap.Timestamp, snap.PlayerGil, "pinch_run");
      foreach (var (name, gil) in snap.RetainerGil)
        InsertRetainerSnapshot(snapshotId, name, gil);
    }

    foreach (var ms in data.MarketHistory)
      InsertMarketSnapshot(ms.Timestamp, ms.ItemCount,
          ms.TotalListingValue, ms.AverageListingAgeDays, "full");

    foreach (var listing in data.CurrentListings)
      UpsertListing(listing.RetainerName, listing.SlotIndex,
          listing.ItemId, listing.ItemName, listing.Category,
          listing.UnitPrice, listing.Quantity, listing.IsHQ,
          listing.FirstSeenTimestamp, listing.LastUpdatedTimestamp);

    Svc.Log.Info($"[GilTrack] Migrated {data.Sales.Count} sales, " +
        $"{data.GilHistory.Count} snapshots from JSON to SQLite. Backup: gil_data.json.bak");
    return true;
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
  // Sitrep - one-paste diagnostics
  // =========================================================================

  /// <summary>
  /// Every scalar the sitrep dump quotes, in one pass. The dump used to hold its
  /// own SQL strings and hand them to an arbitrary-SQL escape hatch, which put ten
  /// queries in a file that formats text - and made this class's "no other file
  /// touches SQL" header a claim rather than a fact. The statements now sit beside
  /// the tables they name; Sitrep formats the struct.
  /// </summary>
  internal static SitrepCounts ReadSitrepCounts() => new(
    Scalar("SELECT COUNT(*) FROM listings"),
    Scalar("SELECT SUM(unit_price * MAX(quantity, 1)) FROM listings"),
    Scalar("SELECT venture_tokens FROM gil_snapshots WHERE venture_tokens IS NOT NULL ORDER BY timestamp DESC LIMIT 1"),
    Scalar("SELECT COUNT(*) FROM routing_receipts"),
    Scalar("SELECT COUNT(*) FROM routing_receipts WHERE executed_action IS NOT NULL"),
    Scalar("SELECT COUNT(*) FROM routing_receipts WHERE player_overrode = 1"),
    Scalar("SELECT COUNT(*) FROM routing_overrides"),
    Scalar("SELECT COUNT(*) FROM triage_flags WHERE status = 'open'"),
    Scalar("SELECT COUNT(*) FROM desynth_runs"),
    Scalar("SELECT COUNT(*) FROM market_events"));

  /// <summary>Returns 0 on NULL (empty table aggregate, or no row at all).</summary>
  private static long Scalar(string sql)
  {
    using var cmd = new SqliteCommand(sql, _connection);
    var result = cmd.ExecuteScalar();
    return result is null or DBNull ? 0L : Convert.ToInt64(result);
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
  /// - Banked recon decisions older than 90 days: deleted
  /// - Round headers (and any transcript still hanging off them) older than 90 days: deleted
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

    // THE ROUNDS TABLES' SLOW BURN (the minors batch, 2026-08-12). Both were written
    // with a bound on the wrong axis: the decision cache upserts per item so it cannot
    // grow within a night but never sheds a variant, and the supersede bounds the
    // transcript but leaves one header row per round standing forever. Neither is
    // urgent and both are unbounded, which is precisely what this sweep is for.
    var cacheDropped = DecisionCacheSchema.PruneStale(Connection, ninetyDays);
    var runsDropped = RoundLogSchema.PruneOldRuns(Connection, ninetyDays);
    if (cacheDropped > 0 || runsDropped > 0)
      Svc.Log.Info($"[Prune] Dropped {cacheDropped} banked decisions and {runsDropped} round headers older than 90 days");
  }
}
