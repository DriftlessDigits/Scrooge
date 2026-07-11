using Xunit;

namespace Scrooge.Tests;

public class FlagRuleTests
{
  [Fact]
  public void Banned_RoutesToBan()
    => Assert.Equal(RoutingExit.Ban, RoutingRules.Evaluate(T.Gear(banned: true), T.Batch()).Exit);

  [Fact]
  public void Banned_BeatsProtection()
    => Assert.Equal(RoutingExit.Ban,
      RoutingRules.Evaluate(T.Gear(banned: true, isProtected: true), T.Batch()).Exit);

  [Fact]
  public void Protected_RoutesToHold_WithReason()
  {
    var v = RoutingRules.Evaluate(T.Gear(isProtected: true, protection: "in a gearset"), T.Batch());
    Assert.Equal(RoutingExit.Hold, v.Exit);
    Assert.Contains("in a gearset", v.Reason);
  }

  [Fact]
  public void AlwaysVendor_RoutesToVendor()
    => Assert.Equal(RoutingExit.Vendor, RoutingRules.Evaluate(T.Gear(alwaysVendor: true), T.Batch()).Exit);
}

public class VenturePanicTests
{
  [Fact]
  public void StockBelowPanicBand_ChurnsSealEligible()
  {
    var v = RoutingRules.Evaluate(T.Gear(seals: 500), T.Batch(stock: 499));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.Contains("Venture panic", v.Reason);
  }

  [Fact]
  public void PanicFiresBeforeListEvidence_DocumentedBehavior()
  {
    // The panic band has no value escape by design (rule sketch as-drafted;
    // revisit with override data). This test documents it so a change is loud.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 500, sale: (900_000, 0, 2)), T.Batch(stock: 499));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.Contains("Venture panic", v.Reason);
  }

  [Fact]
  public void StockAtPanicBand_DoesNotPanic()
  {
    var v = RoutingRules.Evaluate(T.Gear(seals: 500), T.Batch(stock: 500));
    Assert.DoesNotContain("panic", v.Reason);
  }

  [Theory]
  [InlineData(null)] // read failed
  [InlineData(0)]    // ambiguous until the token id is verified in-game
  public void MissingOrZeroStock_NeverPanics(int? stock)
  {
    var v = RoutingRules.Evaluate(T.Gear(seals: 500), T.Batch(stock: stock));
    Assert.DoesNotContain("panic", v.Reason);
  }
}

