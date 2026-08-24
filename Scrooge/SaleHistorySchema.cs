using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// The V23 sale-history DDL and write path, extracted Dalamud-free so it is
/// linked-source testable against a real temp SQLite DB (the V19/V21 model).
///
/// <para>sale_history is THE TAPE: the market-board history packet - everyone's
/// settled sales, the last ~20 per item - arrives at every pinch, feeds velocity,
/// and until now was thrown away. A price regime is a sequence; this table holds
/// the sequence. Settled sales are ground truth for "does anything still clear at
/// this price," which is exactly what a regime anchor has to ask.</para>
///
/// <para>The read side landed with the regime arc: <see cref="ReadRing"/> hands the
/// lane the whole banked ring instead of the one window the packet happens to be
/// holding. Same pinch, deeper evidence - a slow mover's ring reaches back months
/// where the packet reaches back one visit.</para>
///
/// <para>Dedup is natural, not procedural: each pinch re-sees most of the same
/// 20-entry window, so the insert is OR IGNORE against a UNIQUE index on
/// (item_id, is_hq, sale_time, unit_price, quantity) and only new entries land.
/// is_hq is in the key deliberately - an NQ and an HQ sale are different market
/// facts even at the same second and price, and must never swallow each other.</para>
/// </summary>
internal static class SaleHistorySchema
{
  /// <summary>
  /// Ring retention per (item_id, is_hq): after banking, everything but the
  /// newest RingKeep rows is pruned in the same transaction. Count-based, NEVER
  /// age-based - any age cutoff is wrong for half the market at once (a week of
  /// tape is nothing for a slow furnishing and an ocean for shards), while a
  /// count ring self-scales to each item's velocity. Retention knob, not a
  /// pricing operand; oversizing costs kilobytes, so 100 buys several regimes
  /// of tape for any realistic velocity.
  /// </summary>
  internal const int RingKeep = 100;

  /// <summary>One settled sale from the history packet, ready to bank.</summary>
  internal readonly record struct SaleRow(
    uint ItemId, bool IsHq, long UnitPrice, int Quantity,
    string BuyerName, long SaleTime, long SeenAt);

  /// <summary>One settled sale read back off the tape (both qualities, newest-first).</summary>
  internal readonly record struct BankedSale(
    long UnitPrice, int Quantity, bool IsHq, long SaleTime, string BuyerName);

