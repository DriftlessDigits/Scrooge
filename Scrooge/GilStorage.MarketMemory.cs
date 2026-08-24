using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// What the board looked like and what changed: the board-snapshot and
/// <c>market_events</c> tables, plus the pass-throughs to the banked sale-history and
/// community-history schemas.
///
/// <para>A scan is not stored as a fact but as a diff against the last one — see
/// <see cref="ApplyBoardScan"/>, which turns two board states into appearance,
/// price-change and disappearance events. The events are the durable part; the
/// snapshot is only the baseline for the next diff.</para>
/// </summary>
internal static partial class GilStorage
{
  // =========================================================================
  // Ripeness sensors - how stale is the board read
  // =========================================================================

  /// <summary>The most recent FULL pinch scan (market_snapshots.source = 'full'), or 0 when none.</summary>
  internal static long GetLastFullScanTime()
  {
    using var cmd = new SqliteCommand(
      "SELECT MAX(timestamp) FROM market_snapshots WHERE source = 'full'", _connection);
    return cmd.ExecuteScalar() is long l ? l : 0;
  }

  // =========================================================================
  // Market memory (V19) — append-diff events + current-board snapshot
  // =========================================================================

  private static string EventKindTag(MarketEvents.EventKind k) => k switch
  {
    MarketEvents.EventKind.Appeared => "appeared",
    MarketEvents.EventKind.Disappeared => "disappeared",
    _ => "price_moved",
  };

  private static string ResolutionTag(MarketEvents.DisappearResolution r) => r switch
  {
    MarketEvents.DisappearResolution.Sold => "sold",
    MarketEvents.DisappearResolution.Pulled => "pulled",
    _ => "gone",
  };

