using System;
using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE BAG CALLS TRIBUNAL (Task 2, spec ruled 2026-08-15) - the case a contested row
/// draws, and every sentence it speaks.
///
/// <para>The specimens are verbatim on purpose. These strings ARE the red-pen surface:
/// Drift walks a real triage and rewrites them, and the whole point of one pure function
/// per source type is that a rewrite is a one-line swap with a pinned test underneath
/// it (spec, "Case-sentence discipline").</para>
///
/// <para>The doctrine the assertions defend: <b>no sentence predicts a price.</b> Every
/// line names where its number came from, and a line with nothing behind it does not
/// fire at all.</para>
/// </summary>
public class TriageCaseTests
{
  private static ExitScore Score(BoardPile exit, long worth,
    WorthProvenance p = WorthProvenance.Measured) => new(exit, worth, p);

  // ==========================================================================
  // The verdict lead - WHAT THE CALL IS (F4, ruled 08-22)
  // ==========================================================================

  [Fact]
  public void Verdict_LeadsWithTheCallAndTheNumbersThatDecidedIt()
  {
    // The Dinosaur Leather Shoes ruling: every case opens with the composed call.
    var e = new CaseEvidence { Key = "k", Title = "Shoes", VelocityPerDay = 0.14 };
    var argued = new List<ExitScore> { Score(BoardPile.List, 17_999), Score(BoardPile.Melt, 1_297) };
    Assert.Equal(
      "The call: list at 17,999 - next best melt at 1,297. It clears ~0.14/day on this world.",
      TriageCases.VerdictLine(e, argued));
  }

  [Fact]
  public void Verdict_ANonListLeadCarriesNoPace()
  {
    var e = new CaseEvidence { Key = "k", Title = "Scepter", VelocityPerDay = 2.0 };
    var argued = new List<ExitScore> { Score(BoardPile.Melt, 100_000), Score(BoardPile.List, 12_000) };
    Assert.Equal("The call: melt at 100,000 - next best list at 12,000.",
      TriageCases.VerdictLine(e, argued));
  }

  [Fact]
  public void Verdict_SaysNothingOverNoNumbers_AndNamesALoneContender()
  {
    var e = new CaseEvidence { Key = "k", Title = "Ring" };
    Assert.Equal("", TriageCases.VerdictLine(e, new List<ExitScore>()));
    Assert.Equal("The call: vendor at 75 - nothing else scored.",
      TriageCases.VerdictLine(e, new List<ExitScore> { Score(BoardPile.PullAndVendor, 75) }));
  }

  // ==========================================================================
  // Referral sentences - WHY IT'S HERE
  // ==========================================================================

  // (Referral_TheReviewBandArmSaysTwoScoresAndTheWordForThem died with
  // ReferralTooClose, final pass 08-23: the dead-heat fold made that seat
  // unreachable in production, so the voice test was pinning a sentence no
  // board could ever see. The fold's own qualifier is pinned in the
  // VerdictLine tests.)

  [Fact]
  public void Referral_TheTierDemotionArmNamesTheLeadAndTheEvidenceUnderIt()
  {
    // V3, ruled B5. THE SILVERGRACE RECEIPT, from the other side: the row that read
    // "13,556 gil apart. Too close for the router to call." was never a dead heat -
    // it was a lead the tier would not seat. This arm says which and why.
    Assert.Equal("melt leads at 41,200 on shaky evidence - 2 sales, read 12d ago.",
      CaseVoice.ReferralThinEvidence(BoardPile.Melt, 41_200, 2, 12));
    Assert.Equal("list leads at 14,993 on shaky evidence - 1 sale, read 3d ago.",
      CaseVoice.ReferralThinEvidence(BoardPile.List, 14_993, 1, 3));
  }

  [Fact]
  public void Referral_TheThinEvidenceClausesDoNotHedgeWhenTheyHaveNoOperand()
  {
    // The house doctrine: a line with nothing behind it does not fire. "0 sales, read
    // 0d ago" would be two invented facts dressed as measurements.
    Assert.Equal("melt leads at 41,200 on shaky evidence - read 12d ago.",
      CaseVoice.ReferralThinEvidence(BoardPile.Melt, 41_200, 0, 12));
    Assert.Equal("melt leads at 41,200 on shaky evidence - 2 sales.",
      CaseVoice.ReferralThinEvidence(BoardPile.Melt, 41_200, 2, 0));
    Assert.Equal("melt leads at 41,200 on shaky evidence.",
      CaseVoice.ReferralThinEvidence(BoardPile.Melt, 41_200, 0, 0));
  }

  [Fact]
  public void Referral_NoDataIsHonestAboutBeingThin()
    => Assert.Equal("no evidence either way - never measured.", CaseVoice.ReferralNoData());

