using System.Collections.Generic;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE MELT PRIOR (07-25). The contract: a desynthable item with no melt history
/// still gets a REAL melt number, drawn from what gear of its weight actually
/// yielded - and it says out loud that that is where the number came from.
/// </summary>
public class MeltPriorTests
{
  private static MeltObservation Obs(int ilvl, int attempts, long value)
    => new(ilvl, attempts, value);

  // ========================================================================
  // The banding
  // ========================================================================

  [Fact]
  public void BandOf_BucketsByFifty()
  {
    Assert.Equal(0, MeltPriorTable.BandOf(1));
    Assert.Equal(0, MeltPriorTable.BandOf(49));
    Assert.Equal(50, MeltPriorTable.BandOf(50));
    Assert.Equal(650, MeltPriorTable.BandOf(690));
    Assert.Equal(700, MeltPriorTable.BandOf(700));
  }

  [Fact]
  public void Band_PoolsByAttempt_NotByItem()
  {
    // Two items in the same band: one melted 9 times for 900 gil, one melted
    // once for 10,000. Averaging the per-item averages gives ~5,050 - a single
    // lucky attempt outvoting nine. Pooling gives 10,900/10 = 1,090.
    var table = MeltPriorTable.Build([Obs(660, 9, 900), Obs(670, 1, 10_000)]);
    var prior = table.For(665);
    Assert.NotNull(prior);
    Assert.Equal(1_090, prior!.Value.ValuePerAttempt);
    Assert.Equal(10, prior.Value.Attempts);
    Assert.Equal(2, prior.Value.SourceItems);
  }

  [Fact]
  public void Band_NamesItsOwnRange()
  {
    var table = MeltPriorTable.Build([Obs(690, 10, 20_000)]);
    var prior = table.For(690);
    Assert.Equal("ilvl 650-699", prior!.Value.BandLabel);
    Assert.False(prior.Value.Widened);
  }

  [Fact]
  public void OtherBands_DoNotLeakIn()
  {
    // A rich high band must never price a low one on its own.
    var table = MeltPriorTable.Build([Obs(700, 50, 500_000), Obs(100, 10, 2_000)]);
    Assert.Equal(200, table.For(120)!.Value.ValuePerAttempt);
    Assert.Equal(10_000, table.For(710)!.Value.ValuePerAttempt);
  }

  // ========================================================================
  // Never invented from nothing
  // ========================================================================

  [Fact]
  public void ThinBand_WidensToItsNeighbours()
  {
    // Band 650 has 2 attempts - an anecdote. Its neighbours carry it.
    var table = MeltPriorTable.Build([Obs(660, 2, 2_000), Obs(610, 4, 4_000), Obs(710, 4, 4_000)]);
    var prior = table.For(660);
    Assert.NotNull(prior);
    Assert.True(prior!.Value.Widened);
    Assert.Equal(10, prior.Value.Attempts);
    Assert.Equal(1_000, prior.Value.ValuePerAttempt);
    Assert.Equal("ilvl 600-749", prior.Value.BandLabel);
  }

  [Fact]
  public void StillThinAfterWidening_HasNoPrior()
  {
    var table = MeltPriorTable.Build([Obs(660, 2, 2_000), Obs(610, 1, 1_000)]);
    Assert.Null(table.For(660)); // 3 attempts across 150 ilvl names no number
  }

  [Fact]
  public void EmptyAlmanac_HasNoPrior()
    => Assert.Null(MeltPriorTable.Build([]).For(690));

  [Fact]
  public void UnpricedYields_AreNotAPriorOfZero()
  {
    // Plenty of attempts, but nothing the mat prices could value. "Expected 0"
    // would lose every comparison exactly as loudly as the forfeit - so silence.
    Assert.Null(MeltPriorTable.Build([Obs(690, 40, 0)]).For(690));
  }

  [Fact]
  public void ItemsWithNoGearLevel_AreDropped_NotPiledIntoTheBottomBand()
  {
    // Mats and tokens melt too; they are not gear, and must not price ilvl-20 gear.
    var table = MeltPriorTable.Build([Obs(0, 100, 5_000_000), Obs(-1, 100, 5_000_000)]);
    Assert.Null(table.For(20));
    Assert.Equal(0, table.SolidBandCount);
  }

  [Fact]
  public void ZeroIlvl_AsksNothing()
    => Assert.Null(MeltPriorTable.Build([Obs(690, 40, 40_000)]).For(0));

  [Fact]
  public void SolidBandCount_CountsOnlyBandsThatCanSpeakUnaided()
  {
    var table = MeltPriorTable.Build([Obs(690, 40, 40_000), Obs(110, 2, 200)]);
    Assert.Equal(1, table.SolidBandCount);
  }
}

/// <summary>
/// The prior where it matters: the router's melt-versus-turn-in comparison, which
/// the turn-in used to win by forfeit whenever the ledger had never seen the item.
/// </summary>
public class MeltPriorRoutingTests
{
  private static MeltPrior Prior(long per = 1_800, int attempts = 60)
    => new(per, 650, 699, attempts, 22, Widened: false);

