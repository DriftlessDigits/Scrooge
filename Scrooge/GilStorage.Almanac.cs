using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// The lookaside: <c>universalis_stats</c> and <c>decision_cache</c>.
///
/// <para>Both tables exist to keep a round from re-asking a question it already paid
/// for — the first caches what the market API said, the second caches what we decided
/// off it. Everything here is disposable by definition: reads are freshness-gated and
/// stale rows are pruned rather than repaired.</para>
/// </summary>
internal static partial class GilStorage
{
  /// <summary>
  /// Upserts one Universalis fetch round for a world (V16). Framework thread
  /// only — callers marshal here from the fetch worker.
  /// </summary>
  internal static void UpsertUniversalisStats(uint worldId, List<UniversalisStat> stats, long fetchedAt)
  {
    using var tx = Connection.BeginTransaction();
    using var cmd = new SqliteCommand(
      @"INSERT INTO universalis_stats (item_id, world_id, nq_velocity, hq_velocity, last_sale_at, last_upload_at, fetched_at)
      VALUES (@item, @world, @nq, @hq, @sale, @upload, @fetched)
      ON CONFLICT (item_id, world_id) DO UPDATE SET
        nq_velocity = @nq, hq_velocity = @hq, last_sale_at = @sale,
        last_upload_at = @upload, fetched_at = @fetched",
      _connection, tx);
    var pItem = cmd.Parameters.Add("@item", Microsoft.Data.Sqlite.SqliteType.Integer);
    var pWorld = cmd.Parameters.Add("@world", Microsoft.Data.Sqlite.SqliteType.Integer);
    var pNq = cmd.Parameters.Add("@nq", Microsoft.Data.Sqlite.SqliteType.Real);
    var pHq = cmd.Parameters.Add("@hq", Microsoft.Data.Sqlite.SqliteType.Real);
    var pSale = cmd.Parameters.Add("@sale", Microsoft.Data.Sqlite.SqliteType.Integer);
    var pUpload = cmd.Parameters.Add("@upload", Microsoft.Data.Sqlite.SqliteType.Integer);
    var pFetched = cmd.Parameters.Add("@fetched", Microsoft.Data.Sqlite.SqliteType.Integer);

    pWorld.Value = worldId;
    pFetched.Value = fetchedAt;
    foreach (var stat in stats)
    {
      pItem.Value = stat.ItemId;
      pNq.Value = stat.NqVelocity;
      pHq.Value = stat.HqVelocity;
      pSale.Value = (object?)stat.LastSaleAt ?? DBNull.Value;
      pUpload.Value = (object?)stat.LastUploadAt ?? DBNull.Value;
      cmd.ExecuteNonQuery();
    }
    tx.Commit();
  }

