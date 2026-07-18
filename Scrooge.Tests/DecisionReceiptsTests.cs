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
    double spread = 0.2, int depth = 3, long? undercut = null,
    double? velocity = null, int qty = 1, double? stackNorm = null)
    => new(price, median, n, age, spread, depth, undercut, velocity, qty, stackNorm);

  // --- Relative coordinates (item-agnostic) -----------------------------

  [Fact]
  public void PositionInLane_IsPriceOverMedian()
  {
    var c = Compute(Inputs(price: 150, median: 100));
    Assert.Equal(1.5, c.PositionInLane);
  }

  [Fact]
  public void UndercutTargetRatio_IsTargetOverMedian()
  {
    var c = Compute(Inputs(median: 100, undercut: 90));
    Assert.Equal(0.9, c.UndercutTargetRatio);
  }

  [Fact]
  public void SameShape_DifferentAbsolutePrices_ProduceIdenticalCoordinates()
  {
    // A dye lane at 12k and a crafting-mat lane at 120 with the same shape must
    // yield the same relative coordinates - that is the whole point of the receipt.
    var dye = Compute(Inputs(price: 18_000, median: 12_000, undercut: 10_800, depth: 3, velocity: 2));
    var mat = Compute(Inputs(price: 180, median: 120, undercut: 108, depth: 3, velocity: 2));
    Assert.Equal(dye.PositionInLane, mat.PositionInLane);
    Assert.Equal(dye.UndercutTargetRatio, mat.UndercutTargetRatio);
    Assert.Equal(dye.ForecastClearingDays, mat.ForecastClearingDays);
  }

  [Fact]
  public void ZeroMedian_YieldsNullRatios_NotDivideByZero()
  {
    // A held/thin item (no lane) still records board depth, velocity and stack facts.
    var c = Compute(Inputs(price: 500, median: 0, depth: 2, velocity: 1, qty: 4, stackNorm: 1.2));
    Assert.Null(c.PositionInLane);
    Assert.Null(c.UndercutTargetRatio);
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

  // --- Arm resolver seam -------------------------------------------------

  [Theory]
  [InlineData(null)]
  [InlineData("arm_floor_wait")]
  [InlineData("arm_mid_queue")]
  [InlineData("anything")]
  public void ResolveRacePolicy_AlwaysFloorWait_WhateverArmIsStamped(string? armId)
  {
    // M4 builds the seam only: the ruled default fires regardless of arm_id.
    Assert.Equal(RacePolicy.FloorWait, ResolveRacePolicy(armId));
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
  public void OutcomeStates_AreOpenClearedNeverCleared()
  {
    // The three states the join walks through: written open, confirmed cleared,
    // or closed never-cleared by an evict/pull.
    Assert.Equal(3, System.Enum.GetValues(typeof(OutcomeState)).Length);
    Assert.Equal(OutcomeState.Open, default(OutcomeState));
  }
}
