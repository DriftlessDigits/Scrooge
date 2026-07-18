using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// V19 migration contract: the DDL is diffable + idempotent (the V11 model). This
/// runs the real <see cref="Scrooge.MarketMemorySchema.ApplyV19"/> against a temp
/// in-memory SQLite DB, twice, and asserts the second run is a clean no-op.
/// </summary>
public class MarketMemorySchemaTests
{
  private static SqliteConnection OpenTempDb()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    return conn;
  }

  private static void CreateLegacyTriageFlags(SqliteConnection conn)
  {
    // A minimal pre-V19 triage_flags (V12 shape) so the guarded ALTER has a table
    // to add the scope column to - mirrors an upgrading DB.
    using var cmd = new SqliteCommand(
      @"CREATE TABLE triage_flags (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          item_id INTEGER NOT NULL,
          reason TEXT NOT NULL,
          status TEXT NOT NULL DEFAULT 'open'
        );", conn);
    cmd.ExecuteNonQuery();
  }

  private static bool TableExists(SqliteConnection conn, string name)
  {
    using var cmd = new SqliteCommand(
      "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n", conn);
    cmd.Parameters.AddWithValue("@n", name);
    return System.Convert.ToInt64(cmd.ExecuteScalar()) > 0;
  }

  private static List<string> Columns(SqliteConnection conn, string table)
  {
    var cols = new List<string>();
    using var cmd = new SqliteCommand($"PRAGMA table_info({table});", conn);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      cols.Add(reader.GetString(1));
    return cols;
  }

  [Fact]
  public void ApplyV19_CreatesAllThreeTables()
  {
    using var conn = OpenTempDb();
    CreateLegacyTriageFlags(conn);

    Scrooge.MarketMemorySchema.ApplyV19(conn);

    Assert.True(TableExists(conn, "market_board_snapshot"));
    Assert.True(TableExists(conn, "market_events"));
    Assert.True(TableExists(conn, "decision_receipts"));
    Assert.Contains("scope", Columns(conn, "triage_flags"));
  }

  [Fact]
  public void ApplyV19_IsIdempotent_SecondRunDoesNotThrow()
  {
    using var conn = OpenTempDb();
    CreateLegacyTriageFlags(conn);

    Scrooge.MarketMemorySchema.ApplyV19(conn);
    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV19(conn));
    Assert.Null(ex);

    // Scope column added exactly once (a second ALTER would have thrown "duplicate column").
    Assert.Single(Columns(conn, "triage_flags"), c => c == "scope");
  }

  [Fact]
  public void ApplyV19_OnFreshDbWithoutTriageFlags_SkipsScopeAlterCleanly()
  {
    // A bare DB (no triage_flags yet) must not throw on the guarded ALTER.
    using var conn = OpenTempDb();
    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV19(conn));
    Assert.Null(ex);
    Assert.True(TableExists(conn, "market_events"));
  }

  [Fact]
  public void MarketEvents_HasWindowColumns_ButNoForeignPointTimestamp()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    var cols = Columns(conn, "market_events");

    Assert.Contains("seen_after", cols);
    Assert.Contains("seen_by", cols);
    Assert.Contains("observer", cols);
    Assert.Contains("certainty", cols);
    Assert.Contains("ambiguous_match", cols);
    // No single point-in-time "observed_at"/"timestamp" for foreign activity.
    Assert.DoesNotContain("observed_at", cols);
    Assert.DoesNotContain("timestamp", cols);
  }

  [Fact]
  public void BoardSnapshot_PersistsTwinListings_NotDroppedByANaturalKey()
  {
    // Two listings share the soft identity (item, hq, retainer, qty). A natural PK
    // would keep only one and phantom a disappear next scan; the surrogate key keeps both.
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);

    for (var i = 0; i < 2; i++)
      using (var ins = new SqliteCommand(
        @"INSERT INTO market_board_snapshot (item_id, is_hq, retainer_name, quantity, unit_price, seen_at)
          VALUES (100, 0, 'Alice', 1, @p, 1000)", conn))
      {
        ins.Parameters.AddWithValue("@p", 90 + i);
        ins.ExecuteNonQuery();
      }

    using var count = new SqliteCommand(
      "SELECT COUNT(*) FROM market_board_snapshot WHERE item_id = 100 AND retainer_name = 'Alice'", conn);
    Assert.Equal(2L, System.Convert.ToInt64(count.ExecuteScalar()));
  }

  [Fact]
  public void DecisionReceipts_CarryArmAndCategoryAndStackFromDayOne()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    var cols = Columns(conn, "decision_receipts");

    Assert.Contains("arm_id", cols);
    Assert.Contains("item_category", cols);
    Assert.Contains("quantity", cols);
    Assert.Contains("lane_stack_norm", cols);
    Assert.Contains("time_to_clear_days", cols);
    Assert.Contains("outcome_state", cols);
  }
}
