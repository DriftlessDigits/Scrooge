using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE DEFER PILE (headliner, 2026-08-06). What is worth pinning here is not
/// the pile - it is the three things the pile is made of, each of which was a
/// judgment call somebody could quietly reverse later:
///
/// <list type="number">
///   <item>membership is a NAMED BRANCH, never a confidence number;</item>
///   <item>the three states are exhaustive and Review keeps its boundary;</item>
///   <item>the tape accumulates and does not act.</item>
/// </list>
/// </summary>
public class DeferPileTests
{
  // ======================================================================
  // The classifier
  // ======================================================================

  [Fact]
  public void Classify_TheLaneBranchOutranksTheTier()
  {
    // A named thing that happened during the walk beats a summary of the
    // evidence. Replacing "we broke a dead heat" with "the exits disagree"
    // would trade a fact for a shrug - and the tape would count the shrug.
    Assert.Equal(DoubtBranch.DeadHeat,
      DeferPlan.Classify(DoubtBranch.DeadHeat, ConfidenceTier.Mixed));
    Assert.Equal(DoubtBranch.UnconvictableHq,
      DeferPlan.Classify(DoubtBranch.UnconvictableHq, ConfidenceTier.Unanimous));
  }

  [Fact]
  public void Classify_MixedIsTheFallbackForRowsTheLaneNeverSpokeAbout()
  {
    // Bag gear has no board walk behind it, so the tier is the only doubt it
    // can report - and it is a real one: the four exits disagreed.
    Assert.Equal(DoubtBranch.MixedTier,
      DeferPlan.Classify(DoubtBranch.None, ConfidenceTier.Mixed));
  }

  [Fact]
  public void Classify_ConfidenceIsNotADoubt()
  {
    Assert.Equal(DoubtBranch.None,
      DeferPlan.Classify(DoubtBranch.None, ConfidenceTier.Unanimous));
  }

  [Fact]
  public void Classify_ContradictedIsNotABranch()
  {
    // The market DISAGREEING with the verdict is not thin ice, it is evidence
    // against the verdict - and it has always been demoted to Review. A branch
    // here would have quietly promoted the Alexander Miniature rule's whole
    // population into a pile that acts.
    Assert.Equal(DoubtBranch.None,
      DeferPlan.Classify(DoubtBranch.None, ConfidenceTier.Contradicted));
  }

  // ======================================================================
  // The stable vocabulary
  // ======================================================================

  [Fact]
  public void EveryBranchHasAKeyThatRoundTrips()
  {
    // These strings are PERSISTED and counted across weeks. A rename splits a
    // tally in half without a single test failing anywhere else.
    foreach (DoubtBranch b in System.Enum.GetValues(typeof(DoubtBranch)))
    {
      if (b == DoubtBranch.None) continue;
      var key = DeferPlan.KeyOf(b);
      Assert.NotEqual("", key);
      Assert.Equal(b, DeferPlan.BranchOf(key));
    }
  }

  [Fact]
  public void TheKeysAreTheOnesTheDatabaseAlreadyHolds()
  {
    // Written out literally, on purpose: this is the one place a refactor that
    // "tidied" the enum names would be caught before it reached a live book.
    Assert.Equal("dead_heat", DeferPlan.KeyOf(DoubtBranch.DeadHeat));
    Assert.Equal("no_tape", DeferPlan.KeyOf(DoubtBranch.NoTape));
    Assert.Equal("unconvictable_hq", DeferPlan.KeyOf(DoubtBranch.UnconvictableHq));
    Assert.Equal("mixed_tier", DeferPlan.KeyOf(DoubtBranch.MixedTier));
  }

  [Fact]
  public void NoBranchWritesNoKey_AndAnUnknownKeyIsNoBranch()
  {
    Assert.Equal("", DeferPlan.KeyOf(DoubtBranch.None));
    Assert.Equal(DoubtBranch.None, DeferPlan.BranchOf(""));
    Assert.Equal(DoubtBranch.None, DeferPlan.BranchOf(null));
    // A key from a future build is not guessed at - folding a stranger's count
    // into a real one is worse than dropping it.
    Assert.Equal(DoubtBranch.None, DeferPlan.BranchOf("age_of_listing"));
  }

