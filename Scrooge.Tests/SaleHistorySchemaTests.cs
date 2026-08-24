using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE TAPE (V23). Two contracts against a real SQLite database:
///
/// The V13 phantom cleanup - the quality split carried quality-blind rows over
/// as NQ, minting fake NQ twins with IDENTICAL price and timestamp to their HQ
/// row. Identical on BOTH is the proof of carry; anything less is a legitimate
/// pair and must survive.
///
/// The sale_history bank - insert dedups naturally against the re-seen window,
/// the two qualities never swallow each other, and the count-based ring prunes
/// to the newest RingKeep by sale_time. The insert reports how many rows the
/// database actually took (the writer-that-can't-be-caught-writing-none lesson).
/// </summary>
public class SaleHistorySchemaTests
{
  private const long T = 1_700_000_000;

  private static SqliteConnection OpenTempDb()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    return conn;
  }

  private static SaleHistorySchema.SaleRow Sale(
    uint itemId = 1, bool hq = false, long price = 100, int qty = 1,
    string buyer = "Buyer Name", long soldAt = T, long seenAt = T + 500)
    => new(itemId, hq, price, qty, buyer, soldAt, seenAt);

  private static long Count(SqliteConnection conn, string sql)
  {
    using var cmd = new SqliteCommand(sql, conn);
    return (long)cmd.ExecuteScalar()!;
  }

  // ========================================================================
  // V23 half one: the phantom NQ cleanup
  // ========================================================================

  private static void CreateLastSalePrices(SqliteConnection conn)
  {
    using var cmd = new SqliteCommand(
      @"CREATE TABLE last_sale_prices (
          item_id         INTEGER NOT NULL,
          is_hq           INTEGER NOT NULL DEFAULT 0,
          unit_price      INTEGER NOT NULL,
          timestamp       INTEGER NOT NULL,
          sold_after_days INTEGER,
          PRIMARY KEY (item_id, is_hq)
        );", conn);
    cmd.ExecuteNonQuery();
  }

  private static void InsertLastSale(SqliteConnection conn, long itemId, int isHq, long price, long ts)
  {
    using var cmd = new SqliteCommand(
      "INSERT INTO last_sale_prices (item_id, is_hq, unit_price, timestamp) VALUES (@i, @h, @p, @t)", conn);
    cmd.Parameters.AddWithValue("@i", itemId);
    cmd.Parameters.AddWithValue("@h", isHq);
    cmd.Parameters.AddWithValue("@p", price);
    cmd.Parameters.AddWithValue("@t", ts);
    cmd.ExecuteNonQuery();
  }

  [Fact]
  public void DeleteV13Phantoms_KillsExactTwins()
  {
    using var conn = OpenTempDb();
    CreateLastSalePrices(conn);
    // The V13 carry shape: NQ row identical to the HQ row on price AND timestamp.
    InsertLastSale(conn, itemId: 10, isHq: 1, price: 5000, ts: T);
    InsertLastSale(conn, itemId: 10, isHq: 0, price: 5000, ts: T);

    var deleted = SaleHistorySchema.DeleteV13PhantomNqRows(conn);

    Assert.Equal(1, deleted); // the log line's operand
    Assert.Equal(0L, Count(conn, "SELECT COUNT(*) FROM last_sale_prices WHERE is_hq = 0"));
    Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM last_sale_prices WHERE is_hq = 1"));
  }

  [Fact]
  public void DeleteV13Phantoms_SparesSamePriceDifferentTimestamp()
  {
    using var conn = OpenTempDb();
    CreateLastSalePrices(conn);
    // A real market can sell NQ and HQ at the same price on different days.
    InsertLastSale(conn, itemId: 10, isHq: 1, price: 5000, ts: T);
    InsertLastSale(conn, itemId: 10, isHq: 0, price: 5000, ts: T + 3600);

    Assert.Equal(0, SaleHistorySchema.DeleteV13PhantomNqRows(conn));
    Assert.Equal(2L, Count(conn, "SELECT COUNT(*) FROM last_sale_prices"));
  }

  [Fact]
  public void DeleteV13Phantoms_SparesSameTimestampDifferentPrice()
  {
    using var conn = OpenTempDb();
    CreateLastSalePrices(conn);
    // Both qualities clearing in the same second at different prices is real.
    InsertLastSale(conn, itemId: 10, isHq: 1, price: 8000, ts: T);
    InsertLastSale(conn, itemId: 10, isHq: 0, price: 5000, ts: T);

    Assert.Equal(0, SaleHistorySchema.DeleteV13PhantomNqRows(conn));
    Assert.Equal(2L, Count(conn, "SELECT COUNT(*) FROM last_sale_prices"));
  }

  [Fact]
  public void DeleteV13Phantoms_NeverTouchesHqRowsOrLoneNq()
  {
    using var conn = OpenTempDb();
    CreateLastSalePrices(conn);
    InsertLastSale(conn, itemId: 10, isHq: 1, price: 5000, ts: T);  // lone HQ
    InsertLastSale(conn, itemId: 20, isHq: 0, price: 5000, ts: T);  // lone NQ - no HQ twin
    // A twin pair on a DIFFERENT item must not cross-match.
    InsertLastSale(conn, itemId: 30, isHq: 1, price: 5000, ts: T);

    Assert.Equal(0, SaleHistorySchema.DeleteV13PhantomNqRows(conn));
    Assert.Equal(3L, Count(conn, "SELECT COUNT(*) FROM last_sale_prices"));
  }

  [Fact]
  public void DeleteV13Phantoms_IsIdempotent()
  {
    using var conn = OpenTempDb();
    CreateLastSalePrices(conn);
    InsertLastSale(conn, itemId: 10, isHq: 1, price: 5000, ts: T);
    InsertLastSale(conn, itemId: 10, isHq: 0, price: 5000, ts: T);

    Assert.Equal(1, SaleHistorySchema.DeleteV13PhantomNqRows(conn));
    Assert.Equal(0, SaleHistorySchema.DeleteV13PhantomNqRows(conn)); // second run: nothing left to match
  }

  // ========================================================================
  // V23 half two: the DDL contract
  // ========================================================================

  [Fact]
  public void ApplyV23_CreatesTheTape()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);

    var cols = new List<string>();
    using (var cmd = new SqliteCommand("PRAGMA table_info(sale_history);", conn))
    using (var reader = cmd.ExecuteReader())
      while (reader.Read()) cols.Add(reader.GetString(1));

    Assert.Contains("item_id", cols);
    Assert.Contains("is_hq", cols);
    Assert.Contains("unit_price", cols);
    Assert.Contains("quantity", cols);
    Assert.Contains("buyer_name", cols);
    Assert.Contains("sale_time", cols); // the packet's PurchaseTime
    Assert.Contains("seen_at", cols);   // when we banked it
  }

  [Fact]
  public void ApplyV23_IsIdempotent()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);
    var ex = Record.Exception(() => SaleHistorySchema.ApplyV23(conn));
    Assert.Null(ex);
  }

  // ========================================================================
  // Banking: new entries land and are COUNTED
  // ========================================================================

  [Fact]
  public void BankSales_LandsAndCountsEveryNewRow()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);

    var window = new List<SaleHistorySchema.SaleRow>
    {
      Sale(price: 100, soldAt: T),
      Sale(price: 110, soldAt: T + 60),
      Sale(price: 120, soldAt: T + 120),
    };

    Assert.Equal(3, SaleHistorySchema.BankSales(conn, window));
    Assert.Equal(3L, Count(conn, "SELECT COUNT(*) FROM sale_history"));
  }

  [Fact]
  public void BankSales_RebankingTheSameWindowInsertsZero()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);

    var window = new List<SaleHistorySchema.SaleRow> { Sale(price: 100), Sale(price: 110, soldAt: T + 60) };
    SaleHistorySchema.BankSales(conn, window);

    // The next pinch re-sees the same 20-entry window; only NEW entries land.
    // seen_at differs on the re-bank - it is deliberately not part of identity.
    var rebank = new List<SaleHistorySchema.SaleRow>
    {
      Sale(price: 100, seenAt: T + 9000),
      Sale(price: 110, soldAt: T + 60, seenAt: T + 9000),
    };
    Assert.Equal(0, SaleHistorySchema.BankSales(conn, rebank));
    Assert.Equal(2L, Count(conn, "SELECT COUNT(*) FROM sale_history"));
  }

  [Fact]
  public void BankSales_AShiftedWindowLandsOnlyItsNews()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);
    SaleHistorySchema.BankSales(conn, [Sale(price: 100), Sale(price: 110, soldAt: T + 60)]);

    // The window slid: one old entry re-seen, one genuinely new sale.
    var slid = new List<SaleHistorySchema.SaleRow>
    {
      Sale(price: 110, soldAt: T + 60),
      Sale(price: 120, soldAt: T + 120),
    };
    Assert.Equal(1, SaleHistorySchema.BankSales(conn, slid));
    Assert.Equal(3L, Count(conn, "SELECT COUNT(*) FROM sale_history"));
  }

  [Fact]
  public void BankSales_QualitiesNeverDedupAgainstEachOther()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);

    // Same item, second, price, and quantity - but different market facts.
    var window = new List<SaleHistorySchema.SaleRow>
    {
      Sale(hq: false, price: 100, soldAt: T),
      Sale(hq: true, price: 100, soldAt: T),
    };
    Assert.Equal(2, SaleHistorySchema.BankSales(conn, window));
    Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM sale_history WHERE is_hq = 0"));
    Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM sale_history WHERE is_hq = 1"));
  }

  [Fact]
  public void BankSales_EmptyWindowWritesNothing()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);
    Assert.Equal(0, SaleHistorySchema.BankSales(conn, []));
  }

  // ========================================================================
  // The ring: count-based, per (item, quality), newest by sale_time survive
  // ========================================================================

  [Fact]
  public void BankSales_RingPrunesToRingKeepKeepingTheNewest()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);

    // Overfill by 20: distinct sale_times so identity never collides.
    var window = new List<SaleHistorySchema.SaleRow>();
    for (var i = 0; i < SaleHistorySchema.RingKeep + 20; i++)
      window.Add(Sale(price: 100 + i, soldAt: T + i * 60));

    SaleHistorySchema.BankSales(conn, window);

    Assert.Equal((long)SaleHistorySchema.RingKeep, Count(conn, "SELECT COUNT(*) FROM sale_history"));
    // The survivors are the NEWEST by sale_time: the oldest 20 are gone.
    Assert.Equal((long)(T + 20 * 60), Count(conn, "SELECT MIN(sale_time) FROM sale_history"));
    Assert.Equal((long)(T + (SaleHistorySchema.RingKeep + 19) * 60), Count(conn, "SELECT MAX(sale_time) FROM sale_history"));
  }

  [Fact]
  public void BankSales_RingIsPerItemAndQuality()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);

    // Overfill item 1 NQ; item 1 HQ and item 2 NQ keep their few rows - the
    // ring never lets a hot item's traffic evict a slow item's tape.
    var window = new List<SaleHistorySchema.SaleRow>();
    for (var i = 0; i < SaleHistorySchema.RingKeep + 5; i++)
      window.Add(Sale(itemId: 1, hq: false, price: 100 + i, soldAt: T + i * 60));
    window.Add(Sale(itemId: 1, hq: true, price: 999, soldAt: T));
    window.Add(Sale(itemId: 2, hq: false, price: 50, soldAt: T));

    SaleHistorySchema.BankSales(conn, window);

    Assert.Equal((long)SaleHistorySchema.RingKeep,
      Count(conn, "SELECT COUNT(*) FROM sale_history WHERE item_id = 1 AND is_hq = 0"));
    Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM sale_history WHERE item_id = 1 AND is_hq = 1"));
    Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM sale_history WHERE item_id = 2"));
  }

  // ========================================================================
  // Operands round-trip
  // ========================================================================

  [Fact]
  public void BankSales_RoundTripsEveryOperand()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);

    SaleHistorySchema.BankSales(conn,
      [new SaleHistorySchema.SaleRow(4321, true, 12_345, 7, "Rich Buyer", T + 42, T + 500)]);

    using var cmd = new SqliteCommand(
      "SELECT item_id, is_hq, unit_price, quantity, buyer_name, sale_time, seen_at FROM sale_history;", conn);
    using var reader = cmd.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal(4321L, reader.GetInt64(0));
    Assert.Equal(1, reader.GetInt32(1));
    Assert.Equal(12_345L, reader.GetInt64(2)); // per-unit, never the stack total
    Assert.Equal(7, reader.GetInt32(3));
    Assert.Equal("Rich Buyer", reader.GetString(4));
    Assert.Equal(T + 42, reader.GetInt64(5));  // the packet's PurchaseTime
    Assert.Equal(T + 500, reader.GetInt64(6)); // when we banked it
    Assert.False(reader.Read());
  }

  // ========================================================================
  // The read side: the lane's evidence source (bank then read round-trip)
  // ========================================================================

  [Fact]
  public void ReadRing_RoundTripsWhatWasBanked()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);
    SaleHistorySchema.BankSales(conn,
      [new SaleHistorySchema.SaleRow(4321, true, 12_345, 7, "Rich Buyer", T + 42, T + 500)]);

    var ring = SaleHistorySchema.ReadRing(conn, 4321);

    var only = Assert.Single(ring);
    Assert.Equal(12_345L, only.UnitPrice);
    Assert.Equal(7, only.Quantity);
    Assert.True(only.IsHq);
    Assert.Equal(T + 42, only.SaleTime);
    Assert.Equal("Rich Buyer", only.BuyerName); // the demand-shape column (A4b)
  }

  [Fact]
  public void ReadRing_ComesBackNewestFirst()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);
    SaleHistorySchema.BankSales(conn,
    [
      Sale(price: 100, soldAt: T),
      Sale(price: 300, soldAt: T + 7200),
      Sale(price: 200, soldAt: T + 3600),
    ]);

    // The segment walks backward from now; the read owes it that order.
    var ring = SaleHistorySchema.ReadRing(conn, 1);
    Assert.Equal([300L, 200L, 100L], ring.Select(r => r.UnitPrice));
  }

  [Fact]
  public void ReadRing_CarriesBothQualitiesForTheCallerToMatch()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);
    SaleHistorySchema.BankSales(conn,
    [
      Sale(hq: false, price: 100, soldAt: T),
      Sale(hq: true, price: 900, soldAt: T + 60),
    ]);

    // Quality-matching is the LANE's job - the NQ x premium rung and the cross-cap
    // both need the other side of the same read, so one query hands back both.
    var ring = SaleHistorySchema.ReadRing(conn, 1);
    Assert.Equal(2, ring.Count);
    Assert.Single(ring, r => r.IsHq);
    Assert.Single(ring, r => !r.IsHq);
  }

  [Fact]
  public void ReadRing_NeverCrossesItems()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);
    SaleHistorySchema.BankSales(conn, [Sale(itemId: 1, price: 100), Sale(itemId: 2, price: 999)]);

    Assert.Equal(100L, Assert.Single(SaleHistorySchema.ReadRing(conn, 1)).UnitPrice);
    Assert.Equal(999L, Assert.Single(SaleHistorySchema.ReadRing(conn, 2)).UnitPrice);
  }

  [Fact]
  public void ReadRing_SilentItemReadsEmptyNotThrown()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);

    // A market with no tape is a legitimate answer, never an exception.
    Assert.Empty(SaleHistorySchema.ReadRing(conn, 9999));
  }

  [Fact]
  public void ReadRing_LimitTakesTheNewest()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);
    var window = new List<SaleHistorySchema.SaleRow>();
    for (var i = 0; i < 10; i++)
      window.Add(Sale(price: 100 + i, soldAt: T + i * 60));
    SaleHistorySchema.BankSales(conn, window);

    var ring = SaleHistorySchema.ReadRing(conn, 1, limit: 3);
    Assert.Equal([109L, 108L, 107L], ring.Select(r => r.UnitPrice));
  }

  // --- THE TAPE'S LAST-SOLD DATE (V31, ruled 08-22) ---

  [Fact]
  public void NewestSaleTime_AnswersPerVariant()
  {
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);
    SaleHistorySchema.BankSales(conn, new List<SaleHistorySchema.SaleRow>
    {
      Sale(itemId: 7, hq: false, soldAt: T),
      Sale(itemId: 7, hq: false, price: 120, soldAt: T + 900),
      Sale(itemId: 7, hq: true, price: 200, soldAt: T + 100),
    });

    // The variant's own newest sale, not the item's: an HQ that moves says
    // nothing about the NQ twin - the seat describes THIS variant's liquidity.
    Assert.Equal(T + 900, SaleHistorySchema.NewestSaleTime(conn, 7, false));
    Assert.Equal(T + 100, SaleHistorySchema.NewestSaleTime(conn, 7, true));
    Assert.Null(SaleHistorySchema.NewestSaleTime(conn, 8, false));
  }

  [Fact]
  public void NewestSaleTime_TheOwnLedgerIsTheSecondWitness()
  {
    // F7 (ruled 08-22, the Silvergrace): the tape never sees our own retainer
    // sales - they land in last_sale_prices via GilTrack - so "Last sold 37d
    // ago" stood beside a 4-day-old own sale. The freshness read takes the
    // newer of the two witnesses, and answers from either alone.
    using var conn = OpenTempDb();
    SaleHistorySchema.ApplyV23(conn);
    using (var ddl = new Microsoft.Data.Sqlite.SqliteCommand(
      @"CREATE TABLE last_sale_prices (
          item_id INTEGER NOT NULL, is_hq INTEGER NOT NULL DEFAULT 0,
          unit_price INTEGER NOT NULL, timestamp INTEGER NOT NULL,
          sold_after_days INTEGER)", conn))
      ddl.ExecuteNonQuery();

    SaleHistorySchema.BankSales(conn,
      new List<SaleHistorySchema.SaleRow> { Sale(itemId: 9, hq: true, soldAt: T) });
    using (var ins = new Microsoft.Data.Sqlite.SqliteCommand(
      @"INSERT INTO last_sale_prices VALUES (9, 1, 12299, @ts, 23)", conn))
    {
      ins.Parameters.AddWithValue("@ts", T + 5_000);
      ins.ExecuteNonQuery();
    }

    // Own sale newer than the tape: the own ledger answers.
    Assert.Equal(T + 5_000, SaleHistorySchema.NewestSaleTime(conn, 9, true));
    // A variant only the ledger knows: still an answer, never a null.
    using (var ins2 = new Microsoft.Data.Sqlite.SqliteCommand(
      @"INSERT INTO last_sale_prices VALUES (10, 0, 500, @ts, 1)", conn))
    {
      ins2.Parameters.AddWithValue("@ts", T + 42);
      ins2.ExecuteNonQuery();
    }
    Assert.Equal(T + 42, SaleHistorySchema.NewestSaleTime(conn, 10, false));
  }
}