  [Fact]
  public void Assemble_TheDispatcherPicksTheSentence()
  {
    // THE SEAM (ruled B5). One row, two dispatchers, two sentences. The ONLY thing
    // that differs between these two cases is who declined - and before B5 both spoke
    // the dead-heat wording, which is how a 40k gap got narrated as "too close".
    // SINCE 08-23 (docket #1) the dead-heat arm speaks through the VERDICT LEAD:
    // the referral echo read the same two numbers one line under the call.
    var e = new CaseEvidence("k", "Alexander Miniature", ReferralReason.TooClose,
      new[] { Score(BoardPile.List, 41_900), Score(BoardPile.Melt, 41_200) },
      RecentSalesCount: 2, LaneAgeDays: 12);

    var deadHeat = TriageCases.Assemble(e with { RouterDeclined = true });
    Assert.Equal("The call: list at 41,900 - next best melt at 41,200, a dead heat.",
      deadHeat.Verdict);
    Assert.Equal("", deadHeat.Referral);

    var thin = TriageCases.Assemble(e);
    Assert.Equal("list leads at 41,900 on shaky evidence - 2 sales, read 12d ago.",
      thin.Referral);
    Assert.DoesNotContain("dead heat", thin.Verdict);
  }

  [Fact]
  public void Assemble_TheFoldAndTheDropNeverDisagree()
  {
    // Docket #1's invariant: the qualifier lives in EXACTLY one seat. A lone
    // contender is not a race - no fold, and the one-contender sentence stands.
    var lone = new CaseEvidence("k", "thing", ReferralReason.TooClose,
      new[] { Score(BoardPile.List, 43_000) }, RouterDeclined: true);
    var c = TriageCases.Assemble(lone);
    Assert.DoesNotContain("dead heat", c.Verdict);
    Assert.Equal("only one exit has any evidence - the rest were never measured.",
      c.Referral);
  }

  [Fact]
  public void Assemble_TheDisputeLeadsOnlyAtTheContradictionDoor()
  {
    // Ruled 08-23 (the cotton-cloth case): a contradiction-door case opens with
    // the dispute - a verdict the next line impeaches reads as the case arguing
    // with itself. Every other door keeps F4's verdict-first order.
    var contradicted = new CaseEvidence("k", "Cotton Cloth", ReferralReason.Contradicted,
      new[] { Score(BoardPile.PullAndVendor, 4) },
      ContradictedExit: BoardPile.PullAndVendor, ContradictionAxis: Accord.Agree,
      Lean: VerdictLean.OffMarket);
    Assert.True(TriageCases.Assemble(contradicted).DisputeLeads);

    var scored = new CaseEvidence("k", "thing", ReferralReason.TooClose,
      new[] { Score(BoardPile.List, 43_000), Score(BoardPile.Melt, 41_200) });
    Assert.False(TriageCases.Assemble(scored).DisputeLeads);
    Assert.False(TriageCases.Assemble(
      new CaseEvidence("k", "thing", ReferralReason.NoData, Array.Empty<ExitScore>())).DisputeLeads);
  }

  [Fact]
  public void Assemble_TheDeadHeatClaimDefaultsOff()
  {
    // Fail-closed, the way the run log writes its undercut test: "a dead heat" is the
    // claim that needs the proof, so an unsupplied dispatcher never asserts one.
    var e = new CaseEvidence("k", "thing", ReferralReason.TooClose,
      new[] { Score(BoardPile.List, 43_000), Score(BoardPile.Melt, 41_200) });
    Assert.DoesNotContain("dead heat", TriageCases.Assemble(e).Referral);
  }

  [Fact]
  public void Assemble_AScoredCaseWithOneContenderSaysThatInstead()
  {
    // V4. It used to answer with the no-data sentence, which denied the one real
    // number sitting on the page under it.
    var e = new CaseEvidence("k", "thing", ReferralReason.TooClose,
      new[] { Score(BoardPile.List, 43_000) });
    Assert.Equal("only one exit has any evidence - the rest were never measured.",
      TriageCases.Assemble(e).Referral);
    Assert.Equal("only one exit has any evidence - the rest were never measured.",
      TriageCases.Assemble(e with { RouterDeclined = true }).Referral);
  }

  [Fact]
  public void Assemble_AContradictedRowWithNoSalesDisagreementNamesTheOtherAxis()
  {
    // V4. A Contradicted tier has exactly two doors (BoardConfidence.BaseTier) and
    // CaseVoice.Contradiction speaks only the sales one - so a row that arrives
    // Contradicted with the sales axis quiet fell back from the local-vs-community
    // disagreement, and the fallback names it instead of claiming nothing was measured.
    var e = new CaseEvidence("k", "thing", ReferralReason.Contradicted,
      new[] { Score(BoardPile.Melt, 1_246) },
      ContradictedExit: BoardPile.Melt, ContradictionAxis: Accord.Agree,
      Lean: VerdictLean.OffMarket);
    Assert.Equal("your own sales and the community read disagree.",
      TriageCases.Assemble(e).Referral);
  }

  // ==========================================================================
  // The last-sold seat (V31, ruled B1 coda: a DISPLAY, not a gate)
  // ==========================================================================

