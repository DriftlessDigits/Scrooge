using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// Decision receipts - the pure core (LanePricing/StandingMemory mold: no game reads,
/// no storage, no statics, linked into Scrooge.Tests). One receipt per pricing
/// decision; its coordinates are ALL RELATIVE (item-agnostic), so a lane in dye
/// scores the same shape as a lane in crafting mats (design Section 4).
///
/// The decision-time coordinates come from the live board + lane at the moment of
/// decision, NOT from the event log (design Section 7). The outcome join
/// (time-to-clear) is filled LATER by a GilTrack confirm, never at write time; an
/// evict/pull closes the row as never-cleared (design Section 4).
///
/// <para>A9 grades these rows (see <see cref="ReceiptGrading"/>), and the verdicts
/// score the DECISION - which spot in the line it took - never the price. Grading
/// carries a NAMED ASYMMETRY that will make the stamps misread if you meet them
/// cold: a sold listing always reads PASS/CLEARED, so a clean board is a
/// survivorship board and never a scoreboard. The argument lives once, on
/// <see cref="ReceiptGrading"/>; the counterweight is the margin_donated
/// measurement banked beside the verdicts.</para>
/// </summary>
internal static class DecisionReceipts
{
  /// <summary>The lifecycle of a receipt's outcome column (design Section 4).</summary>
  internal enum OutcomeState
  {
    /// <summary>Written, waiting on a sale confirm or a pull. The join is unfilled.</summary>
    Open,
    /// <summary>A GilTrack confirm filled time-to-clear.</summary>
    Cleared,
    /// <summary>An evict/pull closed the listing before it sold - the spot it took never got its test.</summary>
    NeverCleared,
    /// <summary>
    /// The pinch reconciler found the lane absent from a full sell-list read: the
    /// listing left the board while nobody watched (sold offline, expired, or pulled
    /// by another hand). Provisional - a late sale confirm upgrades it to Cleared.
    /// </summary>
    GoneUnobserved,
  }

  // The race-policy resolver (floor-wait / mid-queue / join arms) lived here
  // until A10. It resolved a policy at a site that no longer exists: there is no
  // race branch to take a position in, only a queue to cut into. The arm_id
  // column survives untouched - when the A/B lab lands, its arms will be about
  // insertion position, and this is where its resolver goes.

  /// <summary>
  /// The decision-time facts a receipt is computed from. Everything here is read off
  /// the live board + lane at the moment of decision; the core turns the absolute
  /// numbers into item-agnostic ratios.
  ///
  /// <para><b>UndercutTarget left with its ratio (doctrine sweep, 2026-08-15).</b> The
  /// in-lane listing being undercut was carried here for exactly one consumer -
  /// undercut_target_ratio - and when that column's writer was retired the fact had no
  /// reader left anywhere in the plugin. Keeping an input nobody computes from is the
  /// same apparatus the sweep was cutting, one layer up.</para>
  /// </summary>
  internal readonly record struct ReceiptInputs(
    long DecidedPrice,          // the price the decision landed on (or the held price)
    double LaneMedian,          // recency-weighted clearing price
    int LaneSampleCount,        // lane confidence: n
    double LaneWeightedAgeDays, // LaneModel.WeightedAgeDays
    double LaneSpread,          // lane confidence: relative spread of evidence (0 when unknown)
    int BoardDepth,             // competing foreign listings on the board
    double? VelocityPerDay,     // sales/day (the active segment, or packet/community)
    int Quantity,               // this listing's stack size (observational only - Scrooge never splits)
    double? LaneStackNorm,      // typical sale quantity in the lane (observational only)
    double BandLow = 0,         // LaneModel.BandLow - weighted p25 of the segment (A4c)
    double BandHigh = 0,        // LaneModel.BandHigh - weighted p75
    int SegmentCount = 0,       // how many sales were still voting (RegimeSegmentModel.SegmentCount)
    string SegmentCut = "",     // why the electorate was cut: None / Cliff / Glide
    int? QueuePosition = null,  // foreign rows still ahead (cheaper) after listing; 0 = front. Null = not listed (held).
    int ClusterSize = 0,        // foreign rows agreeing with the anchor's neighborhood, itself included
    int? CompetitorPosition = null, // COMPETITOR rows still ahead (crashers don't count); 0 = front of the real line. Null = not listed, or pre-A11.
    int? BoardTotal = null,     // the game's own listing count for this board (x-of-y). Null = proxy silent, or pre-V34. depth < total = the decision was censored.
    long? CrasherFloor = null,  // gil span of the stepped-over prefix (V35): what the crashers WERE, not just how many. Null = none stepped.
    long? CrasherCeiling = null,
    long? ClusterFloor = null,  // gil span of the anchor cluster (V35): what the real line asked. Null = no board anchor (empty board / premium / held).
    long? ClusterCeiling = null,
    int? CrowdBehind = null,    // the crowd that won the A11 outnumbering test (V37). Null = no pack step, or pre-V37.
    int? SeatAtWrite = null,    // THE TRUE SEAT (V47, ruled 08-23): 1-based, every foreign row cheaper than the write counts - crashers included. Null = never listed.
    long? ShadowPrice = null,   // what the 3.1 queue-doctrine candidate would have written on the same board (V47). Null = no real line, or held pre-shadow.
    int? ShadowSeat = null,     // the 1-based seat that shadow ask buys, same counting as SeatAtWrite.
    string? ShadowDefense = null); // the doctrine's present-tense reason: "front" or "stepped N - successor Rx the pack".

