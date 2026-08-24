using System;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE CACHED POST (Rounds unit 3): the gate that decides whether the act leg spends
/// a banked decision, and the panel veto's arithmetic behind its provider seam.
///
/// <para>The veto cases matter more here than their reachability suggests. Two of the
/// three branches cannot fire in the shipped plugin - no post-time board read exists
/// (verified in game 2026-08-10) - so a fake provider is the ONLY thing that ever
/// exercises them. Untested unreachable code is how a seam becomes a guess the day
/// something finally arrives to use it.</para>
/// </summary>
public class CachedPostTests
{
  private const long Now = 1_700_000_000L;
  private const int FreshHours = 24;

  private static DecisionCacheRow Row(long? price, long bankedAt, string outcome = "Undercut")
    => new(100u, false, price, outcome, "the evidence line", 7L, 42L, bankedAt);

  private static PostGuards NoGuards => new(0);

  // --- The gate: which door ---

  [Fact]
  public void NoBankedRow_PaysTheChain()
  {
    var plan = CachedPostGate.Decide(null, Now, FreshHours, NoGuards);
    Assert.Equal(CachedPostRoute.FullChain, plan.Route);
    Assert.False(plan.RidesCache);
    Assert.Contains("nothing banked", plan.Reason);
  }

  [Fact]
  public void FreshRowWithPrice_RidesTheCache()
  {
    var plan = CachedPostGate.Decide(Row(1890, Now - 7200), Now, FreshHours, NoGuards);
    Assert.True(plan.RidesCache);
    Assert.Equal(1890, plan.Price);
    Assert.Equal(Now - 7200, plan.BankedAt);
  }

  [Fact]
  public void StaleRow_PaysTheChain()
  {
    // A Round resumed two days later posts nothing blind.
    var plan = CachedPostGate.Decide(Row(1890, Now - 2 * 86400), Now, FreshHours, NoGuards);
    Assert.Equal(CachedPostRoute.FullChain, plan.Route);
    Assert.Contains("stale", plan.Reason);
  }

  [Fact]
  public void GateFreshnessBoundary_IsReconFreshnessBoundary()
  {
    // ONE rule, two doors: the gate must agree with the recon filter to the second,
    // or a round re-reads what it was about to post - or posts what it just called
    // too old to walk past.
    var cutoff = ReconFreshness.Cutoff(Now, FreshHours);

    Assert.True(CachedPostGate.Decide(Row(1890, cutoff), Now, FreshHours, NoGuards).RidesCache);
    Assert.False(CachedPostGate.Decide(Row(1890, cutoff - 1), Now, FreshHours, NoGuards).RidesCache);

    // And the same boundary read from the other door.
    Assert.False(ReconFreshness.NeedsRecon(cutoff, Now, FreshHours));
    Assert.True(ReconFreshness.NeedsRecon(cutoff - 1, Now, FreshHours));
  }

  [Fact]
  public void ZeroFreshHours_ClampsToAnHour_AndDoesNotPinTheActLegForever()
  {
    // A config typo read literally would make every row fresh forever (now - 0 =
    // now). The clamp turns the same typo into "almost everything is stale".
    Assert.False(CachedPostGate.Decide(Row(1890, Now - 7200), Now, 0, NoGuards).RidesCache);
    Assert.True(CachedPostGate.Decide(Row(1890, Now - 600), Now, 0, NoGuards).RidesCache);
  }

  [Fact]
  public void NullPriceRow_PaysTheChain()
  {
    // Recon HELD this item: a real answer, and not one anybody can post from. The row
    // buys recon a skip, not the act leg a shortcut.
    var plan = CachedPostGate.Decide(Row(null, Now - 600, "HeldThinHistory"), Now, FreshHours, NoGuards);
    Assert.Equal(CachedPostRoute.FullChain, plan.Route);
    Assert.Contains("hold", plan.Reason);
  }

  [Fact]
  public void GuardRejectedRow_BankedAsNullPrice_PaysTheChain()
  {
    // Unit 2's semantic: a below-floor / below-minimum verdict banks the OUTCOME with
    // no price. It reads here exactly like any other hold.
    var plan = CachedPostGate.Decide(Row(null, Now - 60, "Undercut"), Now, FreshHours, NoGuards);
    Assert.False(plan.RidesCache);
  }

  [Fact]
  public void ZeroOrNegativePrice_PaysTheChain()
  {
    Assert.False(CachedPostGate.Decide(Row(0, Now - 60), Now, FreshHours, NoGuards).RidesCache);
    Assert.False(CachedPostGate.Decide(Row(-5, Now - 60), Now, FreshHours, NoGuards).RidesCache);
  }