  [Fact]
  public void AgeIsNotABranch()
  {
    // Ruled 08-06. A long-sitter is not a doubtful decision - it is a decision
    // the market has not answered yet - and what Drift wanted there was the
    // forward-looking next-round preview, which Slow Movers already draws.
    var names = System.Enum.GetNames(typeof(DoubtBranch));
    Assert.DoesNotContain(names, n => n.Contains("Age") || n.Contains("Slow") || n.Contains("Sat"));
  }

  // ======================================================================
  // The three states
  // ======================================================================

  [Fact]
  public void State_ABranchWithAWinnerDefers()
  {
    Assert.Equal(EyesState.Defer,
      DeferPlan.State(DoubtBranch.DeadHeat, inReview: false, hasWinner: true, playerResolved: false));
  }

  [Fact]
  public void State_NoBranchIsTheSilentBoard()
  {
    Assert.Equal(EyesState.Silent,
      DeferPlan.State(DoubtBranch.None, inReview: false, hasWinner: true, playerResolved: false));
  }

  [Fact]
  public void State_ReviewKeepsItsBoundary_TheRouterDeclining()
  {
    // The one thing Defer must NOT eat. "The router declined to call it" means
    // there is nothing to defer TO, so the action stays withheld and the launch
    // keeps refusing - branch or no branch.
    Assert.Equal(EyesState.Review,
      DeferPlan.State(DoubtBranch.DeadHeat, inReview: true, hasWinner: true, playerResolved: false));
  }

  [Fact]
  public void State_NoWinnerIsReview_NotDefer()
  {
    // A genuinely unrulable row. Deferring it would be the system claiming an
    // opinion it does not hold - "I have opinions" is the middle state's whole
    // premise.
    Assert.Equal(EyesState.Review,
      DeferPlan.State(DoubtBranch.NoTape, inReview: false, hasWinner: false, playerResolved: false));
  }

  [Fact]
  public void State_ThePlayersOwnCallIsNotThinIce()
  {
    Assert.Equal(EyesState.Silent,
      DeferPlan.State(DoubtBranch.MixedTier, inReview: false, hasWinner: true, playerResolved: true));
  }

  [Fact]
  public void State_AWholePileRowNeverDefers()
  {
    // The melt run spends every tier identically, so a Mixed melt row has no
    // doubt anyone could act on differently. Same reason it is not in the
    // judgment queue: a flag nobody can answer is noise.
    Assert.Equal(EyesState.Silent,
      DeferPlan.State(DoubtBranch.MixedTier, inReview: false, hasWinner: true,
        playerResolved: false, ridesWholePile: true));
  }

  [Fact]
  public void State_AndNeedsRuling_NeverBothClaimTheSameRow()
  {
    // The invariant the board is drawn on: a row cannot be both waiting on a
    // human and running without one. Walked over every combination rather than
    // asserted once, because the two predicates live in different files.
    foreach (DoubtBranch branch in System.Enum.GetValues(typeof(DoubtBranch)))
      foreach (var tier in new[]
        { ConfidenceTier.Unanimous, ConfidenceTier.Mixed, ConfidenceTier.Contradicted })
        foreach (var inReview in new[] { true, false })
          foreach (var hasWinner in new[] { true, false })
            foreach (var ruled in new[] { true, false })
            {
              var state = DeferPlan.State(branch, inReview, hasWinner, ruled);
              var deferred = state == EyesState.Defer;
              var needs = GatePlan.NeedsRuling(tier, ruled, inReview, deferred: deferred);
              Assert.False(deferred && needs);
            }
  }

  // ======================================================================
  // What the row says
  // ======================================================================

