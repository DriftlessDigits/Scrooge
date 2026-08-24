using Microsoft.Data.Sqlite;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// THE LIST SCORE'S EVIDENCE, ACROSS A RELOAD (07-26).
///
/// <para><b>The hole.</b> <see cref="UniversalisHistory"/> was memory-only by
/// design ("a within-session rescue doesn't need to survive restarts"). It does:
/// the DC-scope settled sales are what <c>CommunityMedian</c> /
/// <c>CommunitySampleCount</c> are derived from, and those two are the whole of
/// the LIST score's community case. A reload emptied the cache, so every ledger
/// row whose list verdict rested on "the DC pays ~11-30k" lost its evidence and
/// re-routed to Melt BY FORFEIT until the lazy fetches trickled back - five rows
/// sat at a ~2k melt verdict for ten minutes on Drift's live run.</para>
///
/// <para><b>The fill.</b> The V27 pattern, one table over: the fetch write-throughs
/// here, a cold start reads back through, and TryGet's contract does not change by
/// one line. The RAW sales are banked rather than the median, because the median is
/// only one of the two things the cache feeds - the community LANE
/// (<see cref="LanePricing.BuildLane"/>) reads timestamps and half-life-weights
/// them. Banking the derived number would have restored the router and left the
/// lane still forfeiting, and two representations of one fetch is exactly the dual
/// state we don't build.</para>
///
/// <para><b>No new freshness policy.</b> <c>fetched_at</c> and
/// <c>last_upload_at</c> are persisted verbatim, so the TTL and the trust window
/// are evaluated after a restart against the same instants they were evaluated
/// against before it. This table makes yesterday's answer survive; it never makes
/// it fresher than it was.</para>
///
/// <para>Scoped by DC name because the cache is: an alt on another data center
/// gets its own rows rather than poisoning the ones it isn't asking about
/// (<c>EnsureScope</c>'s in-memory rule, written down in SQL).</para>
///
/// <para>Dalamud-free so the seam is linked-source testable (the
/// MeltYieldPrices / SaleHistorySchema model): this owns the DDL, the round-trip
/// and the evidence derivation; the shell owns only the fetch.</para>
/// </summary>
internal static class CommunityHistorySchema
{
  /// <summary>
  /// One item's banked fetch round: the sales, the upload instant the trust gate
  /// judges, and when we asked. Doubles as <see cref="UniversalisHistory"/>'s
  /// in-memory cache row, so the thing written and the thing read are one type.
  /// </summary>
  internal readonly record struct Row(
    IReadOnlyList<LaneSale> Sales, long? LastUploadAt, long FetchedAt);