  // --- The gate: today's guards, re-checked ---

  [Fact]
  public void PriceUnderTodaysFloor_PaysTheChain()
  {
    // The floor moved between the Look and the Act (the player switched to Doman
    // Enclave pricing). Recon's verdict was honest against a rule that no longer
    // applies, so the banked price never posts.
    var plan = CachedPostGate.Decide(Row(1890, Now - 60), Now, FreshHours, new PostGuards(2000));
    Assert.Equal(CachedPostRoute.FullChain, plan.Route);
    Assert.Contains("floor", plan.Reason);
  }

  [Fact]
  public void PriceUnderTodaysMinimum_PaysTheChain_AsTheSameFloor()
  {
    // The one floor law (2026-08-21): the player's minimum arrives here already
    // max()'d with the mode floor, so the gate has one comparison to make and the
    // reason says "floor" whichever of the two rules bound.
    var plan = CachedPostGate.Decide(Row(1890, Now - 60), Now, FreshHours, new PostGuards(5000));
    Assert.Equal(CachedPostRoute.FullChain, plan.Route);
    Assert.Contains("floor", plan.Reason);
  }

  [Fact]
  public void PriceExactlyAtTheGuards_StillRides()
  {
    // The guards refuse prices BELOW them; the pipeline's own comparison is the same
    // strict one, and a re-check that disagreed by a gil would refuse listings the
    // fresh chain would happily post.
    Assert.True(CachedPostGate.Decide(Row(1890, Now - 60), Now, FreshHours,
      new PostGuards(1890)).RidesCache);
  }

  [Fact]
  public void GuardsOffMeansZero_NotAFloorOfZero()
  {
    Assert.True(CachedPostGate.Decide(Row(1, Now - 60), Now, FreshHours, new PostGuards(0)).RidesCache);
  }

  // --- The plan knows what it was built for ---

  [Fact]
  public void ThePlanCarriesTheVariantItWasBuiltFor()
  {
    var plan = CachedPostGate.Decide(
      new DecisionCacheRow(5057u, true, 1890, "Undercut", "e", null, 1, Now - 60),
      Now, FreshHours, NoGuards);

    Assert.True(plan.RidesCache);
    Assert.True(plan.Matches(5057u, true));
    // HQ is a slot flag the upstream slot guard never compares, so this is the check
    // that stops an NQ answer being posted on an HQ listing.
    Assert.False(plan.Matches(5057u, false));
    Assert.False(plan.Matches(100u, true));
  }

  // --- The veto: the three Q3 cases, against a fake provider ---

  private static Func<uint, bool, FreshBoardRead?> Provider(FreshBoardRead? read)
    => (_, _) => read;

  [Fact]
  public void NoProvider_PostsCached()
  {
    Assert.Equal(VetoVerdict.PostCached,
      CachedPostVeto.Decide(100u, false, 1890, null, 10));
  }

  [Fact]
  public void ProviderReturnsNothing_PostsCached()
  {
    // Case 3, and the only branch the shipped plugin reaches today.
    Assert.Equal(VetoVerdict.PostCached,
      CachedPostVeto.Decide(100u, false, 1890, Provider(null), 10));
  }

  [Fact]
  public void ProviderThrows_PostsCached()
  {
    Func<uint, bool, FreshBoardRead?> angry = (_, _) => throw new InvalidOperationException("no");
    Assert.Equal(VetoVerdict.PostCached, CachedPostVeto.Decide(100u, false, 1890, angry, 10));
  }

  [Fact]
  public void FreshCompleteBoard_AlwaysRePrices_NoThreshold()
  {
    // Case 1: the recalculation is free and the evidence is strictly better. Even a
    // board that agrees to the gil re-runs the spine - a threshold in front of a free
    // recalculation can only ever produce a worse answer.
    Assert.Equal(VetoVerdict.RePriceOnFresh,
      CachedPostVeto.Arbitrate(1890, new FreshBoardRead(Complete: true, FrontOfLine: 1890), 10));
    Assert.Equal(VetoVerdict.RePriceOnFresh,
      CachedPostVeto.Arbitrate(1890, new FreshBoardRead(Complete: true, FrontOfLine: null), 10));
  }

  [Fact]
  public void FreshPartialInsideTheBand_PostsCached()
  {
    // Case 2, agreeing: complete-but-banked beats fresh-but-censored.
    Assert.Equal(VetoVerdict.PostCached,
      CachedPostVeto.Arbitrate(1000, new FreshBoardRead(false, 1050), 10));
    Assert.Equal(VetoVerdict.PostCached,
      CachedPostVeto.Arbitrate(1000, new FreshBoardRead(false, 950), 10));
  }