  /// <summary>
  /// The computed, item-agnostic coordinates. Nulls mean "not applicable to this
  /// decision shape" (e.g. no band on a lane with no evidence) - honest absence,
  /// never a zero that reads as a real ratio.
  ///
  /// <para><b>THE TWO PRICE-COORDINATE RIDERS DIED HERE (doctrine sweep, ruled
  /// 2026-08-15).</b> <c>PositionInLane</c> (decided_price / median) and
  /// <c>UndercutTargetRatio</c> (undercut target / median) used to lead this record.
  /// The house doctrine is "avoid trying to predict a price - pick the best spot in
  /// the line", and position_in_lane was a PRICE ratio wearing a queue name on a table
  /// whose grading coordinate is the insertion index (queue_position, V25). Nothing
  /// read either one: undercut_target_ratio was write-only from the day it landed, and
  /// position_in_lane's only remaining machinery was the true-up arm that kept it
  /// correctable - apparatus maintained for a number with no reader.</para>
  ///
  /// <para>The COLUMNS stay (<c>position_in_lane</c>, <c>undercut_target_ratio</c> on
  /// decision_receipts): no migration, so rows written before the sweep keep the values
  /// they were written with and the diff stays readable. Receipts composed from today
  /// simply leave both NULL - they are not in the INSERT's column list at all.</para>
  /// </summary>
  internal readonly record struct ReceiptCoordinates(
    int BoardDepth,
    double? VelocityPerDay,
    int LaneSampleCount,
    double LaneSpread,
    double LaneWeightedAgeDays,
    double? ForecastClearingDays, // v0 forecast: positions-ahead / velocity (resolver upgrade post-3.0)
    int Quantity,
    double? LaneStackNorm,
    // The band, relative like everything else here: p25/median and p75/median, so
    // a band in dye reads against a band in crafting mats. Their difference is the
    // width A4c narrates ("tight" vs "scattered"), and the edges are what the
    // outcome join scores later: the band is the group we joined, so a clear that
    // lands outside it says the boundaries drew the wrong group. Segmentation
    // scorekeeping, not a price the receipt is holding itself to.
    double? BandLowRatio,
    double? BandHighRatio,
    // The segment's story, so a receipt can be read back without re-deriving it:
    // how many sales were still voting, and what demoted the rest.
    int SegmentCount,
    string SegmentCut,
    // THE A10 COORDINATE: which spot in the queue the decision actually took.
    // Foreign rows still ahead (cheaper) after we list - stepped-over crazies sit
    // ahead of us by construction; 0 = front of the queue. Null = we never listed
    // (a held row has no position). The grading target "did position 1 clear by
    // the next pinch" (A9) is unscoreable without it. ClusterSize is the company
    // at the anchor - 1 = a loner we undercut anyway, N = the crowd rule 1 read.
    int? QueuePosition,
    int ClusterSize,
    // THE A11 COORDINATE: the same insertion index counted over COMPETITORS
    // only - crashers we stepped over sit ahead in the raw queue but not in
    // the real line. Raw stays a measure, never a target (Drift, 08-02); the
    // raw-minus-competitor gap reads "standing behind bait" at a glance.
    int? CompetitorPosition,
    // THE X-OF-Y COORDINATE: how many listings the game said the board held.
    // BoardDepth < BoardTotal = the decision ran censored (windows exhausted
    // before every page arrived). Null = the proxy never said, or pre-V34.
    int? BoardTotal = null,
    // THE V35 SPANS: absolutes like decided_price - operands for a reader, not
    // coordinates for the grader. What the stepped-over rows and the anchor
    // line asked in gil, so a queue position can be read without the run log.
    long? CrasherFloor = null,
    long? CrasherCeiling = null,
    long? ClusterFloor = null,
    long? ClusterCeiling = null,
    // THE V37 OPERAND: the crowd that WON the outnumbering test - every
    // competitor standing behind the stepped-over pack, not just the cluster we
    // landed in front of. Without it a cell can only report the cluster, and
    // "stepped over 4, the line: 3" hid the twenty sellers that convicted the
    // pack (Golden Silk, 08-03). Null = no pack step in that verdict, or pre-V37.
    int? CrowdBehind = null,
    // THE V47 QUARTET (ruled 08-23). seat_at_write: the TRUE 1-based seat the
    // written price bought, counted over the WHOLE foreign queue - crashers
    // count. competitor_position's writer retired the same day: its "crashers
    // don't count" arithmetic made every listed row read 0 (all 1,623 post-V30
    // rows - the 08-23 audit), including the Caligae write that sat 4th, so the
    // doctrine clause "not-first names a defense" could never fire on it. The
    // shadow trio banks what the 3.1 queue-doctrine candidate would have done
    // on the same board, so the 3.1 ruling opens on paired data.
    int? SeatAtWrite = null,
    long? ShadowPrice = null,
    int? ShadowSeat = null,
    string? ShadowDefense = null);

