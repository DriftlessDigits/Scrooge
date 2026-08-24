using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// V19 migration contract: the DDL is diffable + idempotent (the V11 model). This
/// runs the real <see cref="Scrooge.MarketMemorySchema.ApplyV19"/> against a temp
/// in-memory SQLite DB, twice, and asserts the second run is a clean no-op.
/// </summary>
public class MarketMemorySchemaTests
{
  private static SqliteConnection OpenTempDb()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    return conn;
  }

  private static void CreateLegacyStandingFlags(SqliteConnection conn)
  {
    // A minimal pre-V19 triage_flags (V12 shape) so the guarded ALTER has a table
    // to add the scope column to - mirrors an upgrading DB.
    using var cmd = new SqliteCommand(
      @"CREATE TABLE triage_flags (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          item_id INTEGER NOT NULL,
          reason TEXT NOT NULL,
          status TEXT NOT NULL DEFAULT 'open'
        );", conn);
    cmd.ExecuteNonQuery();
  }

  private static bool TableExists(SqliteConnection conn, string name)
  {
    using var cmd = new SqliteCommand(
      "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n", conn);
    cmd.Parameters.AddWithValue("@n", name);
    return System.Convert.ToInt64(cmd.ExecuteScalar()) > 0;
  }

  private static List<string> Columns(SqliteConnection conn, string table)
  {
    var cols = new List<string>();
    using var cmd = new SqliteCommand($"PRAGMA table_info({table});", conn);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      cols.Add(reader.GetString(1));
    return cols;
  }

  [Fact]
  public void ApplyV19_CreatesAllThreeTables()
  {
    using var conn = OpenTempDb();
    CreateLegacyStandingFlags(conn);

    Scrooge.MarketMemorySchema.ApplyV19(conn);

    Assert.True(TableExists(conn, "market_board_snapshot"));
    Assert.True(TableExists(conn, "market_events"));
    Assert.True(TableExists(conn, "decision_receipts"));
    Assert.Contains("scope", Columns(conn, "triage_flags"));
  }

  [Fact]
  public void ApplyV19_IsIdempotent_SecondRunDoesNotThrow()
  {
    using var conn = OpenTempDb();
    CreateLegacyStandingFlags(conn);

    Scrooge.MarketMemorySchema.ApplyV19(conn);
    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV19(conn));
    Assert.Null(ex);

    // Scope column added exactly once (a second ALTER would have thrown "duplicate column").
    Assert.Single(Columns(conn, "triage_flags"), c => c == "scope");
  }

  [Fact]
  public void ApplyV19_OnFreshDbWithoutTriageFlags_SkipsScopeAlterCleanly()
  {
    // A bare DB (no triage_flags yet) must not throw on the guarded ALTER.
    using var conn = OpenTempDb();
    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV19(conn));
    Assert.Null(ex);
    Assert.True(TableExists(conn, "market_events"));
  }

  [Fact]
  public void MarketEvents_HasWindowColumns_ButNoForeignPointTimestamp()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    var cols = Columns(conn, "market_events");

    Assert.Contains("seen_after", cols);
    Assert.Contains("seen_by", cols);
    Assert.Contains("observer", cols);
    Assert.Contains("certainty", cols);
    Assert.Contains("ambiguous_match", cols);
    // No single point-in-time "observed_at"/"timestamp" for foreign activity.
    Assert.DoesNotContain("observed_at", cols);
    Assert.DoesNotContain("timestamp", cols);
  }

  [Fact]
  public void BoardSnapshot_PersistsTwinListings_NotDroppedByANaturalKey()
  {
    // Two listings share the soft identity (item, hq, retainer, qty). A natural PK
    // would keep only one and phantom a disappear next scan; the surrogate key keeps both.
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);

    for (var i = 0; i < 2; i++)
      using (var ins = new SqliteCommand(
        @"INSERT INTO market_board_snapshot (item_id, is_hq, retainer_name, quantity, unit_price, seen_at)
          VALUES (100, 0, 'Alice', 1, @p, 1000)", conn))
      {
        ins.Parameters.AddWithValue("@p", 90 + i);
        ins.ExecuteNonQuery();
      }

    using var count = new SqliteCommand(
      "SELECT COUNT(*) FROM market_board_snapshot WHERE item_id = 100 AND retainer_name = 'Alice'", conn);
    Assert.Equal(2L, System.Convert.ToInt64(count.ExecuteScalar()));
  }

  [Fact]
  public void DecisionReceipts_CarryArmAndCategoryAndStackFromDayOne()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    var cols = Columns(conn, "decision_receipts");

    Assert.Contains("arm_id", cols);
    Assert.Contains("item_category", cols);
    Assert.Contains("quantity", cols);
    Assert.Contains("lane_stack_norm", cols);
    Assert.Contains("time_to_clear_days", cols);
    Assert.Contains("outcome_state", cols);
  }

  // ========================================================================
  // V24: the receipt grows the band and the segment's story
  // ========================================================================

  [Fact]
  public void ApplyV24_WidensTheReceiptWithBandAndSegment()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV24(conn);

    var cols = Columns(conn, "decision_receipts");
    Assert.Contains("band_low_ratio", cols);   // relative, like every coordinate here
    Assert.Contains("band_high_ratio", cols);
    Assert.Contains("segment_n", cols);        // how many sales were still voting
    Assert.Contains("segment_cut", cols);      // None / Cliff / Glide
  }

  [Fact]
  public void ApplyV24_IsIdempotent()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV24(conn);

    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV24(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void ApplyV24_LeavesLegacyRowsReadableAsPreSegment()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    using (var insert = new SqliteCommand(
      "INSERT INTO decision_receipts (created_at, item_id, outcome) VALUES (1, 42, 'InLane')", conn))
      insert.ExecuteNonQuery();

    Scrooge.MarketMemorySchema.ApplyV24(conn);

    // A receipt written before the segment existed says exactly that: no band,
    // no cut - never a zero band that reads as a real measurement.
    using var read = new SqliteCommand(
      "SELECT band_low_ratio, segment_n, segment_cut FROM decision_receipts WHERE item_id = 42", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.True(reader.IsDBNull(0));
    Assert.Equal(0L, reader.GetInt64(1));
    Assert.Equal("", reader.GetString(2));
  }

  [Fact]
  public void ApplyV24_OnADbWithNoReceiptsTableIsANoOp()
  {
    using var conn = OpenTempDb();
    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV24(conn));
    Assert.Null(ex);
  }

  // ========================================================================
  // V25: the receipt learns which spot in the queue it took (A10)
  // ========================================================================

  [Fact]
  public void ApplyV25_WidensTheReceiptWithQueuePositionAndClusterSize()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV25(conn);

    var cols = Columns(conn, "decision_receipts");
    Assert.Contains("queue_position", cols); // insertion index: rows still ahead after listing
    Assert.Contains("cluster_size", cols);   // company at the anchor
  }

  [Fact]
  public void ApplyV25_IsIdempotent()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV25(conn);

    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV25(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void ApplyV25_LegacyRowsReadNullNeverFrontOfQueue()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    using (var insert = new SqliteCommand(
      "INSERT INTO decision_receipts (created_at, item_id, outcome) VALUES (1, 42, 'Undercut')", conn))
      insert.ExecuteNonQuery();

    Scrooge.MarketMemorySchema.ApplyV25(conn);

    // A receipt written before the queue frame must read "position unknown",
    // never a 0 default that would claim front-of-queue for a row that was
    // graded by a different model entirely.
    using var read = new SqliteCommand(
      "SELECT queue_position, cluster_size FROM decision_receipts WHERE item_id = 42", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.True(reader.IsDBNull(0));
    Assert.True(reader.IsDBNull(1));
  }

  [Fact]
  public void ApplyV25_OnADbWithNoReceiptsTableIsANoOp()
  {
    using var conn = OpenTempDb();
    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV25(conn));
    Assert.Null(ex);
  }

  // ========================================================================
  // V26: the receipt learns whether the spot it took worked (A9 grading)
  // ========================================================================

  [Fact]
  public void ApplyV26_WidensTheReceiptWithTheAskItsCloseAndTheGradeStamps()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV26(conn);

    var cols = Columns(conn, "decision_receipts");
    // The two operands the grader cannot fabricate: every other coordinate on
    // this table is relative, so the absolute ask has to be stored, and the
    // close instant has to be to-the-second (time_to_clear_days is day-floored).
    Assert.Contains("decided_price", cols);
    Assert.Contains("closed_at", cols);
    // The stamps themselves.
    Assert.Contains("interim_grade", cols);
    Assert.Contains("interim_graded_at", cols);
    Assert.Contains("final_grade", cols);
    Assert.Contains("final_graded_at", cols);
  }

  [Fact]
  public void ApplyV26_IsIdempotent()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV26(conn);

    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV26(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void ApplyV26_LegacyRowsReadNullNeverAGrade()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    using (var insert = new SqliteCommand(
      "INSERT INTO decision_receipts (created_at, item_id, outcome) VALUES (1, 42, 'Undercut')", conn))
      insert.ExecuteNonQuery();

    Scrooge.MarketMemorySchema.ApplyV26(conn);

    // NULL is never a default and never a verdict - it means the grader has not
    // spoken. A '' or a 'SILENCE' default here would fabricate findings for every
    // receipt written before A9 existed.
    using var read = new SqliteCommand(
      @"SELECT decided_price, closed_at, interim_grade, interim_graded_at,
               final_grade, final_graded_at
        FROM decision_receipts WHERE item_id = 42", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    for (var i = 0; i < 6; i++)
      Assert.True(reader.IsDBNull(i));
  }

  [Fact]
  public void ApplyV26_OnADbWithNoReceiptsTableIsANoOp()
  {
    using var conn = OpenTempDb();
    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV26(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void ApplyV26_StacksOnTopOfV24AndV25WithoutDisturbingThem()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV24(conn);
    Scrooge.MarketMemorySchema.ApplyV25(conn);
    Scrooge.MarketMemorySchema.ApplyV26(conn);

    var cols = Columns(conn, "decision_receipts");
    Assert.Contains("band_low_ratio", cols);
    Assert.Contains("queue_position", cols);
    Assert.Contains("decided_price", cols);
    Assert.Contains("final_grade", cols);
  }

  // ========================================================================
  // V30: the receipt learns its spot AMONG THE COMPETITORS (A11)
  // ========================================================================

  [Fact]
  public void ApplyV30_WidensTheReceiptWithCompetitorPosition()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV30(conn);

    var cols = Columns(conn, "decision_receipts");
    Assert.Contains("competitor_position", cols); // insertion index over real sellers only
  }

  [Fact]
  public void ApplyV30_IsIdempotent()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV30(conn);

    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV30(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void ApplyV30_LegacyRowsReadNullNeverFrontOfTheRealLine()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    using (var insert = new SqliteCommand(
      "INSERT INTO decision_receipts (created_at, item_id, outcome) VALUES (1, 42, 'Undercut')", conn))
      insert.ExecuteNonQuery();

    Scrooge.MarketMemorySchema.ApplyV30(conn);

    // Same discipline as V25's queue_position: a receipt written before the
    // classifier reads "unknown", never a 0 that would claim front of a line
    // the model it was graded by had no concept of.
    using var read = new SqliteCommand(
      "SELECT competitor_position FROM decision_receipts WHERE item_id = 42", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.True(reader.IsDBNull(0));
  }

  [Fact]
  public void ApplyV30_OnADbWithNoReceiptsTableIsANoOp()
  {
    using var conn = OpenTempDb();
    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV30(conn));
    Assert.Null(ex);
  }

  // ========================================================================
  // V47: the true seat, and the doctrine's shadow beside it (ruled 08-23)
  // ========================================================================

  [Fact]
  public void ApplyV47_WidensTheReceiptWithTheSeatAndTheShadowTrio()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV47(conn);

    var cols = Columns(conn, "decision_receipts");
    Assert.Contains("seat_at_write", cols);  // 1-based, crashers count - the seat competitor_position could never say
    Assert.Contains("shadow_price", cols);
    Assert.Contains("shadow_seat", cols);
    Assert.Contains("shadow_defense", cols);
  }

  [Fact]
  public void ApplyV47_IsIdempotent()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV47(conn);

    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV47(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void ApplyV47_LegacyRowsStaySilentAboutSeatsTheyNeverMeasured()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    using (var insert = new SqliteCommand(
      "INSERT INTO decision_receipts (created_at, item_id, outcome) VALUES (1, 42, 'Undercut')", conn))
      insert.ExecuteNonQuery();

    Scrooge.MarketMemorySchema.ApplyV47(conn);

    using var read = new SqliteCommand(
      "SELECT seat_at_write, shadow_price FROM decision_receipts WHERE item_id = 42", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.True(reader.IsDBNull(0));
    Assert.True(reader.IsDBNull(1));
  }

  // ========================================================================
  // V48: the standing recon phantoms get their mark (ruled 08-23)
  // ========================================================================

  private static SqliteConnection OpenV48Db()
  {
    var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.DecisionCacheSchema.ApplyV39(conn);
    Scrooge.StandingBookSchema.ApplyV21(conn);
    return conn;
  }

  private static void SeedV48Receipt(SqliteConnection conn, long id, uint itemId, string state)
  {
    using var cmd = new SqliteCommand(
      $"INSERT INTO decision_receipts (id, created_at, item_id, outcome, outcome_state)"
      + $" VALUES ({id}, 100, {itemId}, 'Undercut', '{state}')", conn);
    cmd.ExecuteNonQuery();
  }

  private static void PointCacheAt(SqliteConnection conn, uint itemId, long receiptId)
  {
    using var cmd = new SqliteCommand(
      $"INSERT INTO decision_cache (item_id, outcome, evidence, receipt_id, banked_at)"
      + $" VALUES ({itemId}, 'Undercut', 'e', {receiptId}, 100)", conn);
    cmd.ExecuteNonQuery();
  }

  private static string? ArmOf(SqliteConnection conn, long receiptId)
  {
    using var cmd = new SqliteCommand(
      $"SELECT arm_id FROM decision_receipts WHERE id = {receiptId}", conn);
    var v = cmd.ExecuteScalar();
    return v is System.DBNull or null ? null : (string)v;
  }

  [Fact]
  public void ApplyV48_MarksTheUnadoptedLook_AndSparesTheAdoptedOne()
  {
    // The Neo-Ishgardian Sword shape (08-23): receipt 1 is a Look nobody posted -
    // open, pointed at by the cache, no write behind it. Receipt 2 was adopted:
    // same pointers, but a listing write followed. Only the phantom gets marked.
    using var conn = OpenV48Db();
    SeedV48Receipt(conn, 1, 29404, "open");
    PointCacheAt(conn, 29404, 1);
    SeedV48Receipt(conn, 2, 555, "open");
    PointCacheAt(conn, 555, 2);
    using (var w = new SqliteCommand(
      "INSERT INTO own_listing_writes (written_at, kind, item_id, unit_price)"
      + " VALUES (200, 'listed', 555, 19950)", conn))
      w.ExecuteNonQuery();

    Scrooge.MarketMemorySchema.ApplyV48(conn);

    Assert.Equal("recon", ArmOf(conn, 1));
    Assert.Null(ArmOf(conn, 2));
  }

  [Fact]
  public void ApplyV48_LeavesClosedAndUnpointedRowsAlone_AndIsIdempotent()
  {
    // A closed receipt is history, not a phantom; a row no cache points at is
    // unprovable and stays as it is (the predicate IS the class - V36's rule).
    using var conn = OpenV48Db();
    SeedV48Receipt(conn, 3, 42, "cleared");
    PointCacheAt(conn, 42, 3);
    SeedV48Receipt(conn, 4, 43, "open"); // no cache pointer

    Scrooge.MarketMemorySchema.ApplyV48(conn);
    Assert.Null(ArmOf(conn, 3));
    Assert.Null(ArmOf(conn, 4));

    var ex = Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV48(conn));
    Assert.Null(ex);
  }

  // ========================================================================
  // V34: the receipt learns how big the game said the board WAS (x-of-y)
  // ========================================================================

  [Fact]
  public void ApplyV34_WidensTheReceiptWithBoardTotal()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV34(conn);

    var cols = Columns(conn, "decision_receipts");
    Assert.Contains("board_total", cols); // depth < total = the decision ran censored
  }

  [Fact]
  public void ApplyV34_IsIdempotent_AndLegacyRowsReadNull()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    using (var insert = new SqliteCommand(
      "INSERT INTO decision_receipts (created_at, item_id, outcome) VALUES (1, 42, 'Undercut')", conn))
      insert.ExecuteNonQuery();

    Scrooge.MarketMemorySchema.ApplyV34(conn);
    Assert.Null(Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV34(conn)));

    // A pre-V34 receipt reads "the proxy never said" - never a zero that would
    // claim an empty board.
    using var read = new SqliteCommand(
      "SELECT board_total FROM decision_receipts WHERE item_id = 42", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.True(reader.IsDBNull(0));
  }

  // ========================================================================
  // V35: the receipt learns what the crashers and the line actually COST
  // ========================================================================

  [Fact]
  public void ApplyV35_WidensTheReceiptWithTheGilSpans()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV35(conn);

    var cols = Columns(conn, "decision_receipts");
    // Drift, 08-02: "I do want to know what the crashers were" - the counts alone
    // could not show the 16-crasher receipt was a board bug; the prices could.
    Assert.Contains("crasher_floor", cols);
    Assert.Contains("crasher_ceiling", cols);
    Assert.Contains("cluster_floor", cols);
    Assert.Contains("cluster_ceiling", cols);
  }

  [Fact]
  public void ApplyV35_IsIdempotent_AndLegacyRowsReadNull()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    using (var insert = new SqliteCommand(
      "INSERT INTO decision_receipts (created_at, item_id, outcome) VALUES (1, 42, 'CrazySkipped')", conn))
      insert.ExecuteNonQuery();

    Scrooge.MarketMemorySchema.ApplyV35(conn);
    Assert.Null(Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV35(conn)));

    // A pre-V35 receipt banked no spans - null, never a zero that reads as gil.
    using var read = new SqliteCommand(
      "SELECT crasher_floor, cluster_ceiling FROM decision_receipts WHERE item_id = 42", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.True(reader.IsDBNull(0));
    Assert.True(reader.IsDBNull(1));
  }

  // ========================================================================
  // V41: the receipt learns what the market paid AFTER we left, in gil
  // ========================================================================

  [Fact]
  public void ApplyV41_WidensTheReceiptWithMarginDonated()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV41(conn);

    var cols = Columns(conn, "decision_receipts");
    // The A9 reframe took the tape out of the verdicts; this is where the tape's
    // one remaining answer lands - a measurement beside the verdict, never a grade.
    Assert.Contains("margin_donated", cols);
  }

  [Fact]
  public void ApplyV41_IsIdempotent_AndLegacyRowsReadNull()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    using (var insert = new SqliteCommand(
      "INSERT INTO decision_receipts (created_at, item_id, outcome) VALUES (1, 42, 'Undercut')", conn))
      insert.ExecuteNonQuery();

    Scrooge.MarketMemorySchema.ApplyV41(conn);
    Assert.Null(Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV41(conn)));

    // NULL, never 0: a pre-V41 receipt was never measured, and 0 would claim we
    // looked and the market paid no premium. That distinction is the column.
    using var read = new SqliteCommand(
      "SELECT margin_donated FROM decision_receipts WHERE item_id = 42", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.True(reader.IsDBNull(0));
  }

  [Fact]
  public void ApplyV41_SkipsABareDbWithNoReceiptTable()
  {
    // V19 creates the table; a database that never got there has nothing to
    // widen, and the guard must be the column read finding no columns at all.
    using var bare = OpenTempDb();
    Assert.Null(Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV41(bare)));
  }

  // ========================================================================
  // V45: the receipt learns the STANCE its price was written under
  // ========================================================================

  [Fact]
  public void ApplyV45_WidensTheReceiptWithTheUndercutPosture()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV45(conn);

    var cols = Columns(conn, "decision_receipts");
    // Where we sat was already banked; what the hand was holding was not, and the
    // 4.0 report card grades the knobs rather than the seat.
    Assert.Contains("undercut_posture", cols);
  }

  [Fact]
  public void ApplyV45_IsIdempotent_AndLegacyRowsReadNull()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    using (var insert = new SqliteCommand(
      "INSERT INTO decision_receipts (created_at, item_id, outcome) VALUES (1, 42, 'Undercut')", conn))
      insert.ExecuteNonQuery();

    Scrooge.MarketMemorySchema.ApplyV45(conn);
    Assert.Null(Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV45(conn)));

    // Never back-filled from today's config: the knobs move, and a stance composed
    // now would describe a night nobody recorded.
    using var read = new SqliteCommand(
      "SELECT undercut_posture FROM decision_receipts WHERE item_id = 42", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.True(reader.IsDBNull(0));
  }

  [Fact]
  public void ApplyV45_SkipsABareDbWithNoReceiptTable()
  {
    using var bare = OpenTempDb();
    Assert.Null(Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV45(bare)));
  }

  // ========================================================================
  // Receipt retention is scoped to the LISTING LANE (item, quality, retainer)
  // ========================================================================

  /// <summary>Writes one receipt into a lane at a controlled created_at.</summary>
  private static void InsertReceipt(SqliteConnection conn, uint itemId, bool isHq,
    string retainer, long createdAt)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO decision_receipts (created_at, item_id, is_hq, retainer_name, outcome)
        VALUES (@at, @iid, @hq, @ret, 'Undercut')", conn);
    cmd.Parameters.AddWithValue("@at", createdAt);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@ret", retainer);
    cmd.ExecuteNonQuery();
  }

  private static List<long> LaneCreatedAts(SqliteConnection conn, uint itemId, bool isHq, string retainer)
  {
    var ats = new List<long>();
    using var cmd = new SqliteCommand(
      @"SELECT created_at FROM decision_receipts
        WHERE item_id = @iid AND is_hq = @hq AND retainer_name = @ret
        ORDER BY created_at DESC", conn);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@ret", retainer);
    using var reader = cmd.ExecuteReader();
    while (reader.Read()) ats.Add(reader.GetInt64(0));
    return ats;
  }

  [Fact]
  public void PruneReceipts_TwoRetainersOnTheSameItem_EachKeepsItsOwnNewestThree()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);

    // Interleaved, the way two retainers actually get pinched: alternating writes
    // into the same (item, quality) but different listing lanes. Under the old
    // shared budget these two ate each other's window.
    for (var i = 1; i <= 5; i++)
    {
      InsertReceipt(conn, 42, isHq: false, "Alice", createdAt: i * 10);
      Scrooge.MarketMemorySchema.PruneReceipts(conn, null, 42, false, "Alice", keep: 3);
      InsertReceipt(conn, 42, isHq: false, "Bob", createdAt: i * 10 + 1);
      Scrooge.MarketMemorySchema.PruneReceipts(conn, null, 42, false, "Bob", keep: 3);
    }

    Assert.Equal(new List<long> { 50, 40, 30 }, LaneCreatedAts(conn, 42, false, "Alice"));
    Assert.Equal(new List<long> { 51, 41, 31 }, LaneCreatedAts(conn, 42, false, "Bob"));
  }

  [Fact]
  public void PruneReceipts_AThirdRetainersLoneReceipt_SurvivesTheOtherTwosPinches()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);

    // Carol lists the item once and is never pinched again. Her receipt is the
    // only evidence that listing ever existed - and A9 cannot grade what
    // retention already deleted.
    InsertReceipt(conn, 42, isHq: false, "Carol", createdAt: 1);

    for (var i = 1; i <= 3; i++)
    {
      InsertReceipt(conn, 42, isHq: false, "Alice", createdAt: 100 + i);
      Scrooge.MarketMemorySchema.PruneReceipts(conn, null, 42, false, "Alice", keep: 3);
      InsertReceipt(conn, 42, isHq: false, "Bob", createdAt: 200 + i);
      Scrooge.MarketMemorySchema.PruneReceipts(conn, null, 42, false, "Bob", keep: 3);
    }

    // Six inserts by two other retainers, every one of them newer than hers.
    // Under the (item, quality) budget Carol's row was the oldest of seven and
    // died first, ungraded.
    Assert.Equal(new List<long> { 1 }, LaneCreatedAts(conn, 42, false, "Carol"));
  }

  [Fact]
  public void PruneReceipts_TheQualitySplitStillHolds()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);

    // Adding the retainer to the key must not loosen the two already in it: one
    // retainer's NQ and HQ listings are different lanes and always were.
    for (var i = 1; i <= 4; i++)
    {
      InsertReceipt(conn, 42, isHq: false, "Alice", createdAt: i * 10);
      InsertReceipt(conn, 42, isHq: true, "Alice", createdAt: i * 10 + 1);
    }
    Scrooge.MarketMemorySchema.PruneReceipts(conn, null, 42, false, "Alice", keep: 3);
    Scrooge.MarketMemorySchema.PruneReceipts(conn, null, 42, true, "Alice", keep: 3);

    Assert.Equal(new List<long> { 40, 30, 20 }, LaneCreatedAts(conn, 42, false, "Alice"));
    Assert.Equal(new List<long> { 41, 31, 21 }, LaneCreatedAts(conn, 42, true, "Alice"));
  }

  [Fact]
  public void PruneReceipts_UnderTheWindow_PrunesNothing()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);

    InsertReceipt(conn, 42, isHq: false, "Alice", createdAt: 10);
    InsertReceipt(conn, 42, isHq: false, "Alice", createdAt: 20);

    Assert.Equal(0, Scrooge.MarketMemorySchema.PruneReceipts(conn, null, 42, false, "Alice", keep: 3));
    Assert.Equal(2, LaneCreatedAts(conn, 42, false, "Alice").Count);
  }

  // ========================================================================
  // V33: the phantom purge (delete em - Drift, 08-02)
  // ========================================================================

  private static void InsertEvent(SqliteConnection conn, long itemId, string kind,
    long seenAfter, long seenBy, string? resolution = null)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO market_events (item_id, kind, seen_after, seen_by, resolution)
        VALUES (@i, @k, @a, @b, @r)", conn);
    cmd.Parameters.AddWithValue("@i", itemId);
    cmd.Parameters.AddWithValue("@k", kind);
    cmd.Parameters.AddWithValue("@a", seenAfter);
    cmd.Parameters.AddWithValue("@b", seenBy);
    cmd.Parameters.AddWithValue("@r", (object?)resolution ?? System.DBNull.Value);
    cmd.ExecuteNonQuery();
  }

  private static long EventCount(SqliteConnection conn)
  {
    using var cmd = new SqliteCommand("SELECT COUNT(*) FROM market_events", conn);
    return System.Convert.ToInt64(cmd.ExecuteScalar());
  }

  /// <summary>A phantom burst: 6+ events, one item, one instant, both kinds, sub-30s window.</summary>
  private static void InsertPhantomBurst(SqliteConnection conn, long itemId, long seenBy)
  {
    for (var i = 0; i < 4; i++) InsertEvent(conn, itemId, "appeared", seenBy - 5, seenBy);
    for (var i = 0; i < 3; i++) InsertEvent(conn, itemId, "disappeared", seenBy - 5, seenBy);
  }

  /// <summary>Well before the fix's 2026-08-02 21:04 UTC bound.</summary>
  private const long OldEnough = 1_753_000_000;

  [Fact]
  public void ApplyV33_DeletesThePhantomFingerprint_AndOnlyIt()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);

    InsertPhantomBurst(conn, 42, OldEnough);

    // A real full-board turnover: same burst shape, but observed across 3 hours
    // - a board CAN fully change between looks that far apart.
    for (var i = 0; i < 4; i++) InsertEvent(conn, 43, "appeared", OldEnough - 10_800, OldEnough);
    for (var i = 0; i < 3; i++) InsertEvent(conn, 43, "disappeared", OldEnough - 10_800, OldEnough);

    // The legit twice-read path: sub-30s window but only a 2-event diff, no burst.
    InsertEvent(conn, 44, "appeared", OldEnough - 5, OldEnough);
    InsertEvent(conn, 44, "disappeared", OldEnough - 5, OldEnough);

    Scrooge.MarketMemorySchema.ApplyV33(conn);

    Assert.Equal(9, EventCount(conn)); // 7 phantoms gone; the slow 7 + quick 2 survive
  }

  [Fact]
  public void ApplyV33_SparesResolvedRows_AndFutureEvents()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);

    // A burst the reconciler graded one row of: the graded row earned its keep.
    for (var i = 0; i < 6; i++) InsertEvent(conn, 42, "appeared", OldEnough - 5, OldEnough);
    InsertEvent(conn, 42, "disappeared", OldEnough - 5, OldEnough, resolution: "sold");

    // A phantom-shaped burst AFTER the fix bound must never be eaten - the
    // armed-query gate makes new phantoms impossible, so anything matching the
    // shape out there is the market being weird, and observations stand.
    InsertPhantomBurst(conn, 43, 1_800_000_000);

    Scrooge.MarketMemorySchema.ApplyV33(conn);

    Assert.Equal(1 + 7, EventCount(conn)); // the sold row + the future burst
  }

  [Fact]
  public void ApplyV33_IsIdempotent()
  {
    using var conn = OpenTempDb();
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    InsertPhantomBurst(conn, 42, OldEnough);
    InsertEvent(conn, 44, "appeared", OldEnough - 10_800, OldEnough);

    Scrooge.MarketMemorySchema.ApplyV33(conn);
    Scrooge.MarketMemorySchema.ApplyV33(conn);

    Assert.Equal(1, EventCount(conn));
  }

  // ---- V36: the pre-V19 immortal Watch tenants come down ----

  private static void InsertFlag(SqliteConnection conn, long itemId, string reason,
    string status, string scope)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO triage_flags (item_id, reason, status, scope)
        VALUES (@i, @r, @s, @c)", conn);
    cmd.Parameters.AddWithValue("@i", itemId);
    cmd.Parameters.AddWithValue("@r", reason);
    cmd.Parameters.AddWithValue("@s", status);
    cmd.Parameters.AddWithValue("@c", scope);
    cmd.ExecuteNonQuery();
  }

  private static string FlagStatus(SqliteConnection conn, long itemId)
  {
    using var cmd = new SqliteCommand(
      "SELECT status FROM triage_flags WHERE item_id = @i", conn);
    cmd.Parameters.AddWithValue("@i", itemId);
    return (string)cmd.ExecuteScalar()!;
  }

  [Fact]
  public void ApplyV36_RetiresTheUnprovableClass_AndOnlyIt()
  {
    using var conn = OpenTempDb();
    CreateLegacyStandingFlags(conn);
    Scrooge.MarketMemorySchema.ApplyV19(conn);

    // The class: open lane_held with no recorded container - no zombie round
    // can EVER close it (Unknown scope fails the proof by design, forever).
    InsertFlag(conn, 1, "lane_held", "open", "");
    // A post-V19 hold: its container is on record, the round owns it.
    InsertFlag(conn, 2, "lane_held", "open", "board");
    InsertFlag(conn, 3, "lane_held", "open", "inventory");
    // Empty scope but a different reason - not the class, not touched.
    InsertFlag(conn, 4, "below_minimum", "open", "");
    // Already closed - history is not rewritten.
    InsertFlag(conn, 5, "lane_held", "dismissed", "");
    InsertFlag(conn, 6, "lane_held", "resolved", "");

    Scrooge.MarketMemorySchema.ApplyV36(conn);

    Assert.Equal("legacy_unprovable", FlagStatus(conn, 1));
    Assert.Equal("open", FlagStatus(conn, 2));
    Assert.Equal("open", FlagStatus(conn, 3));
    Assert.Equal("open", FlagStatus(conn, 4));
    Assert.Equal("dismissed", FlagStatus(conn, 5));
    Assert.Equal("resolved", FlagStatus(conn, 6));
  }

  // ---- The receipt true-up: the price we posted AND the retainer selling it (S13).
  //      The position_in_lane arm died with the ratio (doctrine sweep, 2026-08-15). ----

  private static SqliteConnection ReceiptsDb()
  {
    var conn = OpenTempDb();
    CreateLegacyStandingFlags(conn);
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    Scrooge.MarketMemorySchema.ApplyV26(conn); // decided_price
    return conn;
  }

  private static long InsertReceipt(SqliteConnection conn, string retainer, long decided)
  {
    using (var cmd = new SqliteCommand(
      @"INSERT INTO decision_receipts (created_at, item_id, is_hq, retainer_name, outcome,
                                       decided_price, position_in_lane)
        VALUES (1, 42, 0, @ret, 'Undercut', @price, 1.5)", conn))
    {
      cmd.Parameters.AddWithValue("@ret", retainer);
      cmd.Parameters.AddWithValue("@price", decided);
      cmd.ExecuteNonQuery();
    }
    using var id = new SqliteCommand("SELECT last_insert_rowid()", conn);
    return (long)id.ExecuteScalar()!;
  }

  private static (long? Price, double? Ratio, string Retainer) ReadReceipt(SqliteConnection conn, long id)
  {
    using var cmd = new SqliteCommand(
      "SELECT decided_price, position_in_lane, retainer_name FROM decision_receipts WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("@id", id);
    using var r = cmd.ExecuteReader();
    r.Read();
    return (r.IsDBNull(0) ? null : r.GetInt64(0),
            r.IsDBNull(1) ? null : r.GetDouble(1),
            r.IsDBNull(2) ? "" : r.GetString(2));
  }

  /// <summary>
  /// "IF KAREN IS SELLING THE ITEM, THAT IS WHAT THE DATA NEEDS TO SAY" (Drift, ruling
  /// S13). Recon borrowed Dave's panel to read the board and wrote the receipt under
  /// his name; the bell listed the item on Karen after a 20/20 swap. Three readers key
  /// on that name - the On Market lane, the retention window, and the sale confirm's
  /// PASS join - so a receipt left on Dave shows the row on the wrong shelf and never
  /// closes when it sells.
  /// </summary>
  [Fact]
  public void TrueUpReceipt_MovesTheRowToTheRetainerThatIsActuallySelling()
  {
    using var conn = ReceiptsDb();
    var id = InsertReceipt(conn, "Dave", decided: 195);

    Scrooge.MarketMemorySchema.TrueUpReceipt(conn, id, appliedPrice: 190, retainerName: "Karen");

    var row = ReadReceipt(conn, id);
    Assert.Equal(190, row.Price);
    Assert.Equal("Karen", row.Retainer);
  }

  [Fact]
  public void TrueUpReceipt_AdoptionClearsTheLooksMark()
  {
    // 08-23: the true-up runs exactly when a REAL listing was written against the
    // receipt - the moment a recon Look stops being a Look. From here the ask
    // surfaces and the grader treat the row as the ask it now genuinely is.
    using var conn = ReceiptsDb();
    var id = InsertReceipt(conn, "Elwyn", decided: 20_000);
    using (var mark = new SqliteCommand(
      $"UPDATE decision_receipts SET arm_id = 'recon' WHERE id = {id}", conn))
      mark.ExecuteNonQuery();

    Scrooge.MarketMemorySchema.TrueUpReceipt(conn, id, appliedPrice: 19_950, retainerName: "Elwyn");

    using var read = new SqliteCommand(
      $"SELECT arm_id, decided_price FROM decision_receipts WHERE id = {id}", conn);
    using var r = read.ExecuteReader();
    Assert.True(r.Read());
    Assert.True(r.IsDBNull(0));
    Assert.Equal(19_950, r.GetInt64(1));
  }

  /// <summary>
  /// THE DOCTRINE SWEEP'S PIN (2026-08-15): the true-up corrects the record of what we
  /// PUT THE ITEM UP FOR - decided_price and the retainer holding it - and touches
  /// nothing else. position_in_lane used to be recomputed in the same statement; that
  /// ratio's writer is retired, and a legacy row that still carries a value must keep
  /// it exactly as written rather than be half-migrated by whichever pass happens to
  /// walk past it.
  /// </summary>
  [Fact]
  public void TrueUpReceipt_LeavesALegacyRatioExactlyAsItWasWritten()
  {
    using var conn = ReceiptsDb();
    var id = InsertReceipt(conn, "Dave", decided: 195); // inserted carrying position_in_lane 1.5

    Scrooge.MarketMemorySchema.TrueUpReceipt(conn, id, appliedPrice: 190, retainerName: "Karen");

    var row = ReadReceipt(conn, id);
    Assert.Equal(190, row.Price);
    Assert.Equal("Karen", row.Retainer);
    Assert.Equal(1.5, row.Ratio!.Value, 6); // NOT 190/95 - the arm is gone, the old row stands
  }

  /// <summary>
  /// An unknown retainer is not evidence that the known one is wrong. A pass that
  /// cannot name where the listing went leaves the column standing rather than blanking
  /// the one fact the outcome join has to close on.
  /// </summary>
  [Theory]
  [InlineData(null)]
  [InlineData("")]
  public void TrueUpReceipt_ANamelessPassLeavesTheRetainerAlone(string? nameless)
  {
    using var conn = ReceiptsDb();
    var id = InsertReceipt(conn, "Dave", decided: 195);

    Scrooge.MarketMemorySchema.TrueUpReceipt(conn, id, appliedPrice: 190, retainerName: nameless);

    var row = ReadReceipt(conn, id);
    Assert.Equal(190, row.Price);
    Assert.Equal("Dave", row.Retainer);
  }

  /// <summary>
  /// Nothing was applied, so there is nothing to true up - and in particular the
  /// retainer must not move on the strength of a pass that listed nothing. Same
  /// fail-closed reading the price half always had.
  /// </summary>
  [Fact]
  public void TrueUpReceipt_NoAppliedPriceCorrectsNothingAtAll()
  {
    using var conn = ReceiptsDb();
    var id = InsertReceipt(conn, "Dave", decided: 195);

    Assert.Equal(0, Scrooge.MarketMemorySchema.TrueUpReceipt(conn, id, 0, "Karen"));

    var row = ReadReceipt(conn, id);
    Assert.Equal(195, row.Price);
    Assert.Equal("Dave", row.Retainer);
  }

  [Fact]
  public void ApplyV36_IsIdempotent_AndSkipsABareDb()
  {
    // A bare DB (no triage_flags) must no-op cleanly - the fresh-install path.
    using (var bare = OpenTempDb())
      Assert.Null(Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV36(bare)));

    using var conn = OpenTempDb();
    CreateLegacyStandingFlags(conn);
    Scrooge.MarketMemorySchema.ApplyV19(conn);
    InsertFlag(conn, 1, "lane_held", "open", "");

    Scrooge.MarketMemorySchema.ApplyV36(conn);
    Assert.Null(Record.Exception(() => Scrooge.MarketMemorySchema.ApplyV36(conn)));
    Assert.Equal("legacy_unprovable", FlagStatus(conn, 1));
  }

  // ---- SchemaGuards: the table_info reads every migration above opens with ----

  [Fact]
  public void TableExists_AnswersForPresentAndAbsentTables()
  {
    using var conn = OpenTempDb();
    Assert.False(Scrooge.SchemaGuards.TableExists(conn, "triage_flags"));

    CreateLegacyStandingFlags(conn);
    Assert.True(Scrooge.SchemaGuards.TableExists(conn, "triage_flags"));
  }

  [Fact]
  public void Columns_ReadsCaseInsensitively_AndIsEmptyForAMissingTable()
  {
    using var conn = OpenTempDb();
    Assert.Empty(Scrooge.SchemaGuards.Columns(conn, "triage_flags"));

    CreateLegacyStandingFlags(conn);
    var cols = Scrooge.SchemaGuards.Columns(conn, "triage_flags");
    Assert.Contains("ITEM_ID", cols);
  }

  [Fact]
  public void EnsureColumns_AddsOnlyWhatIsMissing_AndReRunsCleanly()
  {
    using var conn = OpenTempDb();
    CreateLegacyStandingFlags(conn);

    // 'reason' is already there; the other two are not.
    Scrooge.SchemaGuards.EnsureColumns(conn, "triage_flags",
      "reason TEXT NOT NULL DEFAULT ''",
      "scope TEXT NOT NULL DEFAULT ''",
      "note TEXT");

    var cols = Columns(conn, "triage_flags");
    Assert.Contains("scope", cols);
    Assert.Contains("note", cols);
    Assert.Single(cols, c => c == "reason");

    Assert.Null(Record.Exception(() => Scrooge.SchemaGuards.EnsureColumns(conn, "triage_flags",
      "reason TEXT NOT NULL DEFAULT ''",
      "scope TEXT NOT NULL DEFAULT ''",
      "note TEXT")));
    Assert.Equal(cols.Count, Columns(conn, "triage_flags").Count);
  }

  [Fact]
  public void EnsureColumns_NoOpsOnAMissingTable()
  {
    using var conn = OpenTempDb();
    Assert.Null(Record.Exception(() =>
      Scrooge.SchemaGuards.EnsureColumns(conn, "triage_flags", "scope TEXT NOT NULL DEFAULT ''")));
    Assert.False(Scrooge.SchemaGuards.TableExists(conn, "triage_flags"));
  }
}
