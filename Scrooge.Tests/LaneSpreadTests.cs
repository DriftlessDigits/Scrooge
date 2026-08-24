using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE LANE'S SPREAD, END TO END (code-shine 3-3b; Drift: <i>"I 100% want confidence in the
/// router decision to play a part"</i>).
///
/// <para>Every producer of <see cref="BoardConfidence.Evidence"/> used to hand in
/// <c>LaneSpread: 0.0</c>, which made the tier's <c>tight</c> gate structurally dead: it
/// could refuse nothing, because nothing could ever disagree. These pin the wire - the
/// one measure, its TWO boundaries since the 3b-4 un-weld, and the demotion a wide lane
/// now actually causes.</para>
/// </summary>
public class LaneSpreadTests
{
  // A lane that agrees with itself: the band is a fifth of the going rate.
  private const double TightLow = 900, TightHigh = 1_100, TightMedian = 1_000;
  // A lane that does not: the band is the going rate over again.
  private const double WideLow = 500, WideHigh = 1_500, WideMedian = 1_000;

  private static BoardConfidence.Evidence Listing(double spread) => new(
    Lean: VerdictLean.OnMarket,
    LaneSampleCount: 5,
    LaneSpread: spread,
    VelocityPerDay: 0.8,
    RecentSalesCount: 6,
    EvidenceAgeDays: 1,
    LocalCommunityAccord: Accord.Unknown,
    MinSamples: 3,
    StaleDays: BoardConfidence.EvidenceStaleDays);

  [Fact]
  public void BandSpread_IsTheBandOverItsOwnMiddle()
  {
    Assert.Equal(0.2, LanePricing.BandSpread(TightLow, TightHigh, TightMedian), 6);
    Assert.Equal(1.0, LanePricing.BandSpread(WideLow, WideHigh, WideMedian), 6);
  }

  [Fact]
  public void BandSpread_WithNoBand_IsZero()
  {
    // Absence of evidence is not disagreement. A lane the walk never banded, a median
    // of zero, or an inverted pair all read 0.0 - which is TIGHT everywhere it is
    // compared, so silence can never demote a verdict.
    Assert.Equal(0.0, LanePricing.BandSpread(null, null, null));
    Assert.Equal(0.0, LanePricing.BandSpread(TightLow, TightHigh, 0));
    Assert.Equal(0.0, LanePricing.BandSpread(TightHigh, TightLow, TightMedian));
  }

  [Fact]
  public void WideLane_DemotesUnanimousToMixed()
  {
    // THE POINT OF THE WHOLE WIRE. Same verdict, same live market, same sample count,
    // same freshness - only the lane's own disagreement differs, and it is what decides
    // whether the round may spend the row without asking.
    Assert.Equal(ConfidenceTier.Unanimous,
      BoardConfidence.BaseTier(Listing(LanePricing.BandSpread(TightLow, TightHigh, TightMedian))));
    Assert.Equal(ConfidenceTier.Mixed,
      BoardConfidence.BaseTier(Listing(LanePricing.BandSpread(WideLow, WideHigh, WideMedian))));
  }

  [Fact]
  public void NoBand_LeavesTheVerdictUnanimous()
  {
    // The guard the producers keep: a synthetic flag row, a player's own contest, and
    // bag gear all reach the tier with no band behind them, and none of them may be
    // demoted for it.
    Assert.Equal(ConfidenceTier.Unanimous,
      BoardConfidence.BaseTier(Listing(LanePricing.BandSpread(null, null, null))));
  }

  [Fact]
  public void TheTierReadsTheTapeNoiseCeiling_AndItIsInclusive()
  {
    // The tier's own dial since the un-weld (3b-4): the SALE TAPE's noise ceiling.
    // Exactly at the line is still tight - a lane that only just reaches the boundary
    // has not disagreed past it.
    var atTheLine = BoardConfidence.TapeNoiseCeiling;
    Assert.Equal(ConfidenceTier.Unanimous, BoardConfidence.BaseTier(Listing(atTheLine)));
    Assert.Equal(ConfidenceTier.Mixed, BoardConfidence.BaseTier(Listing(atTheLine + 0.01)));
  }

  [Fact]
  public void TheCaseCaveatFiresOnTheBoardNowBoundary()
  {
    // The tribunal's scattered-lane caveat reads ScatteredBandPct - the BOARD-NOW dial,
    // fed by board-ask quartiles. Same measure as the tier's, its own boundary.
    var wide = LanePricing.BandSpread(WideLow, WideHigh, WideMedian);
    Assert.True(wide > LanePricing.ScatteredBandPct);
    Assert.False(LanePricing.BandSpread(TightLow, TightHigh, TightMedian) > LanePricing.ScatteredBandPct);
  }

  [Fact]
  public void TheTwoDialsAreSeparateSymbols_AtTheSameValueToday()
  {
    // THE UN-WELD, PINNED (3b-4, ruled B1.5a). One constant used to serve both
    // instruments, so tuning either moved the other silently. They are two now. This
    // test asserts the VALUE only to record that the un-weld changed no behaviour on the
    // day it landed - it is not a claim that they must stay equal, and the first
    // evidence-driven retune of either is expected to break this line and should just
    // delete it. What must NOT happen is the two collapsing back into one symbol.
    Assert.Equal(LanePricing.ScatteredBandPct, BoardConfidence.TapeNoiseCeiling);
  }
}
