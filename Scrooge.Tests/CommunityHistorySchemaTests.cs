using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// RELOAD SURVIVAL (V29). The community cache used to die with the process, and
/// the LIST score died with it — five rows carrying "the DC pays 11-30k" routed
/// to Melt at ~2k for ten minutes after a reload, purely because the evidence was
/// gone. These tests hold the round-trip that fixes it, against a real SQLite DB:
/// what goes into the cache comes back out of the table, an item Universalis has
/// NOTHING for stays a distinguishable fact, a scope answers only for itself, and
/// the TTL cutoff means the same thing on both sides of a restart.
///
/// Plus the derivation the score actually reads: median + count of the
/// quality-matched settles, which is the entire community case.
/// </summary>
public class CommunityHistorySchemaTests
{
  private const long T = 1_700_000_000;
  private const string Dc = "Aether";

  private static SqliteConnection OpenTempDb()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    CommunityHistorySchema.ApplyV29(conn);
    return conn;
  }

  private static Dictionary<uint, CommunityHistorySchema.Row> Round(
    uint itemId, long? uploadedAt, long fetchedAt, params LaneSale[] sales)
    => new() { [itemId] = new CommunityHistorySchema.Row(sales, uploadedAt, fetchedAt) };

  // ========================================================================
  // The DDL
  // ========================================================================

  [Fact]
  public void ApplyV29_IsIdempotent()
  {
    using var conn = OpenTempDb();
    CommunityHistorySchema.ApplyV29(conn);
    CommunityHistorySchema.ApplyV29(conn);

    // Still usable, still empty — a re-run neither throws nor resets.
    Assert.Empty(CommunityHistorySchema.ReadScope(conn, Dc, 0));
  }

  [Fact]
  public void ApplyV29_PreservesExistingRows()
  {
    using var conn = OpenTempDb();
    CommunityHistorySchema.UpsertRounds(conn, Dc,
      Round(100, T, T, new LaneSale(500, T - 10, false)));

    CommunityHistorySchema.ApplyV29(conn);

    Assert.Single(CommunityHistorySchema.ReadScope(conn, Dc, 0));
  }

  // ========================================================================
  // The round trip
  // ========================================================================

  [Fact]
  public void BankedRound_ReadsBackWhole()
  {
    using var conn = OpenTempDb();
    CommunityHistorySchema.UpsertRounds(conn, Dc, Round(100, T - 3600, T,
      new LaneSale(11_000, T - 100, false),
      new LaneSale(30_000, T - 200, true)));

    var rows = CommunityHistorySchema.ReadScope(conn, Dc, 0);

    var row = Assert.Contains(100u, rows);
    Assert.Equal(T - 3600, row.LastUploadAt);
    Assert.Equal(T, row.FetchedAt);
    Assert.Equal(2, row.Sales.Count);
    Assert.Contains(row.Sales, s => s is { UnitPrice: 11_000, Timestamp: T - 100, IsHq: false });
    Assert.Contains(row.Sales, s => s is { UnitPrice: 30_000, Timestamp: T - 200, IsHq: true });
  }

  [Fact]
  public void KnownNothing_SurvivesAsAnItemWithNoSales()
  {
    // The cache's "we asked and the DC has nothing" row — it exists so a silent
    // item stops re-queueing every round. It must not read back as absent.
    using var conn = OpenTempDb();
    CommunityHistorySchema.UpsertRounds(conn, Dc, Round(100, null, T));

    var row = Assert.Contains(100u, CommunityHistorySchema.ReadScope(conn, Dc, 0));
    Assert.Null(row.LastUploadAt);
    Assert.Empty(row.Sales);
  }

  [Fact]
  public void Refetch_ReplacesSalesWholesale()
  {
    using var conn = OpenTempDb();
    CommunityHistorySchema.UpsertRounds(conn, Dc, Round(100, T, T,
      new LaneSale(500, T - 10, false),
      new LaneSale(600, T - 20, false)));

    CommunityHistorySchema.UpsertRounds(conn, Dc, Round(100, T + 50, T + 60,
      new LaneSale(900, T + 40, false)));

    var row = Assert.Contains(100u, CommunityHistorySchema.ReadScope(conn, Dc, 0));
    Assert.Equal(T + 50, row.LastUploadAt);
    Assert.Equal(T + 60, row.FetchedAt);
    var sale = Assert.Single(row.Sales);
    Assert.Equal(900, sale.UnitPrice);
  }

  [Fact]
  public void IdenticalSales_BothSurvive()
  {
    // No dedup key: two genuine settles can share item, quality, price and
    // second, and losing one would shrink a sample count the score reads.
    using var conn = OpenTempDb();
    CommunityHistorySchema.UpsertRounds(conn, Dc, Round(100, T, T,
      new LaneSale(500, T - 10, false),
      new LaneSale(500, T - 10, false)));

    var row = Assert.Contains(100u, CommunityHistorySchema.ReadScope(conn, Dc, 0));
    Assert.Equal(2, row.Sales.Count);
  }

  [Fact]
  public void Scope_AnswersOnlyForItsOwnDataCenter()
  {
    using var conn = OpenTempDb();
    CommunityHistorySchema.UpsertRounds(conn, Dc, Round(100, T, T,
      new LaneSale(500, T - 10, false)));
    CommunityHistorySchema.UpsertRounds(conn, "Primal", Round(100, T, T,
      new LaneSale(9_999, T - 10, false)));

    var aether = Assert.Contains(100u, CommunityHistorySchema.ReadScope(conn, Dc, 0));
    Assert.Equal(500, Assert.Single(aether.Sales).UnitPrice);

    var primal = Assert.Contains(100u, CommunityHistorySchema.ReadScope(conn, "Primal", 0));
    Assert.Equal(9_999, Assert.Single(primal.Sales).UnitPrice);
  }

  [Fact]
  public void EmptyRound_WritesNothing()
  {
    using var conn = OpenTempDb();
    CommunityHistorySchema.UpsertRounds(conn, Dc, new Dictionary<uint, CommunityHistorySchema.Row>());
    Assert.Empty(CommunityHistorySchema.ReadScope(conn, Dc, 0));
  }

  // ========================================================================
  // The TTL cutoff — the same instant on both sides of a restart
  // ========================================================================

  [Fact]
  public void ReadScope_HidesRoundsOlderThanTheCutoff()
  {
    using var conn = OpenTempDb();
    CommunityHistorySchema.UpsertRounds(conn, Dc, Round(100, T, T - 10_000,
      new LaneSale(500, T - 10, false)));
    CommunityHistorySchema.UpsertRounds(conn, Dc, Round(200, T, T,
      new LaneSale(700, T - 10, false)));

    var rows = CommunityHistorySchema.ReadScope(conn, Dc, T - 5_000);

    Assert.DoesNotContain(100u, rows);
    Assert.Contains(200u, rows);
  }

  [Fact]
  public void ReadScope_CutoffHidesTheStaleRoundsSalesToo()
  {
    // The sales join reads the HEADER's freshness, never its own — a stale
    // round must not leak its settles into a fresh item's evidence.
    using var conn = OpenTempDb();
    CommunityHistorySchema.UpsertRounds(conn, Dc, Round(100, T, T - 10_000,
      new LaneSale(500, T - 10, false)));

    Assert.Empty(CommunityHistorySchema.ReadScope(conn, Dc, T - 5_000));
  }

  [Fact]
  public void PruneStale_DropsExpiredRoundsAndTheirSales()
  {
    using var conn = OpenTempDb();
    CommunityHistorySchema.UpsertRounds(conn, Dc, Round(100, T, T - 10_000,
      new LaneSale(500, T - 10, false)));
    CommunityHistorySchema.UpsertRounds(conn, Dc, Round(200, T, T,
      new LaneSale(700, T - 10, false)));

    var removed = CommunityHistorySchema.PruneStale(conn, T - 5_000);

    Assert.Equal(1, removed);
    Assert.Equal(0, CountSales(conn, 100));
    Assert.Equal(1, CountSales(conn, 200));
    Assert.Contains(200u, CommunityHistorySchema.ReadScope(conn, Dc, 0));
  }

  [Fact]
  public void PruneStale_LeavesFreshRoundsAlone()
  {
    using var conn = OpenTempDb();
    CommunityHistorySchema.UpsertRounds(conn, Dc, Round(100, T, T,
      new LaneSale(500, T - 10, false)));

    Assert.Equal(0, CommunityHistorySchema.PruneStale(conn, T - 5_000));
    Assert.Single(CommunityHistorySchema.ReadScope(conn, Dc, 0));
  }

  private static long CountSales(SqliteConnection conn, long itemId)
  {
    using var cmd = new SqliteCommand(
      "SELECT COUNT(*) FROM community_history_sales WHERE item_id = @iid", conn);
    cmd.Parameters.AddWithValue("@iid", itemId);
    return (long)cmd.ExecuteScalar()!;
  }

  // ========================================================================
  // The evidence the list score reads
  // ========================================================================

  [Fact]
  public void Evidence_IsTheQualityMatchedMedianAndCount()
  {
    LaneSale[] sales =
    [
      new(10_000, T, false),
      new(30_000, T, false),
      new(11_000, T, false),
      new(999_999, T, true),
    ];

    var (median, count) = CommunityHistorySchema.Evidence(sales, isHq: false);

    Assert.Equal(11_000, median);
    Assert.Equal(3, count);
  }

  [Fact]
  public void Evidence_EvenSampleAveragesTheMiddlePair()
  {
    LaneSale[] sales = [new(100, T, false), new(300, T, false)];

    var (median, count) = CommunityHistorySchema.Evidence(sales, isHq: false);

    Assert.Equal(200, median);
    Assert.Equal(2, count);
  }

  [Fact]
  public void Evidence_NoQualityMatchIsSilent()
  {
    // Not a zero: "the DC has never sold an HQ one" and "the DC pays 0" are
    // different facts, and only the first is true here.
    LaneSale[] sales = [new(10_000, T, false)];

    var (median, count) = CommunityHistorySchema.Evidence(sales, isHq: true);

    Assert.Null(median);
    Assert.Equal(0, count);
  }

  [Fact]
  public void Evidence_DoesNotDisturbTheCallersOrder()
  {
    // The lane reads the SAME list newest-first; a median that sorted in place
    // would silently reorder its evidence.
    var sales = new List<LaneSale>
    {
      new(300, T, false),
      new(100, T - 1, false),
      new(200, T - 2, false),
    };

    CommunityHistorySchema.Evidence(sales, isHq: false);

    Assert.Equal(300, sales[0].UnitPrice);
    Assert.Equal(100, sales[1].UnitPrice);
    Assert.Equal(200, sales[2].UnitPrice);
  }
}