public class ListEvidenceTests
{
  [Fact]
  public void SaleClearsFloor_ListsConfidently()
  {
    var v = RoutingRules.Evaluate(T.Gear(sale: (20_000, 0, 5)), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.False(v.IsReview);
  }

  [Fact]
  public void SaleBelowFloor_FallsToVendor_WithFloorContext()
  {
    var v = RoutingRules.Evaluate(T.Gear(vendor: 100, sale: (14_999, 0, 5)), T.Batch());
    Assert.Equal(RoutingExit.Vendor, v.Exit);
    Assert.Contains("below your list floor", v.Reason);
  }

  [Fact]
  public void SaleTooSlow_FailsVelocityAxis()
  {
    var v = RoutingRules.Evaluate(T.Gear(vendor: 100, sale: (20_000, 0, 11)), T.Batch());
    Assert.Equal(RoutingExit.Vendor, v.Exit);
  }

  [Fact]
  public void UnknownSitTime_GetsBenefitOfTheDoubt()
    => Assert.Equal(RoutingExit.List,
      RoutingRules.Evaluate(T.Gear(sale: (20_000, 0, null)), T.Batch()).Exit);

  [Fact]
  public void UnknownSitTime_DeadAlmanacVelocity_FailsAxis()
  {
    var v = RoutingRules.Evaluate(
      T.Gear(vendor: 100, sale: (20_000, 0, null), velocity: 0.05), T.Batch());
    Assert.Equal(RoutingExit.Vendor, v.Exit);
  }

  [Fact]
  public void NonGear_UsesSimpleGilFloor()
    => Assert.Equal(RoutingExit.List,
      RoutingRules.Evaluate(T.Gear(equipment: false, sale: (6_000, 0, null)), T.Batch()).Exit);

  [Fact]
  public void SubLowStock_CheapListing_ChurnsWithListRunnerUp()
  {
    // 20k < floor 15k x 3 = 45k: the low band claims it.
    var v = RoutingRules.Evaluate(T.Gear(seals: 100, sale: (20_000, 0, 5)), T.Batch(stock: 700));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.Contains("Venture low", v.Reason);
    Assert.Equal(RoutingExit.List, v.RunnerUp);
  }

  [Fact]
  public void SubLowStock_ValuableListing_StaysListed()
  {
    // 50k >= 45k: the value escape holds the listing.
    var v = RoutingRules.Evaluate(T.Gear(seals: 100, sale: (50_000, 0, 5)), T.Batch(stock: 700));
    Assert.Equal(RoutingExit.List, v.Exit);
  }
}

public class ReviewBandTests
{
  [Fact]
  public void ScoresInsideBand_DegradeToReview()
  {
    // |20000 - 17000| = 3000 = exactly 15% of 20000: inside.
    var v = RoutingRules.Evaluate(T.Gear(sale: (20_000, 0, 5), melt: 17_000), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.True(v.IsReview);
    Assert.Equal(RoutingExit.Desynth, v.RunnerUp);
  }

  [Fact]
  public void ScoresOutsideBand_StayConfident()
  {
    var v = RoutingRules.Evaluate(T.Gear(sale: (20_000, 0, 5), melt: 16_999), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.False(v.IsReview);
  }

  [Fact]
  public void BorderlineGcContender_ThinStock_TiltsToChurn()
  {
    // gc = 720 x 25 = 18000, inside the band vs list 20000; stock < full band.
    var v = RoutingRules.Evaluate(T.Gear(seals: 720, sale: (20_000, 0, 5)), T.Batch(stock: 1_200));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.Contains("tilted to churn", v.Reason);
  }

  [Fact]
  public void BorderlineGcContender_FullStock_StaysReview()
  {
    var v = RoutingRules.Evaluate(T.Gear(seals: 720, sale: (20_000, 0, 5)), T.Batch(stock: 1_300));
    Assert.True(v.IsReview);
  }
}

public class SkillupTests
{
  [Fact]
  public void SkillupEligible_NoMeltEvidence_Desynths()
  {
    var v = RoutingRules.Evaluate(T.Gear(skillup: true), T.Batch());
    Assert.Equal(RoutingExit.Desynth, v.Exit);
    Assert.Contains("Skillup", v.Reason);
  }

  [Fact]
  public void SkillupEligible_ProvenJunkYields_IsBlocked()
  {
    // Melt 100 below vendor 500: the ledger PROVES junk; vendor wins downstream.
    var v = RoutingRules.Evaluate(T.Gear(skillup: true, melt: 100, vendor: 500), T.Batch());
    Assert.Equal(RoutingExit.Vendor, v.Exit);
  }

  [Fact]
  public void SkillupEligible_MeltAtLeastVendor_Desynths()
    => Assert.Equal(RoutingExit.Desynth,
      RoutingRules.Evaluate(T.Gear(skillup: true, melt: 600, vendor: 500), T.Batch()).Exit);
}

public class GcRuleTests
{
  [Fact]
  public void SealsBeatEveryGilExit_Churns()
  {
    var v = RoutingRules.Evaluate(T.Gear(seals: 2_000, melt: 10_000, vendor: 500), T.Batch());
    Assert.Equal(RoutingExit.Gc, v.Exit); // 50k gc vs 10k melt
    Assert.False(v.IsReview);
  }

  [Fact]
  public void SubLowStock_ValuableMelt_KeepsDesynth()
  {
    // Era review red 2 regression: melt 50k >= the 45k escape ceiling, so
    // sub-750 stock must NOT hard-churn it away for 20k of seals.
    var v = RoutingRules.Evaluate(T.Gear(seals: 800, melt: 50_000, vendor: 500), T.Batch(stock: 700));
    Assert.Equal(RoutingExit.Desynth, v.Exit);
  }

  [Fact]
  public void SubLowStock_CheapAlternatives_Churns()
  {
    var v = RoutingRules.Evaluate(T.Gear(seals: 800, melt: 10_000, vendor: 500), T.Batch(stock: 700));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.Contains("Venture low", v.Reason);
    Assert.Equal(RoutingExit.Desynth, v.RunnerUp);
  }

  [Fact]
  public void PlaceholderSealRate_ReasonSaysRough()
  {
    var v = RoutingRules.Evaluate(T.Gear(seals: 100), T.Batch(empirical: false));
    Assert.Contains(", rough", v.Reason);
  }

  [Fact]
  public void EmpiricalSealRate_ReasonDoesNotSayRough()
  {
    var v = RoutingRules.Evaluate(T.Gear(seals: 100), T.Batch(empirical: true));
    Assert.DoesNotContain("rough", v.Reason);
  }
}

public class MeltRuleTests
{
  [Fact]
  public void MeltBeatsVendorMeaningfully_Desynths()
    => Assert.Equal(RoutingExit.Desynth,
      RoutingRules.Evaluate(T.Gear(melt: 2_000, vendor: 1_000), T.Batch()).Exit);

  [Fact]
  public void ThinMeltLead_GoesToReview()
  {
    var v = RoutingRules.Evaluate(T.Gear(melt: 1_400, vendor: 1_000), T.Batch());
    Assert.Equal(RoutingExit.Desynth, v.Exit);
    Assert.True(v.IsReview);
    Assert.Equal(RoutingExit.Vendor, v.RunnerUp);
  }

  [Fact]
  public void MeltBelowVendor_VendorWins()
    => Assert.Equal(RoutingExit.Vendor,
      RoutingRules.Evaluate(T.Gear(melt: 900, vendor: 1_000), T.Batch()).Exit);

  [Fact]
  public void UnvendorableWithKnownMelt_Desynths()
  {
    // Era review yellow 13: missing vendor price = NO floor, not an unknown
    // one. Any known-positive melt is the only gil exit.
    var v = RoutingRules.Evaluate(T.Gear(melt: 500, vendor: 0), T.Batch());
    Assert.Equal(RoutingExit.Desynth, v.Exit);
    Assert.False(v.IsReview);
  }
}

public class EvidenceOnlyTests
{
  [Fact]
  public void NoEvidenceNoAlmanac_LeansListInReview()
  {
    var v = RoutingRules.Evaluate(T.Gear(), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.True(v.IsReview);
  }

  [Fact]
  public void HealthyAlmanacVelocity_ListsConfidently()
  {
    var v = RoutingRules.Evaluate(T.Gear(velocity: 0.2), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.False(v.IsReview);
    Assert.Contains("moves here", v.Reason);
  }

  [Fact]
  public void DeadAlmanacMarket_VendorsWithListRunnerUp()
  {
    var v = RoutingRules.Evaluate(T.Gear(vendor: 100, velocity: 0.05), T.Batch());
    Assert.Equal(RoutingExit.Vendor, v.Exit);
    Assert.False(v.IsReview);
    Assert.Equal(RoutingExit.List, v.RunnerUp);
  }

  [Fact]
  public void UntradableVendorTrash_VendorsConfidently()
  {
    // Era review red 3 regression: the dungeon-clear tail must one-confirm,
    // not re-ask every run (Universalis can never settle an untradable).
    var v = RoutingRules.Evaluate(T.Gear(marketable: false, vendor: 120), T.Batch());
    Assert.Equal(RoutingExit.Vendor, v.Exit);
    Assert.False(v.IsReview);
    Assert.Contains("Untradable", v.Reason);
  }

  [Fact]
  public void UntradableUnvendorable_HonestShrug()
  {
    var v = RoutingRules.Evaluate(T.Gear(marketable: false, vendor: 0), T.Batch());
    Assert.Equal(RoutingExit.Vendor, v.Exit);
    Assert.True(v.IsReview);
  }
}

public class FallbackTests
{
  [Fact]
  public void NonGearNoEvidence_Vendorable_Vendors()
    => Assert.Equal(RoutingExit.Vendor,
      RoutingRules.Evaluate(T.Gear(equipment: false, vendor: 50), T.Batch()).Exit);

  [Fact]
  public void NothingAtAll_HonestShrug()
  {
    var v = RoutingRules.Evaluate(T.Gear(equipment: false, vendor: 0), T.Batch());
    Assert.Equal(RoutingExit.Vendor, v.Exit);
    Assert.True(v.IsReview);
  }
}