  [Fact]
  public void LastSold_OneSeat_FiredByADateAndNothingElse()
  {
    Assert.Equal("Last sold 3d ago.", CaseVoice.LastSold(3));
    Assert.Equal("Last sold today.", CaseVoice.LastSold(0));

    // No date anywhere: the line does not hedge, it does not fire.
    var none = new CaseEvidence("k", "thing", ReferralReason.NoData, Array.Empty<ExitScore>());
    Assert.Equal("", TriageCases.Assemble(none).LastSold);

    // Your own settled receipt answers when nothing else has a date.
    var own = none with { OwnSalePrice = 43_000, OwnSaleAgeDays = 6 };
    Assert.Equal("Last sold 6d ago.", TriageCases.Assemble(own).LastSold);

    // The tape's date wins when it exists - same seat, same grammar, whichever
    // witness carries it.
    Assert.Equal("Last sold 2d ago.",
      TriageCases.Assemble(own with { LastSoldAgeDays = 2 }).LastSold);
    Assert.Equal("Last sold 2d ago.",
      TriageCases.Assemble(none with { LastSoldAgeDays = 2 }).LastSold);
  }

  // ==========================================================================
  // The top-3 rule, and the buttons that never narrow
  // ==========================================================================

  [Fact]
  public void Assemble_OnlyTheTopThreeContendersArgue()
  {
    var e = new CaseEvidence("k", "thing", ReferralReason.TooClose, new[]
    {
      Score(BoardPile.PullAndVendor, 1_200),
      Score(BoardPile.List, 43_000),
      Score(BoardPile.Churn, 30_000),
      Score(BoardPile.Melt, 41_200),
    });
    var c = TriageCases.Assemble(e);

    Assert.Equal(new[] { BoardPile.List, BoardPile.Melt, BoardPile.Churn },
      c.Options.Select(o => o.Exit).ToArray());
  }

  [Fact]
  public void Assemble_EveryExitIsStillAButton()
  {
    // The override grammar never narrows (ruled 08-15): a click on an unargued exit is
    // still a valid override, so the case carries every door whatever it argued.
    var e = new CaseEvidence("k", "thing", ReferralReason.NoData, Array.Empty<ExitScore>());
    var c = TriageCases.Assemble(e);

    Assert.Empty(c.Options);
    Assert.Equal(new[] { BoardPile.List, BoardPile.Melt, BoardPile.Churn, BoardPile.PullAndVendor },
      c.Exits.ToArray());
  }

  [Fact]
  public void Assemble_ContradictedLeadsWithTheVerdictWhateverItScored()
  {
    // "the verdict, the evidence against it, and the next-best exit." Burying the
    // verdict under the exits that outscored it would answer a question nobody asked.
    var e = new CaseEvidence("k", "Alexander Miniature", ReferralReason.Contradicted, new[]
    {
      Score(BoardPile.List, 43_000),
      Score(BoardPile.Churn, 30_000),
      Score(BoardPile.PullAndVendor, 1_200),
      Score(BoardPile.Melt, 900),
    },
      ContradictedExit: BoardPile.Melt,
      ContradictionAxis: Accord.Disagree,
      Lean: VerdictLean.OffMarket,
      RecentSalesCount: 5);

    var c = TriageCases.Assemble(e);
    Assert.Equal(new[] { BoardPile.Melt, BoardPile.List, BoardPile.Churn },
      c.Options.Select(o => o.Exit).ToArray());
    // This fixture never sets IsStanding, so it is a BAG row and takes the DC arm
    // (V10, ruled 08-21). The standing arm's stronger wording is pinned beside its
    // bagged twin in Assemble_ABaggedContradictedRowOpensWithTheDcSentence; what
    // THIS test is about is the option order above - the verdict leads.
    Assert.StartsWith("~DC sales disagree with melting it:", c.Referral);
  }

  [Fact]
  public void Assemble_ContradictedKeepsTheVerdictInPlaceWhenItAlreadyLeads()
  {
    var e = new CaseEvidence("k", "thing", ReferralReason.Contradicted, new[]
    {
      Score(BoardPile.List, 43_000),
      Score(BoardPile.Melt, 41_200),
    },
      ContradictedExit: BoardPile.List,
      ContradictionAxis: Accord.Disagree,
      Lean: VerdictLean.OnMarket,
      VelocityPerDay: 0);

    Assert.Equal(new[] { BoardPile.List, BoardPile.Melt },
      TriageCases.Assemble(e).Options.Select(o => o.Exit).ToArray());
  }