  [Fact]
  public void PriorFillsTheMeltScore_SoTheTurnInCannotWinByForfeit()
  {
    // 400 seals at 25 = 10,000 gil. Without a prior the melt score is null and
    // the turn-in takes it uncontested; with one the comparison is real.
    var forfeit = RoutingRules.Evaluate(T.Gear(seals: 400, desynthable: true), T.Batch(sealRate: 25));
    Assert.Equal(RoutingExit.Gc, forfeit.Exit);
    Assert.Null(forfeit.Scores!.Value.Melt);

    var contested = RoutingRules.Evaluate(
      T.Gear(seals: 400, meltPrior: Prior(per: 40_000)), T.Batch(sealRate: 25));
    Assert.Equal(RoutingExit.Desynth, contested.Exit);
    Assert.Equal(40_000, contested.Scores!.Value.Melt);
  }

  [Fact]
  public void PriorLosesHonestly_WhenTheSealsAreActuallyWorthMore()
  {
    // The point is a real comparison, not a thumb on the scale.
    var v = RoutingRules.Evaluate(T.Gear(seals: 400, meltPrior: Prior(per: 1_800)), T.Batch(sealRate: 25));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.Equal(1_800, v.Scores!.Value.Melt); // still weighed, and still on the receipt
  }

  [Fact]
  public void ItemHistorySupersedesTheBand()
  {
    // The measured number wins even when the band's estimate is far rosier -
    // and it wins DOWNWARD, which is the whole point of a prior that only fills
    // nulls. Here 3,000 measured loses to the seals that 40,000 estimated would
    // have beaten.
    var beaten = RoutingRules.Evaluate(
      T.Gear(seals: 400, melt: 3_000, meltPrior: Prior(per: 40_000)), T.Batch(sealRate: 25));
    Assert.Equal(3_000, beaten.Scores!.Value.Melt);
    Assert.Equal(RoutingExit.Gc, beaten.Exit);

    // And when the measurement wins, it narrates as a measurement.
    var wins = RoutingRules.Evaluate(
      T.Gear(seals: 400, melt: 60_000, meltPrior: Prior(per: 40_000)), T.Batch(sealRate: 25));
    Assert.Equal(60_000, wins.Scores!.Value.Melt);
    Assert.Contains("from your ledger", wins.Reason);
    Assert.DoesNotContain("band average", wins.Reason);
  }

  [Fact]
  public void NonDesynthableGear_KeepsItsNullMelt()
  {
    // Turn-in by forfeit stays CORRECT here - the game will not melt this.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 400, meltPrior: Prior(per: 40_000), desynthable: false), T.Batch(sealRate: 25));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.Null(v.Scores!.Value.Melt);
  }

  [Fact]
  public void PriorNarratesAsAnEstimate_NeverAsItemHistory()
  {
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 400, meltPrior: Prior(per: 40_000)), T.Batch(sealRate: 25));
    var said = v.Reason + v.RunnerUpReason;
    Assert.Contains("ilvl 650-699", said);
    Assert.Contains("no history for this one", said);
    Assert.DoesNotContain("from your ledger", said);
  }

  [Fact]
  public void TheGradeRidesTheScore_SoACellCanTellAnEstimateFromAMeasurement()
  {
    // 08-03: the band sentence only ever rode the verdict reason, and the
    // verdict only carries the melt clause when melt WINS. Everywhere else a
    // 1,474 prior and a 1,474 measurement were the same four digits.
    var estimated = RoutingRules.Evaluate(
      T.Gear(seals: 400, meltPrior: Prior(per: 40_000)), T.Batch(sealRate: 25));
    Assert.Equal(MeltGrade.Prior, estimated.Scores!.Value.MeltGrade);

    // Losing does not change what the number IS - this is the case the reason
    // string could never speak for.
    var estimatedAndBeaten = RoutingRules.Evaluate(
      T.Gear(seals: 400, meltPrior: Prior(per: 1_800)), T.Batch(sealRate: 25));
    Assert.Equal(RoutingExit.Gc, estimatedAndBeaten.Exit);
    Assert.Equal(MeltGrade.Prior, estimatedAndBeaten.Scores!.Value.MeltGrade);

    var measured = RoutingRules.Evaluate(
      T.Gear(seals: 400, melt: 3_000, meltPrior: Prior(per: 40_000)), T.Batch(sealRate: 25));
    Assert.Equal(MeltGrade.Measured, measured.Scores!.Value.MeltGrade);

    var none = RoutingRules.Evaluate(T.Gear(seals: 400, desynthable: true), T.Batch(sealRate: 25));
    Assert.Equal(MeltGrade.None, none.Scores!.Value.MeltGrade);
  }

  [Fact]
  public void AKnobPricedSkillup_IsNeitherMeasuredNorEstimated()
  {
    // The skill-up worth outbids the yields, so the number in the Melt column
    // stopped being a yield. It is not evidence of a desynth return, and it
    // must never be read as one.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 400, melt: 3_000, redSkillup: true), T.Batch(sealRate: 25));
    Assert.Equal(MeltGrade.Skillup, v.Scores!.Value.MeltGrade);
  }

  [Fact]
  public void WidenedPriorSaysSo()
  {
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 400, meltPrior: new MeltPrior(40_000, 600, 749, 12, 5, Widened: true)),
      T.Batch(sealRate: 25));
    Assert.Contains("widened", v.Reason + v.RunnerUpReason);
  }

  [Fact]
  public void Skillup_StillOutbidsAThinPrior()
  {
    // The skillup floor is a separate claim about worth; the prior does not
    // suppress it, and the higher of the two is what the comparison ran on.
    var v = RoutingRules.Evaluate(T.Gear(redSkillup: true, meltPrior: Prior(per: 100), vendor: 100), T.Batch());
    Assert.Equal(new RoutingConfig().SkillupWorthRed, v.Scores!.Value.Melt);
  }
}