  [Fact]
  public void FreshPartialBeyondTheBand_RePrices()
  {
    // Case 2, disagreeing: the market moved under the banked anchor, in either
    // direction - a wall appeared, or the front of the line collapsed.
    Assert.Equal(VetoVerdict.RePriceOnFresh,
      CachedPostVeto.Arbitrate(1000, new FreshBoardRead(false, 1200), 10));
    Assert.Equal(VetoVerdict.RePriceOnFresh,
      CachedPostVeto.Arbitrate(1000, new FreshBoardRead(false, 700), 10));
  }

  [Fact]
  public void FreshPartialExactlyAtTheThreshold_PostsCached()
  {
    // The knob names the TOLERANCE, so a disagreement equal to it is tolerated.
    Assert.Equal(VetoVerdict.PostCached,
      CachedPostVeto.Arbitrate(1000, new FreshBoardRead(false, 1100), 10));
    Assert.Equal(VetoVerdict.RePriceOnFresh,
      CachedPostVeto.Arbitrate(1000, new FreshBoardRead(false, 1101), 10));
  }

  [Fact]
  public void FreshPartialWithNothingToCompare_PostsCached()
  {
    // A partial read that knows nothing must not outvote a complete board.
    Assert.Equal(VetoVerdict.PostCached,
      CachedPostVeto.Arbitrate(1000, new FreshBoardRead(false, null), 10));
    Assert.Equal(VetoVerdict.PostCached,
      CachedPostVeto.Arbitrate(1000, new FreshBoardRead(false, 0), 10));
    Assert.Equal(VetoVerdict.PostCached,
      CachedPostVeto.Arbitrate(0, new FreshBoardRead(false, 900), 10));
  }

  [Fact]
  public void NegativeThreshold_ClampsToZero_AndStillTakesEquality()
  {
    Assert.Equal(VetoVerdict.PostCached, CachedPostVeto.Arbitrate(1000, new FreshBoardRead(false, 1000), -50));
    Assert.Equal(VetoVerdict.RePriceOnFresh, CachedPostVeto.Arbitrate(1000, new FreshBoardRead(false, 1001), -50));
  }

  [Fact]
  public void ProviderIsAskedWithTheItemsIdentity()
  {
    uint sawItem = 0;
    var sawHq = false;
    Func<uint, bool, FreshBoardRead?> spy = (id, hq) => { sawItem = id; sawHq = hq; return null; };
    CachedPostVeto.Decide(5057u, true, 1890, spy, 10);
    Assert.Equal(5057u, sawItem);
    Assert.True(sawHq);
  }

  // --- Provenance ---

  [Fact]
  public void AgeReadsInTheCoarsestHonestUnit()
  {
    // Plain words since Movement 1: the whole job of this clause is to stop an
    // hours-old decision reading like a fresh board read, and an abbreviation is
    // easier to skim past than a sentence.
    Assert.Equal("just now", CachedPostNote.Age(Now - 30, Now));
    Assert.Equal("12 minutes old", CachedPostNote.Age(Now - 12 * 60, Now));
    Assert.Equal("2 hours old", CachedPostNote.Age(Now - 2 * 3600, Now));
    Assert.Equal("3 days old", CachedPostNote.Age(Now - 3 * 86400, Now));
    Assert.Equal("1 minute old", CachedPostNote.Age(Now - 60, Now));
  }

  [Fact]
  public void TheLineSaysItPostedFromRecon_WithItsAgeAndItsPrice()
  {
    // A cached post must never dress as a fresh board read.
    var line = CachedPostNote.Line(1890, Now - 7200, Now).Line;
    Assert.Equal("List: Posted at 1,890. Priced from your Look, 2 hours old.", line);
  }

  [Fact]
  public void TheLineClaimsNoSeat_BecauseNoBoardWasRead()
  {
    // "1st in line" would be exactly the fresh-read costume this line refuses.
    var line = CachedPostNote.Line(1890, Now - 60, Now).Line;
    Assert.DoesNotContain("in line", line);
  }

  // ==========================================================================
  // The receipt true-up (Rounds unit 4): a cached post stops leaving recon's
  // anchor standing on the record
  // ==========================================================================

  private static DecisionCacheRow Banked(long? receiptId)
    => new(100u, false, 1890, "Undercut", "the evidence line", receiptId, 42L, Now - 60);

