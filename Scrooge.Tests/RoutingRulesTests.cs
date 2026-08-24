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

public class RoutingScoresTests
{
  // V20 receipts: every value-rule verdict carries the four alternative scores
  // as weighed - the counterfactual the scoreboard joins against.

  [Fact]
  public void ValueVerdict_CarriesAllFourScores()
  {
    var v = RoutingRules.Evaluate(
      T.Gear(sale: (50_000, 0, 5), seals: 400, melt: 3_000, vendor: 900), T.Batch(sealRate: 25));
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.NotNull(v.Scores);
    Assert.Equal(50_000, v.Scores!.Value.List);
    Assert.Equal(400L * 25, v.Scores!.Value.Gc);
    Assert.Equal(3_000, v.Scores!.Value.Melt);
    Assert.Equal(900, v.Scores!.Value.Vendor);
  }

  [Fact]
  public void MissingEvidence_LeavesScoreNull_NotZero()
  {
    var v = RoutingRules.Evaluate(T.Gear(vendor: 500), T.Batch());
    Assert.NotNull(v.Scores);
    Assert.Null(v.Scores!.Value.List);
    Assert.Null(v.Scores!.Value.Gc);
    Assert.Null(v.Scores!.Value.Melt);
    Assert.Equal(500, v.Scores!.Value.Vendor);
  }

  [Fact]
  public void SettledTape_ScoresListWhenTheOwnSaleIsSilent()
  {
    // F6 (ruled 08-22): this world's banked settled sales - everyone's tape,
    // already trusted to BUILD prices - finally seated as a List scoring
    // witness. No own sale, tape at 120 over 8 banked sales: the router reads
    // the table its accuser reads, and the 75-gil-class penny referral never
    // has to happen.
    var v = RoutingRules.Evaluate(
      T.Gear(vendor: 4, tapeMedian: 120, tapeCount: 8, tapeAgeDays: 6), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.Equal(120, v.Scores!.Value.List);
    Assert.Contains("this world's settled sales", v.Reason);
  }

  [Fact]
  public void SettledTape_LocalOutranksDC_TheVetoNeverFires()
  {
    // F4's precedence law: with the local tape speaking, the DC's read is
    // context, not the witness - even when the DC number is bigger.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 400, tapeMedian: 30_000, tapeCount: 6,
        communityMedian: 60_000, communityCount: 9), T.Batch(sealRate: 25));
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.Equal(30_000, v.Scores!.Value.List);
    Assert.Contains("this world's settled sales", v.Reason);
  }

  [Fact]
  public void SettledTape_AFreshOwnSaleStillOutranksIt()
  {
    // The own receipt is the strongest witness the plugin holds - the tape
    // speaks only where it is silent or stale.
    var v = RoutingRules.Evaluate(
      T.Gear(sale: (50_000, 0, 5), saleAgeDays: 3, tapeMedian: 9_000, tapeCount: 20),
      T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.Equal(50_000, v.Scores!.Value.List);
  }

  [Fact]
  public void SettledTape_UnderTheFloor_ForfeitsListWithoutAccusing()
  {
    // The Tumbleclaw composition, bag-side: a tape clearing under the minimum
    // names an honest ask the floor refuses - List forfeits, the other exits
    // compete, and no contradiction theater rides along.
    var v = RoutingRules.Evaluate(
      T.Gear(vendor: 4, tapeMedian: 44, tapeCount: 12), T.Batch(cfg: new RoutingConfig
      {
        MinimumListingPrice = 75,
      }));
    Assert.Equal(RoutingExit.Vendor, v.Exit);
    Assert.Null(v.Scores!.Value.List);
  }

  [Fact]
  public void SettledTape_TooFewSales_StaysSilent()
  {
    // The same min-samples bar every market witness answers to: a 1-sale tape
    // never SCORES List. The item routes exactly as it did before the witness
    // existed - here, the evidence-only door (Review, leaning List).
    var v = RoutingRules.Evaluate(
      T.Gear(vendor: 400, tapeMedian: 9_000, tapeCount: 1), T.Batch());
    Assert.True(v.IsReview);
    Assert.DoesNotContain("this world's settled sales", v.Reason);
  }

  [Fact]
  public void CommunityVeto_PutsTheMedianItWeighedInTheListSlot()
  {
    // No local sale - the community median IS the list witness the router used.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 400, communityMedian: 60_000, communityCount: 8), T.Batch(sealRate: 25));
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.Equal(60_000, v.Scores!.Value.List);
  }

  [Fact]
  public void PreValueEarlyExits_CarryNoScores()
  {
    // Ban / protection / always-vendor fire before any value comparison ran - a
    // receipt with scores there would be an invention.
    Assert.Null(RoutingRules.Evaluate(T.Gear(banned: true), T.Batch()).Scores);
    Assert.Null(RoutingRules.Evaluate(T.Gear(isProtected: true), T.Batch()).Scores);
    Assert.Null(RoutingRules.Evaluate(T.Gear(alwaysVendor: true), T.Batch()).Scores);
  }

  [Fact]
  public void SkillupPricing_ShowsInTheMeltScore()
  {
    // The receipt must show the INFLATED melt candidate - that's the number the
    // comparison actually ran on.
    var v = RoutingRules.Evaluate(T.Gear(redSkillup: true, vendor: 100), T.Batch());
    Assert.Equal(new RoutingConfig().SkillupWorthRed, v.Scores!.Value.Melt);
  }
}

