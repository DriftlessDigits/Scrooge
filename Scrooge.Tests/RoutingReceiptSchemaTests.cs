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

  // ========================================================================
  // V22 - the rate the score was ACTUALLY computed at
  // ========================================================================

  [Fact]
  public void ApplyV22_AddsTheEffectiveRateAndTheDiscountFlag()
  {
    using var conn = OpenTempDb();
    Scrooge.RoutingReceiptSchema.ApplyV20(conn);
    Scrooge.RoutingReceiptSchema.ApplyV22(conn);

    var cols = Columns(conn, "routing_receipts");
    Assert.Contains("effective_seal_rate", cols);
    Assert.Contains("seal_discounted", cols);
    // The base rate stays - the receipt says both, which is what makes the
    // discount readable rather than inferrable.
    Assert.Contains("seal_rate", cols);
  }

  [Fact]
  public void ApplyV22_IsIdempotent_SecondRunDoesNotThrow()
  {
    using var conn = OpenTempDb();
    Scrooge.RoutingReceiptSchema.ApplyV20(conn);
    Scrooge.RoutingReceiptSchema.ApplyV22(conn);
    var ex = Record.Exception(() => Scrooge.RoutingReceiptSchema.ApplyV22(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void ApplyV22_DoesNotRewriteHistory_PreExistingRowsKeepAnUnrecordedRate()
  {
    using var conn = OpenTempDb();
    Scrooge.RoutingReceiptSchema.ApplyV20(conn);

    using (var insert = new SqliteCommand(
      "INSERT INTO routing_receipts (created_at, item_id, exit, seal_rate) VALUES (1, 42, 'Gc', 25);",
      conn))
      insert.ExecuteNonQuery();

    Scrooge.RoutingReceiptSchema.ApplyV22(conn);

    using var read = new SqliteCommand(
      "SELECT seal_rate, effective_seal_rate, seal_discounted FROM routing_receipts WHERE item_id = 42;",
      conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal(25L, reader.GetInt64(0));
    // Not back-filled from the base rate: "we didn't record this" is the honest
    // reading of a pre-V22 row, and a guess would wear a measurement's column.
    Assert.Equal(0d, reader.GetDouble(1));
    Assert.Equal(0L, reader.GetInt64(2));
  }

  [Fact]
  public void ApplyV22_OnADbWithNoReceiptTable_IsANoOp()
  {
    using var conn = OpenTempDb();
    var ex = Record.Exception(() => Scrooge.RoutingReceiptSchema.ApplyV22(conn));
    Assert.Null(ex);
  }

  // ========================================================================
  // V44 - what the melt number IS, and the skillup standing behind it
  // ========================================================================

  [Fact]
  public void ApplyV44_AddsTheMeltProvenanceAndTheSkillupStanding()
  {
    using var conn = OpenTempDb();
    Scrooge.RoutingReceiptSchema.ApplyV20(conn);
    Scrooge.RoutingReceiptSchema.ApplyV44(conn);

    var cols = Columns(conn, "routing_receipts");
    // The crossover derivation needs both: the score's provenance, and which of
    // the two skillup knobs the row was standing under.
    Assert.Contains("melt_grade", cols);
    Assert.Contains("skillup_color", cols);
    // The number itself stays - the grade says what it IS, it is not a replacement.
    Assert.Contains("melt_score", cols);
  }

  [Fact]
  public void ApplyV44_IsIdempotent_SecondRunDoesNotThrow()
  {
    using var conn = OpenTempDb();
    Scrooge.RoutingReceiptSchema.ApplyV20(conn);
    Scrooge.RoutingReceiptSchema.ApplyV44(conn);
    var ex = Record.Exception(() => Scrooge.RoutingReceiptSchema.ApplyV44(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void ApplyV44_DoesNotRewriteHistory_PreExistingRowsAreSilentOnBoth()
  {
    using var conn = OpenTempDb();
    Scrooge.RoutingReceiptSchema.ApplyV20(conn);

    using (var insert = new SqliteCommand(
      "INSERT INTO routing_receipts (created_at, item_id, exit, melt_score) VALUES (1, 42, 'Desynth', 100000);",
      conn))
      insert.ExecuteNonQuery();

    Scrooge.RoutingReceiptSchema.ApplyV44(conn);

    using var read = new SqliteCommand(
      "SELECT melt_score, melt_grade, skillup_color FROM routing_receipts WHERE item_id = 42;", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal(100000L, reader.GetInt64(0));
    // The whole reason the column exists: 100,000 could be measured yields or the
    // red knob, and a back-fill would have to guess which. '' says "not recorded".
    Assert.Equal("", reader.GetString(1));
    Assert.True(reader.IsDBNull(2));
  }

  [Fact]
  public void ApplyV44_OnADbWithNoReceiptTable_IsANoOp()
  {
    using var conn = OpenTempDb();
    var ex = Record.Exception(() => Scrooge.RoutingReceiptSchema.ApplyV44(conn));
    Assert.Null(ex);
  }
}
