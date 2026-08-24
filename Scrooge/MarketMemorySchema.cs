using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// The V19 market-memory DDL, extracted Dalamud-free so it is linked-source testable
/// (the migration idempotency contract runs this against a real temp SQLite DB, no
/// game statics). GilStorageBootstrap.MigrateV19 calls <see cref="ApplyV19"/> and owns
/// the logging; this class owns the schema statements - and, following the
/// SaleHistorySchema model, the retention rule that rides with them
/// (<see cref="PruneReceipts"/>), so the table's bound is testable rather than
/// buried in a Dalamud-linked caller.
///
/// <para>decision_receipts is the 4.0 scoreboard's other corpus (routing_receipts
/// is the first - see RoutingReceiptSchema). undercut_posture (V45) is banked for
/// it and nothing else: the undercut report card grades the PEGS a price was
/// written under, and the seat coordinates alone cannot name them.</para>
///
/// Every statement is CREATE ... IF NOT EXISTS or a column-guarded ALTER, so applying
/// it twice is a no-op (diffable + idempotent, the V11 model).
/// </summary>
internal static class MarketMemorySchema
{
  private const string ReceiptsTable = "decision_receipts";

  /// <summary>Creates the three market-memory tables (idempotent) and the scope column.</summary>
  internal static void ApplyV19(SqliteConnection connection)
  {
    EnsureTables(connection);
    EnsureStandingScopeColumn(connection);
  }

