using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// WHAT SHAPE OF LINE this is, so a banked transcript can be rendered back as the
/// thing it was rather than as a wall of prose.
///
/// <para>The run log draws four different rows - an item's outcome, a run-level
/// marker, a retainer section header, a desynth yield sub-row - and each one reads
/// differently on screen (colour, indent, glyph). <c>outcome</c> cannot carry that
/// distinction because it means a different enum in each case (an
/// <c>ItemOutcome</c> here, a <c>RunEvent</c> there, nothing at all for the other
/// two). So the kind is its own column: without it, a rehydrated transcript could
/// only be rendered as one flat type, and the reload would silently cost the
/// reader every visual cue the live log has.</para>
/// </summary>
internal enum RoundLogKind
{
  /// <summary>One item's outcome line - the commonest row by far.</summary>
  Entry,
  /// <summary>A run-level marker or summary line (start, complete, tallies).</summary>
  Run,
  /// <summary>A retainer section header.</summary>
  RetainerHeader,
  /// <summary>A desynth yield sub-row, rendered indented under its item.</summary>
  Yield,
}

/// <summary>
/// ONE BANKED TRANSCRIPT LINE. The storage-side shape of whatever the run log drew,
/// flattened to columns and stamped with the stage it belongs to.
///
/// <para><see cref="Seq"/> is the ordering and it is the STORE's, not the caller's:
/// a transcript is read back in the order it was written and nothing else about it
/// is ordered (two lines can share a second). <see cref="Stage"/> is the round stage
/// the line came from, which is what makes per-stage durations DERIVABLE - first and
/// last timestamp of a stage's lines - rather than a second set of columns that
/// could disagree with the transcript they summarise.</para>
/// </summary>
internal readonly record struct RoundLogLine(
  long Seq,
  long Ts,
  string Stage,
  RoundLogKind Kind,
  string Outcome,
  string ItemName,
  string Detail);

/// <summary>
/// A BANKED ROUND'S HEADER: when it started, when its Look half finished, and when
/// it ended.
///
/// <para>The durations are the point ("how long did the entire run take"), and they
/// are why this table exists beside the transcript rather than being read off it:
/// the transcript is CAPPED, so on a long round the oldest lines - including the
/// start marker - are dropped, and a start time derived from surviving lines would
/// quietly get later the longer the round ran. The header outlives the cap.</para>
///
/// <para><see cref="LookDoneAt"/> and <see cref="EndedAt"/> are nullable because
/// both are genuinely unknown until they happen: a round holding at the hinge has
/// no end, and a round still pinching has no Look time. Zero would be a time.</para>
/// </summary>
internal readonly record struct RoundRunRow(long Id, long StartedAt, long? LookDoneAt, long? EndedAt);

