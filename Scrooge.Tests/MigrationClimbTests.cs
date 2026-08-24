using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE LADDER CLIMBS FOR REAL (Drift, 08-22 pre-ship: "can we test the migrations?
/// and the behavior on both an empty and decently filled db?"). The bootstrap went
/// Dalamud-free for exactly this file: these tests run the ACTUAL ladder - every
/// rung, real DDL, real data migrations - against a real temp SQLite database.
///
/// <para>The two shapes v3.0.0.0 will actually land on:</para>
/// <list type="bullet">
/// <item>A FRESH INSTALL - user_version 0, no JSON, nothing. The whole ladder must
/// climb in one Run and produce the complete current schema.</item>
/// <item>A 2.6.2.0-ERA DATABASE (the household's second install) - user_version 12,
/// months of transactions and quality-blind last_sale_prices. One leap of 34 rungs,
/// with the destructive V13 quality split and V23's phantom sweep in the middle, and
/// every row of that history must come out the far side.</item>
/// </list>
///
/// <para>The era fixture is built by the ladder ITSELF (Run with a ceiling of 12):
/// the rungs are append-only history, so rungs 1..12 of today's ladder ARE the
/// schema 2.6.2.0 shipped. No hand-maintained DDL snapshot to rot.</para>
/// </summary>
public class MigrationClimbTests : IDisposable
{
  /// <summary>The ladder's top rung. Bump when a migration lands - the climb tests
  /// asserting this is what catches a rung added without a stamp.</summary>
  private const int CurrentVersion = 48;

  /// <summary>The schema version v2.6.2.0 stamped (read from the tag's bootstrap).</summary>
  private const int V262Version = 12;

  private readonly string _path = Path.Combine(Path.GetTempPath(), $"scrooge_climb_{Guid.NewGuid():N}.db");
  private readonly SqliteConnection _connection;

  public MigrationClimbTests()
  {
    _connection = new SqliteConnection($"Data Source={_path}");
    _connection.Open();
  }

  public void Dispose()
  {
    _connection.Dispose();
    SqliteConnection.ClearAllPools();
    try { File.Delete(_path); } catch { /* temp file */ }
    GC.SuppressFinalize(this);
  }

  private void Exec(string sql)
  {
    using var cmd = new SqliteCommand(sql, _connection);
    cmd.ExecuteNonQuery();
  }

  private long Scalar(string sql)
  {
    using var cmd = new SqliteCommand(sql, _connection);
    return Convert.ToInt64(cmd.ExecuteScalar());
  }

  private int SchemaVersion() => (int)Scalar("PRAGMA user_version;");

  private bool TableExists(string name)
  {
    using var cmd = new SqliteCommand(
      "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @n;", _connection);
    cmd.Parameters.AddWithValue("@n", name);
    return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
  }

  private bool ColumnExists(string table, string column)
  {
    using var cmd = new SqliteCommand($"PRAGMA table_info({table});", _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
        return true;
    return false;
  }

  /// <summary>Every table the current machine reads or writes. A rung that forgets
  /// one fails here by name instead of as a runtime SqliteException in the field.</summary>
  private static readonly string[] CurrentTables =
  [
    "transactions", "listings", "gil_snapshots", "retainer_snapshots",
    "market_snapshots", "category_groups", "quotes", "last_sale_prices",
    "desynth_runs", "desynth_yields", "triage_flags", "routing_overrides",
    "venture_returns", "universalis_stats", "market_board_snapshot",
    "market_events", "decision_receipts", "routing_receipts",
    "own_listing_writes", "sale_history", "community_history",
    "community_history_sales", "pull_intents", "contest_receipts",
    "decision_cache", "round_runs", "round_log", "coffer_pulls",
    "community_mat_prices", "vendor_mat_prices",
  ];

  // ==========================================================================
  // The fresh install
  // ==========================================================================

  [Fact]
  public void EmptyDb_TheWholeLadderClimbsToTheCurrentSchema()
  {
    GilStorageBootstrap.Run(_connection);

    Assert.Equal(CurrentVersion, SchemaVersion());
    foreach (var table in CurrentTables)
      Assert.True(TableExists(table), $"table missing after fresh climb: {table}");

    // The fresh install is not a blank stare: the quote board seeds on rung 1.
    Assert.True(Scalar("SELECT COUNT(*) FROM quotes") > 0, "quotes did not seed");
  }

  [Fact]
  public void EmptyDb_ASecondRunIsANoOp()
  {
    GilStorageBootstrap.Run(_connection);
    var quotes = Scalar("SELECT COUNT(*) FROM quotes");

    GilStorageBootstrap.Run(_connection); // startup number two

    Assert.Equal(CurrentVersion, SchemaVersion());
    Assert.Equal(quotes, Scalar("SELECT COUNT(*) FROM quotes")); // no double seed
  }

  // ==========================================================================
  // The 2.6.2.0-era one-leap (the second install's upgrade)
  // ==========================================================================

  /// <summary>Builds the v2.6.2.0 shape and fills it like a lived-in install.</summary>
  private void BuildV262EraDb()
  {
    GilStorageBootstrap.Run(_connection, ceiling: V262Version);
    Assert.Equal(V262Version, SchemaVersion());

    // FIXTURE FIDELITY: today's base rungs create tables in their MODERN shape
    // (the base DDL is current-at-birth, and V13's rebuild tolerates either),
    // but a real 2.6-era database was born under old DLLs and carries the
    // genuinely quality-blind last_sale_prices. Restore that truth so the leap
    // exercises the destructive V13 split the way that live upgrade will.
    Exec("DROP TABLE last_sale_prices;");
    Exec(@"CREATE TABLE last_sale_prices (
             item_id INTEGER PRIMARY KEY,
             unit_price INTEGER NOT NULL,
             timestamp INTEGER NOT NULL);");
    Assert.False(ColumnExists("last_sale_prices", "is_hq"),
      "fixture is not 2.6-era: last_sale_prices already split");

    // A lived-in book: sales both qualities, a legacy last-sale row whose
    // transactions were pruned (V13 must carry it as NQ), and an open flag from
    // a producer that died with the lane rewrite (V17 must close it).
    Exec(@"INSERT INTO transactions (timestamp, direction, source, amount, item_id,
             item_name, category, quantity, unit_price, is_hq, retainer_name, counterparty)
           VALUES (1750000000, 'earned', 'retainer_sale', 12299, 21770,
             'Silvergrace Ring', 'Ring', 1, 12299, 1, 'Kif', 'Buyer'),
                  (1750000100, 'earned', 'retainer_sale', 78, 12567,
             'Griffin Leather', 'Leather', 1, 78, 0, 'Kif', 'Buyer'),
                  (1750000200, 'spent', 'market_purchase', 5000, 0,
             '', '', 0, 0, 0, '', '');");
    Exec("INSERT INTO last_sale_prices (item_id, unit_price, timestamp) VALUES (99001, 4444, 1749000000);");
    Exec(@"INSERT INTO triage_flags (created_at, item_id, retainer_name, slot_index,
             reason, detail, old_price, flagged_price, status)
           VALUES (1750000300, 21770, 'Kif', 1, 'upward_held', 'legacy producer', 100, 200, 'open');");
    Exec("INSERT INTO gil_snapshots (timestamp, player_gil, source) VALUES (1750000400, 1000000, 'pinch_run');");
  }

  [Fact]
  public void V262EraDb_OneLeapClimbsAndTheHistorySurvives()
  {
    BuildV262EraDb();

    GilStorageBootstrap.Run(_connection); // the leap: 12 -> current, one startup

    Assert.Equal(CurrentVersion, SchemaVersion());
    foreach (var table in CurrentTables)
      Assert.True(TableExists(table), $"table missing after era climb: {table}");

    // The lived-in book came through whole.
    Assert.Equal(3, (int)Scalar("SELECT COUNT(*) FROM transactions"));
    Assert.Equal(1, (int)Scalar("SELECT COUNT(*) FROM gil_snapshots"));

    // V13 rebuilt last_sale_prices quality-aware FROM the transactions...
    Assert.Equal(12299, (int)Scalar(
      "SELECT unit_price FROM last_sale_prices WHERE item_id = 21770 AND is_hq = 1"));
    Assert.Equal(78, (int)Scalar(
      "SELECT unit_price FROM last_sale_prices WHERE item_id = 12567 AND is_hq = 0"));
    // ...and carried the pruned-history row as NQ (V23's phantom sweep only
    // removes NQ rows that exactly mirror an HQ twin - this one has none).
    Assert.Equal(4444, (int)Scalar(
      "SELECT unit_price FROM last_sale_prices WHERE item_id = 99001 AND is_hq = 0"));

    // V17 closed the dead-producer flag instead of leaving it immortal.
    Assert.Equal(0, (int)Scalar(
      "SELECT COUNT(*) FROM triage_flags WHERE reason = 'upward_held' AND status = 'open'"));
  }

  [Fact]
  public void V262EraDb_TheLeapIsCrashSafe_AResumedClimbFinishes()
  {
    BuildV262EraDb();

    // The mid-leap crash: the climb dies partway up (simulated by a ceiling), the
    // next startup runs the full ladder. Rungs are transactional, so the resume
    // starts exactly where the stamp says and the result is identical.
    GilStorageBootstrap.Run(_connection, ceiling: 23);
    Assert.Equal(23, SchemaVersion());
    Assert.True(TableExists("sale_history"));
    Assert.False(TableExists("round_log"));

    GilStorageBootstrap.Run(_connection); // "next startup"

    Assert.Equal(CurrentVersion, SchemaVersion());
    Assert.True(TableExists("round_log"));
    Assert.Equal(3, (int)Scalar("SELECT COUNT(*) FROM transactions"));
    Assert.Equal(12299, (int)Scalar(
      "SELECT unit_price FROM last_sale_prices WHERE item_id = 21770 AND is_hq = 1"));
  }
}