  [Fact]
  public void Assemble_NoDataCaseArguesNothingAndSaysSo()
  {
    var e = new CaseEvidence("k", "never seen one", ReferralReason.NoData,
      new[] { Score(BoardPile.PullAndVendor, 1_200, WorthProvenance.VendorFloor) },
      VendorPrice: 1_200);
    var c = TriageCases.Assemble(e);

    Assert.Equal("no evidence either way - never measured.", c.Referral);
    // The one number that exists still argues - a vendor floor is a fact - but it wears
    // its provenance, so nobody reads 1,200 as a market read.
    var only = Assert.Single(c.Options);
    Assert.Equal("(vendor floor)", only.Provenance);
    Assert.Equal(new[] { "The NPC counter pays 1,200 for one. That is a floor, not a market." },
      only.For.ToArray());
  }

  [Fact]
  public void Assemble_CarriesTheHumansVerdictWhenHeHasRuled()
  {
    var e = new CaseEvidence("k", "thing", ReferralReason.NoData, Array.Empty<ExitScore>());
    Assert.Null(TriageCases.Assemble(e).DecidedExit);
    Assert.Equal(BoardPile.Melt, TriageCases.Assemble(e, BoardPile.Melt).DecidedExit);
  }

  // ==========================================================================
  // Provenance - what a number IS
  // ==========================================================================

  [Fact]
  public void Provenance_OneWordPerKindOfNumber()
  {
    Assert.Equal("(measured)", CaseVoice.Provenance(WorthProvenance.Measured));
    Assert.Equal("(asked, not proven)", CaseVoice.Provenance(WorthProvenance.Asked));
    Assert.Equal("(vendor floor)", CaseVoice.Provenance(WorthProvenance.VendorFloor));
    Assert.Equal("(band prior, never measured here)", CaseVoice.Provenance(WorthProvenance.BandPrior));
    Assert.Equal("(placeholder, never measured)", CaseVoice.Provenance(WorthProvenance.Placeholder));
    // F3's ruling, previously unpinned (final pass, 08-23): the player's own
    // constant must never fall through to the placeholder apology.
    Assert.Equal("(your skill-up price - a ruled constant)", CaseVoice.Provenance(WorthProvenance.Peg));
  }

  // ==========================================================================
  // Sentence formatters - one per source type, exact strings
  // ==========================================================================

  [Fact]
  public void Voice_OwnLastSale()
  {
    Assert.Equal("You sold one at 43,000, 6d ago, and it took 3d to clear.",
      CaseVoice.OwnLastSale(43_000, 6, 3));
    Assert.Equal("You sold one at 43,000, today, and it cleared inside a day.",
      CaseVoice.OwnLastSale(43_000, 0, 0));
    Assert.Equal("You sold one at 43,000, 6d ago.", CaseVoice.OwnLastSale(43_000, 6, null));
  }

  [Fact]
  public void Voice_MeltRollup()
    => Assert.Equal("12 of your own melts returned 41,200 an attempt on average, between 33,000 and 52,000.",
      CaseVoice.MeltRollup(12, 41_200, 33_000, 52_000));

  [Fact]
  public void Voice_MeltBandPrior()
    => Assert.Equal("You have never melted one. The ilvl 560 band averages 41,200 an attempt.",
      CaseVoice.MeltBandPrior(560, 41_200));

  [Fact]
  public void Voice_SealRate_ShowsCountGilAndRate_ProvenanceStaysInTheHeaderTag()
  {
    // Ruled 08-15 night: the trailing provenance clause (both variants) re-hedged a
    // number the option's header tag already hedges - cut. The line is the arithmetic:
    // count, the gil it comes to, and the ~rate between them.
    Assert.Equal("1,937 seals - about 2,529 gil at ~1.31 a seal.",
      CaseVoice.SealRate(1_937, 2_529, 1.31));
    Assert.Equal("22 seals - about 94 gil at ~4.25 a seal.",
      CaseVoice.SealRate(22, 94, 4.25));
  }

  [Fact]
  public void Voice_VendorFloorSaysItIsAFloor()
    => Assert.Equal("The NPC counter pays 1,200 for one. That is a floor, not a market.",
      CaseVoice.VendorFloor(1_200));

  [Fact]
  public void Voice_LaneRead()
  {
    Assert.Equal("The lane read 43,000 across 5 listings, 2d ago.", CaseVoice.LaneRead(43_000, 5, 2));
    Assert.Equal("The lane read 43,000 across 1 listing, today.", CaseVoice.LaneRead(43_000, 1, 0));
  }

  [Fact]
  public void Voice_ReconLook()
  {
    Assert.Equal("Your Look priced this lane at 9,997, 4m ago.",
      CaseVoice.ReconLook(9_997, 4 * 60));
    // "0m", not "<1m": the line switched from Durations.Span to Durations.Elapsed
    // (ruled 08-21) because this is an AGE, and Span is the "will take" grammar with
    // no day rung. The half-minute read is what the grammar change looks like at the
    // bottom rung - the old pin recorded the mismatched grammar.
    Assert.Equal("Your Look priced this lane at 9,997, 0m ago.",
      CaseVoice.ReconLook(9_997, 30));
  }