  /// <summary>All cached Universalis rows for one world, keyed by item id.</summary>
  internal static Dictionary<uint, (UniversalisStat Stat, long FetchedAt)> GetUniversalisStats(uint worldId)
  {
    var rows = new Dictionary<uint, (UniversalisStat, long)>();
    using var cmd = new SqliteCommand(
      @"SELECT item_id, nq_velocity, hq_velocity, last_sale_at, last_upload_at, fetched_at
        FROM universalis_stats WHERE world_id = @world",
      _connection);
    cmd.Parameters.AddWithValue("@world", worldId);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      var itemId = (uint)reader.GetInt64(0);
      rows[itemId] = (new UniversalisStat(
        itemId,
        reader.GetDouble(1),
        reader.GetDouble(2),
        reader.IsDBNull(3) ? null : reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4)),
        reader.GetInt64(5));
    }
    return rows;
  }

  // ==========================================================================
  // Decision cache (V39) - what recon banked, and how long ago
  // ==========================================================================

  /// <summary>
  /// Banks (or re-banks) recon's answer for one item variant. The later recon
  /// SUPERSEDES the earlier one - there is deliberately no history here (the
  /// decision receipts are the history, with their own coordinates and their own
  /// grading pass). This table answers exactly one question, "what does the act
  /// half do with this item tonight", and a second row for the same variant could
  /// only make that question ambiguous.
  ///
  /// <para><paramref name="decidedPrice"/> rides null for a HELD decision, which is
  /// an answer and not an absence - see <see cref="DecisionCacheRow"/>.</para>
  ///
  /// <para><b>lane_median is no longer written (doctrine sweep, 2026-08-15).</b> The
  /// V40 column existed to carry the receipt ratio's denominator across the
  /// recon-to-cached-post boundary; the ratio's writer is retired, so the column is
  /// left at its default and dropped from this statement. The column itself stays -
  /// migrations are history and the rows that have it keep it.</para>
  /// </summary>
  internal static void UpsertDecisionCache(uint itemId, bool isHq, long? decidedPrice,
      string outcome, string evidence, long? receiptId, long runId, long bankedAt)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO decision_cache
          (item_id, is_hq, decided_price, outcome, evidence, receipt_id, run_id, banked_at)
        VALUES (@iid, @hq, @price, @outcome, @evidence, @receipt, @run, @at)
        ON CONFLICT(item_id, is_hq) DO UPDATE SET
          decided_price = @price, outcome = @outcome, evidence = @evidence,
          receipt_id = @receipt, run_id = @run, banked_at = @at",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@price", (object?)decidedPrice ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@outcome", outcome);
    cmd.Parameters.AddWithValue("@evidence", evidence);
    cmd.Parameters.AddWithValue("@receipt", (object?)receiptId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@run", runId);
    cmd.Parameters.AddWithValue("@at", bankedAt);
    cmd.ExecuteNonQuery();
  }

  /// <summary>One variant's banked decision, or null when nobody ever looked.</summary>
  internal static DecisionCacheRow? GetDecisionCache(uint itemId, bool isHq)
  {
    using var cmd = new SqliteCommand(
      @"SELECT decided_price, outcome, evidence, receipt_id, run_id, banked_at
        FROM decision_cache WHERE item_id = @iid AND is_hq = @hq",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return null;
    return new DecisionCacheRow(itemId, isHq,
      reader.IsDBNull(0) ? null : reader.GetInt64(0),
      reader.GetString(1), reader.GetString(2),
      reader.IsDBNull(3) ? null : reader.GetInt64(3),
      reader.GetInt64(4), reader.GetInt64(5));
  }

  /// <summary>
  /// Every row banked at or after <paramref name="cutoff"/>, keyed by variant -
  /// the act half's price source (unit 3's cached post reads exactly this set, and
  /// a row that falls out of it pays the classic ComparePrice chain instead).
  /// The cutoff is the caller's, from <see cref="ReconFreshness.Cutoff"/>, so the
  /// freshness rule lives in ONE pure place rather than half in SQL.
  /// </summary>
  internal static Dictionary<(uint ItemId, bool IsHq), DecisionCacheRow> GetFreshDecisionCache(long cutoff)
  {
    var rows = new Dictionary<(uint, bool), DecisionCacheRow>();
    using var cmd = new SqliteCommand(
      @"SELECT item_id, is_hq, decided_price, outcome, evidence, receipt_id, run_id, banked_at
        FROM decision_cache WHERE banked_at >= @cutoff",
      _connection);
    cmd.Parameters.AddWithValue("@cutoff", cutoff);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      var itemId = (uint)reader.GetInt64(0);
      var isHq = reader.GetInt32(1) != 0;
      rows[(itemId, isHq)] = new DecisionCacheRow(itemId, isHq,
        reader.IsDBNull(2) ? null : reader.GetInt64(2),
        reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt64(5),
        reader.GetInt64(6), reader.GetInt64(7));
    }
    return rows;
  }

  /// <summary>
  /// How many of THIS run's banked decisions are still fresh - the resume line's
  /// first count.
  ///
  /// <para>Scoped to the run on purpose. The cache is global and outlives every
  /// round in it, so a global count would tell a resuming player about decisions
  /// some other night bought. The number he is being offered is what tonight's Look
  /// half paid for, and nothing else.</para>
  ///
  /// <para>Read on the ledger's refresh schedule, never per frame.</para>
  /// </summary>
  internal static int CountFreshDecisionCacheForRun(long runId, long cutoff)
  {
    if (runId <= 0) return 0;
    using var cmd = new SqliteCommand(
      "SELECT COUNT(*) FROM decision_cache WHERE run_id = @run AND banked_at >= @cutoff",
      _connection);
    cmd.Parameters.AddWithValue("@run", runId);
    cmd.Parameters.AddWithValue("@cutoff", cutoff);
    return Convert.ToInt32(cmd.ExecuteScalar());
  }

  /// <summary>
  /// Every variant's banked_at, keyed - the cheapest possible operand for the
  /// staleness filter, which needs one timestamp per row and nothing else.
  ///
  /// <para>Read ONCE per Ledger refresh and cached, never per frame: the recon
  /// count is recomputed every draw from this dictionary plus the already-cached
  /// listable scan, so the arithmetic is in memory and the SQLite hit is on the
  /// same schedule as every other cached read on that surface.</para>
  /// </summary>
  internal static Dictionary<(uint ItemId, bool IsHq), long> GetDecisionCacheBankTimes()
  {
    var rows = new Dictionary<(uint, bool), long>();
    using var cmd = new SqliteCommand("SELECT item_id, is_hq, banked_at FROM decision_cache", _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      rows[((uint)reader.GetInt64(0), reader.GetInt32(1) != 0)] = reader.GetInt64(2);
    return rows;
  }
}
