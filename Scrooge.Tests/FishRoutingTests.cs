using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// Fish enter the contest (ruled 2026-08-28: fish are the main way to train
/// CUL desynth). A desynthable is a desynthable — the routing doors that used
/// to say "gear" must give a fish-shaped item the same answer they give
/// equipment, and the tag map must speak for it.
/// </summary>
public class FishRoutingTests
{
  /// <summary>A fish: marketable, desynthable, not equipment, no evidence.</summary>
  private static RoutingItemInputs Fish(
    int vendor = 0, bool marketable = true,
    long? communityMedian = null, int communityCount = 0,
    double? velocity = null)
    => T.Gear(vendor: vendor, equipment: false, marketable: marketable,
      desynthable: true, communityMedian: communityMedian,
      communityCount: communityCount, velocity: velocity);

  [Fact]
  public void NoEvidenceDoor_AnUntradableFishGetsGearsAnswer()
  {
    // The untradable no-evidence arm: desynth is the only surviving exit,
    // handed over as a Review ("melt one to learn"). Same shape as gear.
    var batch = T.Batch();
    var gear = RoutingRules.Evaluate(
      T.Gear(equipment: true, marketable: false, desynthable: true), batch);
    var fish = RoutingRules.Evaluate(Fish(marketable: false), batch);

    Assert.Equal(RoutingExit.Desynth, fish.Exit);
    Assert.True(fish.IsReview);
    Assert.Equal(gear.Exit, fish.Exit);
    Assert.Equal(gear.Reason, fish.Reason);
  }

  [Fact]
  public void NoEvidenceDoor_AMarketableFishHearsTheSameWitnessesAsGear()
  {
    // Marketable, every local witness silent, DC history speaks: whatever the
    // door concludes for gear, it concludes for the fish. The invariant is
    // equality — the door no longer reads the equip slot.
    var batch = T.Batch();
    var gear = RoutingRules.Evaluate(
      T.Gear(equipment: true, desynthable: true,
        communityMedian: 500, communityCount: 8, velocity: 2.0), batch);
    var fish = RoutingRules.Evaluate(
      Fish(communityMedian: 500, communityCount: 8, velocity: 2.0), batch);

    Assert.Equal(gear.Exit, fish.Exit);
    Assert.Equal(gear.IsReview, fish.IsReview);
    Assert.Equal(gear.Reason, fish.Reason);
  }

  [Fact]
  public void Melt_AFishWithoutASkillupNeverMelts()
  {
    // Ruled 2026-08-28: fish melt for shit — only melt fish for a skill up.
    // Even a measured yield doesn't open the door without a color.
    var verdict = RoutingRules.Evaluate(
      T.Gear(equipment: false, desynthable: true, melt: 2_000, vendor: 5),
      T.Batch());
    Assert.NotEqual(RoutingExit.Desynth, verdict.Exit);
  }

  [Fact]
  public void Melt_AFishNeverBorrowsTheGearBandPrior()
  {
    // The band average is gear knowledge; a fish reading it would poison the
    // comparison with yields it doesn't have.
    var verdict = RoutingRules.Evaluate(
      T.Gear(equipment: false, desynthable: true, vendor: 5,
        meltPrior: new MeltPrior(50_000, 650, 699, 200, 40, false)),
      T.Batch());
    Assert.NotEqual(RoutingExit.Desynth, verdict.Exit);
  }

  [Fact]
  public void Melt_ASkillupIsTheFishsOneMeltCase()
  {
    // A red-eligible fish IS the CUL training case: skillup worth carries the
    // melt exit past the vendor counter.
    var verdict = RoutingRules.Evaluate(
      T.Gear(equipment: false, desynthable: true, redSkillup: true, vendor: 5),
      T.Batch());
    Assert.Equal(RoutingExit.Desynth, verdict.Exit);
  }