  [Fact]
  public void EveryBranchSpeaksOneLine_AndInvitesRatherThanDemands()
  {
    // "Eyes welcome, not owed" is the contract, and a row that only stated its
    // doubt would read like a warning the player owes an answer to.
    foreach (DoubtBranch b in System.Enum.GetValues(typeof(DoubtBranch)))
    {
      if (b == DoubtBranch.None) continue;
      var line = DeferPlan.RowLine(b);
      Assert.NotEqual("", line);
      Assert.Contains("overrule freely", line);
      Assert.DoesNotContain("?", line);
    }
    Assert.Equal("", DeferPlan.RowLine(DoubtBranch.None));
  }

  [Fact]
  public void KeptAskLine_NamesTheNumberItKept()
  {
    // The lane_held rehome is LABELING ONLY, so the one thing the row owes the
    // reader is what was kept.
    Assert.Contains("12,500", DeferPlan.KeptAskLine(12_500));
    Assert.Contains("overrule freely", DeferPlan.KeptAskLine(12_500));
    // No ask on record: say so, never print a zero that reads like a price.
    Assert.DoesNotContain("0", DeferPlan.KeptAskLine(null));
    Assert.DoesNotContain("0", DeferPlan.KeptAskLine(0));
  }

  // ----------------------------------------------------------------------
  // The tag in the cell, the sentence on the hover (SF-P4/SF-P5, 08-15)
  // ----------------------------------------------------------------------

  [Fact]
  public void RowTag_IsTheSentenceCompressed_NotASecondStory()
  {
    // Movement 4's trade, on the Defer rows: the cell keeps the tag, the hover
    // keeps the sentence, nothing is deleted. Pinned verbatim because a tag
    // that drifts from the line it summarises is two stories about one row.
    Assert.Equal("took the front of a dead heat", DeferPlan.RowTag(DoubtBranch.DeadHeat));
    Assert.Equal("priced with no sales on record", DeferPlan.RowTag(DoubtBranch.NoTape));
    Assert.Equal("priced under a better item, no HQ tape",
      DeferPlan.RowTag(DoubtBranch.UnconvictableHq));
    Assert.Equal("took the best of disagreeing exits", DeferPlan.RowTag(DoubtBranch.MixedTier));

    // The sentence is still there, unchanged, for the hover to carry.
    Assert.Equal(
      "a dead heat on the board - we took the front of the line and let a sale settle it; overrule freely.",
      DeferPlan.RowLine(DoubtBranch.DeadHeat));
  }

  [Fact]
  public void RowTag_EveryTagIsAStatement_AndShorterThanItsSentence()
  {
    // SF-P5 (Drift: "are those questions? or statements"). A Defer row asks
    // nothing, so nothing it wears may be shaped like a question - and the tag
    // is only worth having if it actually fits where the sentence did not.
    foreach (DoubtBranch b in System.Enum.GetValues(typeof(DoubtBranch)))
    {
      if (b == DoubtBranch.None) continue;
      var tag = DeferPlan.RowTag(b);
      Assert.NotEqual("", tag);
      Assert.DoesNotContain("?", tag);
      Assert.True(tag.Length < DeferPlan.RowLine(b).Length / 2,
        $"{b}: the tag has to be a tag, not the sentence again");
      // The invitation is the contract and lives on the sentence; repeating it
      // on every row would make the scan an argument.
      Assert.DoesNotContain("overrule", tag);
    }
    Assert.Equal("", DeferPlan.RowTag(DoubtBranch.None));
  }

  [Fact]
  public void KeptAskTag_KeepsTheNumber_BecauseTheNumberIsTheAct()
  {
    // The lane_held row's whole act was keeping an ask, so the ask survives the
    // compression; the "no lane to judge it against" half goes to the hover.
    Assert.Equal("kept your ask at 12,500", DeferPlan.KeptAskTag(12_500));
    // No ask on record: say so, never print a zero that reads like a price.
    Assert.Equal("kept your ask", DeferPlan.KeptAskTag(null));
    Assert.Equal("kept your ask", DeferPlan.KeptAskTag(0));
    Assert.DoesNotContain("?", DeferPlan.KeptAskTag(12_500));
  }