  [Fact]
  public void Voice_Caveats()
  {
    Assert.Equal("Thin lane: 2 listings against your 5-sample bar.", CaseVoice.ThinLane(2, 5));
    Assert.Equal("Thin lane: 1 listing against your 5-sample bar.", CaseVoice.ThinLane(1, 5));
    Assert.Equal("Only 2 melts behind that average - one good roll moves it.", CaseVoice.ThinMelts(2));
    Assert.Equal("Only 1 melt behind that average - one good roll moves it.", CaseVoice.ThinMelts(1));
    // V11: the caveat says the READ is stale and stops. Where the line sits is the
    // model's business (BoardConfidence.EvidenceStaleDays), not the reader's.
    Assert.Equal("That read is 9d old - stale.", CaseVoice.StaleEvidence(9));
    Assert.Equal("The lane is scattered - prices spread about 80% around the middle.",
      CaseVoice.WideSpread(0.8));
    Assert.Equal("It clears about 0.4 a day on this world.", CaseVoice.Velocity(0.4));
    Assert.Equal("Nothing has sold in the window - there is no measured rate at all.",
      CaseVoice.Velocity(0));
    Assert.Equal("3 sellers sat under you at the last board read, 2d ago.",
      CaseVoice.Undercutters(3, 2));
    Assert.Equal("1 seller sat under you at the last board read, today.",
      CaseVoice.Undercutters(1, 0));
  }

  [Fact]
  public void Voice_ExitWordsAreTheWordsASentenceUses()
  {
    Assert.Equal("list", CaseVoice.ExitWord(BoardPile.List));
    Assert.Equal("melt", CaseVoice.ExitWord(BoardPile.Melt));
    Assert.Equal("GC", CaseVoice.ExitWord(BoardPile.Churn));
    Assert.Equal("vendor", CaseVoice.ExitWord(BoardPile.PullAndVendor));
  }

  // ==========================================================================
  // Option assembly - the right receipts on the right exit
  // ==========================================================================

  [Fact]
  public void Option_TheListCaseCitesTheLaneAndYourOwnSale()
  {
    var e = new CaseEvidence("k", "thing", ReferralReason.TooClose,
      new[] { Score(BoardPile.List, 43_000), Score(BoardPile.Melt, 41_200) },
      OwnSalePrice: 43_000, OwnSaleAgeDays: 6, OwnSaleDaysToSell: 3,
      LaneMedian: 44_000, LaneSampleCount: 5, MinSamples: 5, LaneAgeDays: 2, StaleDays: 3);

    var list = TriageCases.Assemble(e).Options.First(o => o.Exit == BoardPile.List);
    Assert.Equal(new[]
    {
      "The lane read 44,000 across 5 listings, 2d ago.",
      "You sold one at 43,000, 6d ago, and it took 3d to clear.",
    }, list.For.ToArray());
    Assert.Empty(list.Caveats);
  }

  [Fact]
  public void Option_TheFreshLookLeadsTheListArgument()
  {
    // Ruled 08-16: "triage must use the latest information we have." The round's
    // own banked Look is the freshest number in the building by hinge time - it
    // leads the argument, ahead of the older lane read and the own-sale receipt.
    var e = new CaseEvidence("k", "Reading Glasses", ReferralReason.Contradicted,
      new[] { Score(BoardPile.Melt, 1_246), Score(BoardPile.List, 9_997, WorthProvenance.Asked) },
      ContradictedExit: BoardPile.Melt,
      ContradictionAxis: Accord.Disagree,
      Lean: VerdictLean.OffMarket,
      RecentSalesCount: 5,
      ReconAsk: 9_997, ReconAskAgeSeconds: 4 * 60,
      ReconAskAnchor: nameof(LaneOutcome.Undercut),
      ReconAskEvidence: "Sells 9,800-13,500, 14 sales, they agree.");

    var list = TriageCases.Assemble(e).Options.First(o => o.Exit == BoardPile.List);
    Assert.Equal(
      "Your Look priced this lane at 9,997, 4m ago - cut in front of the cheapest cluster."
      + " Sells 9,800-13,500, 14 sales, they agree.",
      list.For.First());
  }

  [Fact]
  public void Option_TheListCaveatsFireOnTheirOwnThresholds()
  {
    var e = new CaseEvidence("k", "thing", ReferralReason.TooClose,
      new[] { Score(BoardPile.List, 43_000) },
      LaneMedian: 44_000, LaneSampleCount: 2, MinSamples: 5, LaneSpread: 0.8,
      LaneAgeDays: 9, StaleDays: 3, VelocityPerDay: 0.4,
      Undercutters: 3, BoardReadAgeDays: 2);

    var assembled = TriageCases.Assemble(e);
    var list = assembled.Options.First(o => o.Exit == BoardPile.List);
    // The pace is NOT among the caveats here: List is the lead, so the verdict
    // line owns the fact ("It clears ~0.4/day on this world") and the caveat
    // stands down - two pace sentences on one page is the docket-#1 echo
    // (final pass, 08-23).
    Assert.Equal(new[]
    {
      "Thin lane: 2 listings against your 5-sample bar.",
      "That read is 9d old - stale.",
      "The lane is scattered - prices spread about 80% around the middle.",
      "3 sellers sat under you at the last board read, 2d ago.",
    }, list.Caveats.ToArray());
    Assert.Contains("clears ~0.4/day", assembled.Verdict);
  }

