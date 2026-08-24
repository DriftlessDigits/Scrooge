using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// <c>decision_receipts</c>: one row per priced decision, opened when the decision is
/// made and closed when the world answers.
///
/// <para>A receipt outlives the decision on purpose. It is opened with the ask, filled
/// in when the item sells, and closed unfilled when the item is pulled, vanishes, or
/// simply never clears — because a decision that produced no sale is still evidence,
/// and dropping it would grade us only on the wins.</para>
/// </summary>
internal static partial class GilStorage
{
  // =========================================================================
  // Decision receipts (V19) — one row per pricing decision, relative coords
  // =========================================================================

  /// <summary>
  /// Writes one decision receipt (design Section 4) and prunes the LISTING LANE -
  /// (item, quality, retainer) - back to its most-recent
  /// <paramref name="keepPerLane"/> receipts (bounded by inventory, not time; each
  /// listing keeps its own window rather than sharing one with every other
  /// retainer holding the same item). All coordinates are relative and
  /// item-agnostic; arm_id + item_category +
  /// stack coords ride from day one. The outcome join (time_to_clear / outcome_state)
  /// is left open — a GilTrack confirm or an evict fills it later, never here.
  ///
  /// <para><paramref name="undercutPosture"/> is the V45 stance tag - the pegged
  /// knobs this price was written under, composed by <see cref="UndercutPosture"/>.
  /// Written and never read back here; it is the undercut report card's corpus.</para>
  /// </summary>
  internal static long InsertDecisionReceipt(uint itemId, bool isHq, string retainerName,
      string itemCategory, string? armId, DecisionReceipts.ReceiptCoordinates c,
      string outcome, string evidence, int keepPerLane = 3, long? decidedPrice = null,
      string? undercutPosture = null)
  {
    using var tx = Connection.BeginTransaction();

    using (var insert = new SqliteCommand(
      @"INSERT INTO decision_receipts
          (created_at, item_id, is_hq, retainer_name, item_category, arm_id,
           board_depth, velocity_per_day,
           lane_n, lane_spread, weighted_lane_age_days, forecast_clearing_days,
           quantity, lane_stack_norm, outcome, evidence, outcome_state,
           band_low_ratio, band_high_ratio, segment_n, segment_cut,
           queue_position, cluster_size, decided_price, board_total,
           crasher_floor, crasher_ceiling, cluster_floor, cluster_ceiling, crowd_behind,
           undercut_posture, seat_at_write, shadow_price, shadow_seat, shadow_defense)
        VALUES (@now, @iid, @hq, @ret, @cat, @arm,
                @depth, @vel,
                @n, @spread, @age, @forecast,
                @qty, @stacknorm, @outcome, @evidence, 'open',
                @bandlow, @bandhigh, @segn, @segcut,
                @queuepos, @cluster, @decided, @total,
                @crashfloor, @crashceil, @clusfloor, @clusceil, @crowd,
                @posture, @seat, @shadowprice, @shadowseat, @shadowdefense)",
      _connection, tx))
    {
      insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
      insert.Parameters.AddWithValue("@iid", (long)itemId);
      insert.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      insert.Parameters.AddWithValue("@ret", retainerName);
      insert.Parameters.AddWithValue("@cat", itemCategory);
      insert.Parameters.AddWithValue("@arm", (object?)armId ?? DBNull.Value);
      // position_in_lane and undercut_target_ratio are NOT in the column list above:
      // the doctrine sweep (2026-08-15) retired both writers, so a receipt written
      // from today leaves them NULL. The columns still exist and older rows still
      // carry their values - see DecisionReceipts.ReceiptCoordinates.
      insert.Parameters.AddWithValue("@depth", c.BoardDepth);
      insert.Parameters.AddWithValue("@vel", (object?)c.VelocityPerDay ?? DBNull.Value);
      insert.Parameters.AddWithValue("@n", c.LaneSampleCount);
      insert.Parameters.AddWithValue("@spread", c.LaneSpread);
      insert.Parameters.AddWithValue("@age", c.LaneWeightedAgeDays);
      insert.Parameters.AddWithValue("@forecast", (object?)c.ForecastClearingDays ?? DBNull.Value);
      insert.Parameters.AddWithValue("@qty", c.Quantity);
      insert.Parameters.AddWithValue("@stacknorm", (object?)c.LaneStackNorm ?? DBNull.Value);
      insert.Parameters.AddWithValue("@outcome", outcome);
      insert.Parameters.AddWithValue("@evidence", evidence);
      insert.Parameters.AddWithValue("@bandlow", (object?)c.BandLowRatio ?? DBNull.Value);
      insert.Parameters.AddWithValue("@bandhigh", (object?)c.BandHighRatio ?? DBNull.Value);
      insert.Parameters.AddWithValue("@segn", c.SegmentCount);
      insert.Parameters.AddWithValue("@segcut", c.SegmentCut);
      insert.Parameters.AddWithValue("@queuepos", (object?)c.QueuePosition ?? DBNull.Value);
      insert.Parameters.AddWithValue("@cluster", c.ClusterSize > 0 ? c.ClusterSize : DBNull.Value);
      // competitor_position is NOT in the column list above: its writer retired
      // 08-23 (the audit found every post-V30 row reading 0 - "crashers don't
      // count" made the coordinate incapable of saying "not first"). The column
      // stays, old rows keep their values, new rows leave it NULL - the same
      // retirement shape as position_in_lane. seat_at_write is the honest
      // replacement: the whole-queue 1-based seat the write actually bought.
      insert.Parameters.AddWithValue("@seat", (object?)c.SeatAtWrite ?? DBNull.Value);
      // The V47 shadow trio: the 3.1 queue-doctrine candidate's answer on the
      // same board, banked beside the live decision. Write-only - 3.1's ruling
      // session is the reader.
      insert.Parameters.AddWithValue("@shadowprice", (object?)c.ShadowPrice ?? DBNull.Value);
      insert.Parameters.AddWithValue("@shadowseat", (object?)c.ShadowSeat ?? DBNull.Value);
      insert.Parameters.AddWithValue("@shadowdefense", (object?)c.ShadowDefense ?? DBNull.Value);
      // The absolute ask (V26). An OPERAND, not a coordinate - every other column
      // here is deliberately relative, which is exactly why the grader cannot
      // reconstruct the gil it needs to compare against settle prices. Null when
      // the caller could not name a price; such a receipt is simply ungradeable.
      insert.Parameters.AddWithValue("@decided", decidedPrice is long p && p > 0 ? p : (object)DBNull.Value);
      // The x-of-y coordinate (V34): what the game said the whole board held.
      insert.Parameters.AddWithValue("@total", (object?)c.BoardTotal ?? DBNull.Value);
      // The V35 spans: what the stepped-over rows and the anchor line asked in
      // gil. Absolutes like decided_price - operands for a reader.
      insert.Parameters.AddWithValue("@crashfloor", (object?)c.CrasherFloor ?? DBNull.Value);
      insert.Parameters.AddWithValue("@crashceil", (object?)c.CrasherCeiling ?? DBNull.Value);
      insert.Parameters.AddWithValue("@clusfloor", (object?)c.ClusterFloor ?? DBNull.Value);
      insert.Parameters.AddWithValue("@clusceil", (object?)c.ClusterCeiling ?? DBNull.Value);
      // The V37 operand: what the stepped-over pack was outnumbered BY.
      insert.Parameters.AddWithValue("@crowd", (object?)c.CrowdBehind ?? DBNull.Value);
      // The V45 stance: which pegs were in force while this price was written.
      // Null when the caller did not name one - a receipt that cannot say what it
      // ran under says nothing, rather than banking today's config as if it were
      // the config of the moment.
      insert.Parameters.AddWithValue("@posture", (object?)undercutPosture ?? DBNull.Value);
      insert.ExecuteNonQuery();
    }

    long receiptId;
    using (var rowId = new SqliteCommand("SELECT last_insert_rowid()", Connection, tx))
      receiptId = (long)rowId.ExecuteScalar()!;

    // Retention: keep the newest N for this LISTING LANE - (item, quality,
    // retainer) - and prune the rest. Same transaction as the insert, so the
    // table can never be observed over-full. The rule and its reasoning live in
    // MarketMemorySchema, where they are testable.
    MarketMemorySchema.PruneReceipts(Connection, tx, itemId, isHq, retainerName, keepPerLane);

    tx.Commit();
    return receiptId;
  }

