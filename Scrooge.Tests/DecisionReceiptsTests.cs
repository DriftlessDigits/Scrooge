using System.Collections.Generic;
using Xunit;
using static Scrooge.DecisionReceipts;

namespace Scrooge.Tests;

/// <summary>
/// Decision-receipts core: item-agnostic relative coordinates, per-item bounded
/// retention, the arm resolver seam (always floor-wait), and the outcome-join math.
/// </summary>
public class DecisionReceiptsTests
{
  private static ReceiptInputs Inputs(
    long price = 150, double median = 100, int n = 5, double age = 4,
    double spread = 0.2, int depth = 3,
    double? velocity = null, int qty = 1, double? stackNorm = null,
    double bandLow = 0, double bandHigh = 0, int segmentCount = 0, string segmentCut = "",
    int? queuePosition = null, int clusterSize = 0)
    => new(price, median, n, age, spread, depth, velocity, qty, stackNorm,
           bandLow, bandHigh, segmentCount, segmentCut, queuePosition, clusterSize);

  // --- Relative coordinates (item-agnostic) -----------------------------

  /// <summary>
  /// THE DOCTRINE SWEEP'S PIN (2026-08-15). position_in_lane (decided_price / median)
  /// and undercut_target_ratio were retired: a price ratio on a queue-grading table,
  /// and a write-only column nothing ever read. The coordinates record no longer
  /// carries either field, so a receipt composed today cannot write them - the
  /// columns stay on the table for the rows that already have them, and this test
  /// exists so that "the composer produces no price ratio" is a pinned fact rather
  /// than an absence nobody notices.
  /// </summary>
  [Fact]
  public void TheComposer_ProducesNoPriceRatios_AfterTheDoctrineSweep()
  {
    var fields = typeof(ReceiptCoordinates).GetProperties();
    Assert.DoesNotContain(fields, f => f.Name == "PositionInLane");
    Assert.DoesNotContain(fields, f => f.Name == "UndercutTargetRatio");
  }

  [Fact]
  public void QueuePosition_CarriesThroughVerbatim_AndNullMeansNeverListed()
  {
    // The A10 coordinate is an index, not a ratio - no median math may touch it.
    // 0 is front-of-queue (a real, gradeable position); null is "never listed"
    // (a held row) and must survive as null, never collapse to 0.
    var front = Compute(Inputs(queuePosition: 0, clusterSize: 9));
    Assert.Equal(0, front.QueuePosition);
    Assert.Equal(9, front.ClusterSize);

    var behindCrazies = Compute(Inputs(queuePosition: 3, clusterSize: 2));
    Assert.Equal(3, behindCrazies.QueuePosition);

    var held = Compute(Inputs(queuePosition: null));
    Assert.Null(held.QueuePosition);
  }

  [Fact]
  public void SameShape_DifferentAbsolutePrices_ProduceIdenticalCoordinates()
  {
    // A dye lane at 12k and a crafting-mat lane at 120 with the same shape must
    // yield the same relative coordinates - that is the whole point of the receipt.
    var dye = Compute(Inputs(price: 18_000, median: 12_000, bandLow: 10_800, bandHigh: 13_200, depth: 3, velocity: 2));
    var mat = Compute(Inputs(price: 180, median: 120, bandLow: 108, bandHigh: 132, depth: 3, velocity: 2));
    Assert.Equal(dye.BandLowRatio, mat.BandLowRatio);
    Assert.Equal(dye.BandHighRatio, mat.BandHighRatio);
    Assert.Equal(dye.ForecastClearingDays, mat.ForecastClearingDays);
  }

  [Fact]
  public void ZeroMedian_YieldsNullRatios_NotDivideByZero()
  {
    // A held/thin item (no lane) still records board depth, velocity and stack facts.
    var c = Compute(Inputs(price: 500, median: 0, bandLow: 400, bandHigh: 600,
                           depth: 2, velocity: 1, qty: 4, stackNorm: 1.2));
    Assert.Null(c.BandLowRatio);
    Assert.Null(c.BandHighRatio);
    Assert.Equal(2, c.BoardDepth);
    Assert.Equal(4, c.Quantity);
    Assert.Equal(1.2, c.LaneStackNorm);
  }

  [Fact]
  public void ForecastClearing_IsPositionsAheadOverVelocity()
  {
    // 3 listings ahead + this one = 4 positions; 2 sales/day => 2 days.
    var c = Compute(Inputs(depth: 3, velocity: 2));
    Assert.Equal(2.0, c.ForecastClearingDays);
  }