  [Fact]
  public void Option_ThePaceCaveatKeepsItsSeatWhenListIsNotTheLead()
  {
    // A List runner-up's pace never folds - the verdict lead speaks a different
    // exit and no pace at all, so the caveat is the fact's only seat.
    var e = new CaseEvidence("k", "thing", ReferralReason.TooClose,
      new[] { Score(BoardPile.Melt, 45_000), Score(BoardPile.List, 43_000) },
      MeltAttempts: 12, MeltAverage: 45_000, MeltSpreadLow: 40_000, MeltSpreadHigh: 50_000,
      VelocityPerDay: 0.4);
    var assembled = TriageCases.Assemble(e);
    var list = assembled.Options.First(o => o.Exit == BoardPile.List);
    Assert.Contains("It clears about 0.4 a day on this world.", list.Caveats);
    Assert.DoesNotContain("/day", assembled.Verdict);
  }

  [Fact]
  public void Option_TheMeltCaseCitesYourOwnMeltsOrSaysItNeverRanOne()
  {
    var measured = new CaseEvidence("k", "thing", ReferralReason.TooClose,
      new[] { Score(BoardPile.Melt, 41_200) },
      MeltAttempts: 12, MeltAverage: 41_200, MeltSpreadLow: 33_000, MeltSpreadHigh: 52_000);
    Assert.Equal(new[]
      { "12 of your own melts returned 41,200 an attempt on average, between 33,000 and 52,000." },
      TriageCases.Assemble(measured).Options[0].For.ToArray());

    // The band prior travels with its companion (ruled 08-16): the gap said flat,
    // and melt named as the exit that closes it. A measured melt (above) never
    // carries the pitch - a real number needs no companion.
    var prior = new CaseEvidence("k", "thing", ReferralReason.TooClose,
      new[] { Score(BoardPile.Melt, 41_200, WorthProvenance.BandPrior) },
      MeltBandIlvl: 560, MeltBandAverage: 41_200);
    Assert.Equal(new[]
      {
        "You have never melted one. The ilvl 560 band averages 41,200 an attempt.",
        "We need data to calculate desynth value for this item - melting one is how we get it.",
      },
      TriageCases.Assemble(prior).Options[0].For.ToArray());
  }

  [Fact]
  public void Option_AThinMeltAverageCarriesItsOwnCaveat()
  {
    var e = new CaseEvidence("k", "thing", ReferralReason.TooClose,
      new[] { Score(BoardPile.Melt, 41_200) },
      MeltAttempts: 2, MeltAverage: 41_200, MeltSpreadLow: 33_000, MeltSpreadHigh: 52_000);
    Assert.Equal(new[] { "Only 2 melts behind that average - one good roll moves it." },
      TriageCases.Assemble(e).Options[0].Caveats.ToArray());
  }

  [Fact]
  public void Option_TheGcCaseShowsTheMultiplication_ProductMatchesItsOwnHeader()
  {
    // Operands that actually multiply: 22 x 4.25 = 93.5 ~ 94. The old fixture
    // pinned "22 seals - about 30,000 gil at ~4.25 a seal" - arithmetic off by
    // 319x, so the test guarded only that the formatter formats (final pass,
    // 08-23). The gil in the sentence is the option's own scored worth - the
    // arithmetic on screen reproduces the score on screen (red-penned 08-15:
    // 1,937 x a rounded 1.31 landed 8 gil away from its own header).
    var e = new CaseEvidence("k", "thing", ReferralReason.TooClose,
      new[] { Score(BoardPile.Churn, 94, WorthProvenance.Placeholder) },
      SealCount: 22, SealGilRate: 4.25);
    Assert.Equal(new[] { "22 seals - about 94 gil at ~4.25 a seal." },
      TriageCases.Assemble(e).Options[0].For.ToArray());
  }

  [Fact]
  public void Option_AnExitWithNothingBehindItArguesNothingRatherThanHedging()
  {
    var e = new CaseEvidence("k", "thing", ReferralReason.TooClose,
      new[] { Score(BoardPile.List, 43_000), Score(BoardPile.Melt, 41_200) });
    var c = TriageCases.Assemble(e);
    Assert.All(c.Options, o => Assert.Empty(o.For));
    Assert.All(c.Options, o => Assert.Empty(o.Caveats));
  }


  // ==========================================================================
  // The contradiction voice - and the operand it learned (pen 7, 3b-2)
  // ==========================================================================