  /// <summary>Creates the sale_history table and its indexes (idempotent).</summary>
  internal static void ApplyV23(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(
      @"CREATE TABLE IF NOT EXISTS sale_history (
          id         INTEGER PRIMARY KEY AUTOINCREMENT,
          item_id    INTEGER NOT NULL,
          is_hq      INTEGER NOT NULL DEFAULT 0,
          -- Per-unit, like the rest of the ledger. The packet's price is already
          -- per-unit; total = unit_price * quantity is derived, never stored.
          unit_price INTEGER NOT NULL,
          quantity   INTEGER NOT NULL,
          buyer_name TEXT NOT NULL DEFAULT '',
          -- The packet's PurchaseTime (unix seconds): when the sale SETTLED.
          sale_time  INTEGER NOT NULL,
          -- When WE banked it (unix seconds). The gap between the two is how
          -- stale our view of this item was when the tape caught up.
          seen_at    INTEGER NOT NULL
        );
        -- The natural-dedup key: re-banking the same window is a no-op.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_sale_history_dedup
          ON sale_history(item_id, is_hq, sale_time, unit_price, quantity);
        -- The ring's read shape: newest-first per (item, quality).
        CREATE INDEX IF NOT EXISTS ix_sale_history_ring
          ON sale_history(item_id, is_hq, sale_time DESC);",
      connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// V23's other half: deletes the phantom NQ rows the V13 quality split left in
  /// last_sale_prices. V13 carried every old quality-blind row over AS NQ; any
  /// item whose last pre-split sale was HQ got a fake NQ row with IDENTICAL
  /// unit_price AND timestamp to its rebuilt HQ row, and those phantoms have fed
  /// the routing lane's LastSale evidence since. Identical price AND timestamp
  /// to the second is the proof of carry - two genuine sales never coincide on
  /// both - so exact twins die and every legitimate pair survives. Returns the
  /// deleted count: the operand of the migration log line.
  /// </summary>
  internal static int DeleteV13PhantomNqRows(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(
      @"DELETE FROM last_sale_prices
        WHERE is_hq = 0
          AND EXISTS (SELECT 1 FROM last_sale_prices hq
                      WHERE hq.item_id = last_sale_prices.item_id
                        AND hq.is_hq = 1
                        AND hq.unit_price = last_sale_prices.unit_price
                        AND hq.timestamp = last_sale_prices.timestamp)",
      connection);
    return cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Banks a history window and ring-prunes every (item, quality) it touched,
  /// atomically, and returns HOW MANY rows the database actually took.
  ///
  /// <para>The count is the whole point of the signature (the 07-25 standing-book
  /// lesson: a writer that cannot say how many rows it wrote cannot be caught
  /// writing none). Re-seen entries are OR IGNOREd by the dedup index and simply
  /// don't count; a healthy steady state banks a handful of new rows per pinch
  /// and reports exactly that.</para>
  ///
  /// <para>The prune rides the SAME transaction as the insert so the tape can
  /// never be observed over-full or half-pruned. Newest-by-sale_time survive;
  /// id breaks ties so the ring is deterministic even inside one second.</para>
  /// </summary>
  internal static int BankSales(SqliteConnection connection, IReadOnlyList<SaleRow> sales)
  {
    if (sales.Count == 0)
      return 0;

    using var tx = connection.BeginTransaction();

    var inserted = 0;
    foreach (var s in sales)
    {
      using var cmd = new SqliteCommand(
        @"INSERT OR IGNORE INTO sale_history
            (item_id, is_hq, unit_price, quantity, buyer_name, sale_time, seen_at)
          VALUES (@iid, @hq, @price, @qty, @buyer, @sold, @seen)",
        connection, tx);
      cmd.Parameters.AddWithValue("@iid", (long)s.ItemId);
      cmd.Parameters.AddWithValue("@hq", s.IsHq ? 1 : 0);
      cmd.Parameters.AddWithValue("@price", s.UnitPrice);
      cmd.Parameters.AddWithValue("@qty", s.Quantity);
      cmd.Parameters.AddWithValue("@buyer", s.BuyerName);
      cmd.Parameters.AddWithValue("@sold", s.SaleTime);
      cmd.Parameters.AddWithValue("@seen", s.SeenAt);
      inserted += cmd.ExecuteNonQuery();
    }

    // Prune each (item, quality) this window touched back to the ring.
    var touched = new HashSet<(uint ItemId, bool IsHq)>();
    foreach (var s in sales)
      touched.Add((s.ItemId, s.IsHq));

    foreach (var (itemId, isHq) in touched)
    {
      using var prune = new SqliteCommand(
        @"DELETE FROM sale_history
          WHERE item_id = @iid AND is_hq = @hq
            AND id NOT IN (SELECT id FROM sale_history
                           WHERE item_id = @iid AND is_hq = @hq
                           ORDER BY sale_time DESC, id DESC
                           LIMIT @keep)",
        connection, tx);
      prune.Parameters.AddWithValue("@iid", (long)itemId);
      prune.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      prune.Parameters.AddWithValue("@keep", RingKeep);
      prune.ExecuteNonQuery();
    }

    tx.Commit();
    return inserted;
  }

  /// <summary>
  /// When one variant last changed hands on the tape (unix seconds), or null when it
  /// never has. The case page's last-sold seat (V31) reads this - a DISPLAY operand,
  /// so one MAX over the dedup index is the whole cost, on the case-assembly clock.
  /// Quality-scoped on purpose: the seat describes THIS variant's liquidity, and an
  /// HQ that moves daily says nothing about the NQ twin nobody buys.
  /// </summary>
  internal static long? NewestSaleTime(SqliteConnection connection, uint itemId, bool isHq)
  {
    // MAX OVER BOTH WITNESSES (F7, ruled 08-22 - the Silvergrace "Last sold 37d
    // ago" beside a 4-day-old own sale). The tape is structurally BLIND to our
    // own retainer sales - they arrive as GilTrack ledger events, never in the
    // board's history packet - so a tape-only read reports the market staler
    // than our own receipts whenever the newest sale was ours. The own ledger's
    // last_sale_prices row is the second witness; the freshness line takes the
    // newer of the two. Its own guard: a DB without the listings schema (some
    // test rigs) just answers with the tape.
    long? tape;
    using (var cmd = new SqliteCommand(
      @"SELECT MAX(sale_time) FROM sale_history WHERE item_id = @iid AND is_hq = @hq",
      connection))
    {
      cmd.Parameters.AddWithValue("@iid", (long)itemId);
      cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      tape = cmd.ExecuteScalar() is long t ? t : null;
    }

    long? own = null;
    try
    {
      using var cmd = new SqliteCommand(
        @"SELECT MAX(timestamp) FROM last_sale_prices WHERE item_id = @iid AND is_hq = @hq",
        connection);
      cmd.Parameters.AddWithValue("@iid", (long)itemId);
      cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      own = cmd.ExecuteScalar() is long o ? o : null;
    }
    catch { /* no last_sale_prices table in this DB - the tape answers alone */ }

    if (tape is long tt && own is long oo) return Math.Max(tt, oo);
    return tape ?? own;
  }

  /// <summary>
  /// Reads one item's banked tape back, BOTH qualities, newest sale first.
  ///
  /// <para>Both qualities on purpose: the caller quality-matches (an HQ lane is
  /// built from HQ sales), but the quality rules that arrive later - the NQ x
  /// premium rung, the cross-cap - need the other side of the same read. One
  /// query, one shape, no second trip.</para>
  ///
  /// <para>Newest-first is the segment's required order: a regime is the newest
  /// RUN of sales, and every test that finds its edge walks backward from now.
  /// sale_time DESC with id DESC as the tiebreak matches the ring's prune order
  /// exactly, so read and retention can never disagree about which sale is
  /// newer inside one second.</para>
  ///
  /// <para>Returns an empty list when the table is absent or the item has no
  /// tape - a silent market is a legitimate answer, never an exception.</para>
  /// </summary>
  internal static List<BankedSale> ReadRing(SqliteConnection connection, uint itemId, int limit = RingKeep)
  {
    var rows = new List<BankedSale>();
    if (limit <= 0)
      return rows;

    using var cmd = new SqliteCommand(
      @"SELECT unit_price, quantity, is_hq, sale_time, buyer_name
        FROM sale_history
        WHERE item_id = @iid
        ORDER BY sale_time DESC, id DESC
        LIMIT @limit",
      connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@limit", limit);

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      rows.Add(new BankedSale(
        reader.GetInt64(0),
        reader.GetInt32(1),
        reader.GetInt32(2) != 0,
        reader.GetInt64(3),
        reader.IsDBNull(4) ? "" : reader.GetString(4)));
    }

    return rows;
  }
}
