using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// V46 migration contract: routing_overrides gains the receipt link, diffably and
/// idempotently (the V11 model). The V14 birth DDL is restated here because it lives
/// in GilStorageBootstrap, which cannot be linked - the shape it creates is what the
/// widening runs against, and a test that invented a different one would be proving
/// nothing about the real ladder.
/// </summary>
public class RoutingOverrideSchemaTests
{
  private const string V14Ddl =
    @"CREATE TABLE routing_overrides (
        id             INTEGER PRIMARY KEY AUTOINCREMENT,
        created_at     INTEGER NOT NULL,
        item_id        INTEGER NOT NULL,
        is_hq          INTEGER NOT NULL DEFAULT 0,
        ilvl           INTEGER NOT NULL DEFAULT 0,
        router_verdict TEXT NOT NULL,
        router_reason  TEXT NOT NULL DEFAULT '',
        player_verdict TEXT NOT NULL
      );";

  private static SqliteConnection OpenTempDb(bool withTable = true)
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    if (withTable)
      using (var cmd = new SqliteCommand(V14Ddl, conn))
        cmd.ExecuteNonQuery();
    return conn;
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
  public void ApplyV46_AddsTheReceiptLink()
  {
    using var conn = OpenTempDb();
    Scrooge.RoutingOverrideSchema.ApplyV46(conn);

    Assert.Contains("receipt_id", Columns(conn, "routing_overrides"));
  }

  [Fact]
  public void ApplyV46_IsIdempotent_SecondRunDoesNotThrow()
  {
    using var conn = OpenTempDb();
    Scrooge.RoutingOverrideSchema.ApplyV46(conn);
    var ex = Record.Exception(() => Scrooge.RoutingOverrideSchema.ApplyV46(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void ApplyV46_DoesNotRewriteHistory_OldRulingsKeepNoLink()
  {
    using var conn = OpenTempDb();
    using (var insert = new SqliteCommand(
      @"INSERT INTO routing_overrides (created_at, item_id, router_verdict, player_verdict)
        VALUES (1, 42, 'Desynth', 'Vendor');", conn))
      insert.ExecuteNonQuery();

    Scrooge.RoutingOverrideSchema.ApplyV46(conn);

    using var read = new SqliteCommand(
      "SELECT receipt_id FROM routing_overrides WHERE item_id = 42;", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    // A pre-V46 ruling has no recorded receipt and never will. NULL says so; any
    // back-filled id would be the heuristic join wearing a recorded column.
    Assert.True(reader.IsDBNull(0));
  }

  [Fact]
  public void ApplyV46_OnADbWithNoOverridesTable_IsANoOp()
  {
    using var conn = OpenTempDb(withTable: false);
    var ex = Record.Exception(() => Scrooge.RoutingOverrideSchema.ApplyV46(conn));
    Assert.Null(ex);
  }
}