  /// <summary>
  /// V29: <c>community_history</c> + <c>community_history_sales</c> - the DC
  /// evidence the LIST score reads, kept across a reload.
  ///
  /// <para>Two tables because a fetch round is two facts: one per-item header
  /// (asked at, uploaded at) and N sales. An item Universalis has NOTHING for is
  /// a header with no sales - the "known nothing" row the cache already keeps so
  /// a silent item stops re-queueing every round - and that state is
  /// unrepresentable in a single sales table.</para>
  ///
  /// <para>The sales table has no unique key on purpose: two genuine settles CAN
  /// share item, quality, price and second, and a round is written by replacing
  /// its item's rows wholesale, never by merging. Dedup would silently shrink a
  /// sample count the score reads.</para>
  ///
  /// <para>CREATE ... IF NOT EXISTS, so re-running is a no-op (the V11 model).</para>
  /// </summary>
  internal static void ApplyV29(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(
      @"CREATE TABLE IF NOT EXISTS community_history (
          dc_name        TEXT    NOT NULL,
          item_id        INTEGER NOT NULL,
          -- Universalis' lastUploadTime: what the trust gate judges. NULL means
          -- the DC has no data at all for the item, which is not the same fact
          -- as 'nobody has uploaded lately'.
          last_upload_at INTEGER,
          -- When WE asked. The TTL is measured from here, before and after a
          -- restart alike.
          fetched_at     INTEGER NOT NULL,
          PRIMARY KEY (dc_name, item_id)
        );
        CREATE TABLE IF NOT EXISTS community_history_sales (
          dc_name    TEXT    NOT NULL,
          item_id    INTEGER NOT NULL,
          is_hq      INTEGER NOT NULL DEFAULT 0,
          unit_price INTEGER NOT NULL,
          sold_at    INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_community_history_sales_item
          ON community_history_sales(dc_name, item_id);",
      connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Banks one fetch round for a DC: header upsert plus a wholesale replace of
  /// each item's sales, all in one transaction so a round is never observed
  /// half-landed (the sale_history model).
  /// </summary>
  internal static void UpsertRounds(SqliteConnection connection, string scope,
    IReadOnlyDictionary<uint, Row> rounds)
  {
    if (rounds.Count == 0)
      return;

    using var tx = connection.BeginTransaction();

    foreach (var (itemId, row) in rounds)
    {
      using (var header = new SqliteCommand(
        @"INSERT INTO community_history (dc_name, item_id, last_upload_at, fetched_at)
          VALUES (@dc, @iid, @upload, @fetched)
          ON CONFLICT(dc_name, item_id) DO UPDATE
            SET last_upload_at = excluded.last_upload_at,
                fetched_at     = excluded.fetched_at;",
        connection, tx))
      {
        header.Parameters.AddWithValue("@dc", scope);
        header.Parameters.AddWithValue("@iid", (long)itemId);
        header.Parameters.AddWithValue("@upload",
          (object?)row.LastUploadAt ?? System.DBNull.Value);
        header.Parameters.AddWithValue("@fetched", row.FetchedAt);
        header.ExecuteNonQuery();
      }

      using (var clear = new SqliteCommand(
        "DELETE FROM community_history_sales WHERE dc_name = @dc AND item_id = @iid;",
        connection, tx))
      {
        clear.Parameters.AddWithValue("@dc", scope);
        clear.Parameters.AddWithValue("@iid", (long)itemId);
        clear.ExecuteNonQuery();
      }

      foreach (var sale in row.Sales)
      {
        using var insert = new SqliteCommand(
          @"INSERT INTO community_history_sales
              (dc_name, item_id, is_hq, unit_price, sold_at)
            VALUES (@dc, @iid, @hq, @price, @sold);",
          connection, tx);
        insert.Parameters.AddWithValue("@dc", scope);
        insert.Parameters.AddWithValue("@iid", (long)itemId);
        insert.Parameters.AddWithValue("@hq", sale.IsHq ? 1 : 0);
        insert.Parameters.AddWithValue("@price", sale.UnitPrice);
        insert.Parameters.AddWithValue("@sold", sale.Timestamp);
        insert.ExecuteNonQuery();
      }
    }

    tx.Commit();
  }

  /// <summary>
  /// Every banked round for one DC that is still inside the TTL, keyed by item -
  /// the shape the memory cache wants, so a cold start is one query and no
  /// per-item trips.
  ///
  /// <para><paramref name="freshAfter"/> is the caller's TTL cutoff, applied here
  /// rather than after loading: a row older than the TTL is one TryGet would
  /// discard and re-queue anyway, so handing it to the cache only invites the
  /// question of which layer is the authority.</para>
  /// </summary>
  internal static Dictionary<uint, Row> ReadScope(SqliteConnection connection,
    string scope, long freshAfter)
  {
    var sales = new Dictionary<uint, List<LaneSale>>();
    using (var salesCmd = new SqliteCommand(
      @"SELECT s.item_id, s.unit_price, s.sold_at, s.is_hq
        FROM community_history_sales s
        JOIN community_history h
          ON h.dc_name = s.dc_name AND h.item_id = s.item_id
        WHERE s.dc_name = @dc AND h.fetched_at >= @fresh;",
      connection))
    {
      salesCmd.Parameters.AddWithValue("@dc", scope);
      salesCmd.Parameters.AddWithValue("@fresh", freshAfter);
      using var reader = salesCmd.ExecuteReader();
      while (reader.Read())
      {
        var itemId = (uint)reader.GetInt64(0);
        if (!sales.TryGetValue(itemId, out var list))
          sales[itemId] = list = [];
        list.Add(new LaneSale(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3) != 0));
      }
    }

    var rows = new Dictionary<uint, Row>();
    using (var headerCmd = new SqliteCommand(
      @"SELECT item_id, last_upload_at, fetched_at
        FROM community_history
        WHERE dc_name = @dc AND fetched_at >= @fresh;",
      connection))
    {
      headerCmd.Parameters.AddWithValue("@dc", scope);
      headerCmd.Parameters.AddWithValue("@fresh", freshAfter);
      using var reader = headerCmd.ExecuteReader();
      while (reader.Read())
      {
        var itemId = (uint)reader.GetInt64(0);
        rows[itemId] = new Row(
          sales.TryGetValue(itemId, out var list) ? list : [],
          reader.IsDBNull(1) ? null : reader.GetInt64(1),
          reader.GetInt64(2));
      }
    }

    return rows;
  }

  /// <summary>
  /// Drops rounds past the TTL, every DC, and returns how many headers went.
  ///
  /// <para>Not a freshness rule - these rows are already invisible to
  /// <see cref="ReadScope"/> and would be re-queued on sight by TryGet. This is
  /// the retention the memory cache got for free by dying at unload, handed to
  /// the table that now outlives it.</para>
  /// </summary>
  internal static int PruneStale(SqliteConnection connection, long freshAfter)
  {
    using var tx = connection.BeginTransaction();

    using (var sales = new SqliteCommand(
      @"DELETE FROM community_history_sales
        WHERE EXISTS (SELECT 1 FROM community_history h
                      WHERE h.dc_name = community_history_sales.dc_name
                        AND h.item_id = community_history_sales.item_id
                        AND h.fetched_at < @fresh);",
      connection, tx))
    {
      sales.Parameters.AddWithValue("@fresh", freshAfter);
      sales.ExecuteNonQuery();
    }

    int removed;
    using (var headers = new SqliteCommand(
      "DELETE FROM community_history WHERE fetched_at < @fresh;",
      connection, tx))
    {
      headers.Parameters.AddWithValue("@fresh", freshAfter);
      removed = headers.ExecuteNonQuery();
    }

    tx.Commit();
    return removed;
  }

  /// <summary>
  /// The LIST score's community case, derived from one item's DC sales: the
  /// median of the quality-matched settles and how many voted.
  ///
  /// <para>Median, not the last settle, and the SAME median the melt scale's
  /// weights are built with - the listing score and the melt score read the
  /// market through one statistic or they are not comparable at all.</para>
  ///
  /// <para>No quality-matched sales returns (null, 0), which is the router's
  /// "no community evidence" - never a fabricated zero.</para>
  /// </summary>
  internal static (long? Median, int Count) Evidence(IReadOnlyList<LaneSale> sales, bool isHq)
  {
    var prices = new List<long>();
    foreach (var s in sales)
      if (s.IsHq == isHq)
        prices.Add(s.UnitPrice);

    return (MeltYieldPrices.MedianPrice(prices), prices.Count);
  }
}