  [Fact]
  public void Melt_ASkillupThatOutbidsALookSaysSo()
  {
    // The Ceremonial Codex receipt (2026-08-28): melt won on the red peg over
    // a 33,331 Look and the reason never mentioned the contest. The decision's
    // live operands belong at the decision.
    var verdict = RoutingRules.Evaluate(
      T.Gear(equipment: false, desynthable: true, redSkillup: true, vendor: 5,
        lookAsk: 33_331, lookAgeHours: 0.2),
      T.Batch());
    Assert.Equal(RoutingExit.Desynth, verdict.Exit);
    Assert.Contains("Outbids your Look at 33,331", verdict.Reason);
  }

  [Fact]
  public void Melt_ASkillupOverTheTapeNamesTheTapeNotTheLook()
  {
    // The Ceremonial Earring receipt (2026-08-29): the tape seated List at
    // ~70,000 while the outbid clause quoted the unseated Look at 99,499 —
    // one row, two "what List is worth" numbers. The clause names the SEATED
    // witness (the cell's own operand); the Look rides as a labeled aside in
    // recon's own grammar, because "what it settles for" and "what you'd ask"
    // answer different questions.
    var verdict = RoutingRules.Evaluate(
      T.Gear(equipment: false, desynthable: true, redSkillup: true, vendor: 5,
        tapeMedian: 70_000, tapeCount: 20,
        lookAsk: 99_499, lookAgeHours: 0.2),
      T.Batch());
    Assert.Equal(RoutingExit.Desynth, verdict.Exit);
    Assert.Contains("Outbids what it settles for, ~70,000 (20 sales)", verdict.Reason);
    Assert.Contains("Listed, you'd ask 99,499", verdict.Reason);
    Assert.DoesNotContain("Outbids your Look", verdict.Reason);
  }

  [Fact]
  public void Melt_ASkillupOverTheTapeWithNoLookSkipsTheAskAside()
  {
    // No Look means no ask to speak of — the aside must not invent one.
    var verdict = RoutingRules.Evaluate(
      T.Gear(equipment: false, desynthable: true, redSkillup: true, vendor: 5,
        tapeMedian: 70_000, tapeCount: 20),
      T.Batch());
    Assert.Equal(RoutingExit.Desynth, verdict.Exit);
    Assert.Contains("Outbids what it settles for, ~70,000 (20 sales)", verdict.Reason);
    Assert.DoesNotContain("you'd ask", verdict.Reason);
  }

  [Fact]
  public void Melt_ASkillupOverYourOwnSaleNamesTheSale()
  {
    // Own-sale seat: same law, the seated witness speaks.
    var verdict = RoutingRules.Evaluate(
      T.Gear(equipment: false, desynthable: true, redSkillup: true, vendor: 5,
        sale: (40_000, 1_000, 3), saleAgeDays: 2),
      T.Batch());
    Assert.Equal(RoutingExit.Desynth, verdict.Exit);
    Assert.Contains("Outbids your own sale at 40,000", verdict.Reason);
  }

  [Fact]
  public void Melt_ASkillupUnderTheLookStaysQuietAboutIt()
  {
    // Worth below the Look = no outbid happened; the sentence must not claim one.
    var verdict = RoutingRules.Evaluate(
      T.Gear(equipment: false, desynthable: true, skillup: true, vendor: 5,
        lookAsk: 80_000, lookAgeHours: 0.2),
      T.Batch());
    Assert.DoesNotContain("outbids", verdict.Reason);
  }

  [Fact]
  public void Tags_ADesynthableFishSpeaksItsTag()
  {
    // The Hawk Route column: a fish with a real melt case gets a verdict tag
    // instead of the old blank-by-construction.
    var result = RouteTagMap.Evaluate(
      T.Gear(equipment: false, desynthable: true, melt: 2_000, vendor: 5),
      T.Batch());
    Assert.NotEqual(RouteTagMap.Verdict.None, result.Verdict);
  }

  [Fact]
  public void Tags_PlainMatsStayUntagged()
  {
    // Non-desynthable non-equipment (crafting mats, consumables) keeps the
    // carve-out: no tag.
    var result = RouteTagMap.Evaluate(
      T.Gear(equipment: false, desynthable: false, vendor: 5), T.Batch());
    Assert.Equal(RouteTagMap.Verdict.None, result.Verdict);
  }
}