  [Fact]
  public void Contradiction_OnAStandingRowReadsBothDirections()
  {
    // Off-market: a LIVE market contradicts taking it off the board (Alexander).
    Assert.Equal(
      "the market disagrees with taking it off the board: 5 settled sales say somebody is buying these.",
      CaseVoice.Contradiction(Accord.Disagree, VerdictLean.OffMarket, 5, null,
        isStanding: true, BoardPile.Melt));
    Assert.Equal(
      "the market disagrees with taking it off the board: 1 settled sale says somebody is buying these.",
      CaseVoice.Contradiction(Accord.Disagree, VerdictLean.OffMarket, 1, null,
        isStanding: true, BoardPile.Melt));

    // On-market: a DEAD market contradicts keeping it listed.
    Assert.Equal(
      "the market disagrees with keeping it listed: nothing has sold and it moves 0 a day - a listing here just sits.",
      CaseVoice.Contradiction(Accord.Disagree, VerdictLean.OnMarket, 0, 0,
        isStanding: true, BoardPile.List));
    Assert.Equal(
      "the market disagrees with keeping it listed: nothing has sold - a listing here just sits.",
      CaseVoice.Contradiction(Accord.Disagree, VerdictLean.OnMarket, 0, null,
        isStanding: true, BoardPile.List));
  }

  [Fact]
  public void Contradiction_OnABagRowNamesTheActAndItsDcWitness()
  {
    // V10 / pen 7. A bag row is not on the board, so "taking it off the board" is a
    // claim about a state it is not in - and its settled sales are the DC-wide
    // community read, not the local tape. The "~" is the house provenance prefix for
    // that rung; "there" is the DC.
    Assert.Equal(
      "~DC sales disagree with melting it: 4 sold there lately.",
      CaseVoice.Contradiction(Accord.Disagree, VerdictLean.OffMarket, 4, null,
        isStanding: false, BoardPile.Melt));
    Assert.Equal(
      "~DC sales disagree with churning it: 4 sold there lately.",
      CaseVoice.Contradiction(Accord.Disagree, VerdictLean.OffMarket, 4, null,
        isStanding: false, BoardPile.Churn));
    // The on-market arm forks the same way: a bag row would be LISTED, not kept listed.
    Assert.Equal(
      "~DC sales disagree with listing it: nothing has sold there and it moves 0.5 a day.",
      CaseVoice.Contradiction(Accord.Disagree, VerdictLean.OnMarket, 0, 0.5,
        isStanding: false, BoardPile.List));
  }

  [Fact]
  public void Contradiction_IsSilentUnlessTheAxisActuallyDisagrees()
  {
    Assert.Equal("", CaseVoice.Contradiction(Accord.Agree, VerdictLean.OffMarket, 5, null));
    Assert.Equal("", CaseVoice.Contradiction(Accord.Unknown, VerdictLean.OffMarket, 5, null));
    // A neutral verdict makes no market claim, so sales evidence contradicts nothing.
    Assert.Equal("", CaseVoice.Contradiction(Accord.Disagree, VerdictLean.Neutral, 5, null));
  }

  [Fact]
  public void Referral_TheStandingArmKeepsItsPrefix_TheBagArmSpeaksForItself()
  {
    // The prefix claims the LOCAL tape ("settled sales contradict the melt verdict"),
    // which is a claim only a standing row's witness can carry. The bag arm names its
    // own witness, so re-labelling it here would put a local badge on a DC number.
    Assert.Equal(
      "settled sales contradict the melt verdict: the market disagrees with taking it "
      + "off the board: 5 settled sales say somebody is buying these.",
      CaseVoice.ReferralContradicted(BoardPile.Melt,
        CaseVoice.Contradiction(Accord.Disagree, VerdictLean.OffMarket, 5, null,
          isStanding: true, BoardPile.Melt),
        isStanding: true));

    Assert.Equal(
      "~DC sales disagree with melting it: 4 sold there lately.",
      CaseVoice.ReferralContradicted(BoardPile.Melt,
        CaseVoice.Contradiction(Accord.Disagree, VerdictLean.OffMarket, 4, null,
          isStanding: false, BoardPile.Melt),
        isStanding: false));
  }

  [Fact]
  public void Assemble_ABaggedContradictedRowOpensWithTheDcSentence()
  {
    // The whole seam end to end: BuildFacts hands IsStanding in, Assemble forks on it.
    // A bag row defaults to IsStanding: false, which is the honest default - the flag
    // has to be earned by having a listing.
    var bagged = new CaseEvidence("k", "Reading Glasses", ReferralReason.Contradicted,
      new[] { Score(BoardPile.Melt, 1_246) },
      ContradictedExit: BoardPile.Melt, ContradictionAxis: Accord.Disagree,
      Lean: VerdictLean.OffMarket, RecentSalesCount: 4);
    Assert.Equal("~DC sales disagree with melting it: 4 sold there lately.",
      TriageCases.Assemble(bagged).Referral);

    var standing = bagged with { IsStanding = true };
    Assert.StartsWith("settled sales contradict the melt verdict:",
      TriageCases.Assemble(standing).Referral);
  }