  /// <summary>
  /// The stored current-board snapshot for one item — the prior scan the next diff
  /// compares against. Returns the listings plus the timestamp of the scan that
  /// produced them (0 when the item has never been scanned), which becomes the
  /// next event's seen_after (the window's start).
  /// </summary>
  internal static (List<MarketEvents.BoardListing> Prior, long PriorScanAt) GetBoardSnapshot(uint itemId)
  {
    var prior = new List<MarketEvents.BoardListing>();
    long priorScanAt = 0;
    using var cmd = new SqliteCommand(
      @"SELECT retainer_name, quantity, is_hq, unit_price, is_own, seen_at
        FROM market_board_snapshot WHERE item_id = @iid",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      prior.Add(new MarketEvents.BoardListing(
        reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2) != 0,
        reader.GetInt64(3), reader.GetInt32(4) != 0));
      priorScanAt = Math.Max(priorScanAt, reader.GetInt64(5));
    }
    return (prior, priorScanAt);
  }

  /// <summary>
  /// The ONE write path for market memory (design Section 3): diffs the incoming
  /// board for an item against the stored snapshot, APPENDS the resulting events, and
  /// REPLACES the snapshot with the new board — atomically. Every board packet that
  /// reaches a pricing door runs this, even for items the pricer skips (the
  /// observation was made, record it). Foreign timing is the (seen_after, seen_by)
  /// window only; own disappearances land as 'pulled'/observed, upgradeable to
  /// 'sold'/confirmed by a later GilTrack confirm. Returns the number of events appended.
  /// </summary>
  internal static int ApplyBoardScan(uint itemId, IReadOnlyList<MarketEvents.BoardListing> currentBoard,
      long scanAt, uint worldId = 0, MarketEvents.Observer observer = MarketEvents.Observer.OwnScan)
  {
    var (prior, priorScanAt) = GetBoardSnapshot(itemId);
    var events = MarketEvents.Diff(prior, currentBoard);
    var window = new MarketEvents.ObservationWindow(priorScanAt, scanAt);
    var observerTag = observer == MarketEvents.Observer.Community ? "community" : "own_scan";

    using var tx = Connection.BeginTransaction();

    foreach (var ev in events)
    {
      var row = MarketEvents.ToRow(ev, window, observer);
      using var insert = new SqliteCommand(
        @"INSERT INTO market_events
            (item_id, is_hq, retainer_name, quantity, kind, old_price, new_price,
             is_own, observer, certainty, resolution, ambiguous_match, seen_after, seen_by, world_id)
          VALUES (@iid, @hq, @ret, @qty, @kind, @old, @new, @own, @observer, @certainty,
                  @resolution, @amb, @after, @by, @world)",
        _connection, tx);
      insert.Parameters.AddWithValue("@iid", (long)itemId);
      insert.Parameters.AddWithValue("@hq", ev.IsHq ? 1 : 0);
      insert.Parameters.AddWithValue("@ret", ev.Retainer);
      insert.Parameters.AddWithValue("@qty", ev.Quantity);
      insert.Parameters.AddWithValue("@kind", EventKindTag(ev.Kind));
      insert.Parameters.AddWithValue("@old", (object?)ev.OldPrice ?? DBNull.Value);
      insert.Parameters.AddWithValue("@new", (object?)ev.NewPrice ?? DBNull.Value);
      insert.Parameters.AddWithValue("@own", ev.IsOwn ? 1 : 0);
      insert.Parameters.AddWithValue("@observer", observerTag);
      insert.Parameters.AddWithValue("@certainty", row.Certainty == MarketEvents.Certainty.Confirmed ? "confirmed" : "observed");
      insert.Parameters.AddWithValue("@resolution", ev.Resolution is MarketEvents.DisappearResolution r ? ResolutionTag(r) : (object)DBNull.Value);
      insert.Parameters.AddWithValue("@amb", ev.Ambiguous ? 1 : 0);
      insert.Parameters.AddWithValue("@after", window.SeenAfter);
      insert.Parameters.AddWithValue("@by", window.SeenBy);
      insert.Parameters.AddWithValue("@world", worldId);
      insert.ExecuteNonQuery();
    }

    // Replace the snapshot: the current board IS the new read model.
    using (var del = new SqliteCommand("DELETE FROM market_board_snapshot WHERE item_id = @iid", _connection, tx))
    {
      del.Parameters.AddWithValue("@iid", (long)itemId);
      del.ExecuteNonQuery();
    }
    foreach (var l in currentBoard)
    {
      // Plain INSERT (not OR REPLACE): the DELETE above cleared the item, and twin
      // listings must both survive - a natural-key upsert would drop one.
      using var ins = new SqliteCommand(
        @"INSERT INTO market_board_snapshot
            (item_id, is_hq, retainer_name, quantity, unit_price, is_own, world_id, observer, seen_at)
          VALUES (@iid, @hq, @ret, @qty, @price, @own, @world, @observer, @seen)",
        _connection, tx);
      ins.Parameters.AddWithValue("@iid", (long)itemId);
      ins.Parameters.AddWithValue("@hq", l.IsHq ? 1 : 0);
      ins.Parameters.AddWithValue("@ret", l.Retainer);
      ins.Parameters.AddWithValue("@qty", l.Quantity);
      ins.Parameters.AddWithValue("@price", l.UnitPrice);
      ins.Parameters.AddWithValue("@own", l.IsOwn ? 1 : 0);
      ins.Parameters.AddWithValue("@world", worldId);
      ins.Parameters.AddWithValue("@observer", observerTag);
      ins.Parameters.AddWithValue("@seen", scanAt);
      ins.ExecuteNonQuery();
    }

    tx.Commit();
    return events.Count;
  }

  /// <summary>
  /// Banks one item's MB history window into sale_history (V23) and ring-prunes
  /// what it touched, atomically. Returns how many rows the database actually
  /// took (re-seen entries dedup to zero). SQL lives in SaleHistorySchema —
  /// Dalamud-free and linked-source tested against a real SQLite DB.
  /// </summary>
  internal static int BankSaleHistory(IReadOnlyList<SaleHistorySchema.SaleRow> sales)
    => SaleHistorySchema.BankSales(Connection, sales);

  /// <summary>When this variant last changed hands on the tape, or null - see
  /// <see cref="SaleHistorySchema.NewestSaleTime"/>.</summary>
  internal static long? NewestSaleTime(uint itemId, bool isHq)
    => SaleHistorySchema.NewestSaleTime(Connection, itemId, isHq);

  /// <summary>
  /// The book's experience, counted (the new-vs-experienced grade, ruled 08-22):
  /// banked settled sales and warmed almanac rows. Two COUNT(*)s, called once per
  /// routing batch.
  /// </summary>
  internal static (long BankedSales, long AlmanacAnswers) EvidenceCounts()
  {
    long Count(string sql)
    {
      using var cmd = new Microsoft.Data.Sqlite.SqliteCommand(sql, Connection);
      return Convert.ToInt64(cmd.ExecuteScalar());
    }
    return (Count("SELECT COUNT(*) FROM sale_history"),
            Count("SELECT COUNT(*) FROM universalis_stats"));
  }

  /// <summary>
  /// Reads one item's banked tape back — both qualities, newest sale first. This is
  /// the lane's evidence source: the packet banks at OnHistoryReceived and the lane
  /// reads the RING, so a slow mover prices off months of accumulated tape instead
  /// of the single ~20-entry window this visit happened to catch. SQL lives in
  /// SaleHistorySchema — Dalamud-free and linked-source tested.
  /// </summary>
  internal static List<SaleHistorySchema.BankedSale> ReadSaleHistory(uint itemId, int limit = SaleHistorySchema.RingKeep)
    => SaleHistorySchema.ReadRing(Connection, itemId, limit);

  /// <summary>
  /// Banks one Universalis community-history round for a DC (V29). Framework
  /// thread only — the fetch worker marshals here. SQL lives in
  /// CommunityHistorySchema.
  /// </summary>
  internal static void UpsertCommunityHistory(string scope,
    IReadOnlyDictionary<uint, CommunityHistorySchema.Row> rounds)
    => CommunityHistorySchema.UpsertRounds(Connection, scope, rounds);

  /// <summary>
  /// Every still-fresh community round banked for a DC, keyed by item — what the
  /// history cache warms itself from at a cold start, so the list score's DC
  /// evidence is present on the first round after a reload instead of trickling
  /// back over the next ten minutes. Rounds the TTL has already made invisible
  /// are dropped on the way past.
  /// </summary>
  internal static Dictionary<uint, CommunityHistorySchema.Row> GetCommunityHistory(
    string scope, long freshAfter)
  {
    CommunityHistorySchema.PruneStale(Connection, freshAfter);
    return CommunityHistorySchema.ReadScope(Connection, scope, freshAfter);
  }
}
