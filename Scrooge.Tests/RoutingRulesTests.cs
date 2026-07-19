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
    Assert.Contains("tilted to turn-in", v.Reason);
  }

  [Fact]
  public void BorderlineGcContender_FullStock_StaysReview()
  {
    var v = RoutingRules.Evaluate(T.Gear(seals: 720, sale: (20_000, 0, 5)), T.Batch(stock: 1_300));
    Assert.True(v.IsReview);
  }

  // ---- Saturation: the 7-day projection tilts borderline calls AWAY from churn ----

  [Fact]
  public void BorderlineGcContender_ProjectionStillCruising_TiltsAwayFromChurn()
  {
    // Projected 3,500 - 800 = 2,700 > 2,000 cruise: the marginal seal funds a
    // venture weeks out, so the borderline call keeps the gil exit.
    var v = RoutingRules.Evaluate(T.Gear(seals: 720, sale: (20_000, 0, 5)),
      T.Batch(stock: 3_500, weeklyBurn: 800));
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.False(v.IsReview);
    Assert.Contains("seals saturated", v.Reason);
    Assert.Equal(RoutingExit.Gc, v.RunnerUp);
  }

  [Fact]
  public void BorderlineGcContender_ProjectionBelowCruise_StaysReview()
  {
    // Sam's live numbers the day this shipped: 2,262 - 740 = 1,522 < 2,000.
    // Stock LOOKS saturated; the projection says the seals get spent - no tilt.
    var v = RoutingRules.Evaluate(T.Gear(seals: 720, sale: (20_000, 0, 5)),
      T.Batch(stock: 2_262, weeklyBurn: 740));
    Assert.True(v.IsReview);
  }

  [Fact]
  public void BorderlineGcContender_NoBurnMeasurement_NoSaturationTilt()
  {
    // No measured burn = no projection = no tilt, however high the stock. The
    // saturation rule never acts on a guess.
    var v = RoutingRules.Evaluate(T.Gear(seals: 720, sale: (20_000, 0, 5)),
      T.Batch(stock: 9_999));
    Assert.True(v.IsReview);
  }

  [Fact]
  public void ClearGcWinner_ProjectionCruising_NotBorderline_StillChurns()
  {
    // Saturation only breaks TIES. A clear seal win (no gil contender near it)
    // still churns - the tilt is a tie-break, not a repricer.
    var v = RoutingRules.Evaluate(T.Gear(seals: 2_000, vendor: 1_000),
      T.Batch(stock: 3_500, weeklyBurn: 800));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.False(v.IsReview);
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
  public void SkillupEligible_ProvenJunkYields_StillDesynths()
  {
    // Melt 100 below vendor 500: the yields are proven junk and it does not
    // matter - red/yellow skillups are RARE, seals and gil are common (Sam's
    // ruling 07-18). The skillup is the value; the yield was never the point.
    var v = RoutingRules.Evaluate(T.Gear(skillup: true, melt: 100, vendor: 500), T.Batch());
    Assert.Equal(RoutingExit.Desynth, v.Exit);
    Assert.Contains("Skillup", v.Reason);
  }

  [Fact]
  public void SkillupEligible_MeltAtLeastVendor_Desynths()
    => Assert.Equal(RoutingExit.Desynth,
      RoutingRules.Evaluate(T.Gear(skillup: true, melt: 600, vendor: 500), T.Batch()).Exit);

  // ---- Sam's value hierarchy (07-18): the skillup is PRICED, not gated.
  // Worth seeds: yellow 50k, red 100k. Gil above the worth wins the market;
  // below it, the melter; near it, Review - all emergent from one comparison.

  [Fact]
  public void Skillup_OutranksOrdinaryLocalSale()
  {
    // 20k sale vs a yellow worth 50k: the rare skillup wins.
    var v = RoutingRules.Evaluate(T.Gear(skillup: true, sale: (20_000, 0, 5)), T.Batch());
    Assert.Equal(RoutingExit.Desynth, v.Exit);
    Assert.Contains("Skillup", v.Reason);
  }

  [Fact]
  public void VeryVeryHighLocalSale_OutranksSkillup()
  {
    // 200k sale vs yellow 50k: melting this is burning gil - market wins.
    var v = RoutingRules.Evaluate(T.Gear(skillup: true, sale: (200_000, 0, 5)), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
  }

  [Fact]
  public void VeryVeryHighCommunityValue_OutranksSkillup()
  {
    // Never sold locally, but the DC pays 200k on enough samples - the
    // community veto outbids both the seals and the priced skillup.
    var v = RoutingRules.Evaluate(
      T.Gear(skillup: true, seals: 500, communityMedian: 200_000, communityCount: 5),
      T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
  }

  [Fact]
  public void RedSkillup_WorthMoreThanYellow()
  {
    // An 80k sale outbids a yellow (50k) but NOT a red (100k) - red is rarer.
    Assert.Equal(RoutingExit.List,
      RoutingRules.Evaluate(T.Gear(skillup: true, sale: (80_000, 0, 5)), T.Batch()).Exit);
    Assert.Equal(RoutingExit.Desynth,
      RoutingRules.Evaluate(T.Gear(redSkillup: true, sale: (80_000, 0, 5)), T.Batch()).Exit);
  }

  [Fact]
  public void SaleNearSkillupWorth_LandsInReview()
  {
    // 95k sale vs a red worth 100k: inside the review band - honest coin flip,
    // the player rules it.
    var v = RoutingRules.Evaluate(T.Gear(redSkillup: true, sale: (95_000, 0, 5)), T.Batch());
    Assert.True(v.IsReview);
  }
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
  public void HealthyAlmanacVelocity_VelocityAloneIsNotAConfidentList()
  {
    // Finding 9 (Green Beret): velocity measures MOVEMENT, not WORTH. A live
    // world velocity with no price witness (never sold, no qualifying community
    // median) can no longer confident-List — an item can "move" at 1 gil
    // forever. It leans List but lands in Review; the player supplies the price.
    var v = RoutingRules.Evaluate(T.Gear(velocity: 0.2), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.True(v.IsReview);
    Assert.Equal(RoutingExit.Vendor, v.RunnerUp);
  }

  [Fact]
  public void DeadAlmanacMarket_MarketableGear_IsReviewNotConfidentVendor()
  {
    // Finding 9 (Cashmere Hood): a dead WORLD velocity does not prove a
    // DC-tradable item is worthless — dead-world listings still sell to
    // world-hoppers. With no price witness, a marketable item can no longer be
    // confident-vendored on velocity alone; it leans List-and-forget in Review.
    var v = RoutingRules.Evaluate(T.Gear(vendor: 100, velocity: 0.05), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.True(v.IsReview);
    Assert.Equal(RoutingExit.Vendor, v.RunnerUp);
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

public class CommunityCrossCheckTests
{
  // Rule 6 veto: gear with no LOCAL sale used to lose to seals by forfeit —
  // the market was never consulted. DC settled sales are the missing witness.

  [Fact]
  public void CommunityBeatsSeals_RoutesToList()
  {
    // 2,000 seals at 25 gil/seal = 50,000; the DC pays 100,000 — list it.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 2000, communityMedian: 100_000, communityCount: 3), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.False(v.IsReview);
    Assert.Contains("Universalis community", v.Reason);
  }

  [Fact]
  public void CommunityBelowSeals_SealsStillWin()
  {
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 2000, communityMedian: 30_000, communityCount: 5), T.Batch());
    Assert.Equal(RoutingExit.Gc, v.Exit);
  }

  [Fact]
  public void ThinCommunitySample_DoesNotVeto()
  {
    // Two DC sales is gossip, not evidence — same bar the lane uses.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 2000, communityMedian: 100_000, communityCount: 2), T.Batch());
    Assert.Equal(RoutingExit.Gc, v.Exit);
  }

  [Fact]
  public void CommunityNearSeals_DegradesToReview()
  {
    // 55k vs 50k is inside the 15% band — honest Review, List leading.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 2000, communityMedian: 55_000, communityCount: 3), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.True(v.IsReview);
    Assert.Equal(RoutingExit.Gc, v.RunnerUp);
  }

  [Fact]
  public void LocalSaleEvidence_OutranksCommunity()
  {
    // A real local sale below the list floor already answered the question —
    // the community number never re-litigates rule 4's rejection.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 2000, sale: (3_000, 0, 5), communityMedian: 100_000, communityCount: 5),
      T.Batch());
    Assert.Equal(RoutingExit.Gc, v.Exit);
  }

  [Fact]
  public void VentureLowStock_StillChurnsDespiteCommunity()
  {
    // The band guard outranks the veto by design: thin stock churns.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 2000, communityMedian: 100_000, communityCount: 3), T.Batch(stock: 600));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.Contains("Venture low", v.Reason);
  }

  // Evidence-only branch: DC history outranks home-world velocity as witness.

  [Fact]
  public void EvidenceOnly_CommunityAboveWorthFloor_ListsConfidently()
  {
    var v = RoutingRules.Evaluate(
      T.Gear(communityMedian: 20_000, communityCount: 4), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.False(v.IsReview);
    Assert.Contains("the DC buys it", v.Reason);
  }

  [Fact]
  public void EvidenceOnly_CommunityBelowWorthFloor_VendorsWithListRunnerUp()
  {
    var v = RoutingRules.Evaluate(
      T.Gear(vendor: 300, communityMedian: 800, communityCount: 6), T.Batch());
    Assert.Equal(RoutingExit.Vendor, v.Exit);
    Assert.Equal(RoutingExit.List, v.RunnerUp);
  }

  [Fact]
  public void EvidenceOnly_CommunityOutranksDeadVelocity()
  {
    // Velocity ~0 on one world hides gear that sells DC-wide — the community
    // read wins over the velocity shrug.
    var v = RoutingRules.Evaluate(
      T.Gear(velocity: 0.01, communityMedian: 20_000, communityCount: 4), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.Contains("the DC buys it", v.Reason);
  }
}

/// <summary>
/// Finding 9 (session 3): price x velocity are ONE witness, not two. Velocity
/// measures whether an item MOVES; the price witness (a local sale or a
/// qualifying community median) measures whether it is WORTH listing. The
/// router must weigh them together — the Cashmere Hood was sold for 1 gil
/// (~95k EV) and the Green Beret was listed at a 1-gil junk price, both because
/// velocity was read WITHOUT its price partner.
/// </summary>
public class PriceVelocityWitnessTests
{
  // ---- High value + dead velocity: List-and-forget / Review, NEVER confident Vendor ----

  [Fact]
  public void CashmereHood_HighCommunityValue_DeadWorldVelocity_ListsNotVendors()
  {
    // The DC pays ~95k on 8 sales; world velocity is 0/day. DC travel means a
    // dead-world listing still sells to world-hoppers — the price witness
    // clears the worth floor, so it lists, never confident-vendors for 1 gil.
    var v = RoutingRules.Evaluate(
      T.Gear(vendor: 1, velocity: 0.0, communityMedian: 95_000, communityCount: 8),
      T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.False(v.IsReview);
    Assert.Contains("the DC buys it", v.Reason);
  }

  [Fact]
  public void HighValue_DeadVelocity_NoCommunityYet_IsReviewNeverConfidentVendor()
  {
    // The instability half of the Cashmere miss: the price witness (community)
    // has not arrived, only a dead world velocity has. A marketable item is
    // NEVER confident-vendored on velocity alone — Review leaning List-and-forget.
    var v = RoutingRules.Evaluate(T.Gear(vendor: 1, velocity: 0.0), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.True(v.IsReview);
    Assert.Equal(RoutingExit.Vendor, v.RunnerUp);
  }

  // ---- Low value + live velocity: Vendor, NEVER List ----

  [Fact]
  public void GreenBeret_LowCommunityValue_LiveVelocity_VendorsNotLists()
  {
    // 7 community receipts at ~1 gil (below the worth floor) with a live world
    // velocity: it moves, but only at a junk price. The price witness settles it
    // — Vendor, never a "moves here" List. (This vendor call is correct.)
    var v = RoutingRules.Evaluate(
      T.Gear(vendor: 1, velocity: 0.2, communityMedian: 1, communityCount: 7),
      T.Batch());
    Assert.Equal(RoutingExit.Vendor, v.Exit);
    Assert.Equal(RoutingExit.List, v.RunnerUp);
  }

  [Fact]
  public void LowValue_LiveVelocity_NoPriceWitness_IsReviewNeverConfidentList()
  {
    // The Green Beret smell before the price witness arrives: a live velocity,
    // no price on record. "Moves here" is no longer a confident List — an item
    // can move at 1 gil forever. Review, leaning List, vendor as the runner-up.
    var v = RoutingRules.Evaluate(T.Gear(vendor: 1, velocity: 0.2), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.True(v.IsReview);
    Assert.Equal(RoutingExit.Vendor, v.RunnerUp);
  }
}