  [Fact]
  public void Badge_IsNotAQuestionMark_BecauseTheRowIsNotAsking()
  {
    // SF-P5. The "?" is reserved for the rows that genuinely want a ruling; a
    // Defer row runs either way and nothing is waiting on the player.
    Assert.NotEqual("?", DeferPlan.Badge);
    Assert.DoesNotContain("?", DeferPlan.Badge);
    Assert.Equal(">", DeferPlan.Badge);
  }

  [Fact]
  public void BadgeHint_SaysTheContractOutLoud_WithOrWithoutASentence()
  {
    // The glyph cannot carry the contract, so the hover must - and it must
    // still say it on a row whose line is empty rather than trailing off.
    var hint = DeferPlan.BadgeHint(DeferPlan.RowLine(DoubtBranch.NoTape));
    Assert.StartsWith("Defer - ", hint);
    Assert.Contains("no sales on record", hint);
    Assert.EndsWith("This one runs either way; nothing is waiting on you.", hint);

    Assert.Equal("Defer - this one runs either way; nothing is waiting on you.",
      DeferPlan.BadgeHint(""));
  }

  [Fact]
  public void TheAskingRowAndTheStatingRowAreNeverTheSameRow()
  {
    // The whole point of SF-P5: the "?" means "this one wants you". That only
    // holds while asking and stating are exclusive, so pin the exclusion at the
    // predicates the two presentations read - a row that needs a ruling is never
    // deferred, and a deferred row never needs one.
    foreach (var tier in new[] { ConfidenceTier.Unanimous, ConfidenceTier.Mixed, ConfidenceTier.Contradicted })
      foreach (var branch in new[] { DoubtBranch.None, DoubtBranch.DeadHeat, DoubtBranch.MixedTier })
        foreach (var inReview in new[] { false, true })
          foreach (var hasWinner in new[] { false, true })
            foreach (var ruled in new[] { false, true })
            {
              var states = DeferPlan.State(branch, inReview, hasWinner, ruled) == EyesState.Defer;
              var asks = GatePlan.NeedsRuling(tier, ruled, inReview, deferred: states);
              Assert.False(states && asks);
              // A Review row is the population the "?" is FOR - nobody called it,
              // so it is asking and it is not stating.
              if (!hasWinner || inReview) Assert.False(states);
            }
  }

  [Fact]
  public void TheGroupHintStatesTheContract_AndAsksNothing()
  {
    // The pile's own header is the same claim the badge makes. It has never
    // been a question and must not become one.
    Assert.DoesNotContain("?", DeferPlan.GroupHint);
    Assert.Contains("nothing here is waiting on you", DeferPlan.GroupHint);
  }
}

/// <summary>
/// The lane walk's own doubt. These fire inside <see cref="LanePricing.Decide"/>
/// and are the only branches with a MECHANISM behind them - the rest of Defer's
/// membership is the tier. Each test is the exact board shape the branch is
/// named after.
/// </summary>
public class LaneDoubtTests
{
  private static readonly LaneConfig Cfg = new()
  {
    MinHistorySamples = 3,
    HalfLifeDays = 30,
    CeilingMult = 3.0,
    SeatBudget = 4,
  };

  private static LaneModel? Lane(params long[] prices)
    => LanePricing.BuildLane(
      prices.Select(p => new LaneSale(p, 1_000_000, false)).ToList(),
      isHq: false, Cfg, nowUnix: 1_000_000);

  private static List<LaneListing> Board(params long[] prices)
    => prices.Select(p => new LaneListing(p, false)).ToList();