/// <summary>
/// V40: the Round's header table, its banked transcript, and the one column the
/// cached post's receipt true-up was missing.
///
/// <para><b>round_runs</b> is the DB-issued run identity - a Round's start INSERTs
/// the row and the rowid it gets back is what the persisted state, the transcript
/// and the decision-cache rows all carry. Before this, the identity was the round's
/// start timestamp (unit 2's interim bridge), which worked and was never a KEY: two
/// surfaces could each derive it and disagree, and nothing could ever join to it.
/// The desynth_runs precedent (V9) is followed deliberately - a run row with
/// started_at / ended_at, children pointing at its id.</para>
///
/// <para><b>round_log</b> is the transcript. The run log has always carried a
/// Round's stages as one book (see <see cref="RunLogCarry{T}"/>) and that book has
/// always died with the session; this is its home. The 4000-line cap follows it
/// here, applied as a delete-oldest at write time, so a Round held across many
/// resumes cannot grow the table without bound.</para>
///
/// <para><b>decision_cache.lane_median</b> rode along because it belonged to the
/// same act: a cached post writes a real listing against a receipt written during
/// recon, and the true-up that fixed that receipt recomputed position_in_lane from
/// the lane median the decision was made against. <b>Its writer was retired by the
/// doctrine sweep on 2026-08-15</b> - position_in_lane is a price ratio nothing read,
/// so the median had nothing left to feed. The ALTER stays because migrations are
/// history and re-running V40 on an old DB must still produce the schema those rows
/// were written under; nothing writes the column now, and new rows sit at its
/// default 0.</para>
///
/// <para>Diffable + idempotent: every CREATE is IF NOT EXISTS and the ALTER is
/// column-guarded, so a re-run is a no-op. Dalamud-free (the
/// <see cref="DecisionCacheSchema"/> mold), linked into Scrooge.Tests.</para>
/// </summary>
internal static class RoundLogSchema
{
  internal static void ApplyV40(SqliteConnection connection)
  {
    using (var runs = new SqliteCommand(@"
      CREATE TABLE IF NOT EXISTS round_runs (
        id           INTEGER PRIMARY KEY AUTOINCREMENT,
        started_at   INTEGER NOT NULL,
        look_done_at INTEGER,               -- the checkpoint: reads done, nothing spent
        ended_at     INTEGER                -- completed, abandoned, or superseded
      );", connection))
      runs.ExecuteNonQuery();

    using (var log = new SqliteCommand(@"
      CREATE TABLE IF NOT EXISTS round_log (
        run_id    INTEGER NOT NULL,
        seq       INTEGER NOT NULL,
        ts        INTEGER NOT NULL,
        stage     TEXT    NOT NULL,         -- RoundStage name: per-stage durations DERIVE from these
        kind      TEXT    NOT NULL,         -- RoundLogKind name - which row shape to draw back
        outcome   TEXT    NOT NULL DEFAULT '',
        item_name TEXT    NOT NULL DEFAULT '',
        detail    TEXT    NOT NULL DEFAULT '',
        PRIMARY KEY (run_id, seq),
        FOREIGN KEY (run_id) REFERENCES round_runs(id)
      );", connection))
      log.ExecuteNonQuery();

    // The only read path is "this run's whole transcript, in order", and the only
    // write path appends to the end of it. The primary key already serves both.

    var hasMedian = false;
    using (var check = new SqliteCommand("PRAGMA table_info(decision_cache);", connection))
    using (var reader = check.ExecuteReader())
      while (reader.Read())
        if (reader.GetString(1) == "lane_median") { hasMedian = true; break; }

    // The column-add stays; its WRITER is retired (doctrine sweep, 2026-08-15). See
    // the class remarks - a migration is a record of what the schema became, not a
    // statement that anything still fills the column.
    if (!hasMedian)
      using (var alter = new SqliteCommand(
        "ALTER TABLE decision_cache ADD COLUMN lane_median REAL NOT NULL DEFAULT 0;", connection))
        alter.ExecuteNonQuery();
  }

  /// <summary>
  /// THE HEADER TABLE'S RETENTION (the minors batch, 2026-08-12). The supersede
  /// (<see cref="RoundLogStore.RetireAllExcept"/>) deletes every superseded round's
  /// TRANSCRIPT and stamps its header ended - so the log rows are bounded, and the
  /// round_runs rows are not: one row accumulates per round, forever.
  ///
  /// <para><b>Aged out rather than deleted with the transcript, deliberately.</b> The
  /// obvious fix is for the supersede to drop the old headers too, and it is the wrong
  /// one: the header is exactly the three timestamps the S12 ruling banked a use case
  /// on ("avg round time per item, so the deck can say whether we finish before the
  /// retainers return"). A supersede that deleted them would leave that feature with a
  /// sample size of one - the round underway. Retention keeps a season of history and
  /// still bounds the table, which is what every other prune in this plugin does.</para>
  ///
  /// <para>Log rows belonging to a dropped header go with it. Under the supersede rule
  /// there are none - a header this old was retired long ago - but a row pointing at a
  /// header that no longer exists is an orphan the FK was written to forbid, and the
  /// cheap way to never have one is to not leave one.</para>
  /// </summary>
  internal static int PruneOldRuns(SqliteConnection connection, long cutoff)
  {
    using (var orphans = new SqliteCommand(
      @"DELETE FROM round_log WHERE run_id IN
          (SELECT id FROM round_runs WHERE started_at < @cutoff)", connection))
    {
      orphans.Parameters.AddWithValue("@cutoff", cutoff);
      orphans.ExecuteNonQuery();
    }

    using var runs = new SqliteCommand(
      "DELETE FROM round_runs WHERE started_at < @cutoff", connection);
    runs.Parameters.AddWithValue("@cutoff", cutoff);
    return runs.ExecuteNonQuery();
  }
}

/// <summary>
/// CRUD for round_runs and round_log. Borrowed connection, never disposed - the
/// <see cref="DesynthYieldStore"/> mold, and Dalamud-free for the same reason: the
/// supersede rule and the cap are the two things in this unit most worth proving
/// against real SQL rather than a mock.
/// </summary>
internal sealed class RoundLogStore
{
  private readonly SqliteConnection _connection;

  internal RoundLogStore(SqliteConnection connection) => _connection = connection;

  /// <summary>A Round begins: the row is the identity everything else joins to.</summary>
  internal long StartRun(long startedAt)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO round_runs (started_at) VALUES (@started);
        SELECT last_insert_rowid();", _connection);
    cmd.Parameters.AddWithValue("@started", startedAt);
    return (long)(cmd.ExecuteScalar() ?? 0L);
  }

  /// <summary>
  /// The Look half finished - the checkpoint the whole unit is built around. Stamped
  /// ONCE: a resumed round that re-runs a stage must not move the time its reads were
  /// taken, because that time is exactly what the resume line's honesty rests on.
  /// </summary>
  internal void StampLookDone(long runId, long at)
  {
    using var cmd = new SqliteCommand(
      "UPDATE round_runs SET look_done_at = @at WHERE id = @id AND look_done_at IS NULL",
      _connection);
    cmd.Parameters.AddWithValue("@at", at);
    cmd.Parameters.AddWithValue("@id", runId);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// THE RE-LOOK UN-STAMPS THE CHECKPOINT (review ruling S19). The stamp is one-shot
  /// by design - a resumed round must not move the time its reads were taken - but a
  /// Re-Look is the player saying those reads no longer stand. Leaving the old time in
  /// place made the hinge quote a Look from before the verb he just pressed, which is
  /// the label lying about the button. Clearing re-arms
  /// <see cref="StampLookDone"/>'s own IS NULL guard, so the fresh Look stamps once
  /// when it completes and the hinge shows nothing at all in between.
  /// </summary>
  internal void ClearLookDone(long runId)
  {
    using var cmd = new SqliteCommand(
      "UPDATE round_runs SET look_done_at = NULL WHERE id = @id", _connection);
    cmd.Parameters.AddWithValue("@id", runId);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// The Round is over - completed, abandoned, or superseded; the table does not
  /// distinguish, because the duration question does not. Stamped once for the same
  /// reason the Look time is.
  /// </summary>
  internal void EndRun(long runId, long endedAt)
  {
    using var cmd = new SqliteCommand(
      "UPDATE round_runs SET ended_at = @at WHERE id = @id AND ended_at IS NULL",
      _connection);
    cmd.Parameters.AddWithValue("@at", endedAt);
    cmd.Parameters.AddWithValue("@id", runId);
    cmd.ExecuteNonQuery();
  }

  internal RoundRunRow? GetRun(long runId)
  {
    using var cmd = new SqliteCommand(
      "SELECT id, started_at, look_done_at, ended_at FROM round_runs WHERE id = @id", _connection);
    cmd.Parameters.AddWithValue("@id", runId);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return null;
    return new RoundRunRow(reader.GetInt64(0), reader.GetInt64(1),
      reader.IsDBNull(2) ? null : reader.GetInt64(2),
      reader.IsDBNull(3) ? null : reader.GetInt64(3));
  }

  /// <summary>
  /// THE SUPERSEDE (V36 discipline: no immortal tenants). A new Round's start retires
  /// every other banked run - transcript deleted, header stamped ended.
  ///
  /// <para>Keyed on "everything that is not the new run" rather than on the run id the
  /// config happens to remember, and that is the whole point. A round abandoned by a
  /// crash never got to write its own ending, and a round whose persisted state was
  /// retired as stale left its rows with nobody to name them. Asking the config which
  /// tenant to evict would evict exactly the tenants that still had a lease; asking
  /// the table evicts the ones nobody can name any more, which is all of them.</para>
  ///
  /// <para>Decision-cache rows are deliberately NOT touched. They are keyed by item,
  /// superseded by the next recon and aged by the freshness rule - the cache outlives
  /// the round by design, and it is the one thing a superseded round leaves behind
  /// that the next one wants. (Its own unbounded half is answered by
  /// <see cref="DecisionCacheSchema.PruneStale"/> on the startup sweep, not here.)</para>
  ///
  /// <para>The retired HEADER rows stay too, stamped rather than deleted - they are the
  /// only record of how long past rounds took. They age out on the startup sweep; see
  /// <see cref="RoundLogSchema.PruneOldRuns"/>.</para>
  /// </summary>
  internal int RetireAllExcept(long keepRunId, long endedAt)
  {
    using (var wipe = new SqliteCommand("DELETE FROM round_log WHERE run_id <> @keep", _connection))
    {
      wipe.Parameters.AddWithValue("@keep", keepRunId);
      wipe.ExecuteNonQuery();
    }

    using var stamp = new SqliteCommand(
      "UPDATE round_runs SET ended_at = @at WHERE ended_at IS NULL AND id <> @keep", _connection);
    stamp.Parameters.AddWithValue("@at", endedAt);
    stamp.Parameters.AddWithValue("@keep", keepRunId);
    return stamp.ExecuteNonQuery();
  }

  /// <summary>
  /// Appends a finished stage's lines to the transcript, then enforces the cap by
  /// dropping the OLDEST rows - the same rule and the same direction the in-memory
  /// carry uses, so the banked copy and the live one can never be capped differently.
  ///
  /// <para>One transaction: a half-written chapter is worse than an unwritten one,
  /// because the reader cannot tell which it is looking at.</para>
  /// </summary>
  internal void Append(long runId, IReadOnlyList<RoundLogLine> lines)
  {
    if (runId <= 0 || lines.Count == 0) return;

    using var tx = _connection.BeginTransaction();

    long seq;
    using (var next = new SqliteCommand(
      "SELECT COALESCE(MAX(seq), 0) FROM round_log WHERE run_id = @run", _connection, tx))
    {
      next.Parameters.AddWithValue("@run", runId);
      seq = Convert.ToInt64(next.ExecuteScalar());
    }

    foreach (var line in lines)
    {
      using var insert = new SqliteCommand(
        @"INSERT INTO round_log (run_id, seq, ts, stage, kind, outcome, item_name, detail)
          VALUES (@run, @seq, @ts, @stage, @kind, @outcome, @name, @detail)",
        _connection, tx);
      insert.Parameters.AddWithValue("@run", runId);
      insert.Parameters.AddWithValue("@seq", ++seq);
      insert.Parameters.AddWithValue("@ts", line.Ts);
      insert.Parameters.AddWithValue("@stage", line.Stage);
      insert.Parameters.AddWithValue("@kind", line.Kind.ToString());
      insert.Parameters.AddWithValue("@outcome", line.Outcome);
      insert.Parameters.AddWithValue("@name", line.ItemName);
      insert.Parameters.AddWithValue("@detail", line.Detail);
      insert.ExecuteNonQuery();
    }

    using (var trim = new SqliteCommand(
      @"DELETE FROM round_log
        WHERE run_id = @run AND seq <= @cutoff", _connection, tx))
    {
      trim.Parameters.AddWithValue("@run", runId);
      trim.Parameters.AddWithValue("@cutoff", seq - RunLogCap.MaxEntries);
      trim.ExecuteNonQuery();
    }

    tx.Commit();
  }

  /// <summary>
  /// The newest run with banked transcript rows, or 0. The table only ever holds the
  /// active round and/or the last ended one (<see cref="RetireAllExcept"/> wipes the
  /// rest at each launch), so "newest" IS "the most recent round" - which is what the
  /// ended-book restore wants (ruled 08-15: "I might want to read the most recent one
  /// if I closed it by mistake"; two rounds ago is nobody's question).
  /// </summary>
  internal long LatestBankedRunId()
  {
    using var cmd = new SqliteCommand(
      "SELECT COALESCE(MAX(run_id), 0) FROM round_log", _connection);
    return Convert.ToInt64(cmd.ExecuteScalar());
  }

  /// <summary>The banked transcript, oldest first - what a reload reads back.</summary>
  internal List<RoundLogLine> Read(long runId)
  {
    var lines = new List<RoundLogLine>();
    if (runId <= 0) return lines;

    using var cmd = new SqliteCommand(
      @"SELECT seq, ts, stage, kind, outcome, item_name, detail
        FROM round_log WHERE run_id = @run ORDER BY seq", _connection);
    cmd.Parameters.AddWithValue("@run", runId);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      // An unknown kind is a row written by a version that knew a shape this one
      // does not. Reading it as a plain run-level line keeps the sentence and loses
      // only the styling - the transcript is for reading, and refusing to render a
      // line nobody can draw would lose the words too.
      var kind = Enum.TryParse<RoundLogKind>(reader.GetString(3), out var k) ? k : RoundLogKind.Run;
      lines.Add(new RoundLogLine(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
        kind, reader.GetString(4), reader.GetString(5), reader.GetString(6)));
    }
    return lines;
  }
}

/// <summary>
/// THE LOOK HALF'S CHECKPOINT, and the sentence a held Round says when the player
/// comes back to it.
///
/// <para>Pure and Dalamud-free: the caller supplies the marks, the work answers and
/// the clock. Both halves are here because they are one idea - the checkpoint is
/// what makes the sentence sayable.</para>
/// </summary>
internal static class RoundResume
{
  /// <summary>
  /// The reads. Pinch and Recon are the same errand at the same stop (see
  /// <see cref="RoundPlan.Order"/>), and they are the only two stages that spend
  /// nothing - which is the whole definition of the Look half.
  /// </summary>
  internal static readonly RoundStage[] LookStages = { RoundStage.Pinch, RoundStage.Recon };

  /// <summary>
  /// Is this stage one of the reads? Asked by the completion handler, which stamps the
  /// checkpoint only when a LOOK-half run finished (review ruling S3) - a melt landing
  /// later must never be the thing that dates the Look.
  /// </summary>
  internal static bool IsLookStage(RoundStage stage)
    => Array.IndexOf(LookStages, stage) >= 0;

  /// <summary>
  /// Is the Look half behind us? A stage counts as behind either because it was
  /// DONE or because it had nothing to do - which is not a leniency, it is the
  /// cursor's own rule (an empty stage is skipped silently and never marked). A
  /// checkpoint that waited for marks would never fire on the commonest good night
  /// of all: a fresh board and a fresh cache, both halves of the Look empty.
  /// </summary>
  internal static bool LookComplete(Func<RoundStage, bool> isDone, Func<RoundStage, bool> hasWork)
  {
    foreach (var stage in LookStages)
      if (!isDone(stage) && hasWork(stage)) return false;
    return true;
  }

  /// <summary>
  /// How long ago, in the coarsest unit that is still honest - literally the same
  /// ladder <see cref="RunLogVoice.Age"/> speaks, because the two labels describe the
  /// same staleness from two seats and a player reading "2 hours old" on a post and
  /// "just now" on the Look it came from would be reading a contradiction. Movement 1
  /// made the two agree by CONSTRUCTION rather than by a test: this is that sentence
  /// with its tail swapped, not a second implementation of the rungs.
  /// </summary>
  internal static string Ago(long seconds)
  {
    var age = RunLogVoice.Age(seconds);
    return age.EndsWith(" old", StringComparison.Ordinal) ? age[..^4] + " ago" : age;
  }

  /// <summary>
  /// THE RESUME LINE - an age label and two counts, and NOTHING that forks on them
  /// (ruled 2026-08-10: no staleness threshold, no offer logic, Drift is the
  /// threshold). It says what the Round is holding and how old its reads are; the
  /// Re-Look verb beside it is always available whatever this sentence says.
  ///
  /// <para>The counts are scoped, not global: <paramref name="cachedDecisions"/> is
  /// the rows THIS run banked and still fresh, and <paramref name="rulingsOwed"/> is
  /// the launch gate's own narrowed queue - the rulings tonight's selected act steps
  /// will actually visit. A global count would advertise work the resumed round is
  /// not going to do.</para>
  ///
  /// <para><paramref name="lookDone"/> arrives ALREADY in the player's zone (the
  /// caller converts) and is rendered at its own offset. Converting in here would
  /// make the sentence depend on the machine it is composed on, which is a thing a
  /// test cannot pin and a reader cannot see.</para>
  /// </summary>
  internal static string Line(
    DateTimeOffset lookDone, DateTimeOffset now, int cachedDecisions, int rulingsOwed)
  {
    var ago = Ago((long)Math.Max(0, (now - lookDone).TotalSeconds));
    var decisions = $"{cachedDecisions} decision{(cachedDecisions == 1 ? "" : "s")} cached";
    var rulings = $"{rulingsOwed} ruling{(rulingsOwed == 1 ? "" : "s")} owed";
    return $"Look done {lookDone:H:mm} ({ago}) - {decisions}, {rulings} - resume the Act half.";
  }
}
