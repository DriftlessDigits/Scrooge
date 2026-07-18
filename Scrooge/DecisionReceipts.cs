using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// Decision receipts - the pure core (LanePricing/TriageMemory mold: no game reads,
/// no storage, no statics, linked into Scrooge.Tests). One receipt per pricing
/// decision; its coordinates are ALL RELATIVE (item-agnostic), so a lane in dye
/// scores the same shape as a lane in crafting mats (design Section 4).
///
/// The decision-time coordinates come from the live board + lane at the moment of
/// decision, NOT from the event log (design Section 7). The outcome join
/// (time-to-clear) is filled LATER by a GilTrack confirm, never at write time; an
/// evict/pull closes the row as never-cleared (design Section 4).
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
    /// <summary>An evict/pull closed the listing before it sold - the forecast never got tested.</summary>
    NeverCleared,
  }

  /// <summary>
  /// The race policy an experiment arm selects. M4 ships the SEAM only: the resolver
  /// always returns <see cref="FloorWait"/> (the ruled interim default). The A/B lab
  /// (post-M4 scoreboard branch) unpins this and the other arms go live.
  /// </summary>
  internal enum RacePolicy { FloorWait, MidQueue, Join }

  /// <summary>
  /// The policy resolver at the race site. M4's contract: whatever <paramref name="armId"/>
  /// is stamped on the receipt, this ALWAYS returns floor-wait - the ruled default the
  /// lane already implements (LaneOutcome.RaceDeclined). The seam exists so that when
  /// arms start assigning, the resolver is the one place that changes.
  /// </summary>
  internal static RacePolicy ResolveRacePolicy(string? armId) => RacePolicy.FloorWait;

  /// <summary>
  /// The decision-time facts a receipt is computed from. Everything here is read off
  /// the live board + lane at the moment of decision; the core turns the absolute
  /// numbers into item-agnostic ratios.
  /// </summary>
  internal readonly record struct ReceiptInputs(
    long DecidedPrice,          // the price the decision landed on (or the held price)
    double LaneMedian,          // recency-weighted clearing price
    int LaneSampleCount,        // lane confidence: n
    double LaneWeightedAgeDays, // LaneModel.WeightedAgeDays
    double LaneSpread,          // lane confidence: relative spread of evidence (0 when unknown)
    int BoardDepth,             // competing foreign listings on the board
    long? UndercutTarget,       // the in-lane listing being undercut, when the decision undercuts
    double? VelocityPerDay,     // sales/day (packet or community)
    int Quantity,               // this listing's stack size (observational only - Scrooge never splits)
    double? LaneStackNorm);     // typical sale quantity in the lane (observational only)

  /// <summary>
  /// The computed, item-agnostic coordinates. Nulls mean "not applicable to this
  /// decision shape" (e.g. no undercut target on a LaneOwned verdict) - honest
  /// absence, never a zero that reads as a real ratio.
  /// </summary>
  internal readonly record struct ReceiptCoordinates(
    double? PositionInLane,       // DecidedPrice / median
    int BoardDepth,
    double? UndercutTargetRatio,  // UndercutTarget / median
    double? VelocityPerDay,
    int LaneSampleCount,
    double LaneSpread,
    double LaneWeightedAgeDays,
    double? ForecastClearingDays, // v0 forecast: positions-ahead / velocity (resolver upgrade post-3.0)
    int Quantity,
    double? LaneStackNorm);

  /// <summary>
  /// Turns decision-time facts into relative coordinates. Pure and total: a zero or
  /// missing median yields null ratios rather than a divide-by-zero, so a receipt for
  /// a thin/held item still records its board depth, velocity and stack facts.
  /// </summary>
  internal static ReceiptCoordinates Compute(ReceiptInputs i)
  {
    double? positionInLane = i.LaneMedian > 0 ? i.DecidedPrice / i.LaneMedian : null;
    double? undercutRatio = i.UndercutTarget is long t && i.LaneMedian > 0 ? t / i.LaneMedian : null;

    // v0 forecast clearing window: how many days until this listing clears if the
    // board ahead of it sells at the observed rate (positions-ahead / sales-per-day).
    // The half-life scorekeeping seed compares this against the outcome join's
    // realized time-to-clear; the resolver that sharpens it is post-3.0.
    double? forecast = i.VelocityPerDay is double v && v > 0
      ? (i.BoardDepth + 1) / v
      : null;

    return new ReceiptCoordinates(
      PositionInLane: positionInLane,
      BoardDepth: i.BoardDepth,
      UndercutTargetRatio: undercutRatio,
      VelocityPerDay: i.VelocityPerDay,
      LaneSampleCount: i.LaneSampleCount,
      LaneSpread: i.LaneSpread,
      LaneWeightedAgeDays: i.LaneWeightedAgeDays,
      ForecastClearingDays: forecast,
      Quantity: i.Quantity,
      LaneStackNorm: i.LaneStackNorm);
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