  /// <summary>The current-board snapshot, the append-only event log, and receipts.</summary>
  internal static void EnsureTables(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(
      @"CREATE TABLE IF NOT EXISTS market_board_snapshot (
          id            INTEGER PRIMARY KEY AUTOINCREMENT,
          item_id       INTEGER NOT NULL,
          is_hq         INTEGER NOT NULL DEFAULT 0,
          retainer_name TEXT NOT NULL DEFAULT '',
          quantity      INTEGER NOT NULL DEFAULT 1,
          unit_price    INTEGER NOT NULL,
          is_own        INTEGER NOT NULL DEFAULT 0,
          world_id      INTEGER NOT NULL DEFAULT 0,
          observer      TEXT NOT NULL DEFAULT 'own_scan',
          seen_at       INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_market_board_snapshot_item ON market_board_snapshot(item_id);
        -- Surrogate PK, NOT a natural (item,hq,retainer,qty) key: two twin listings
        -- share that soft identity, and a natural PK would silently drop one - which
        -- would then read as a phantom disappear/appear on the next scan. The write
        -- path deletes-all-for-item then inserts, so twins persist faithfully.
        CREATE TABLE IF NOT EXISTS market_events (
          id              INTEGER PRIMARY KEY AUTOINCREMENT,
          item_id         INTEGER NOT NULL,
          is_hq           INTEGER NOT NULL DEFAULT 0,
          retainer_name   TEXT NOT NULL DEFAULT '',
          quantity        INTEGER NOT NULL DEFAULT 1,
          kind            TEXT NOT NULL,
          old_price       INTEGER,
          new_price       INTEGER,
          is_own          INTEGER NOT NULL DEFAULT 0,
          observer        TEXT NOT NULL DEFAULT 'own_scan',
          certainty       TEXT NOT NULL DEFAULT 'observed',
          resolution      TEXT,
          ambiguous_match INTEGER NOT NULL DEFAULT 0,
          seen_after      INTEGER NOT NULL DEFAULT 0,
          seen_by         INTEGER NOT NULL,
          world_id        INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_market_events_item ON market_events(item_id, is_hq);
        CREATE INDEX IF NOT EXISTS ix_market_events_kind ON market_events(kind, seen_by DESC);
        CREATE INDEX IF NOT EXISTS ix_market_events_ambiguous ON market_events(ambiguous_match) WHERE ambiguous_match = 1;
        CREATE TABLE IF NOT EXISTS decision_receipts (
          id                     INTEGER PRIMARY KEY AUTOINCREMENT,
          created_at             INTEGER NOT NULL,
          item_id                INTEGER NOT NULL,
          is_hq                  INTEGER NOT NULL DEFAULT 0,
          retainer_name          TEXT NOT NULL DEFAULT '',
          item_category          TEXT NOT NULL DEFAULT '',
          arm_id                 TEXT,
          position_in_lane       REAL,   -- WRITER RETIRED 2026-08-15 (doctrine sweep): a price ratio on a queue-grading table. Column kept; pre-sweep rows keep their values, new rows are NULL.
          board_depth            INTEGER NOT NULL DEFAULT 0,
          undercut_target_ratio  REAL,   -- WRITER RETIRED 2026-08-15 (doctrine sweep): write-only since V19, never read.
          velocity_per_day       REAL,
          lane_n                 INTEGER NOT NULL DEFAULT 0,
          lane_spread            REAL,
          weighted_lane_age_days REAL NOT NULL DEFAULT 0,
          forecast_clearing_days REAL,
          quantity               INTEGER NOT NULL DEFAULT 1,
          lane_stack_norm        REAL,
          outcome                TEXT NOT NULL DEFAULT '',
          evidence               TEXT NOT NULL DEFAULT '',
          time_to_clear_days     INTEGER,
          outcome_state          TEXT NOT NULL DEFAULT 'open'
        );
        CREATE INDEX IF NOT EXISTS ix_decision_receipts_item ON decision_receipts(item_id, is_hq, created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_decision_receipts_open ON decision_receipts(item_id, is_hq) WHERE outcome_state = 'open';",
      connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Adds triage_flags.scope (board/inventory/'') for the zombie round. Guarded so a
  /// re-run - or a fresh DB whose CreateTables already ships the column - is a no-op.
  /// A missing triage_flags table (bare test DB) simply reads zero columns and skips.
  /// </summary>
  internal static void EnsureStandingScopeColumn(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, "triage_flags", "scope TEXT NOT NULL DEFAULT ''");

  /// <summary>
  /// V24: the receipt grows the band and the segment's story
  /// ([[Scrooge - Lane Pricing - Design]], A2/A4c).
  ///
  /// A receipt that records only the median cannot answer the question the regime
  /// arc ships dark to ask: did the band draw sane, and who was still voting when
  /// it did. Band edges ride RELATIVE like every other coordinate here (p25/median,
  /// p75/median) so a band in dye reads against a band in crafting mats; their
  /// difference is the width A4c narrates, and the edges are what the outcome join
  /// scores later - the band is the group the listing joined, so a clear landing
  /// outside it says those boundaries drew the wrong group. segment_n and
  /// segment_cut carry the electorate: how many sales voted, and whether a cliff,
  /// a glide, or nothing demoted the rest.
  ///
  /// Column-guarded ALTERs (the V22 model): re-running is a no-op, legacy rows
  /// default to no band and no cut, which reads correctly as "written before the
  /// segment existed."
  /// </summary>
  internal static void ApplyV24(SqliteConnection connection)
    // No receipts table means nothing to widen; V19 creates it before this runs.
    => SchemaGuards.EnsureColumns(connection, ReceiptsTable,
        "band_low_ratio REAL",
        "band_high_ratio REAL",
        "segment_n INTEGER NOT NULL DEFAULT 0",
        "segment_cut TEXT NOT NULL DEFAULT ''");

  /// <summary>
  /// V25: the receipt learns which spot in the queue it took (A10).
  ///
  /// position_in_lane is a PRICE ratio and always was; under the queue frame the
  /// coordinate that grades is the INSERTION INDEX - how many foreign rows were
  /// still ahead (cheaper) after we listed. queue_position carries it (0 = front;
  /// stepped-over crazies sit ahead by construction), cluster_size carries the
  /// company at the anchor (1 = a loner undercut anyway, N = the crowd rule 1
  /// read). Both nullable, no defaults: a legacy NULL reads honestly as "written
  /// before the queue frame", and NULL on a new row means "never listed" (held) -
  /// a 0 default would claim front-of-queue for rows that were never in it.
  ///
  /// The doctrine sweep (2026-08-15) finished what V25 started: position_in_lane's
  /// WRITER is retired, so the queue frame is now the only frame this table records.
  /// The column stays for the rows that already have it.
  ///
  /// Column-guarded ALTERs (the V22/V24 model): re-running is a no-op.
  /// </summary>
  internal static void ApplyV25(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, ReceiptsTable,
        "queue_position INTEGER",
        "cluster_size INTEGER");

  /// <summary>
  /// V30: the receipt learns which spot AMONG THE COMPETITORS it took (A11).
  ///
  /// queue_position (V25) stays the raw insertion index - a measure, never a
  /// target (Drift, 08-02). competitor_position is the same count over real
  /// sellers only: crashers ahead of us do not count. The gap between the two
  /// is a free diagnostic - raw 3, competitor 0 = standing behind bait. Same
  /// null discipline as V25: NULL = never listed, or written before A11.
  ///
  /// Column-guarded ALTER (the V22/V24/V25 model): re-running is a no-op.
  /// </summary>
  internal static void ApplyV30(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, ReceiptsTable, "competitor_position INTEGER");

  /// <summary>
  /// V34: the receipt learns how big the game said the board WAS (x-of-y).
  ///
  /// board_depth has always been "listings seen at decision time" - and 949 of
  /// 1,744 receipts sat at exactly 10, the page boundary, because decisions
  /// fired on the first valid batch while later pages were still streaming.
  /// board_total is the proxy's own count for the same board, so depth &lt; total
  /// reads "this decision ran censored" straight off the row. NULL = the proxy
  /// never said, or the receipt predates V34 - never a zero that reads as an
  /// empty board.
  ///
  /// Column-guarded ALTER (the V22/V24/V25/V30 model): re-running is a no-op.
  /// </summary>
  internal static void ApplyV34(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, ReceiptsTable, "board_total INTEGER");

  /// <summary>
  /// V35: the receipt learns what the stepped-over rows and the anchor line
  /// actually COST (Drift, 08-02: "I do want to know what the crashers were...
  /// I find it hard to believe that there were 15 crashers on a single item" -
  /// he was right, the count was a board bug, and the counts alone could not
  /// show it). queue_position says how many we stepped; these four say the gil:
  /// the crasher prefix's span and the anchor cluster's span, absolutes like
  /// decided_price (operands for a reader, not coordinates for the grader).
  /// NULL = no such rows in that verdict, or a pre-V35 receipt.
  ///
  /// Column-guarded ALTERs (the V22/V24/V25/V30/V34 model): re-running is a no-op.
  /// </summary>
  internal static void ApplyV35(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, ReceiptsTable,
        "crasher_floor INTEGER",
        "crasher_ceiling INTEGER",
        "cluster_floor INTEGER",
        "cluster_ceiling INTEGER");

  /// <summary>
  /// V37: the receipt learns what the stepped-over pack was outnumbered BY.
  ///
  /// The A11 step fires on a crowd-vs-crowd count, and the crowd is the whole
  /// competitor queue behind the pack - but the only headcount a cell could read
  /// back was cluster_size, the neighbourhood we finally undercut. So a Golden
  /// Silk step drew "stepped over 4 - the line: 3" off a board where twenty
  /// sellers stood behind the crashers, and the number that actually convicted
  /// them lived in prose (Drift, 08-03). crowd_behind is that number. NULL = no
  /// pack step in that verdict, or a pre-V37 receipt - never a zero, which would
  /// read as "outnumbered by nobody".
  ///
  /// Column-guarded ALTER (the V22/V24/V25/V30/V34/V35 model): re-running is a no-op.
  /// </summary>
  internal static void ApplyV37(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, ReceiptsTable, "crowd_behind INTEGER");

  /// <summary>
  /// V41: the receipt learns what the market paid AFTER we left, in gil.
  ///
  /// The A9 reframe (ruled 2026-08-15) took the tape out of the verdicts - a
  /// receipt now scores the DECISION, and the direction of the next decision is
  /// the verdict on this one. That left the survivorship hole with nothing to
  /// cover it: a sold ask reads CLEARED whether it sold at the top of the band or
  /// instantly at half its worth, and no verdict in the new set can tell those
  /// apart, because neither can be told apart from a decision alone.
  ///
  /// margin_donated is the answer, and it is deliberately NOT a grade. It is a
  /// measurement in gil: the most any quality-matched settle after our close paid
  /// over our ask, floored at 0. It rides beside the verdict so a CLEARED receipt
  /// can carry a positive margin - the exact case the old UNDERSOLD-as-a-grade
  /// design structurally could not represent.
  ///
  /// <para><b>0 and NULL are different findings and the column exists to keep them
  /// different.</b> 0 = we looked and the market paid no premium. NULL = we never
  /// got to look: still open, or the tape scrolled past the close before we came
  /// back. Its ruled consumers are the Phase-4 revival trigger's two reads - how
  /// often it is non-null-and-positive, and how big it is when it is - and neither
  /// exists yet. A triage-case trap read it briefly and died with the traps register
  /// (3b-1), so the column is back to banked-and-unread. No price logic reads it, which
  /// is the part that matters - the column grades nobody and tunes nothing.</para>
  ///
  /// Column-guarded ALTER (the V22/V24/V25/V30/V34/V35/V37 model): re-running is a
  /// no-op, and every legacy row reads NULL - honest, since nothing measured them.
  /// </summary>
  internal static void ApplyV41(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, ReceiptsTable, "margin_donated INTEGER");

  /// <summary>
  /// V45: THE UNDERCUT POSTURE the decided price was written under - the
  /// scoreboard food for the 4.0 undercut report card.
  ///
  /// <para>decision_receipts already banks the seat we took (queue_position,
  /// competitor_position, the crasher/cluster spans) and the gil we wrote
  /// (decided_price), which between them describe WHERE we sat. What no column
  /// carried is the stance the hand was holding while it wrote: whether
  /// UndercutSelf was on, which write style and step the mode was spending,
  /// which of the two caps was armed and at what percentage, and where the
  /// queue-confidence rail sat. Every one of those is a pegged knob, and a
  /// revealed-value derivation over them cannot run against a corpus that only
  /// records the outcome - a receipt written under a 50% increase cap and one
  /// written with the cap off are indistinguishable rows today.</para>
  ///
  /// <para>ONE COMPACT TAG rather than seven columns, deliberately. The knobs
  /// move together and are read together (the posture is the unit of meaning,
  /// not any single peg), the set will grow when a knob is added, and the
  /// composition is pure and pinned - see <see cref="UndercutPosture"/>, which
  /// owns the grammar so a reader in 4.0 parses one documented shape instead of
  /// seven columns' worth of NULL discipline.</para>
  ///
  /// <para>Column-guarded ALTER, nullable, nothing back-filled: a pre-V45 row is
  /// silent about the stance it ran under, and silence is what it earned.</para>
  /// </summary>
  internal static void ApplyV45(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, ReceiptsTable, "undercut_posture TEXT");

  /// <summary>
  /// V47 (ruled 08-23): the receipt learns its TRUE seat, and the doctrine's
  /// shadow answer beside it.
  ///
  /// <para>seat_at_write: the 1-based seat the written price actually bought,
  /// counted over the WHOLE foreign queue - crashers count. It exists because
  /// competitor_position could not say "not first": its "crashers don't count"
  /// arithmetic read 0 on all 1,623 post-V30 rows, including the Caligae write
  /// that sat 4th (the 08-23 audit). That column's writer retires the same day -
  /// the position_in_lane retirement shape: column stays, old rows keep their
  /// values, new rows leave it NULL.</para>
  ///
  /// <para>shadow_price / shadow_seat / shadow_defense: what the 3.1
  /// queue-doctrine candidate would have written on the same board
  /// (<see cref="QueueDoctrine"/>) - Drift, 08-23: "can we calculate both prices
  /// as an A/B test and gather some data for a bit?". Write-only; the 3.1 ruling
  /// session is the reader. NULL = no real line on that board, or a pre-V47 row.</para>
  ///
  /// <para>Column-guarded ALTERs, nullable, nothing back-filled: re-running is
  /// a no-op, and a pre-V47 row is silent about seats it never measured.</para>
  /// </summary>
  internal static void ApplyV47(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, ReceiptsTable,
        "seat_at_write INTEGER",
        "shadow_price INTEGER",
        "shadow_seat INTEGER",
        "shadow_defense TEXT");

  /// <summary>
  /// V48: marks the standing recon phantoms (08-23, the Neo-Ishgardian Sword -
  /// "I haven't listed the sword yet"). Recon banks its decision through the same
  /// receipt insert a pinch uses, and every ask-speaking surface read those OPEN
  /// rows as standing asks: "asked 20,000 - still standing" over a sword still
  /// in the bags, on the trail, the state line, and the On Market tab - and the
  /// grader would eventually grade the fight that never happened. New recon
  /// receipts are born with arm_id 'recon' (cleared at adoption, the true-up);
  /// this rung back-marks the ones already standing.
  ///
  /// <para>THE PREDICATE IS THE CLASS (the V36 discipline): open + pointed at by
  /// a decision_cache row + no own listing write for the lane since it was
  /// created can only be an un-adopted Look - an adopted one has a write behind
  /// it by definition. Recon receipts whose cache row was superseded are
  /// unmarkable and stay as they are; retention prunes them with their lane.
  /// Idempotent by construction (marked rows still match, the SET is a no-op;
  /// re-running changes nothing).</para>
  /// </summary>
  internal static void ApplyV48(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(
      @"UPDATE decision_receipts
        SET arm_id = 'recon'
        WHERE outcome_state = 'open'
          AND id IN (SELECT receipt_id FROM decision_cache WHERE receipt_id IS NOT NULL)
          AND NOT EXISTS (
            SELECT 1 FROM own_listing_writes w
            WHERE w.item_id = decision_receipts.item_id
              AND w.is_hq = decision_receipts.is_hq
              AND w.written_at >= decision_receipts.created_at)",
      connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// V36: retires the pre-V19 immortal Watch tenants (Drift, 08-03: the five
  /// lane_held holds flagged Jul 16-18 whose items had SOLD weeks ago, still
  /// drawing in the Watch pile with no door to close them).
  ///
  /// <para>The zombie round closes a lane_held flag only when a run walked the
  /// flag's own container and the item was absent - and an Unknown-scope flag
  /// fails that test FOREVER, by design: its container was never recorded
  /// (scope shipped in V19), so no run can ever prove absence. Fail-toward-open
  /// is correct per flag; a class that is permanently unprovable is not open,
  /// it is unanswerable - and the premium ladder (07-26) ended the inflow, so
  /// the class cannot refill.</para>
  ///
  /// <para>The predicate IS the class, not the instance: since V19 every
  /// lane_held writer stamps a scope, so open + lane_held + empty scope can
  /// only be a pre-V19 row - on ANY user's book, not just the one this was
  /// found in. The status says what actually happened: closed by ruling
  /// because unprovable, never "observed gone". Idempotent by construction
  /// (closed rows no longer match).</para>
  /// </summary>
  internal static void ApplyV36(SqliteConnection connection)
  {
    if (!SchemaGuards.TableExists(connection, "triage_flags")) return;

    using var cmd = new SqliteCommand(@"
      UPDATE triage_flags SET status = 'legacy_unprovable'
      WHERE status = 'open' AND reason = 'lane_held' AND scope = '';", connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// V33: deletes the paged-read bug's fabrications from the event log (Drift,
  /// 08-02: "delete em" - marking was "a lot of machinery for data we are
  /// trying to avoid"; a kept phantom taxes every future query with a WHERE
  /// clause, and fabrications are not observations, so the append-only
  /// discipline does not protect them).
  ///
  /// <para>The fingerprint is the combination nothing real produces: a burst of
  /// 6+ events for one item at one instant, claiming both appearances AND
  /// disappearances, all inside a sub-30s observation window. A real board can
  /// fully turn over between looks HOURS apart; it cannot do so in under 30
  /// seconds. These are orphan pages founded as boards and flushed by the next
  /// item's door (fixed 08-02, the armed-query birth gate) - the mechanism ran
  /// from the day market memory shipped: 2,773 events, ~13% of the table.</para>
  ///
  /// <para>Bounded to events observed before the fix shipped (2026-08-02 21:04
  /// UTC), so the fingerprint can never eat a future event however the market
  /// contorts. Rows the reconciler already resolved are kept: a graded event
  /// earned its grade against real evidence. Idempotent by construction.</para>
  /// </summary>
  internal static void ApplyV33(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(@"
      DELETE FROM market_events
      WHERE resolution IS NULL
        AND seen_by < strftime('%s','2026-08-02 21:04:00')
        AND (item_id, seen_by) IN (
          SELECT item_id, seen_by FROM market_events
          GROUP BY item_id, seen_by
          HAVING COUNT(*) >= 6
             AND SUM(kind = 'appeared') > 0
             AND SUM(kind = 'disappeared') > 0
             AND MAX(seen_by - seen_after) < 30);", connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// V26: the receipt learns whether the spot it took worked (A9 grading).
  ///
  /// <para>Six columns, two of them operands the grader cannot fabricate and four
  /// of them the stamps themselves.</para>
  ///
  /// <para><b>decided_price</b> - the ABSOLUTE gil the decision wrote. Every other
  /// coordinate here is deliberately relative (item-agnostic), and position_in_lane
  /// is a ratio against a median that is not stored, so the absolute ask cannot be
  /// reconstructed from a V25 receipt. Grading is a comparison against SETTLE
  /// PRICES, which arrive absolute; without this column there is nothing to compare
  /// them to. It is an operand, never a coordinate - no aggregate reads it.</para>
  ///
  /// <para><b>closed_at</b> - when the receipt's ask stopped standing (the sale
  /// confirm's timestamp, or the pull). The margin measurement (V41) asks "what
  /// settled AFTER our exit"; day-floored time_to_clear_days cannot answer that to
  /// the second. A receipt superseded by a newer receipt for the same listing has
  /// no closed_at and does not need one - its successor's created_at IS its
  /// close.</para>
  ///
  /// <para><b>interim_grade / final_grade</b> (+ their _at stamps) - the readout.
  /// NULL is never a default and never a verdict: it means the grader has not
  /// spoken. The interim board stamps SILENCE rather than leaving NULL precisely so
  /// "ungraded" stays unambiguous (see ReceiptGrading). The vocabulary in these two
  /// columns changed with the A9 reframe (2026-08-15) and the columns did not:
  /// rows written before it keep MISS / ON_TRACK / UNDERSOLD / WELL_TIMED /
  /// UNGRADEABLE, which are history and read verbatim wherever they are drawn.</para>
  ///
  /// Column-guarded ALTERs (the V22/V24/V25 model): re-running is a no-op, legacy
  /// rows read NULL across the board, which is the honest reading - they were
  /// written before anything scored them.
  /// </summary>
  internal static void ApplyV26(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, ReceiptsTable,
        "decided_price INTEGER",
        "closed_at INTEGER",
        "interim_grade TEXT",
        "interim_graded_at INTEGER",
        "final_grade TEXT",
        "final_graded_at INTEGER");

  /// <summary>
  /// THE RECEIPT'S TRUE-UP, once the listing is real: the absolute gil that got
  /// posted, and THE RETAINER THAT IS SELLING IT. Two columns, and they are the two
  /// the 08-10 ruling asked for in one sentence - "we should log what we put the item
  /// up for".
  ///
  /// <para>The price half is A12's: a receipt is inserted at DECISION time knowing
  /// only the anchor, but decided_price's spec is the gil the decision WROTE (V26),
  /// and the anchor is not the write. Golden Silk receipt 5943 banked 195 while the
  /// market got 190.</para>
  ///
  /// <para><b>THE RATIO ARM DIED WITH THE RATIO (doctrine sweep, ruled 2026-08-15).</b>
  /// This statement also used to recompute <c>position_in_lane</c> as @price / @median,
  /// which is why the method took a lane median at all. position_in_lane's writer is
  /// retired (a price ratio on a queue-grading table, read by nothing), so the arm has
  /// nothing to correct and the @median parameter is gone with it. Old rows keep the
  /// ratio they were written with; this statement no longer touches that column, which
  /// is the honest behaviour - a value from a retired formula should not be half-fixed
  /// by a pass that happens to walk past it.</para>
  ///
  /// <para><b>The retainer half is review ruling S13</b> (2026-08-12): "if Karen is
  /// selling the item, that is what the data needs to say". A cached post lists against
  /// a receipt recon wrote from whichever retainer had a spare slot to borrow a panel
  /// from, and the bell lists from whichever retainer it happens to be standing at -
  /// which is a different one whenever the 20/20 swap fires. Three readers key on that
  /// name (the On Market lane, receipt retention, and the sale confirm's PASS join), so
  /// a receipt left on the wrong retainer shows the row on the wrong shelf and never
  /// closes when it sells. An empty or null name changes nothing: an unknown retainer
  /// is not evidence that the old one is wrong.</para>
  ///
  /// <para>Lives here rather than in GilStorage for the <see cref="PruneReceipts"/>
  /// reason: Dalamud-free means linked-source testable, and this is a correction to
  /// the one column the reader surfaces quote verbatim.</para>
  /// </summary>
  internal static int TrueUpReceipt(SqliteConnection connection, long receiptId,
      long appliedPrice, string? retainerName)
  {
    if (appliedPrice <= 0) return 0;

    using var update = new SqliteCommand(
      // arm_id clears here too (08-23): the true-up runs exactly when a REAL
      // listing was written against this receipt, which is the moment a recon
      // Look stops being a Look. From here on the ask surfaces and the grader
      // treat the row as the standing ask it now genuinely describes.
      @"UPDATE decision_receipts
        SET decided_price = @price,
            retainer_name = CASE WHEN @ret <> '' THEN @ret ELSE retainer_name END,
            arm_id = NULL
        WHERE id = @id",
      connection);
    update.Parameters.AddWithValue("@price", appliedPrice);
    update.Parameters.AddWithValue("@ret", retainerName ?? "");
    update.Parameters.AddWithValue("@id", receiptId);
    return update.ExecuteNonQuery();
  }

  /// <summary>
  /// Receipt retention: keep the newest <paramref name="keep"/> receipts per LISTING
  /// LANE - (item, quality, retainer) - and prune the rest. Bounded by inventory,
  /// never by time (the SaleHistorySchema.RingKeep discipline: any age cutoff is
  /// wrong for half the market at once).
  ///
  /// <para><b>The retainer is in the key deliberately.</b> Scoping this to
  /// (item, quality) alone made the window a SHARED budget: two retainers holding
  /// the same item both wrote into one 3-row allowance, so a third retainer's only
  /// receipt could be pruned by the other two's pinches before anything ever
  /// graded it. The lane IS the listing, and each listing keeps its own last
  /// three. Same intent the window always had - it just now counts the right
  /// population.</para>
  ///
  /// <para>Retention stays grade-BLIND on purpose. Graded rows rotate out
  /// naturally as their own lane refills; a grade-aware prune would keep rows
  /// alive for their verdicts and quietly turn a bounded readout into an
  /// unbounded archive.</para>
  ///
  /// <para>Lives here rather than in GilStorage for the SaleHistorySchema reason:
  /// Dalamud-free means linked-source testable, and a retention rule nobody can
  /// test is a retention rule nobody can trust. The pure pick is still
  /// <see cref="DecisionReceipts.ReceiptsToPrune"/>; this owns only the SQL around
  /// it. Returns the number of rows pruned.</para>
  /// </summary>
  internal static int PruneReceipts(SqliteConnection connection, SqliteTransaction? tx,
      uint itemId, bool isHq, string retainerName, int keep)
  {
    var ids = new System.Collections.Generic.List<long>();
    using (var read = new SqliteCommand(
      @"SELECT id FROM decision_receipts
        WHERE item_id = @iid AND is_hq = @hq AND retainer_name = @ret
        ORDER BY created_at DESC, id DESC",
      connection, tx))
    {
      read.Parameters.AddWithValue("@iid", (long)itemId);
      read.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      read.Parameters.AddWithValue("@ret", retainerName);
      using var reader = read.ExecuteReader();
      while (reader.Read()) ids.Add(reader.GetInt64(0));
    }

    var pruned = 0;
    foreach (var id in DecisionReceipts.ReceiptsToPrune(ids, keep))
    {
      using var del = new SqliteCommand("DELETE FROM decision_receipts WHERE id = @id", connection, tx);
      del.Parameters.AddWithValue("@id", id);
      pruned += del.ExecuteNonQuery();
    }
    return pruned;
  }
}