  /// <summary>
  /// The V47 shadow corpus, counted for the sitrep (ruled 08-23): how many
  /// receipts carry the queue-doctrine shadow, and on how many the two worlds
  /// made a DIFFERENT STEP DECISION - live stepped where the shadow joins, or
  /// joined where it steps. GEOMETRY, never gil: the shadow's ask is one gil
  /// under its front by construction while live writes wear posture (a
  /// Humanized pinch, a gentleman's match), so a price comparison counts every
  /// posture delta as a disagreement - the first live gauge read 123/135
  /// "divergent" when the step decisions actually differed on 4 (08-23,
  /// caught within the hour). Held rows sit out of BOTH counts (final pass,
  /// 08-23): a hold banks a shadow but writes nothing live, so it can never be
  /// divergent - counting it in Banked inflated the denominator and read the
  /// divergence rate low. Banked = rows where both worlds wrote. The 3.1 ruling
  /// session reads the rows themselves; this is the gauge that says whether
  /// there is a corpus yet.
  /// </summary>
  internal static (int Banked, int Divergent) CountDoctrineShadow()
  {
    using var cmd = new SqliteCommand(
      @"SELECT COUNT(*),
               COALESCE(SUM(CASE WHEN queue_position IS NOT NULL
                                  AND ((queue_position > 0 AND shadow_defense = 'front')
                                    OR (queue_position = 0 AND shadow_defense <> 'front'))
                                 THEN 1 ELSE 0 END), 0)
          FROM decision_receipts
         WHERE shadow_price IS NOT NULL
           AND queue_position IS NOT NULL", Connection);
    using var reader = cmd.ExecuteReader();
    return reader.Read()
      ? (Convert.ToInt32(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)))
      : (0, 0);
  }

