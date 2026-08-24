using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Scrooge;
using Scrooge.Windows;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// V40 (Rounds unit 4): the Round's header row and its banked transcript.
///
/// <para>The receipts here are the rules the durable Round rests on - the DB issues
/// the identity, the checkpoint stamps once, the transcript survives at its own cap,
/// and a new Round's start evicts every tenant that never got to end. The store is
/// exercised against real SQLite for the same reason the migration tests are: the
/// eviction and the cap are both DELETEs, and a DELETE that matches the wrong rows
/// is not a thing a mock can catch.</para>
/// </summary>
public class RoundLogTests
{
  private static SqliteConnection Open()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    // decision_cache first: V40's ALTER lands on it.
    DecisionCacheSchema.ApplyV39(conn);
    RoundLogSchema.ApplyV40(conn);
    return conn;
  }

  private static RoundLogLine Line(string text, string stage = "Recon")
    => new(0, 100, stage, RoundLogKind.Entry, "Reconned", text, $"{text}: would ask 1,890");

  [Fact]
  public void ApplyV40_IsIdempotent()
  {
    using var conn = Open();
    var ex = Record.Exception(() => RoundLogSchema.ApplyV40(conn));
    Assert.Null(ex);
  }

  /// <summary>
  /// The column-guarded ALTER is the whole reason a re-run is safe - SQLite has no
  /// ADD COLUMN IF NOT EXISTS, so a second unguarded run would throw and take the
  /// whole bootstrap with it.
  /// </summary>
  [Fact]
  public void ApplyV40_AddsLaneMedianOnce()
  {
    using var conn = Open();
    RoundLogSchema.ApplyV40(conn);

    using var cmd = new SqliteCommand(
      "SELECT COUNT(*) FROM pragma_table_info('decision_cache') WHERE name = 'lane_median'", conn);
    Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
  }

  [Fact]
  public void StartRun_IssuesTheIdentity_AndLeavesBothEndingsOpen()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);

    var runId = store.StartRun(1000);

    Assert.True(runId > 0);
    var row = store.GetRun(runId);
    Assert.NotNull(row);
    Assert.Equal(1000, row!.Value.StartedAt);
    Assert.Null(row.Value.LookDoneAt);
    Assert.Null(row.Value.EndedAt);
  }

  /// <summary>
  /// The Look time is what the resume line's honesty rests on: a second stamp would
  /// let a resumed round move the instant its reads were taken, so a stale Look could
  /// re-label itself fresh by running one more stage.
  /// </summary>
  [Fact]
  public void StampLookDone_StampsOnce()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);
    var runId = store.StartRun(1000);

    store.StampLookDone(runId, 1200);
    store.StampLookDone(runId, 9999);

    Assert.Equal(1200, store.GetRun(runId)!.Value.LookDoneAt);
  }

  [Fact]
  public void EndRun_StampsOnce()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);
    var runId = store.StartRun(1000);

    store.EndRun(runId, 2000);
    store.EndRun(runId, 5000);

    Assert.Equal(2000, store.GetRun(runId)!.Value.EndedAt);
  }

  // ==========================================================================
  // The transcript: write-through, order, and the round trip a reload makes
  // ==========================================================================

  [Fact]
  public void Append_ThenRead_RoundTripsTheTranscriptInOrder()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);
    var runId = store.StartRun(1000);

    store.Append(runId, new[] { Line("Eikon Iron Ingot"), Line("Palladium Ingot") });
    store.Append(runId, new[] { Line("Grade 8 Tincture", "BellRun") });

    var read = store.Read(runId);
    Assert.Equal(3, read.Count);
    Assert.Equal(new[] { "Eikon Iron Ingot", "Palladium Ingot", "Grade 8 Tincture" },
      read.Select(l => l.ItemName));
    // Seq continues across chapters - a second Append must not restart the ordering
    // and interleave the second stage into the first.
    Assert.Equal(new[] { 1L, 2L, 3L }, read.Select(l => l.Seq));
    // The stage rides each line, which is what makes per-stage durations DERIVABLE
    // rather than a second set of stored columns.
    Assert.Equal("Recon", read[0].Stage);
    Assert.Equal("BellRun", read[2].Stage);
    Assert.Equal(RoundLogKind.Entry, read[0].Kind);
  }

  [Fact]
  public void Append_IgnoresAnEmptyChapterAndAnUnbankedRound()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);
    var runId = store.StartRun(1000);

    store.Append(runId, System.Array.Empty<RoundLogLine>());
    store.Append(0, new[] { Line("Eikon Iron Ingot") });   // no banked run

    Assert.Empty(store.Read(runId));
    Assert.Empty(store.Read(0));
  }

  /// <summary>
  /// THE CAP, on the banked copy. Same ceiling and same direction as the in-memory
  /// carry (<see cref="RunLogCap.MaxEntries"/>): oldest lines go first, so what is
  /// lost is the top of a transcript nobody scrolls to rather than the stage the
  /// player is standing in.
  /// </summary>
  [Fact]
  public void Append_CapsTheBankedCopy_DroppingOldestFirst()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);
    var runId = store.StartRun(1000);

    var overflow = 25;
    for (var i = 0; i < RunLogCap.MaxEntries + overflow; i++)
      store.Append(runId, new[] { Line($"item {i}") });

    var read = store.Read(runId);
    Assert.Equal(RunLogCap.MaxEntries, read.Count);
    Assert.Equal($"item {overflow}", read[0].ItemName);
    Assert.Equal($"item {RunLogCap.MaxEntries + overflow - 1}", read[^1].ItemName);
  }

  /// <summary>An unknown kind keeps its words; only the styling is lost.</summary>
  [Fact]
  public void Read_FallsBackForAKindThisBuildDoesNotKnow()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);
    var runId = store.StartRun(1000);

    using (var insert = new SqliteCommand(
      @"INSERT INTO round_log (run_id, seq, ts, stage, kind, outcome, item_name, detail)
        VALUES (@run, 1, 100, 'Recon', 'SomeFutureShape', '', '', 'the words survive')", conn))
    {
      insert.Parameters.AddWithValue("@run", runId);
      insert.ExecuteNonQuery();
    }

    var read = store.Read(runId);
    Assert.Equal(RoundLogKind.Run, read[0].Kind);
    Assert.Equal("the words survive", read[0].Detail);
  }

  // ==========================================================================
  // The supersede: no immortal tenants (V36 discipline)
  // ==========================================================================

  /// <summary>
  /// THE ONE THIS UNIT OWES A PROOF OF: a round nobody ever resumed dies at the next
  /// start. Its transcript is deleted and its header is stamped ended, so the table
  /// can never accumulate the rounds that died badly - which is every round a crash
  /// or a stale-retirement ever took.
  /// </summary>
  [Fact]
  public void RetireAllExcept_KillsTheNeverResumedRun()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);

    var abandoned = store.StartRun(1000);
    store.Append(abandoned, new[] { Line("Eikon Iron Ingot"), Line("Palladium Ingot") });
    store.StampLookDone(abandoned, 1100);

    var fresh = store.StartRun(2000);
    store.RetireAllExcept(fresh, 2000);

    Assert.Empty(store.Read(abandoned));
    Assert.Equal(2000, store.GetRun(abandoned)!.Value.EndedAt);
    // The new round is untouched by its own eviction.
    Assert.Null(store.GetRun(fresh)!.Value.EndedAt);
  }

  [Fact]
  public void RetireAllExcept_LeavesTheNewRoundsOwnTranscriptAlone()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);

    var old = store.StartRun(1000);
    store.Append(old, new[] { Line("Eikon Iron Ingot") });
    var fresh = store.StartRun(2000);
    store.Append(fresh, new[] { Line("Palladium Ingot") });

    store.RetireAllExcept(fresh, 2000);

    Assert.Empty(store.Read(old));
    Assert.Single(store.Read(fresh));
  }

  /// <summary>
  /// An ALREADY-ended round (the explicit Abandon stamped it) still loses its lines
  /// at the next start. The stamp is not a lease.
  /// </summary>
  [Fact]
  public void RetireAllExcept_EvictsAnAlreadyEndedRoundsLines()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);

    var abandoned = store.StartRun(1000);
    store.Append(abandoned, new[] { Line("Eikon Iron Ingot") });
    store.EndRun(abandoned, 1500);

    var fresh = store.StartRun(2000);
    store.RetireAllExcept(fresh, 2000);

    Assert.Empty(store.Read(abandoned));
    // Its original ending stands - the eviction does not rewrite history it found.
    Assert.Equal(1500, store.GetRun(abandoned)!.Value.EndedAt);
  }

  /// <summary>
  /// Three dead rounds, one start: ALL of them go. Keying the eviction on "everything
  /// that is not the new run" rather than on the id the config remembers is what
  /// makes that true - a config can only ever name one predecessor, and a crash names
  /// none.
  /// </summary>
  [Fact]
  public void RetireAllExcept_EvictsEveryTenant()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);

    var dead = new List<long>();
    for (var i = 0; i < 3; i++)
    {
      var id = store.StartRun(1000 + i);
      store.Append(id, new[] { Line($"round {i}") });
      dead.Add(id);
    }

    var fresh = store.StartRun(9000);
    store.RetireAllExcept(fresh, 9000);

    foreach (var id in dead)
    {
      Assert.Empty(store.Read(id));
      Assert.Equal(9000, store.GetRun(id)!.Value.EndedAt);
    }
  }

  /// <summary>
  /// The decision cache is NOT collateral. Its rows are keyed by item, superseded by
  /// the next recon and aged by the freshness rule - the cache outlives the round by
  /// design, and it is the one thing a superseded round leaves behind that the next
  /// one wants.
  /// </summary>
  [Fact]
  public void RetireAllExcept_LeavesTheDecisionCacheStanding()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);
    var old = store.StartRun(1000);

    using (var bank = new SqliteCommand(
      @"INSERT INTO decision_cache
          (item_id, is_hq, decided_price, outcome, evidence, receipt_id, run_id, banked_at, lane_median)
        VALUES (5057, 0, 1890, 'Undercut', 'the line', 42, @run, 1050, 1900.0)", conn))
    {
      bank.Parameters.AddWithValue("@run", old);
      bank.ExecuteNonQuery();
    }

    store.RetireAllExcept(store.StartRun(2000), 2000);

    using var count = new SqliteCommand("SELECT COUNT(*) FROM decision_cache", conn);
    Assert.Equal(1L, (long)count.ExecuteScalar()!);
  }

  /// <summary>
  /// THE RE-LOOK UN-STAMPS IT (review ruling S19). The one-shot guard is what makes
  /// the latch-clear alone insufficient: without wiping the column, the fresh Look's
  /// stamp is refused and the hinge quotes a read the player has just declared out of
  /// date. Clear, and the column takes the new time.
  /// </summary>
  [Fact]
  public void ClearLookDone_ReArmsTheStamp()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);
    var runId = store.StartRun(1000);

    store.StampLookDone(runId, 1200);
    store.ClearLookDone(runId);

    // Between the two, the hinge has NO look time to show - which is the point: it
    // says nothing rather than saying something stale.
    Assert.Null(store.GetRun(runId)!.Value.LookDoneAt);

    store.StampLookDone(runId, 5000);
    Assert.Equal(5000, store.GetRun(runId)!.Value.LookDoneAt);
  }

  /// <summary>A Re-Look touches one Round's checkpoint, never the table's.</summary>
  [Fact]
  public void ClearLookDone_LeavesOtherRunsAlone()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);
    var mine = store.StartRun(1000);
    var other = store.StartRun(2000);
    store.StampLookDone(mine, 1200);
    store.StampLookDone(other, 2200);

    store.ClearLookDone(mine);

    Assert.Null(store.GetRun(mine)!.Value.LookDoneAt);
    Assert.Equal(2200, store.GetRun(other)!.Value.LookDoneAt);
  }

  // ==========================================================================
  // Retention (the minors batch, 2026-08-12): the two tables with no upper bound
  // ==========================================================================

  /// <summary>
  /// The supersede bounds the transcript and stamps the headers; nothing bounded the
  /// HEADERS, so one row accumulated per round forever. They age out instead of dying
  /// with their transcripts, because the three timestamps on them are the whole basis
  /// of the banked "how long does a round take" use case (S12).
  /// </summary>
  [Fact]
  public void PruneOldRuns_DropsAgedHeadersAndKeepsTheSeason()
  {
    using var conn = Open();
    var store = new RoundLogStore(conn);
    var ancient = store.StartRun(1_000);
    var recent = store.StartRun(9_000);
    store.Append(ancient, new List<RoundLogLine> { Line("old news") });
    store.Append(recent, new List<RoundLogLine> { Line("tonight") });

    Assert.Equal(1, RoundLogSchema.PruneOldRuns(conn, cutoff: 5_000));

    Assert.Null(store.GetRun(ancient));
    Assert.NotNull(store.GetRun(recent));
    // The dropped header takes its rows with it - no orphan pointing at a run id
    // nothing can name.
    Assert.Empty(store.Read(ancient));
    Assert.Single(store.Read(recent));
  }

  /// <summary>
  /// The decision cache upserts per (item, quality), so it cannot grow within a night
  /// and never shrinks across them. Retention is months, not the freshness window: the
  /// freshness rule owns what recon walks, this owns what the table costs.
  /// </summary>
  [Fact]
  public void PruneStale_DropsAgedDecisionsOnly()
  {
    using var conn = Open();
    Bank(conn, itemId: 100, bankedAt: 1_000);
    Bank(conn, itemId: 200, bankedAt: 9_000);

    Assert.Equal(1, DecisionCacheSchema.PruneStale(conn, cutoff: 5_000));

    using var cmd = new SqliteCommand("SELECT item_id FROM decision_cache", conn);
    using var reader = cmd.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal(200L, reader.GetInt64(0));
    Assert.False(reader.Read());
  }

  private static void Bank(SqliteConnection conn, long itemId, long bankedAt)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO decision_cache (item_id, is_hq, decided_price, outcome, evidence, run_id, banked_at)
        VALUES (@id, 0, 1890, 'Undercut', 'evidence', 1, @at)", conn);
    cmd.Parameters.AddWithValue("@id", itemId);
    cmd.Parameters.AddWithValue("@at", bankedAt);
    cmd.ExecuteNonQuery();
  }

  // ==========================================================================
  // S12: the row's stamp is the row's own write time
  // ==========================================================================

  /// <summary>
  /// THE DURATIONS DERIVE, OR THEY DO NOT (review ruling S12). The bank used to read
  /// one clock per CHAPTER and stamp every row in it with that instant, so max-minus-min
  /// over a stage was structurally zero and the "per-stage durations derive from
  /// round_log" half of the option-B ruling could never be true. The stamp belongs to
  /// the row, set when the row is written.
  /// </summary>
  [Fact]
  public void Flatten_StampsEachRowWithItsOwnWriteTime()
  {
    var opened = DateTimeOffset.FromUnixTimeSeconds(1_000);
    var closed = DateTimeOffset.FromUnixTimeSeconds(1_240);

    var first = RoundLogEntry.Flatten(
      new LogEntry(ItemOutcome.Reconned, "Karen", "Grade 4 Glaze", "would ask 1,890")
      { WrittenAt = opened }, "Recon");
    var last = RoundLogEntry.Flatten(
      new LogEntry(ItemOutcome.Reconned, "Karen", "Titanium Ore", "would ask 240")
      { WrittenAt = closed }, "Recon");

    Assert.Equal(1_000, first!.Value.Ts);
    Assert.Equal(1_240, last!.Value.Ts);
    // The whole point, stated as the arithmetic the 3.1 ETA will run.
    Assert.Equal(240, last.Value.Ts - first.Value.Ts);
  }

  /// <summary>
  /// A rehydrated row keeps the stamp the bank holds. Nothing re-banks it today, but a
  /// transcript whose restored half claims to have been written at reload time lies
  /// about the one column the durations come from.
  /// </summary>
  [Fact]
  public void Restore_KeepsTheBankedStamp()
  {
    var banked = new RoundLogLine(1, 4_242, "Recon", RoundLogKind.Entry,
      "Reconned", "Grade 4 Glaze", "would ask 1,890");

    var row = RoundLogEntry.Restore(banked);

    Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4_242), row.WrittenAt);
    // Seq is the STORE's, never the caller's - a flattened row asks for 0 and is told
    // its place on the way in. Everything else about the line survives the trip.
    Assert.Equal(banked with { Seq = 0 }, RoundLogEntry.Flatten(row, "Recon"));
  }

  /// <summary>
  /// Every row shape survives the round trip, stamp included - the transcript is a
  /// thing people read, and a shape lost at the reload is a line that changes meaning.
  /// </summary>
  [Fact]
  public void FlattenRestore_RoundTripsEveryRowShape()
  {
    var at = DateTimeOffset.FromUnixTimeSeconds(7_000);
    ILogItem[] rows =
    [
      new LogEntry(ItemOutcome.PostedFromRecon, "", "Glaze", "posted from recon") { WrittenAt = at },
      new RunEntry(RunEvent.Start, "Recon Run Started") { WrittenAt = at },
      new RetainerHeader("Karen") { WrittenAt = at },
      new YieldEntry("Titanium Ore", 3, true, 480) { WrittenAt = at },
    ];

    foreach (var row in rows)
    {
      var line = RoundLogEntry.Flatten(row, "Desynth");
      Assert.NotNull(line);
      Assert.Equal(row, RoundLogEntry.Restore(line.Value));
    }
  }
}