  [Fact]
  public void ForecastClearing_NullWhenNoVelocity()
  {
    Assert.Null(Compute(Inputs(depth: 3, velocity: null)).ForecastClearingDays);
    Assert.Null(Compute(Inputs(depth: 3, velocity: 0)).ForecastClearingDays);
  }

  [Fact]
  public void LaneConfidenceAndAge_CarryThroughVerbatim()
  {
    var c = Compute(Inputs(n: 7, age: 31.5, spread: 0.42));
    Assert.Equal(7, c.LaneSampleCount);
    Assert.Equal(31.5, c.LaneWeightedAgeDays);
    Assert.Equal(0.42, c.LaneSpread);
  }

  [Fact]
  public void StackCoordinates_AreObservationalOnly()
  {
    // Scrooge never splits stacks - quantity and lane stack norm ride as facts, not knobs.
    var c = Compute(Inputs(qty: 2, stackNorm: 1.0));
    Assert.Equal(2, c.Quantity);
    Assert.Equal(1.0, c.LaneStackNorm);
  }

  // --- Retention (bounded per item, not by time) ------------------------

  [Fact]
  public void ReceiptsToPrune_KeepsNewestN_PrunesTheRest()
  {
    // Five receipts for one item, newest first; keep 3 => prune the oldest two.
    var ids = new List<long> { 50, 40, 30, 20, 10 };
    Assert.Equal(new List<long> { 20, 10 }, ReceiptsToPrune(ids, keep: 3));
  }

  [Fact]
  public void ReceiptsToPrune_NothingToPrune_WhenUnderCap()
  {
    var ids = new List<long> { 30, 20, 10 };
    Assert.Empty(ReceiptsToPrune(ids, keep: 3));
  }

  [Fact]
  public void ReceiptsToPrune_IsPerItemBounded_NotTimeBounded()
  {
    // Even ancient ids survive as long as they are within the item's last-N.
    var ids = new List<long> { 3, 2, 1 };
    Assert.Empty(ReceiptsToPrune(ids, keep: 3));
  }

  // --- Outcome join ------------------------------------------------------

  [Fact]
  public void TimeToClear_IsWholeDaysFromReceiptToSale()
  {
    var created = 1_700_000_000L;
    var sold = created + (3 * 86400) + 500; // 3 days and change
    Assert.Equal(3, TimeToClearDays(created, sold));
  }

  [Fact]
  public void TimeToClear_ClampsAtZero_OnClockSkew()
  {
    var created = 1_700_000_000L;
    Assert.Equal(0, TimeToClearDays(created, created - 5000));
  }

  [Fact]
  public void OutcomeStates_AreOpenClearedNeverClearedGoneUnobserved()
  {
    // The four states the join walks through: written open, confirmed cleared,
    // closed never-cleared by an observed evict/pull, or closed gone-unobserved
    // by the pinch reconciler (08-02) when a full sell-list read proved the lane
    // absent. GoneUnobserved is the only provisional close - a late sale confirm
    // upgrades it to Cleared.
    Assert.Equal(4, System.Enum.GetValues(typeof(OutcomeState)).Length);
    Assert.Equal(OutcomeState.Open, default(OutcomeState));
  }

  // --- The band and the segment's story (A2/A4c) ------------------------

  [Fact]
  public void BandRides_RelativeLikeEveryOtherCoordinate()
  {
    // A band in dye has to read against a band in crafting mats, so the edges
    // ride as ratios to the median. Their difference is the width A4c narrates.
    var c = Compute(Inputs(median: 100, bandLow: 80, bandHigh: 130));

    Assert.Equal(0.8, c.BandLowRatio!.Value, 6);
    Assert.Equal(1.3, c.BandHighRatio!.Value, 6);
  }

  [Fact]
  public void BandRatios_AreNullWhenThereIsNoBandToReport()
  {
    // Honest absence, never a zero that reads as a real ratio: a held item with
    // no lane has no band, and a receipt still records everything else.
    var noLane = Compute(Inputs(median: 0, bandLow: 0, bandHigh: 0));
    Assert.Null(noLane.BandLowRatio);
    Assert.Null(noLane.BandHighRatio);
  }

  [Fact]
  public void SegmentStory_RidesVerbatim()
  {
    // Who was still voting, and what demoted the rest - the receipt has to read
    // back without re-deriving the segment from a tape that has moved on.
    var c = Compute(Inputs(segmentCount: 7, segmentCut: "Cliff"));

    Assert.Equal(7, c.SegmentCount);
    Assert.Equal("Cliff", c.SegmentCut);
  }
}