  /// <summary>
  /// The decided_price TRUE-UP (A12, walk #2): the receipt is written at
  /// decision time, when only the ANCHOR is known - but decided_price's spec is
  /// "the ABSOLUTE gil the decision wrote" (V26), and the anchor is not the
  /// write. Golden Silk receipt 5943 banked 195 (the anchor) while the market
  /// got 190 (the undercut) - the grader would have judged a price nobody was
  /// asking. Called once the applied price is known, with the same honest
  /// accounting rule the run summary uses (held = old price stands).
  ///
  /// <para>position_in_lane used to ride along here as decided_price / median. The
  /// doctrine sweep (2026-08-15) retired that ratio's writer, so the arm and its lane
  /// median argument are gone - what is left is the record of what we wrote.</para>
  ///
  /// <para><paramref name="retainerName"/> is trued up in the same statement (review
  /// ruling S13): a cached post lists against a receipt recon wrote at a different
  /// retainer, and the on-market lane, the retention window and the sale-outcome join
  /// all key on that name. The SQL and its reasoning live in
  /// <see cref="MarketMemorySchema.TrueUpReceipt"/>, where they are testable.</para>
  /// </summary>
  internal static void UpdateReceiptDecidedPrice(long receiptId, long appliedPrice,
      string? retainerName = null)
    => MarketMemorySchema.TrueUpReceipt(Connection, receiptId, appliedPrice, retainerName);

