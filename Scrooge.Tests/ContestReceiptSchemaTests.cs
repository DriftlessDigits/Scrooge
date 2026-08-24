using Microsoft.Data.Sqlite;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// V32: contest receipts (walk ruling 4). The load-bearing receipts: the table
/// takes all three verdicts with nullable evidence, and the reason+verdict
/// index exists - "below-min: 14 raised, 11 dismissed" is the promised read.
/// </summary>
public class ContestReceiptSchemaTests
{
  private static SqliteConnection Open()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    Scrooge.ContestReceiptSchema.ApplyV32(conn);
    return conn;
  }

  [Fact]
  public void ApplyV32_IsIdempotent()
  {
    using var conn = Open();
    var ex = Record.Exception(() => Scrooge.ContestReceiptSchema.ApplyV32(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void TheFlagClassAggregation_IsOneQuery()
  {
    using var conn = Open();
    Exec(conn, @"INSERT INTO contest_receipts (verdict_at, item_id, reason, verdict)
                 VALUES (1, 100, 'below_minimum', 'dismissed'),
                        (2, 101, 'below_minimum', 'dismissed'),
                        (3, 102, 'below_minimum', 'upheld'),
                        (4, 103, 'upward_held', 'overruled')");

    using var cmd = new SqliteCommand(
      @"SELECT COUNT(*), SUM(verdict = 'dismissed')
        FROM contest_receipts WHERE reason = 'below_minimum'", conn);
    using var reader = cmd.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal(3, reader.GetInt32(0)); // raised
    Assert.Equal(2, reader.GetInt32(1)); // dismissed - the flag indicting itself
  }

  [Fact]
  public void EvidenceStaysNull_WhereNoFactWasHeld()
  {
    using var conn = Open();
    Exec(conn, @"INSERT INTO contest_receipts (verdict_at, item_id, reason, verdict)
                 VALUES (1, 100, 'no_data', 'dismissed')");
    using var cmd = new SqliteCommand(
      "SELECT listing_price, cheapest_competitor, sale_count, latest_sale_at FROM contest_receipts", conn);
    using var reader = cmd.ExecuteReader();
    Assert.True(reader.Read());
    for (var i = 0; i < 4; i++)
      Assert.True(reader.IsDBNull(i)); // a receipt never invents a fact
  }

  // ---- V38: the receipt learns WHICH DOUBT the row acted through ----

  [Fact]
  public void ApplyV38_IsIdempotent_AndSurvivesRunningTwice()
  {
    using var conn = Open();
    Scrooge.ContestReceiptSchema.ApplyV38(conn);
    var ex = Record.Exception(() => Scrooge.ContestReceiptSchema.ApplyV38(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void ApplyV38_OnAMissingTable_IsANoOp()
  {
    // V32 creates the table before this runs. A migration that assumed
    // otherwise would throw on a book that never got that far.
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    var ex = Record.Exception(() => Scrooge.ContestReceiptSchema.ApplyV38(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void LegacyReceiptsReadNullBranch_NotABranchCalledNothing()
  {
    // The NULL semantics, asserted: a pre-V38 row has no opinion about which
    // doubt it answered. If these were ever bucketed as a branch of their own,
    // that bucket would cross the tape's threshold on day one and say nothing.
    using var conn = Open();
    Exec(conn, @"INSERT INTO contest_receipts (verdict_at, item_id, reason, verdict)
                 VALUES (1, 100, 'lane_held', 'overruled')");
    Scrooge.ContestReceiptSchema.ApplyV38(conn);

    using var cmd = new SqliteCommand("SELECT doubt_branch FROM contest_receipts", conn);
    using var reader = cmd.ExecuteReader();
    Assert.True(reader.Read());
    Assert.True(reader.IsDBNull(0));
  }

  [Fact]
  public void TheTapeAggregation_IsOneQueryPerBranch()
  {
    using var conn = Open();
    Scrooge.ContestReceiptSchema.ApplyV38(conn);
    Exec(conn, @"INSERT INTO contest_receipts (verdict_at, item_id, reason, verdict, doubt_branch)
                 VALUES (1, 100, 'lane_held', 'overruled', 'dead_heat'),
                        (2, 101, 'lane_held', 'overruled', 'dead_heat'),
                        (3, 102, 'lane_held', 'dismissed', 'dead_heat'),
                        (4, 103, 'lane_held', 'upheld',    'no_tape')");

    using var cmd = new SqliteCommand(
      @"SELECT SUM(CASE WHEN verdict = 'overruled' THEN 1 ELSE 0 END), COUNT(*)
        FROM contest_receipts WHERE doubt_branch = 'dead_heat'", conn);
    using var reader = cmd.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal(2, reader.GetInt32(0)); // overruled
    Assert.Equal(3, reader.GetInt32(1)); // pivots - a dismissal is one too
  }

  [Fact]
  public void TheNamingSweepReachesTheData_WithoutCostingThePlayerHisHistory()
  {
    // A listed row's teaching signal records its PILE NAME. Every ruling ever
    // made against the Watch pile is sitting in the book under a name no code
    // will ask for again - and the override-count refinement that reads those
    // rows would silently lose them. Silent, at load, like a renamed setting.
    using var conn = Open();
    Exec(conn, @"CREATE TABLE routing_overrides (
                   id INTEGER PRIMARY KEY AUTOINCREMENT,
                   router_verdict TEXT NOT NULL,
                   player_verdict TEXT NOT NULL)");
    Exec(conn, @"INSERT INTO routing_overrides (router_verdict, player_verdict)
                 VALUES ('Watch', 'Vendor'), ('Watch', 'None'), ('Reprice', 'Vendor')");

    Scrooge.ContestReceiptSchema.MigrateWatchVerdictNames(conn);

    Assert.Equal(2, Scalar(conn, "SELECT COUNT(*) FROM routing_overrides WHERE router_verdict = 'Defer'"));
    Assert.Equal(0, Scalar(conn, "SELECT COUNT(*) FROM routing_overrides WHERE router_verdict = 'Watch'"));
    // Nothing else moved, and nothing was lost.
    Assert.Equal(3, Scalar(conn, "SELECT COUNT(*) FROM routing_overrides"));

    // Idempotent by construction: after one pass no row matches.
    Scrooge.ContestReceiptSchema.MigrateWatchVerdictNames(conn);
    Assert.Equal(2, Scalar(conn, "SELECT COUNT(*) FROM routing_overrides WHERE router_verdict = 'Defer'"));
  }

  [Fact]
  public void TheNamingSweep_OnABookWithNoOverridesTable_IsANoOp()
  {
    using var conn = Open();
    var ex = Record.Exception(() => Scrooge.ContestReceiptSchema.MigrateWatchVerdictNames(conn));
    Assert.Null(ex);
  }

  private static long Scalar(SqliteConnection conn, string sql)
  {
    using var cmd = new SqliteCommand(sql, conn);
    return System.Convert.ToInt64(cmd.ExecuteScalar());
  }

  private static void Exec(SqliteConnection conn, string sql)
  {
    using var cmd = new SqliteCommand(sql, conn);
    cmd.ExecuteNonQuery();
  }
}