public class ListEvidenceTests
{
  [Fact]
  public void SaleOnRecord_ListsConfidently()
  {
    var v = RoutingRules.Evaluate(T.Gear(sale: (20_000, 0, 5)), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.False(v.IsReview);
  }

  // THE DOOR GATES ARE GONE (cleanup pass): a sale is evidence of what the market
  // pays, and the exits compete on score. A small sale, a slow sale or a dead
  // world velocity no longer disqualifies the List exit - it just gives the other
  // exits a smaller number to beat.

  [Fact]
  public void SmallSale_StillScoresAsTheListValue()
  {
    var v = RoutingRules.Evaluate(T.Gear(vendor: 100, sale: (14_999, 0, 5)), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.Equal(14_999, v.Scores!.Value.List);
  }

  [Fact]
  public void SlowSale_StillLists()
  {
    var v = RoutingRules.Evaluate(T.Gear(vendor: 100, sale: (20_000, 0, 11)), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
  }

  [Fact]
  public void UnknownSitTime_Lists()
    => Assert.Equal(RoutingExit.List,
      RoutingRules.Evaluate(T.Gear(sale: (20_000, 0, null)), T.Batch()).Exit);

  [Fact]
  public void UnknownSitTime_DeadAlmanacVelocity_StillLists()
  {
    var v = RoutingRules.Evaluate(
      T.Gear(vendor: 100, sale: (20_000, 0, null), velocity: 0.05), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
  }

  [Fact]
  public void NonGear_SaleScoresAsIs()
    => Assert.Equal(RoutingExit.List,
      RoutingRules.Evaluate(T.Gear(equipment: false, sale: (6_000, 0, null)), T.Batch()).Exit);

  [Fact]
  public void SubLowStock_WinningListing_Lists()
  {
    // THE LOW-STOCK DECREE IS DEAD (ruled 08-22): sub-750 stock used to seize a
    // 20k listing for the GC because 20k sat under a 45k escape nobody could see.
    // Exits compete on score, period - a live 20k sale beats 100 seals of turn-in,
    // and restocking is the player's call, made at the dashboard's warning color.
    var v = RoutingRules.Evaluate(T.Gear(seals: 100, sale: (20_000, 0, 5)), T.Batch(stock: 700));
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.DoesNotContain("Venture low", v.Reason);
  }

  [Fact]
  public void SubLowStock_ValuableListing_StaysListed()
  {
    // Same law from the other side - always listed, now for the honest reason
    // (score), not because 50k happened to clear a hidden escape.
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
    // 1,250: at the tilt band's edge (no thin-stock tilt) and barely onto the
    // curve (~96% seal value) - the tie survives and Review is the honest call.
    var v = RoutingRules.Evaluate(T.Gear(seals: 720, sale: (20_000, 0, 5)), T.Batch(stock: 1_250));
    Assert.True(v.IsReview);
  }

  // ---- The seal S-curve through Resolve (2026-08-05) ----

  // The saturation tilt (07-25's burn-projection tie-break) retired with the
  // curve: everywhere the projection used to act, the curve has already cut
  // the seal score smoothly with stock, so the tie the tilt broke never forms.
  // These pin the curve producing the same calls without the extra mechanism.

  [Fact]
  public void StockedUp_TheCurveSettlesTheOldSaturationCall()
  {
    // The saturation tilt's own convicting board, re-decided by the curve:
    // 5,100 tokens is past the melt line, seals score at nothing, and the gil
    // exit wins outright - no projection, no tie to break.
    var v = RoutingRules.Evaluate(T.Gear(seals: 720, sale: (20_000, 0, 5)),
      T.Batch(stock: 5_100));
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.False(v.IsReview);
  }

  [Fact]
  public void MidCurve_SealsCutEnoughThatTheOldReviewTieNeverForms()
  {
    // 2,262 tokens = ~31% seal value: 720 seals score ~5,500 against a 20,000
    // sale. The old flat rate made this a Review tie; the curve makes it no
    // contest. (Drift's live numbers from the day the saturation tilt shipped.)
    var v = RoutingRules.Evaluate(T.Gear(seals: 720, sale: (20_000, 0, 5)),
      T.Batch(stock: 2_262));
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.False(v.IsReview);
  }

  [Fact]
  public void PastTheMeltLine_EvenABigSealPileLosesToAnyGilExit()
  {
    // "if I am above 3k ventures, melt everything, don't turn in anything" -
    // 2,000 seals at factor 0 score 0, so even a vendor price beats them.
    var v = RoutingRules.Evaluate(T.Gear(seals: 2_000, vendor: 1_000),
      T.Batch(stock: 3_500));
    Assert.Equal(RoutingExit.Vendor, v.Exit);
  }

  [Fact]
  public void UnreadableStock_NeverCurvesBlind()
  {
    // No stock read = no curve: the seal pile scores at full rate and this
    // stays the honest Review tie it always was.
    var v = RoutingRules.Evaluate(T.Gear(seals: 720, sale: (20_000, 0, 5)),
      T.Batch(stock: null));
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
  public void SkillupEligible_ProvenJunkYields_StillDesynths()
  {
    // Melt 100 below vendor 500: the yields are proven junk and it does not
    // matter - red/yellow skillups are RARE, seals and gil are common (Drift's
    // ruling 07-18). The skillup is the value; the yield was never the point.
    var v = RoutingRules.Evaluate(T.Gear(skillup: true, melt: 100, vendor: 500), T.Batch());
    Assert.Equal(RoutingExit.Desynth, v.Exit);
    Assert.Contains("Skillup", v.Reason);
  }

  [Fact]
  public void SkillupEligible_MeltAtLeastVendor_Desynths()
    => Assert.Equal(RoutingExit.Desynth,
      RoutingRules.Evaluate(T.Gear(skillup: true, melt: 600, vendor: 500), T.Batch()).Exit);

  // ---- Drift's value hierarchy (07-18): the skillup is PRICED, not gated.
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
  public void SubLowStock_SealsWinOnScoreAlone()
  {
    // The decree is dead (ruled 08-22), and this fixture proves nothing changed
    // for a row the seals genuinely earn: 800 seals (~20k gil) beat a 10k melt on
    // SCORE, at any stock level. The reason speaks the arithmetic, not the band.
    var v = RoutingRules.Evaluate(T.Gear(seals: 800, melt: 10_000, vendor: 500), T.Batch(stock: 700));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.DoesNotContain("Venture low", v.Reason);
    Assert.Contains("Turn in", v.Reason);
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
    // Vendor is a viable exit (PriceLow > 0), so NOT excluded.
    var v = RoutingRules.Evaluate(T.Gear(marketable: false, vendor: 120), T.Batch());
    Assert.False(v.IsExcluded);
    Assert.Equal(RoutingExit.Vendor, v.Exit);
    Assert.False(v.IsReview);
    Assert.Contains("Untradable", v.Reason);
  }
}

public class NoViableExitTests
{
  // Drift's 07-23 ruling: gear with zero viable exits (untradable, not
  // desynthable, no seals, no vendor value — the Vana'dielian raid pair) is not
  // a decision, so it is excluded from the Ledger entirely. The exclusion is a
  // live derivation over the four existing exit signals, never a persisted flag.

  [Fact]
  public void ZeroExitGear_IsExcluded()
  {
    // Untradable, not desynthable, no seals, no vendor value: nothing to weigh.
    var item = T.Gear(marketable: false, desynthable: false, vendor: 0);
    Assert.True(RoutingRules.HasNoViableExit(item));

    var v = RoutingRules.Evaluate(item, T.Batch());
    Assert.True(v.IsExcluded);
  }

  [Fact]
  public void UntradableWithVendorPrice_NotExcluded_KeepsVendorVerdict()
  {
    // Regression guard: a vendor price is an exit — must keep the Vendor verdict.
    var item = T.Gear(marketable: false, desynthable: false, vendor: 120);
    Assert.False(RoutingRules.HasNoViableExit(item));

    var v = RoutingRules.Evaluate(item, T.Batch());
    Assert.False(v.IsExcluded);
    Assert.Equal(RoutingExit.Vendor, v.Exit);
    Assert.False(v.IsReview);
  }

  [Fact]
  public void UntradableWithMeltEvidence_NotExcluded_MeltsForGil()
  {
    // Melt evidence is an exit — the item routes to Desynth as before.
    var item = T.Gear(marketable: false, vendor: 0, melt: 3_000);
    Assert.False(RoutingRules.HasNoViableExit(item));

    var v = RoutingRules.Evaluate(item, T.Batch());
    Assert.False(v.IsExcluded);
    Assert.Equal(RoutingExit.Desynth, v.Exit);
  }

  [Fact]
  public void UntradableWithSeals_NotExcluded()
  {
    var item = T.Gear(marketable: false, desynthable: false, vendor: 0, seals: 400);
    Assert.False(RoutingRules.HasNoViableExit(item));
    Assert.False(RoutingRules.Evaluate(item, T.Batch()).IsExcluded);
  }

  [Fact]
  public void DesynthableButNoEvidence_NotExcluded_SurvivesAsReview()
  {
    // Desynthable gear with no yield history still HAS an exit (melt it to
    // learn). Not excluded; lands in Review pointing at the desynth exit —
    // this is the case the old "No gil exit at all" row over-claimed.
    var item = T.Gear(marketable: false, desynthable: true, vendor: 0);
    Assert.False(RoutingRules.HasNoViableExit(item));

    var v = RoutingRules.Evaluate(item, T.Batch());
    Assert.False(v.IsExcluded);
    Assert.True(v.IsReview);
    Assert.Contains("desynth", v.Reason, System.StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void MarketableWithNoEvidence_NotExcluded()
  {
    // Marketable gear always has the List exit open, even with no local
    // evidence — the honest-shrug Review row, unchanged.
    var item = T.Gear(marketable: true, desynthable: false, vendor: 0);
    Assert.False(RoutingRules.HasNoViableExit(item));
    Assert.False(RoutingRules.Evaluate(item, T.Batch()).IsExcluded);
  }

  [Fact]
  public void ExcludedVerdict_FailsSafeToHoldNotAction()
  {
    // Defense in depth: if an excluded verdict ever leaked past the Ledger
    // filter, its exit must map to the observed-only SILENT board, never a
    // phantom Vendor/Melt/List action.
    var v = RoutingRules.Evaluate(T.Gear(marketable: false, desynthable: false, vendor: 0), T.Batch());
    Assert.True(v.IsExcluded);
    Assert.Equal(RoutingExit.Hold, v.Exit);
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
    // A real FRESH local sale below the list floor already answered the question —
    // the community number never re-litigates rule 4's rejection.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 2000, sale: (3_000, 0, 5), saleAgeDays: 3,
        communityMedian: 100_000, communityCount: 5),
      T.Batch());
    Assert.Equal(RoutingExit.Gc, v.Exit);
  }

  // ---- SF-B2 (ruled 2026-08-13): "let the DC speak if the local sale is stale" ----

  [Fact]
  public void StaleLocalSale_NoLongerSilencesTheCommunity()
  {
    // THE LABRYS EXHIBIT (2026-08-13): an April sale at 13,338 silenced 9 fresh
    // DC sales at ~7,780, and the row sat unruled in Review over a question the
    // DC had already answered. A sale past the window is the same evidential
    // silence as no sale - the veto runs and the DC's number wins.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 1691, sale: (13_338, 0, 5), saleAgeDays: 107,
        communityMedian: 7_780, communityCount: 9),
      T.Batch(sealRate: 2));
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.Equal(7_780, v.Scores!.Value.List);
    Assert.Contains("Universalis community", v.Reason);
    // The verdict names the sale it overruled - outranked, not forgotten.
    Assert.Contains("13,338", v.Reason);
    Assert.Contains("fresher", v.Reason);
  }

  [Fact]
  public void SaleInsideTheWindow_StillOutranksCommunity()
  {
    // RoutingRules.LocalSaleSeniorityDays is the wall: day 14 is still ours.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 2000, sale: (3_000, 0, 5), saleAgeDays: 14,
        communityMedian: 100_000, communityCount: 5),
      T.Batch());
    Assert.Equal(RoutingExit.Gc, v.Exit);
  }

  [Fact]
  public void SaleWithUnknownAge_KeepsTheOldBehavior()
  {
    // An unstamped batch (age null) must not invent staleness - fail toward
    // the pre-SF-B2 shape, where any local sale outranks the DC.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 2000, sale: (3_000, 0, 5),
        communityMedian: 100_000, communityCount: 5),
      T.Batch());
    Assert.Equal(RoutingExit.Gc, v.Exit);
  }

  [Fact]
  public void StaleSale_OpensTheEvidenceOnlyBranchToo()
  {
    // Same silence, other gate (same-pattern-same-scope): gear with no melt or
    // seal evidence and only a stale sale gets the community/velocity witnesses,
    // exactly as a never-sold item does.
    var v = RoutingRules.Evaluate(
      T.Gear(sale: (3_000, 0, 5), saleAgeDays: 60,
        communityMedian: 20_000, communityCount: 4),
      T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.Contains("the DC buys it", v.Reason);
  }

  [Fact]
  public void VentureLowStock_NoLongerOutranksTheCommunityVeto()
  {
    // The decree outranked the veto BY DESIGN; the design died 08-22. A DC that
    // pays 100k for what the seals value at ~50k is the witness the veto exists
    // to hear, and thin stock no longer shouts it down.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 2000, communityMedian: 100_000, communityCount: 3), T.Batch(stock: 600));
    Assert.NotEqual(RoutingExit.Gc, v.Exit);
    Assert.DoesNotContain("Venture low", v.Reason);
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
  public void EvidenceOnly_SmallCommunityMedian_StillWitnessesTheList()
  {
    // The worth floor retired with the door gates: the DC's read is the price
    // witness, and a small median is a small List score, not a disqualification.
    var v = RoutingRules.Evaluate(
      T.Gear(vendor: 300, communityMedian: 800, communityCount: 6), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.Equal(800, v.Scores!.Value.List);
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
    // clears the listing floor, so it lists, never confident-vendors for 1 gil.
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

  // ---- Low value + live velocity: the DC's price IS the witness ----

  [Fact]
  public void GreenBeret_LowCommunityValue_LiveVelocity_ListsAtTheDcPrice()
  {
    // 7 community receipts at ~1 gil with a live world velocity: it moves, and
    // the DC says what it moves AT. Since the door gates retired there is no
    // worth floor to fail - the junk price is simply a junk List score, and the
    // vendor takes it whenever the vendor scores higher.
    var v = RoutingRules.Evaluate(
      T.Gear(vendor: 1, velocity: 0.2, communityMedian: 1, communityCount: 7),
      T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.Equal(1, v.Scores!.Value.List);
    // The reason names settled DC sales, not a forecast: "fetches" is what an
    // unsold item would get, and the operand here is 7 sales that already happened.
    Assert.Contains("the DC buys it", v.Reason);
    Assert.DoesNotContain("fetches", v.Reason);
    Assert.DoesNotContain("floor", v.Reason);
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

/// <summary>
/// THE ONE FLOOR LAW at the router's door, and THE DOMAN DESTINY (ruled 2026-08-21).
///
/// <para>The pinch and the Ledger ask the floor question with the same arithmetic, so a
/// row can never be proposed for a List exit the pinch would then refuse to write. And
/// under the Enclave floor the refusal closes the VENDOR exit too: an item worth twice
/// vendor at the Enclave must never be sold at once vendor by a machine.</para>
/// </summary>
public class RoutingFloorTests
{
  private static RoutingConfig Floor(PriceFloorMode mode, int minimum = 0)
    => new() { FloorMode = mode, MinimumListingPrice = minimum };

  [Fact]
  public void NoFloorConfigured_ChangesNothing()
  {
    var v = RoutingRules.Evaluate(T.Gear(sale: (900, 0, 3), vendor: 100), T.Batch());
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.Equal(900, v.Scores!.Value.List);
  }

  [Fact]
  public void AnAskUnderThePlayersMinimum_ForfeitsListAndTheOthersCompete()
  {
    // Honest ask 900, minimum 5,000: no legal listing exists. The vendor is still
    // a real exit and wins it on score - nothing is priced UP to reach the floor.
    var v = RoutingRules.Evaluate(
      T.Gear(sale: (900, 0, 3), vendor: 100),
      T.Batch(cfg: Floor(PriceFloorMode.None, minimum: 5_000)));
    Assert.Equal(RoutingExit.Vendor, v.Exit);
    Assert.Null(v.Scores!.Value.List);
  }

  [Fact]
  public void AnAskUnderTheVendorFloor_ForfeitsList_ButTheVendorStillTakesIt()
  {
    var v = RoutingRules.Evaluate(
      T.Gear(sale: (900, 0, 3), vendor: 1_159),
      T.Batch(cfg: Floor(PriceFloorMode.Vendor)));
    Assert.Equal(RoutingExit.Vendor, v.Exit);
    Assert.Null(v.Scores!.Value.List);
    Assert.Equal(1_159, v.Scores!.Value.Vendor);
  }

  [Fact]
  public void AnAskThatClearsTheFloor_KeepsItsListScore()
  {
    var v = RoutingRules.Evaluate(
      T.Gear(sale: (5_000, 0, 3), vendor: 1_159),
      T.Batch(cfg: Floor(PriceFloorMode.Vendor)));
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.Equal(5_000, v.Scores!.Value.List);
  }

  [Fact]
  public void TheCommunityWitnessAnswersToTheFloorToo()
  {
    // No local sale: the DC's read is the honest ask, and it fails the same law.
    var v = RoutingRules.Evaluate(
      T.Gear(vendor: 1_159, communityMedian: 900, communityCount: 8),
      T.Batch(cfg: Floor(PriceFloorMode.Vendor)));
    Assert.NotEqual(RoutingExit.List, v.Exit);
    Assert.Null(v.Scores!.Value.List);
  }

  [Fact]
  public void UnderTheEnclaveFloor_TheVendorExitClosesToo()
  {
    // 2x1,159 = 2,318. A 900 ask forfeits List; selling at 1,159 would realize half
    // of what the Enclave pays, so the item is kept in the bags instead.
    var v = RoutingRules.Evaluate(
      T.Gear(sale: (900, 0, 3), vendor: 1_159),
      T.Batch(cfg: Floor(PriceFloorMode.DomanEnclave)));
    Assert.Equal(RoutingExit.Hold, v.Exit);
    Assert.Contains("Kept in bags for the Enclave", v.Reason);
    Assert.Contains("2,318", v.Reason);
    Assert.Contains("1,159", v.Reason);
    Assert.Null(v.Scores!.Value.List);
    Assert.Null(v.Scores!.Value.Vendor);
  }

  [Fact]
  public void TheEnclaveHoldNamesTheEnclavesNumber_NotThePlayersMinimum()
  {
    // floor.Floor is a max() - when the minimum is the binding rule it reads as
    // the minimum, and the old sentence printed that as the Enclave's payout
    // ("worth 5,000 there" over an Enclave that pays 20). The payout operand is
    // always 2x vendor; the minimum gets named as the rule that bound.
    var v = RoutingRules.Evaluate(
      T.Gear(sale: (300, 0, 3), vendor: 10),
      T.Batch(cfg: Floor(PriceFloorMode.DomanEnclave, minimum: 5_000)));
    Assert.Equal(RoutingExit.Hold, v.Exit);
    Assert.Contains("worth 20 there", v.Reason);
    Assert.Contains("5,000 minimum", v.Reason);
    Assert.DoesNotContain("worth 5,000 there", v.Reason);
  }

  [Fact]
  public void UnderTheEnclaveFloor_MeltStillCompetesAndCanWin()
  {
    // The hold is the LAST answer, not the first: melt and the turn-in get their
    // ordinary comparison, and a measurable melt beats sitting in the bags.
    var v = RoutingRules.Evaluate(
      T.Gear(sale: (900, 0, 3), vendor: 1_159, melt: 40_000),
      T.Batch(cfg: Floor(PriceFloorMode.DomanEnclave)));
    Assert.Equal(RoutingExit.Desynth, v.Exit);
  }

  [Fact]
  public void UnderTheEnclaveFloor_SealsStillCompeteAndCanWin()
  {
    var v = RoutingRules.Evaluate(
      T.Gear(sale: (900, 0, 3), vendor: 1_159, seals: 400),
      T.Batch(sealRate: 25, cfg: Floor(PriceFloorMode.DomanEnclave)));
    Assert.Equal(RoutingExit.Gc, v.Exit);
  }

  [Fact]
  public void TheEnclaveHoldNeverFiresForAnItemTheFloorDidNotRefuse()
  {
    var v = RoutingRules.Evaluate(
      T.Gear(sale: (5_000, 0, 3), vendor: 1_159),
      T.Batch(cfg: Floor(PriceFloorMode.DomanEnclave)));
    Assert.Equal(RoutingExit.List, v.Exit);
  }

  [Fact]
  public void NoPriceWitnessMeansNoForfeit_AFloorCannotRefuseAnAskNobodyNamed()
  {
    // Gear with no sale and no community read routes exactly as it always did.
    var v = RoutingRules.Evaluate(
      T.Gear(vendor: 1_159, melt: 40_000),
      T.Batch(cfg: Floor(PriceFloorMode.DomanEnclave)));
    Assert.Equal(RoutingExit.Desynth, v.Exit);
  }

  // ========================================================================
  // The two agreeing 14s (3b-8, RULED B1.5b/c)
  // ========================================================================

  [Fact]
  public void TheTwoFourteens_AreTwoNamedConstantsMeasuringTwoThings()
  {
    // WITNESS SENIORITY vs EVIDENCE FRESHNESS. They agreed at 14 and one's doc claimed
    // it "mirrored" the other, which welded two different questions - whose evidence
    // wins, and whether any of it is still current. Both are named now, each with its
    // own reason, so a retune of either cannot move the other by accident.
    Assert.Equal(14, RoutingRules.LocalSaleSeniorityDays);
    Assert.Equal(14, BoardConfidence.EvidenceStaleDays);
  }

  [Fact]
  public void TheSeniorityWall_RunsOffTheConstantAndNotAConfigOmission()
  {
    // It used to run by accident: a RoutingConfig init property BeginBatch never copied,
    // so the record default always won. The behaviour must be identical off the constant
    // - a sale exactly AT the wall still outranks the DC, one past it does not.
    var atTheWall = RoutingRules.Evaluate(
      T.Gear(seals: 2000, sale: (3_000, 0, 5), saleAgeDays: RoutingRules.LocalSaleSeniorityDays,
        communityMedian: 100_000, communityCount: 5),
      T.Batch());
    Assert.Equal(RoutingExit.Gc, atTheWall.Exit);

    var pastIt = RoutingRules.Evaluate(
      T.Gear(seals: 2000, sale: (3_000, 0, 5), saleAgeDays: RoutingRules.LocalSaleSeniorityDays + 1,
        communityMedian: 100_000, communityCount: 5),
      T.Batch());
    Assert.NotEqual(RoutingExit.Gc, pastIt.Exit);
  }
}

public class LookWitnessTests
{
  // THE LOOK'S RUNG (ruled 08-23 - the Nightsteel Sword, routing receipt 2356):
  // recon runs the whole pricing spine on a real board and banks its answer, and
  // the router consumes it as its best available evidence when the settled rungs
  // are silent. Reaching the rung at all means the history is thin, so a List
  // that WINS on the Look goes to Bag decisions; a Look that loses fairly lets
  // the better exit ride.

  [Fact]
  public void TheNightsteelSword_AFreshLookSendsItToDecisions_NotSilentlyToGc()
  {
    // The receipt's own numbers: gc ~2,150, melt 1,537, vendor 414, tape 2 sales
    // (under the bar), and a banked Look at 20,454 off a 5-seller queue. The old
    // ladder never scored List and called GC "Unanimous" - a lane that never voted.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 86, melt: 1_537, vendor: 414, tapeMedian: 20_000, tapeCount: 2,
        lookAsk: 20_454, lookAgeHours: 0.2),
      T.Batch(sealRate: 25));

    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.True(v.IsReview);
    Assert.False(v.BandTie); // a referral, never "a dead heat"
    Assert.Contains("your Look", v.Reason);
    Assert.Contains("20,454", v.Reason);
    Assert.Equal(RoutingExit.Gc, v.RunnerUp);
    Assert.Equal(20_454, v.Scores!.Value.List);
  }

  [Fact]
  public void ALookThatLosesFairly_LetsTheBetterExitRide_NoReview()
  {
    // The lane was consulted and lost - that is the whole fix; no human needed.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 86, melt: 1_537, vendor: 414, lookAsk: 400, lookAgeHours: 2),
      T.Batch(sealRate: 25));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.False(v.IsReview);
    Assert.Equal(400, v.Scores!.Value.List); // consulted, on the receipt
  }

  [Fact]
  public void AFreshLookClosesTheCommunityVetosDoor()
  {
    // The veto's premise is "the market was never consulted" - a Look IS the
    // market consulted (final pass, 08-23). Look 400 loses to GC fairly; the
    // DC's 50,000 story must not re-open List over our own board's read, let
    // alone ride it confident where the Look's own win would be review.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 86, melt: 1_537, vendor: 414, lookAsk: 400, lookAgeHours: 2,
        communityMedian: 50_000, communityCount: 8),
      T.Batch(sealRate: 25));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.False(v.IsReview);
    Assert.DoesNotContain("the DC pays", v.Reason);
    Assert.Equal(400, v.Scores!.Value.List); // the Look's loss stays on the receipt
  }

  [Fact]
  public void AFreshLookBarsTheEvidenceOnlyShrugToo()
  {
    // "No local evidence" is false once recon has priced the item off our live
    // board. A Look that lost to vendor falls to rule 8 with its loss on the
    // record - never to a confident DC List or a "no price on record" review.
    var v = RoutingRules.Evaluate(
      T.Gear(vendor: 500, lookAsk: 40, lookAgeHours: 1,
        communityMedian: 50_000, communityCount: 8),
      T.Batch());
    Assert.Equal(RoutingExit.Vendor, v.Exit);
    Assert.False(v.IsReview);
    Assert.Contains("no better exit in evidence", v.Reason);
    Assert.Equal(40, v.Scores!.Value.List);
  }

  [Fact]
  public void NoFreshLook_KeepsTheOldLadder()
  {
    // The pre-rung Nightsteel behavior, now the fallback: no Look banked, thin
    // tape, GC wins the fight List never entered.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 86, melt: 1_537, vendor: 414, tapeMedian: 20_000, tapeCount: 2),
      T.Batch(sealRate: 25));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.Null(v.Scores!.Value.List);
  }

  [Fact]
  public void TheSettledRungsOutrankTheLook()
  {
    // A speaking tape seats the List score; the Look never fires and the verdict
    // rides confident (no thin-history review) - the rung is strictly the
    // silent-rungs fallback.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 10, tapeMedian: 5_000, tapeCount: 5, lookAsk: 20_454),
      T.Batch(sealRate: 25));
    Assert.Equal(RoutingExit.List, v.Exit);
    Assert.False(v.IsReview);
    Assert.Contains("settled sales", v.Reason);
    Assert.Equal(5_000, v.Scores!.Value.List);
  }

  [Fact]
  public void ALookUnderTheFloor_ForfeitsListLikeEveryOtherWitness()
  {
    // The one floor law reads the Look through the same honest-ask ladder: an
    // ask that cannot clear the floor means no legal listing exists, and the
    // other exits compete without a review.
    var v = RoutingRules.Evaluate(
      T.Gear(seals: 86, vendor: 30, lookAsk: 40, lookAgeHours: 1),
      T.Batch(sealRate: 25, cfg: new RoutingConfig { MinimumListingPrice = 75 }));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.False(v.IsReview);
  }

  [Fact]
  public void BandTie_IsTheBandDoorsClaimAlone()
  {
    // The review band's genuine tie carries BandTie (the case page's "a dead
    // heat" keys off it); the Look-review above must not - see the Nightsteel
    // pin's BandTie assert. Own sale 2,600 vs melt 2,500 sits inside the 15%.
    var v = RoutingRules.Evaluate(
      T.Gear(sale: (2_600, 0, 5), melt: 2_500),
      T.Batch());
    Assert.True(v.IsReview);
    Assert.True(v.BandTie);
  }
}