  /// <summary>
  /// Outcome join on a GilTrack sale confirm (design Section 4): fills time_to_clear
  /// on every OPEN receipt for the sold (item, quality) and marks it cleared. The V13
  /// sold_after_days capture is the wiring model — the confirm is the only thing that
  /// closes a receipt as sold. Also upgrades the item's own market_events disappearance
  /// rows to sold/confirmed (same certainty-tier discipline; foreign rows never move).
  /// Returns the number of receipts cleared.
  /// </summary>
  internal static int FillReceiptOutcomeOnSale(uint itemId, bool isHq, long soldAtUnix,
      string retainerName = "")
  {
    // Read open receipts, compute per-row time_to_clear from their created_at.
    // 'gone_unobserved' is included on purpose: the pinch reconciler closes a
    // vanished lane provisionally, and the sale confirm is the better fact -
    // evidence upgrades ambiguity, never the reverse.
    var open = new List<(long Id, long CreatedAt)>();
    using (var read = new SqliteCommand(
      // Looks sit out (08-23): a sale confirm closes LISTINGS, and an un-adopted
      // recon receipt never described one - 'cleared' on it would bank a
      // time_to_clear for an ask that was never up.
      @"SELECT id, created_at FROM decision_receipts
        WHERE item_id = @iid AND is_hq = @hq
          AND outcome_state IN ('open', 'gone_unobserved')
          AND (arm_id IS NULL OR arm_id <> 'recon')",
      _connection))
    {
      read.Parameters.AddWithValue("@iid", (long)itemId);
      read.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      using var reader = read.ExecuteReader();
      while (reader.Read()) open.Add((reader.GetInt64(0), reader.GetInt64(1)));
    }

    foreach (var (id, createdAt) in open)
    {
      using var upd = new SqliteCommand(
        @"UPDATE decision_receipts SET time_to_clear_days = @days, outcome_state = 'cleared',
                 closed_at = @sold
          WHERE id = @id",
        _connection);
      upd.Parameters.AddWithValue("@days", DecisionReceipts.TimeToClearDays(createdAt, soldAtUnix));
      upd.Parameters.AddWithValue("@sold", soldAtUnix);
      upd.Parameters.AddWithValue("@id", id);
      upd.ExecuteNonQuery();
    }

    // A9 PASS: our listing sold, so the spot it took worked. Deliberately unscored
    // beyond that - see the asymmetry note on ReceiptGrading. Best-effort and
    // narrow: the sale confirm names ONE retainer, so only that retainer's
    // receipts can honestly claim it, and only the NEWEST open ungraded one - the
    // older opens are superseded asks that were not standing when the sale landed,
    // and they belong to the final backfill instead.
    //
    // LEFT UNSTAMPED ON PURPOSE: a sale arriving with no retainer name, and the
    // second of two distinct listings of the same item+quality on the SAME
    // retainer (stack splits). Nothing on the confirm distinguishes them, so one
    // PASS lands and the other listing's receipt waits for its own sale or its own
    // supersession. A guessed PASS would put a sale on a receipt that never had
    // one, and every verdict downstream reads that as "the seat got its turn"; an
    // unstamped row is merely quiet.
    if (!string.IsNullOrEmpty(retainerName))
    {
      using var pass = new SqliteCommand(
        @"UPDATE decision_receipts
          SET interim_grade = 'PASS', interim_graded_at = @at
          WHERE id = (SELECT id FROM decision_receipts
                      WHERE item_id = @iid AND is_hq = @hq AND retainer_name = @ret
                        AND interim_grade IS NULL
                        AND (arm_id IS NULL OR arm_id <> 'recon')
                      ORDER BY created_at DESC, id DESC LIMIT 1)",
        _connection);
      pass.Parameters.AddWithValue("@at", soldAtUnix);
      pass.Parameters.AddWithValue("@iid", (long)itemId);
      pass.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      pass.Parameters.AddWithValue("@ret", retainerName);
      pass.ExecuteNonQuery();
    }

    // Upgrade own disappearance events to confirmed-sold (foreign rows untouched).
    using (var evUpd = new SqliteCommand(
      @"UPDATE market_events SET resolution = 'sold', certainty = 'confirmed'
        WHERE item_id = @iid AND is_hq = @hq AND kind = 'disappeared'
          AND is_own = 1 AND resolution = 'pulled'",
      _connection))
    {
      evUpd.Parameters.AddWithValue("@iid", (long)itemId);
      evUpd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      evUpd.ExecuteNonQuery();
    }

    return open.Count;
  }