  [Fact]
  public void DeadHeat_WhenThePackAndTheCrowdTie()
  {
    // Two rows far below clearing, two rows behind them: the outnumbering test
    // comes back tied, nothing convicted the pack and nothing crowned it, so
    // the walk took the front of the whole queue and let a sale settle it.
    var lane = Lane(1_000, 1_000, 1_000);
    var d = LanePricing.Decide(Board(50, 60, 1_000, 1_100), lane, null, Cfg);

    Assert.Contains("dead heat", d.Evidence);
    Assert.Equal(DoubtBranch.DeadHeat, d.Doubt);
  }

  [Fact]
  public void NoTape_WhenNothingLocalEverCleared()
  {
    // A real queue to price against and no census of our own to judge it with.
    // The price is honest and thin at the same time, which is the branch.
    var d = LanePricing.Decide(Board(1_000, 1_100, 1_200), lane: null, null, Cfg);

    Assert.Equal(DoubtBranch.NoTape, d.Doubt);
  }

  [Fact]
  public void NoTape_GenuineSilenceIsTheBranchAtItsPurest()
  {
    var d = LanePricing.Decide(Board(), lane: null, null, Cfg);

    Assert.Equal(LaneOutcome.HeldThinHistory, d.Outcome);
    Assert.Equal(DoubtBranch.NoTape, d.Doubt);
    // The HOLD does not change - the branch only lets the row say why.
    Assert.Null(d.Anchor);
  }

  [Fact]
  public void NoTape_ACommunityLaneIsNotOurCensus()
  {
    // The fallback is allowed to price - that is what it is for - it just does
    // not get to claim a local sales census exists.
    var community = LanePricing.BuildLane(
      new[] { new LaneSale(1_000, 1_000_000, false), new LaneSale(1_050, 1_000_000, false),
              new LaneSale(1_100, 1_000_000, false) },
      isHq: false, Cfg, nowUnix: 1_000_000, source: LaneSource.Community);

    var d = LanePricing.Decide(Board(2_000, 2_100), community, null, Cfg);

    Assert.Equal(DoubtBranch.NoTape, d.Doubt);
  }

  [Fact]
  public void UnconvictableHq_WhenWePriceUnderABetterItemWeCouldNotCallNonsense()
  {
    // The A12 fail-closed default: an HQ row stands in the NQ line, we have no
    // HQ tape to convict it with, so it is a competitor and we duck under it.
    // Right answer, thin ice - which is exactly the pile.
    var lane = Lane(1_000, 1_000, 1_000);
    var board = new List<LaneListing>
    {
      new(900, false, IsHq: true),
      new(1_000, false),
    };

    var d = LanePricing.Decide(board, lane, null, Cfg, itemIsHq: false);

    Assert.True(d.CrossQualityCapped);
    Assert.Equal(DoubtBranch.UnconvictableHq, d.Doubt);
  }

  [Fact]
  public void NoDoubt_OnAnOrdinaryUndercutOverARealTape()
  {
    // The normal night. If this ever starts reporting a branch, the Defer pile
    // has become the board and the whole three-state distinction is gone.
    var lane = Lane(1_000, 1_050, 1_100);
    var d = LanePricing.Decide(Board(1_000, 1_050, 1_100), lane, null, Cfg);

    Assert.Equal(DoubtBranch.None, d.Doubt);
  }

  [Fact]
  public void SeatRail_ClearsTheDeadHeatItNeverActedOn()
  {
    // Belt and braces on the rail's reset: if the seat rail overrides the walk,
    // the tie it broke was not the reason for the price, so the branch must not
    // ride the receipt. (The rail only fires off the lone-crazy path, so this
    // asserts a shape the code makes unreachable rather than one it allows.)
    var lane = Lane(1_000, 1_000, 1_000);
    var d = LanePricing.Decide(Board(50, 60, 1_000, 1_100), lane, null, Cfg);
    Assert.Equal(DoubtBranch.DeadHeat, d.Doubt);

    var railed = LanePricing.Decide(
      Board(10, 11, 12, 13, 14, 15, 1_000), lane, null, Cfg with { SeatBudget = 1 });
    Assert.NotEqual(DoubtBranch.DeadHeat, railed.Doubt);
  }
}
