using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// V20 migration contract: the routing_receipts DDL is diffable + idempotent
/// (the V11 model). Runs the real <see cref="Scrooge.RoutingReceiptSchema.ApplyV20"/>
/// against a temp in-memory SQLite DB, twice, and asserts the second run is a
/// clean no-op.
/// </summary>
public class RoutingReceiptSchemaTests
{
  private static SqliteConnection OpenTempDb()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
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
  public void ApplyV20_CreatesTheReceiptTable_WithTheScoreboardColumns()
  {
    using var conn = OpenTempDb();
    Scrooge.RoutingReceiptSchema.ApplyV20(conn);

    var cols = Columns(conn, "routing_receipts");
    // The counterfactual columns - the reason this table exists.
    Assert.Contains("list_score", cols);
    Assert.Contains("gc_score", cols);
    Assert.Contains("melt_score", cols);
    Assert.Contains("vendor_score", cols);
    // The join/context columns.
    Assert.Contains("executed_action", cols);
    Assert.Contains("player_overrode", cols);
    Assert.Contains("confidence_tier", cols);
    Assert.Contains("evidence_phase", cols);
    Assert.Contains("seal_rate_empirical", cols);
  }

  [Fact]
  public void ApplyV20_IsIdempotent_SecondRunDoesNotThrow()
  {
    using var conn = OpenTempDb();
    Scrooge.RoutingReceiptSchema.ApplyV20(conn);
    var ex = Record.Exception(() => Scrooge.RoutingReceiptSchema.ApplyV20(conn));
    Assert.Null(ex);
  }
}