  /// <summary>
  /// Every OPEN receipt, for the Ledger's On Market tab. Reads only - the tab is a
  /// readout of what we already banked and can never cause a fetch. Item names are
  /// deliberately NOT resolved here (that is a game read); the window fills them,
  /// exactly as it does for the Listed pile. The supersession rule that picks ONE
  /// receipt per listing lane is pure and lives in <see cref="OnMarket.Standing"/> -
  /// a SQL window function would put the tab's central honesty judgement somewhere
  /// no test can reach it.
  ///
  /// <para>Bounded by construction: receipt retention keeps 3 rows per lane, and
  /// only open ones come back, so this is a small read even on a fat book.</para>
  ///
  /// <para>Un-adopted recon receipts are NOT on market (08-23, the Neo-Ishgardian
  /// Sword: a Look banked at recon wore this tab as a standing ask). arm_id
  /// 'recon' rows sit out until the hawk adopts them - the true-up clears the
  /// mark the instant a real listing exists.</para>
  /// </summary>
  internal static List<ReceiptLine> GetOpenReceipts()
  {
    var rows = new List<ReceiptLine>();
    using var cmd = new SqliteCommand(
      @"SELECT id, created_at, item_id, is_hq, retainer_name, quantity,
               decided_price, queue_position, outcome_state, interim_grade,
               cluster_size, crasher_floor, crasher_ceiling, cluster_floor, cluster_ceiling,
               crowd_behind
        FROM decision_receipts
        WHERE outcome_state = 'open'
          AND (arm_id IS NULL OR arm_id <> 'recon')
        ORDER BY created_at DESC, id DESC",
      _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      rows.Add(new ReceiptLine(
        reader.GetInt64(0),
        reader.GetInt64(1),
        (uint)reader.GetInt64(2),
        reader.GetInt32(3) != 0,
        "", // name resolved window-side
        reader.IsDBNull(4) ? "" : reader.GetString(4),
        reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetInt32(7),
        reader.IsDBNull(8) ? "" : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetInt64(11),
        reader.IsDBNull(12) ? null : reader.GetInt64(12),
        reader.IsDBNull(13) ? null : reader.GetInt64(13),
        reader.IsDBNull(14) ? null : reader.GetInt64(14),
        reader.IsDBNull(15) ? null : reader.GetInt32(15)));
    return rows;
  }

  /// <summary>
  /// The newest banked ask trial for a variant, any retainer - the row memoir's
  /// "we already ran this experiment" line (Drift, 08-02). One indexed point read;
  /// null when the variant was never listed on record.
  /// </summary>
  internal static (long Price, long CreatedAt, long? ClosedAt, string State, int? TimeToClearDays)?
    GetAskTrial(uint itemId, bool isHq)
  {
    using var cmd = new SqliteCommand(
      // Un-adopted recon receipts sit out (08-23): a Look is not an ask trial -
      // nothing was listed, so "an ask at N is still open" over one is a lie
      // the reader has no way to catch. Adoption clears arm_id and the row
      // re-enters as the ask it became.
      @"SELECT decided_price, created_at, closed_at, outcome_state, time_to_clear_days
        FROM decision_receipts
        WHERE item_id = @iid AND is_hq = @hq AND decided_price IS NOT NULL
          AND (arm_id IS NULL OR arm_id <> 'recon')
        ORDER BY created_at DESC, id DESC LIMIT 1",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return null;
    return (reader.GetInt64(0), reader.GetInt64(1),
      reader.IsDBNull(2) ? null : reader.GetInt64(2),
      reader.IsDBNull(3) ? "" : reader.GetString(3),
      reader.IsDBNull(4) ? null : reader.GetInt32(4));
  }

  /// <summary>
  /// THE RECEIPT TRAIL for one variant (item 9, 08-06): the last
  /// <paramref name="limit"/> asks we wrote for it on any retainer, open or
  /// closed, with the A9 stamps where the grader reached them. The Ledger's
  /// detail pane draws these; the selection and ordering rules are pure and
  /// live in <see cref="BoardDetail.Trail"/>, so this only fetches.
  ///
  /// <para>One indexed read (ix_decision_receipts_item), bounded twice over -
  /// by the LIMIT here and by receipt retention upstream, which keeps a handful
  /// of rows per lane. It is called once per selected row per refresh, never
  /// per frame.</para>
  /// </summary>
  internal static List<TrailReceipt> GetReceiptTrail(uint itemId, bool isHq, int limit = 5)
  {
    var rows = new List<TrailReceipt>();
    using var cmd = new SqliteCommand(
      @"SELECT id, created_at, retainer_name, decided_price, outcome_state,
               interim_grade, final_grade, time_to_clear_days, queue_position,
               arm_id
        FROM decision_receipts
        WHERE item_id = @iid AND is_hq = @hq
        ORDER BY created_at DESC, id DESC
        LIMIT @lim",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@lim", Math.Max(1, limit));
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      rows.Add(new TrailReceipt(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.IsDBNull(2) ? "" : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetInt64(3),
        reader.IsDBNull(4) ? "" : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetInt32(8),
        IsLook: !reader.IsDBNull(9) && reader.GetString(9) == "recon"));
    return rows;
  }

  /// <summary>
  /// Closes every OPEN receipt for a pulled/evicted (item, quality) as never-cleared
  /// (design Section 4): the listing left the board before it sold, so the spot it
  /// took never got its test — that absence is the finding, not a gap to leave open.
  /// </summary>
  internal static int CloseReceiptsNeverCleared(uint itemId, bool isHq)
  {
    using var cmd = new SqliteCommand(
      // Looks sit out (08-23): 'never_cleared' means a LISTING left the board
      // unsold, and a recon receipt never had one. The row stays open-and-marked
      // until adoption or retention prunes it.
      @"UPDATE decision_receipts SET outcome_state = 'never_cleared', closed_at = @now
        WHERE item_id = @iid AND is_hq = @hq AND outcome_state = 'open'
          AND (arm_id IS NULL OR arm_id <> 'recon')",
      _connection);
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    return cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// THE PINCH RECONCILER (08-02): a full sell-list read is proof of absence. Every
  /// OPEN receipt on this retainer whose (item, quality) lane was not among the
  /// observed listings closes as 'gone_unobserved' - it sold, expired, or was pulled
  /// while nobody watched. This is the close OnMarket.cs promised ("stays open until
  /// the next pinch reconciles it") and never had; without it a listing that left
  /// the board unobserved stood open forever and the tab drew a ghost.
  ///
  /// <para>Provisional by design: a GilTrack sale confirm arriving later upgrades
  /// the close to 'cleared' (<see cref="FillReceiptOutcomeOnSale"/> accepts both
  /// states), so ordering against sale-history reconciliation cannot lose a grade -
  /// running first costs a moment of pessimism, never a fact.</para>
  /// </summary>
  internal static int CloseReceiptsGoneUnobserved(string retainerName,
    IReadOnlySet<(uint ItemId, bool IsHq)> seenLanes)
  {
    var stale = new List<long>();
    using (var read = new SqliteCommand(
      // Looks sit out (08-23): absence from the sell list proves nothing about
      // a receipt that never described a listing.
      @"SELECT id, item_id, is_hq FROM decision_receipts
        WHERE retainer_name = @ret AND outcome_state = 'open'
          AND (arm_id IS NULL OR arm_id <> 'recon')",
      _connection))
    {
      read.Parameters.AddWithValue("@ret", retainerName);
      using var reader = read.ExecuteReader();
      while (reader.Read())
      {
        var lane = ((uint)reader.GetInt64(1), reader.GetInt64(2) != 0);
        if (!seenLanes.Contains(lane)) stale.Add(reader.GetInt64(0));
      }
    }

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    foreach (var id in stale)
    {
      using var upd = new SqliteCommand(
        @"UPDATE decision_receipts SET outcome_state = 'gone_unobserved', closed_at = @now
          WHERE id = @id AND outcome_state = 'open'",
        _connection);
      upd.Parameters.AddWithValue("@now", now);
      upd.Parameters.AddWithValue("@id", id);
      upd.ExecuteNonQuery();
    }
    return stale.Count;
  }

  /// <summary>
  /// A9 grading, lazily, on recurrence: the item came back through the pipeline, so
  /// the evidence that grades its last visit is in hand RIGHT NOW. No timers, no
  /// collectors, no new reads - the tape and the board were already fetched to
  /// price this pinch. Called BEFORE the new receipt is written, so "the newest
  /// receipt" still means the one being judged.
  ///
  /// <para>Two passes over the same handful of rows (retention keeps 3 per
  /// item+quality, so this is never a scan):</para>
  ///
  /// <para><b>Interim</b> - the newest still-open receipt is by definition still
  /// held (we are repricing it). Grade it once against the settles since it was
  /// written and the board in front of it now. Since the A9 reframe the price test
  /// only OPENS the question and the queue measurement answers it, so the board
  /// read gathered below is the load-bearing operand, not the tape.</para>
  ///
  /// <para><b>Chain verdicts</b> - every receipt whose ask has stopped standing and
  /// still has no final grade. This pass is TAPE-FREE by design: the verdict on a
  /// decision is the direction of the NEXT decision (a successor at a lower price
  /// says the market walked down through us; higher says the board healed above
  /// us), and where there is no successor, the outcome state says whether the seat
  /// got its turn. rows[i+1] is already in hand, so the whole chain walk is a loop
  /// over the handful of rows we just read.</para>
  ///
  /// <para><b>The margin measurement</b> - computed in the same walk, and the only
  /// thing here that still reads the tape. It answers the question the verdicts
  /// structurally cannot ("a sold ask reads CLEARED whether or not it left money on
  /// the table") with gil rather than a stamp. See
  /// <see cref="ReceiptGrading.MarginDonated"/> for the 0-vs-NULL contract, which
  /// is the whole value of the column.</para>
  ///
  /// <para>Every stamp lands once and never moves - the WHERE clauses require the
  /// column to still be NULL, so a re-pinch inside the same window cannot rewrite
  /// a verdict with fresher evidence than the verdict was made on.</para>
  ///
  /// <para>Grades feed NOTHING - no price logic reads them, nothing tunes off them.
  /// They are drawn for a human on the detail pane's receipt trail and are read
  /// nowhere else. margin_donated is the same: its two RULED consumers are the
  /// Phase-4 revival trigger's frequency and magnitude reads, and neither is built.
  /// A triage-case trap read it briefly and died with the traps register (3b-1), so the
  /// column is back to what the corpus is for: banked write-only, food for the
  /// scoreboard era. No price logic reads it, which is the part that matters.</para>
  /// </summary>
  internal static void GradeReceiptsOnRecurrence(uint itemId, bool isHq, string retainerName,
      IReadOnlyList<ReceiptGrading.Settle> qualityMatchedSettles,
      IReadOnlyList<long> foreignBoardPrices)
  {
    var rows = new List<(long Id, long CreatedAt, long? DecidedPrice, int? QueuePosition,
                         string State, long? ClosedAt, bool HasInterim, bool HasFinal,
                         bool HasMargin)>();
    using (var read = new SqliteCommand(
      // Un-adopted recon receipts sit out of the WHOLE walk (08-23): a Look
      // never stood on the board, so grading it is a grade of nothing - and as
      // a successor it must not close a real ask's window either (a Look moved
      // no price). Adoption clears arm_id and the row re-enters.
      @"SELECT id, created_at, decided_price, queue_position, outcome_state, closed_at,
               interim_grade, final_grade, margin_donated
        FROM decision_receipts
        WHERE item_id = @iid AND is_hq = @hq AND retainer_name = @ret
          AND (arm_id IS NULL OR arm_id <> 'recon')
        ORDER BY created_at ASC, id ASC",
      _connection))
    {
      read.Parameters.AddWithValue("@iid", (long)itemId);
      read.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      read.Parameters.AddWithValue("@ret", retainerName);
      using var reader = read.ExecuteReader();
      while (reader.Read())
        rows.Add((
          reader.GetInt64(0),
          reader.GetInt64(1),
          reader.IsDBNull(2) ? null : reader.GetInt64(2),
          reader.IsDBNull(3) ? null : reader.GetInt32(3),
          reader.IsDBNull(4) ? "" : reader.GetString(4),
          reader.IsDBNull(5) ? null : reader.GetInt64(5),
          !reader.IsDBNull(6),
          !reader.IsDBNull(7),
          !reader.IsDBNull(8)));
    }
    if (rows.Count == 0) return;

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // --- Interim: the newest open receipt, the one we are repricing right now ---
    var newest = rows[^1];
    if (!newest.HasInterim && newest.State == "open")
    {
      var since = new List<ReceiptGrading.Settle>();
      foreach (var s in qualityMatchedSettles)
        if (s.SaleTime > newest.CreatedAt) since.Add(s);

      int? belowNow = null;
      if (newest.DecidedPrice is long ask && foreignBoardPrices.Count > 0)
      {
        var n = 0;
        foreach (var p in foreignBoardPrices) if (p < ask) n++;
        belowNow = n;
      }

      var grade = ReceiptGrading.GradeInterim(new ReceiptGrading.InterimOperands(
        newest.DecidedPrice, newest.QueuePosition, since, belowNow));
      if (grade is ReceiptGrading.InterimGrade g)
        StampReceipt(newest.Id, "interim_grade", "interim_graded_at", ReceiptGrading.Name(g), now);
    }

    // --- The chain walk: verdicts, then the margin measurement beside them ---
    long? oldestBanked = null;
    foreach (var s in qualityMatchedSettles)
      if (oldestBanked is not long o || s.SaleTime < o) oldestBanked = s.SaleTime;

    for (var i = 0; i < rows.Count; i++)
    {
      var r = rows[i];
      var hasSuccessor = i + 1 < rows.Count;

      if (!r.HasFinal)
      {
        var verdict = ReceiptGrading.GradeChain(new ReceiptGrading.ChainOperands(
          r.DecidedPrice, hasSuccessor,
          hasSuccessor ? rows[i + 1].DecidedPrice : null,
          OutcomeStateOf(r.State)));
        if (verdict is ReceiptGrading.FinalGrade f)
          StampReceipt(r.Id, "final_grade", "final_graded_at", ReceiptGrading.Name(f), now);
      }

      if (r.HasMargin) continue;

      // Superseded beats sold, the same rule the verdicts use: a REPRICED ask
      // stopped standing the moment its successor was written, which is earlier -
      // and truer - than any later sale on the lane. But only a successor that
      // actually moved the price closes the ask: a same-price successor is a
      // re-affirmation (the ask never left the board), so - exactly like the
      // verdict - the margin lands on the LAST receipt of a same-price run.
      // Measuring a re-affirmed row from its re-affirm time would stamp margins
      // on asks that were still standing, once per re-check, and inflate the very
      // frequency read the Phase-4 trigger exists to keep honest. Otherwise the
      // close is the one stamped on the row, whatever ended it: the margin asks
      // what the market paid after our ask left, and a pull leaves the board
      // exactly as a sale does.
      long? closedAt = hasSuccessor
                     ? (rows[i + 1].DecidedPrice is long np && r.DecidedPrice is long rp && np != rp
                         ? rows[i + 1].CreatedAt : null)
                     : r.State != "open" ? r.ClosedAt
                     : null; // still standing (or re-affirmed) - nothing to measure from yet
      if (closedAt is null) continue;

      var after = new List<ReceiptGrading.Settle>();
      foreach (var s in qualityMatchedSettles)
        if (s.SaleTime > closedAt) after.Add(s);

      var margin = ReceiptGrading.MarginDonated(new ReceiptGrading.MarginOperands(
        r.DecidedPrice, closedAt, oldestBanked, after));
      if (margin is long donated)
        StampReceipt(r.Id, "margin_donated", null, donated, now);
    }
  }

  /// <summary>
  /// The stored outcome_state, as the enum the pure core reasons in. An unknown
  /// spelling reads as Open - the fail-quiet direction, since Open is the one state
  /// that stamps no verdict at all, and a state this build does not understand must
  /// not be allowed to convict a receipt.
  /// </summary>
  private static DecisionReceipts.OutcomeState OutcomeStateOf(string state) => state switch
  {
    "cleared" => DecisionReceipts.OutcomeState.Cleared,
    "never_cleared" => DecisionReceipts.OutcomeState.NeverCleared,
    "gone_unobserved" => DecisionReceipts.OutcomeState.GoneUnobserved,
    _ => DecisionReceipts.OutcomeState.Open,
  };

  /// <summary>
  /// Writes one A9 value onto a receipt, once. The IS NULL guard is the
  /// idempotence: a verdict is a judgement on the evidence that existed when it was
  /// made, and a later pinch with more tape does not get to relitigate it. The same
  /// guard carries the margin measurement, which is a number rather than a stamp
  /// and has no _at column of its own - pass a null <paramref name="stampColumn"/>
  /// and only the value lands. Column names are compile-time literals from this
  /// file; the values are always parameters.
  /// </summary>
  private static void StampReceipt(long receiptId, string valueColumn, string? stampColumn,
      object value, long atUnix)
  {
    var stamp = stampColumn is null ? "" : $", {stampColumn} = @at";
    using var cmd = new SqliteCommand(
      $@"UPDATE decision_receipts SET {valueColumn} = @v{stamp}
         WHERE id = @id AND {valueColumn} IS NULL",
      _connection);
    cmd.Parameters.AddWithValue("@v", value);
    if (stampColumn is not null) cmd.Parameters.AddWithValue("@at", atUnix);
    cmd.Parameters.AddWithValue("@id", receiptId);
    cmd.ExecuteNonQuery();
  }
}