  [Fact]
  public void ExitGerund_NamesEveryExitAsAnAct()
  {
    Assert.Equal("listing it", CaseVoice.ExitGerund(BoardPile.List));
    Assert.Equal("melting it", CaseVoice.ExitGerund(BoardPile.Melt));
    Assert.Equal("churning it", CaseVoice.ExitGerund(BoardPile.Churn));
    Assert.Equal("vendoring it", CaseVoice.ExitGerund(BoardPile.PullAndVendor));
    Assert.Equal("repricing it", CaseVoice.ExitGerund(BoardPile.Reprice));
  }

  // ==========================================================================
  // The melt stats' grammar at n=1 (pen 10, 3b-3)
  // ==========================================================================

  [Fact]
  public void MeltRollup_SpeaksARangeOnlyWhenTheSpreadIsReal()
  {
    Assert.Equal(
      "12 of your own melts returned 41,200 an attempt on average, between 33,000 and 52,000.",
      CaseVoice.MeltRollup(12, 41_200, 33_000, 52_000));

    // ONE receipt has no interval. "between 1,246 and 1,246" is a confidence band drawn
    // on top of a single point - the same sin as "Sells 200-200".
    Assert.Equal("Your one melt of this returned 1,246.",
      CaseVoice.MeltRollup(1, 1_246, 1_246, 1_246));

    // And a many-attempt run whose edges agree has no interval either - the range would
    // be true and empty, which teaches the reader to stop reading ranges.
    Assert.Equal("4 of your own melts returned 800 an attempt, every time.",
      CaseVoice.MeltRollup(4, 800, 800, 800));
  }

  [Fact]
  public void Option_ASingleMeltArguesFlat_AndStillCarriesItsThinCaveat()
  {
    var e = new CaseEvidence("k", "thing", ReferralReason.TooClose,
      new[] { Score(BoardPile.Melt, 1_246) },
      MeltAttempts: 1, MeltAverage: 1_246, MeltSpreadLow: 1_246, MeltSpreadHigh: 1_246);
    var melt = TriageCases.Assemble(e).Options[0];
    Assert.Equal(new[] { "Your one melt of this returned 1,246." }, melt.For.ToArray());
    Assert.Equal(new[] { "Only 1 melt behind that average - one good roll moves it." },
      melt.Caveats.ToArray());
  }

  // ==========================================================================
  // The Look line's derivation and its duration grammar (pen 8a, 3b-5)
  // ==========================================================================

  [Fact]
  public void ReconLook_CarriesItsAnchorAndItsJudgment()
  {
    Assert.Equal(
      "Your Look priced this lane at 9,997, 4m ago - cut in front of the cheapest cluster."
      + " Sells 9,800-13,500, 14 sales, they agree.",
      CaseVoice.ReconLook(9_997, 4 * 60, CaseVoice.LookAnchor(nameof(LaneOutcome.Undercut)),
        "Sells 9,800-13,500, 14 sales, they agree."));
  }

  [Fact]
  public void ReconLook_DropsAClauseItHasNoOperandFor()
  {
    // A missing derivation does not hedge and does not invent - it does not fire.
    Assert.Equal("Your Look priced this lane at 9,997, 4m ago.",
      CaseVoice.ReconLook(9_997, 4 * 60));
    Assert.Equal("Your Look priced this lane at 9,997, 4m ago - nobody real in the queue,"
      + " so it took the top of demonstrated clearing.",
      CaseVoice.ReconLook(9_997, 4 * 60, CaseVoice.LookAnchor(nameof(LaneOutcome.EmptyBoard))));
  }

  [Fact]
  public void LookAnchor_IsSilentOnAnOutcomeItCannotName()
  {
    // A guard-named row (BelowFloor, CapBlocked) banks a hold, so it never reaches the
    // Look line - but an unknown string must produce silence, never a made-up anchor.
    Assert.Equal("", CaseVoice.LookAnchor("BelowFloor"));
    Assert.Equal("", CaseVoice.LookAnchor(""));
    Assert.Equal("", CaseVoice.LookAnchor(null));
    Assert.Equal("stepped past the crashers, then cut in",
      CaseVoice.LookAnchor(nameof(LaneOutcome.CrazySkipped)));
    Assert.Equal("off this item's own NQ tape plus the HQ premium",
      CaseVoice.LookAnchor(nameof(LaneOutcome.PremiumFromNq)));
  }

  [Fact]
  public void ReconLook_ReadsItsAgeInTheElapsedGrammar()
  {
    // THE PLUGIN'S ONE GRAMMAR MISMATCH, FIXED (3b-5). Durations.Span is the "will
    // take" grammar and has no day rung, so a three-day-old Look read "72h ago".
    Assert.Contains("3d ago", CaseVoice.ReconLook(9_997, 3 * 86_400));
    Assert.DoesNotContain("72", CaseVoice.ReconLook(9_997, 3 * 86_400));
  }
}