  /// <summary>
  /// The plan carries recon's receipt id, so the pass's ORDINARY ending can correct a
  /// receipt written hours earlier. Without it on the plan there is no second place to
  /// get it: no spine ran, so nothing this pass knows about the item came from a board.
  ///
  /// <para>The lane median used to ride beside it (V40) for the position_in_lane
  /// recompute; the doctrine sweep (2026-08-15) retired that ratio's writer and the
  /// carrier with it. The id is the whole payload now.</para>
  /// </summary>
  [Fact]
  public void ACachedPost_CarriesTheReceiptItWillTrueUp()
  {
    var plan = CachedPostGate.Decide(Banked(5943), Now, FreshHours, NoGuards);

    Assert.True(plan.RidesCache);
    Assert.Equal(5943, plan.ReceiptId);
    Assert.Equal(5943L, plan.TrueUp);
  }

  /// <summary>
  /// A chained item owes nothing: the spine runs this pass and writes its own receipt,
  /// which the ordinary ending trues up as it always did. A true-up here would correct
  /// a receipt from a different decision.
  /// </summary>
  [Fact]
  public void AChainedItem_OwesNoTrueUp()
  {
    Assert.Null(CachedPostGate.Decide(null, Now, FreshHours, NoGuards).TrueUp);
    Assert.Null(CachedPostTrueUp.Adopt(CachedPostPlan.Chain("stale"), refused: false));
  }

  /// <summary>
  /// A banked row with no receipt behind it (recon's receipt write failed, or the row
  /// predates the column) still POSTS - the price is good - it simply has no record to
  /// correct. Refusing the post over a missing receipt would cost a real listing to
  /// protect a reader surface.
  /// </summary>
  [Fact]
  public void ABankedRowWithNoReceipt_StillPostsAndOwesNoTrueUp()
  {
    var plan = CachedPostGate.Decide(Banked(null), Now, FreshHours, NoGuards);

    Assert.True(plan.RidesCache);
    Assert.Null(plan.TrueUp);
    Assert.Null(CachedPostTrueUp.Adopt(plan, refused: false));
  }

  /// <summary>
  /// A REFUSED cached post (the plan met a panel it was not built for) adopts nothing.
  /// It posts no listing, so there is no applied price a correction could be made
  /// from - and correcting a receipt off an unposted item would write a lie into the
  /// exact column this whole true-up exists to keep honest.
  /// </summary>
  [Fact]
  public void ARefusedCachedPost_AdoptsNothing()
  {
    var plan = CachedPostGate.Decide(Banked(5943), Now, FreshHours, NoGuards);

    Assert.Null(CachedPostTrueUp.Adopt(plan, refused: true));
  }

  /// <summary>
  /// The refusal's second door, downstream - CORRECTED (review ruling S7, 2026-08-12).
  ///
  /// <para>This test used to claim the second door was structural and prove it with
  /// operands the live panel cannot produce: a null current listing price. Live, the
  /// sell panel opens PRE-FILLED with the game's suggested ask, so the refusal arrived
  /// at the ending carrying a real number - and NoData being a held result, the run
  /// booked that suggestion as gil on market and stood ready to true a receipt up to
  /// it. The second door was open the whole time; the test was looking at a different
  /// door.</para>
  ///
  /// <para>The guard is now the caller's own knowledge of which pass ran, and it is
  /// asserted against the operands that actually arrive.</para>
  /// </summary>
  [Fact]
  public void ARefusalLeavesNoListingValueToTrueUpFrom()
  {
    // What the pre-filled panel really hands the ending.
    Assert.Equal(0, ListingAccounting.ListedUnitValue(
      PricingResult.NoData, finalPrice: null, currentListingPrice: 1890, postedNothing: true));

    // And with the guard off, the number the old test could never have caught.
    Assert.Equal(1890, ListingAccounting.ListedUnitValue(
      PricingResult.NoData, finalPrice: null, currentListingPrice: 1890));
  }

  /// <summary>
  /// A receipt written off a spine that produced NO LANE is trued up exactly like any
  /// other. This used to be the zero-median case - the true-up carried a denominator
  /// and had to be proven not to choke on a 0 - and the doctrine sweep (2026-08-15)
  /// removed the denominator entirely, which is the stronger version of the same
  /// guarantee: there is no longer an operand that could make the correction
  /// conditional on the lane.
  /// </summary>
  [Fact]
  public void ALanelessDecisionStillTruesUpThePrice()
  {
    var plan = CachedPostGate.Decide(Banked(5943), Now, FreshHours, NoGuards);

    Assert.Equal(5943L, plan.TrueUp);
  }
}