  /// <summary>
  /// Turns decision-time facts into relative coordinates. Pure and total: a zero or
  /// missing median yields null ratios rather than a divide-by-zero, so a receipt for
  /// a thin/held item still records its board depth, velocity and stack facts.
  ///
  /// <para>The two price ratios this used to compute first - decided_price / median
  /// and undercut target / median - were retired by the doctrine sweep on 2026-08-15;
  /// see the remarks on <see cref="ReceiptCoordinates"/>. The band ratios below are
  /// NOT the same animal and stay: they are segmentation scorekeeping (did the
  /// boundaries draw the right group), not a price this receipt holds itself to.</para>
  /// </summary>
  internal static ReceiptCoordinates Compute(ReceiptInputs i)
  {
    // v0 forecast clearing window: how many days until this listing clears if the
    // board ahead of it sells at the observed rate (positions-ahead / sales-per-day).
    // The half-life scorekeeping seed compares this against the outcome join's
    // realized time-to-clear; the resolver that sharpens it is post-3.0.
    double? forecast = i.VelocityPerDay is double v && v > 0
      ? (i.BoardDepth + 1) / v
      : null;

    return new ReceiptCoordinates(
      BoardDepth: i.BoardDepth,
      VelocityPerDay: i.VelocityPerDay,
      LaneSampleCount: i.LaneSampleCount,
      LaneSpread: i.LaneSpread,
      LaneWeightedAgeDays: i.LaneWeightedAgeDays,
      ForecastClearingDays: forecast,
      Quantity: i.Quantity,
      LaneStackNorm: i.LaneStackNorm,
      BandLowRatio: i.LaneMedian > 0 && i.BandLow > 0 ? i.BandLow / i.LaneMedian : null,
      BandHighRatio: i.LaneMedian > 0 && i.BandHigh > 0 ? i.BandHigh / i.LaneMedian : null,
      SegmentCount: i.SegmentCount,
      SegmentCut: i.SegmentCut,
      QueuePosition: i.QueuePosition,
      ClusterSize: i.ClusterSize,
      CompetitorPosition: i.CompetitorPosition,
      BoardTotal: i.BoardTotal,
      CrasherFloor: i.CrasherFloor,
      CrasherCeiling: i.CrasherCeiling,
      ClusterFloor: i.ClusterFloor,
      ClusterCeiling: i.ClusterCeiling,
      CrowdBehind: i.CrowdBehind,
      SeatAtWrite: i.SeatAtWrite,
      ShadowPrice: i.ShadowPrice,
      ShadowSeat: i.ShadowSeat,
      ShadowDefense: i.ShadowDefense);
  }

  /// <summary>
  /// Retention (design Section 4): keep roughly the <paramref name="keep"/> most
  /// recent receipts per item so the table is bounded by inventory, not time. Given
  /// the item's receipt ids newest-first, returns the ids beyond the keep window to
  /// prune. Bounded by inventory: every item keeps its own last-N, independent of age.
  /// </summary>
  internal static List<long> ReceiptsToPrune(IReadOnlyList<long> newestFirstIds, int keep)
  {
    var prune = new List<long>();
    if (keep < 0) keep = 0;
    for (var idx = keep; idx < newestFirstIds.Count; idx++)
      prune.Add(newestFirstIds[idx]);
    return prune;
  }

  /// <summary>
  /// Time-to-clear in whole days from when a receipt's listing was written to when
  /// GilTrack confirmed the sale. Clamped at 0 (a confirm timestamped before the
  /// receipt - clock skew - reads as same-day, never negative).
  /// </summary>
  internal static int TimeToClearDays(long receiptCreatedUnix, long soldAtUnix)
    => (int)Math.Max(0, (soldAtUnix - receiptCreatedUnix) / 86400);
}
