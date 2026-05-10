using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// CRUD layer for desynth_runs and desynth_yields. Mirrors GilStorage's
/// pattern (single shared connection owned by GilStorage). Methods use
/// the borrowed connection directly — they do not dispose it.
/// </summary>
internal sealed class DesynthYieldStore
{
  private readonly SqliteConnection _connection;

  internal DesynthYieldStore(SqliteConnection connection)
  {
    _connection = connection;
  }

  /// <summary>Inserts a new run row. Returns the auto-generated id.</summary>
  internal long StartRun(string mode, int totalItems, DateTimeOffset startedAt)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO desynth_runs (started_at, mode, total_items)
        VALUES (@started, @mode, @total);
        SELECT last_insert_rowid();",
      _connection);
    cmd.Parameters.AddWithValue("@started", startedAt.ToUnixTimeMilliseconds());
    cmd.Parameters.AddWithValue("@mode", mode);
    cmd.Parameters.AddWithValue("@total", totalItems);
    return (long)(cmd.ExecuteScalar() ?? 0L);
  }

  /// <summary>Marks a run as complete. Sets ended_at; aborted_reason left null.</summary>
  internal void EndRun(long runId, DateTimeOffset endedAt)
  {
    using var cmd = new SqliteCommand(
      @"UPDATE desynth_runs
        SET ended_at = @ended
        WHERE id = @id;",
      _connection);
    cmd.Parameters.AddWithValue("@ended", endedAt.ToUnixTimeMilliseconds());
    cmd.Parameters.AddWithValue("@id", runId);
    cmd.ExecuteNonQuery();
  }

  /// <summary>Marks a run as aborted with a reason string.</summary>
  internal void AbortRun(long runId, DateTimeOffset endedAt, string reason)
  {
    using var cmd = new SqliteCommand(
      @"UPDATE desynth_runs
        SET ended_at = @ended, aborted_reason = @reason
        WHERE id = @id;",
      _connection);
    cmd.Parameters.AddWithValue("@ended", endedAt.ToUnixTimeMilliseconds());
    cmd.Parameters.AddWithValue("@reason", reason);
    cmd.Parameters.AddWithValue("@id", runId);
    cmd.ExecuteNonQuery();
  }

  /// <summary>Inserts a yield event. Called by DesynthYieldTracker.</summary>
  internal void InsertYield(DesynthYield yield)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO desynth_yields
          (run_id, attempt_seq, source_item_id, source_is_hq,
           yield_item_id, yield_qty, yield_is_hq, captured_at)
        VALUES
          (@run, @seq, @src, @srchq,
           @yield, @qty, @yieldhq, @captured);",
      _connection);
    cmd.Parameters.AddWithValue("@run", yield.RunId);
    cmd.Parameters.AddWithValue("@seq", yield.AttemptSeq);
    cmd.Parameters.AddWithValue("@src", yield.SourceItemId);
    cmd.Parameters.AddWithValue("@srchq", yield.SourceIsHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@yield", yield.YieldItemId);
    cmd.Parameters.AddWithValue("@qty", yield.YieldQty);
    cmd.Parameters.AddWithValue("@yieldhq", yield.YieldIsHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@captured", yield.CapturedAt.ToUnixTimeMilliseconds());
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Reads recent yield rows, joined with their run's started_at for the
  /// Desynth Yields tab. Sorted captured_at DESC. Paginated by limit/offset.
  /// </summary>
  internal List<DesynthYieldRow> ReadRecent(int limit, int offset)
  {
    var results = new List<DesynthYieldRow>();
    using var cmd = new SqliteCommand(
      @"SELECT y.id, y.run_id, y.attempt_seq,
               y.source_item_id, y.source_is_hq,
               y.yield_item_id, y.yield_qty, y.yield_is_hq,
               y.captured_at, r.started_at
        FROM desynth_yields y
        JOIN desynth_runs r ON r.id = y.run_id
        ORDER BY y.captured_at DESC
        LIMIT @limit OFFSET @offset;",
      _connection);
    cmd.Parameters.AddWithValue("@limit", limit);
    cmd.Parameters.AddWithValue("@offset", offset);

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      results.Add(new DesynthYieldRow
      {
        Id = reader.GetInt64(0),
        RunId = reader.GetInt64(1),
        AttemptSeq = reader.GetInt32(2),
        SourceItemId = (uint)reader.GetInt64(3),
        SourceIsHq = reader.GetInt32(4) != 0,
        YieldItemId = (uint)reader.GetInt64(5),
        YieldQty = reader.GetInt32(6),
        YieldIsHq = reader.GetInt32(7) != 0,
        CapturedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
        RunStartedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
      });
    }
    return results;
  }

  /// <summary>Total yield row count — for the tab's pagination footer.</summary>
  internal long Count()
  {
    using var cmd = new SqliteCommand("SELECT COUNT(*) FROM desynth_yields;", _connection);
    return (long)(cmd.ExecuteScalar() ?? 0L);
  }
}

/// <summary>
/// Joined row used by the Desynth Yields tab — yield + run start time.
/// </summary>
internal sealed class DesynthYieldRow
{
  public long Id { get; init; }
  public long RunId { get; init; }
  public int AttemptSeq { get; init; }
  public uint SourceItemId { get; init; }
  public bool SourceIsHq { get; init; }
  public uint YieldItemId { get; init; }
  public int YieldQty { get; init; }
  public bool YieldIsHq { get; init; }
  public DateTimeOffset CapturedAt { get; init; }
  public DateTimeOffset RunStartedAt { get; init; }
}
