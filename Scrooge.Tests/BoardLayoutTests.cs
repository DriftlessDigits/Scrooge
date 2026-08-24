using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The ruled ledger's pure core (stage 1): the call-state model behind the
/// zero-button rows, the pile grouping and header arithmetic, and the
/// say-the-shared-reason-once narration rule.
///
/// The load-bearing receipts here are the two asymmetries: an UNRULED row
/// presses no cell and every cell on it is live (including the router's own,
/// where the click IS the ruling), while a RULED row's own cell is inert - the
/// exact behaviour the old green move button had, moved into the cell.
/// </summary>
public class BoardLayoutTests
{
  // ---- The call state ----

  [Fact]
  public void UntouchedRouterVerdict_ReadsAsRouter()
  {
    var s = BoardCalls.Of(RoutingExit.Gc, routerWasReview: false,
      currentExit: RoutingExit.Gc, needsRuling: false);
    Assert.Equal(CallSource.Router, s.Source);
    Assert.Equal("router", BoardCalls.Label(s));
    Assert.Equal(RoutingExit.Gc, s.Pressed);
  }

  [Fact]
  public void MovedRow_ReadsAsPlayer_AndNamesTheRoutersOriginalCall()
  {
    var s = BoardCalls.Of(RoutingExit.Gc, routerWasReview: false,
      currentExit: RoutingExit.Desynth, needsRuling: false);
    Assert.Equal(CallSource.Player, s.Source);
    Assert.Equal("YOU (router: gc)", BoardCalls.Label(s));
    // The PRESSED cell is where the row is going, not where the router sent it.
    Assert.Equal(RoutingExit.Desynth, s.Pressed);
  }

  [Fact]
  public void ReviewRow_IsUnruled_AndPressesNothing()
  {
    // Four neutral cells: the router declined to call it, so no cell may claim it did.
    var s = BoardCalls.Of(RoutingExit.List, routerWasReview: true,
      currentExit: RoutingExit.List, needsRuling: true);
    Assert.Equal(CallSource.Unruled, s.Source);
    Assert.Equal("unruled", BoardCalls.Label(s));
    Assert.Null(s.Pressed);
  }

  [Fact]
  public void ContradictedDemotion_IsAlsoUnruled()
  {
    // A confident verdict demoted into Review by the market. It still has an exit
    // on record, but nobody has ruled it - so it presses nothing either.
    var s = BoardCalls.Of(RoutingExit.Vendor, routerWasReview: false,
      currentExit: RoutingExit.Vendor, needsRuling: true);
    Assert.Equal(CallSource.Unruled, s.Source);
    Assert.Null(s.Pressed);
  }

  [Fact]
  public void RuledReviewRow_IsThePlayersCall_EvenWhenHePickedTheRoutersSuggestion()
  {
    // The router never called this one; it declined to. Agreeing with a suggestion
    // is still the human's ruling, and the Call column must not launder it into
    // "router" - the teaching signal was written against Review.
    var s = BoardCalls.Of(RoutingExit.List, routerWasReview: true,
      currentExit: RoutingExit.List, needsRuling: false);
    Assert.Equal(CallSource.Player, s.Source);
    Assert.Equal("YOU (router: review)", BoardCalls.Label(s));
    Assert.Equal(RoutingExit.List, s.Pressed);
  }

  // ---- The listed rows join the contract (standing is a ruling, 08-02) ----

  [Fact]
  public void UncontestedListing_IsStanding_PressesNothing_OwesNobodyAClick()
  {
    // The ask is the call, and the ask is not a cell - so nothing is pressed,
    // and the label is its own word: the router never called this row at all.
    var s = BoardCalls.OfListed(RoutingExit.List, proposalWasReview: false,
      contested: false);
    Assert.Equal(CallSource.Standing, s.Source);
    Assert.Equal("standing", BoardCalls.Label(s));
    Assert.Null(s.Pressed);
  }

  [Fact]
  public void ContestedListing_IsUnruled_WithTheProposalRiding()
  {
    // A triage contest wants eyes whatever the evidence tier says - the tier
    // gate was a proxy, the contest IS the review.
    var s = BoardCalls.OfListed(RoutingExit.Vendor, proposalWasReview: false,
      contested: true);
    Assert.Equal(CallSource.Unruled, s.Source);
    Assert.Equal("unruled", BoardCalls.Label(s));
    Assert.Equal(RoutingExit.Vendor, s.RouterExit);
    Assert.Null(s.Pressed);
  }

  [Fact]
  public void StagedListing_IsThePlayers_AndPrintsTheVerbHeChose()
  {
    // "pull" has no strip column, so the label prints the triage verb rather
    // than laundering it through the exit grammar - and presses NO cell.
    var pulled = BoardCalls.OfListed(RoutingExit.Vendor, proposalWasReview: false,
      contested: true, staged: StandingAction.Pull);
    Assert.Equal(CallSource.Player, pulled.Source);
    Assert.Equal("YOU (pull)", BoardCalls.ListedLabel(pulled, StandingAction.Pull));
    Assert.Null(pulled.Pressed);
    // A verb with a column presses its own cell - a staged melt IS the call.
    var melted = BoardCalls.OfListed(RoutingExit.Vendor, proposalWasReview: false,
      contested: true, staged: StandingAction.Melt);
    Assert.Equal("YOU (melt)", BoardCalls.ListedLabel(melted, StandingAction.Melt));
    Assert.Equal(RoutingExit.Desynth, melted.Pressed);
  }

  [Fact]
  public void ActionAndExit_AreEachOthersInverse_OnTheFourCells()
  {
    foreach (var exit in BoardCalls.Exits)
      Assert.Equal(exit, BoardCalls.ExitOfAction(BoardCalls.ActionOfExit(exit)));
    Assert.Null(BoardCalls.ExitOfAction(StandingAction.Pull));
    Assert.Null(BoardCalls.ExitOfAction(StandingAction.None));
  }

  [Fact]
  public void APullStagedRow_KeepsEveryCellLive()
  {
    // The pull pressed no cell, so no cell may refuse a click - re-ruling from
    // pull to any exit must always be one click.
    var pulled = BoardCalls.OfListed(RoutingExit.Vendor, false,
      contested: true, staged: StandingAction.Pull);
    foreach (var exit in BoardCalls.Exits)
      Assert.True(BoardCalls.IsActionable(pulled, exit));
  }

  [Fact]
  public void AStagedVerbsPile_FollowsTheAnswer()
  {
    Assert.Equal(BoardPile.Melt, BoardCalls.PileOfAction(StandingAction.Melt, BoardPile.Review));
    Assert.Equal(BoardPile.Churn, BoardCalls.PileOfAction(StandingAction.Gc, BoardPile.Review));
    Assert.Equal(BoardPile.PullAndVendor, BoardCalls.PileOfAction(StandingAction.Pull, BoardPile.Review));
    // Unstaged: the triage pile stands.
    Assert.Equal(BoardPile.Review, BoardCalls.PileOfAction(StandingAction.None, BoardPile.Review));
  }

  [Fact]
  public void StandingRow_EveryCellIsLive()
  {
    // No cell is pressed on a standing row, so any click is the player
    // contesting his own position - which is his right.
    var s = BoardCalls.OfListed(RoutingExit.List, proposalWasReview: false,
      contested: false);
    foreach (var exit in BoardCalls.Exits)
      Assert.True(BoardCalls.IsActionable(s, exit));
  }

  [Fact]
  public void ListedLabel_FallsThroughToTheSharedGrammar_OffThePlayerSource()
  {
    var standing = BoardCalls.OfListed(RoutingExit.List, false, contested: false);
    var contested = BoardCalls.OfListed(RoutingExit.List, false, contested: true);
    Assert.Equal("standing", BoardCalls.ListedLabel(standing, StandingAction.None));
    Assert.Equal("unruled", BoardCalls.ListedLabel(contested, StandingAction.None));
  }

  // ---- The Melt header speaks two currencies (strings pass, 08-02) ----

  [Fact]
  public void MeltHeader_SplitsGilFromSkillups()
  {
    // 14 rows summing 226,875 of which 2 red (100k each) are knob worth:
    // the promise and the desynth line's delivery finally share a currency.
    var s = new PileSummary(14, 226_875, 0);
    Assert.Equal("Melt - 14 rows, ~26,875 gil in mats + 2 red skill-ups",
      BoardLayout.MeltHeaderLine(s, redRows: 2, yellowRows: 0, knobRed: 100_000, knobYellow: 50_000));
  }

  [Fact]
  public void MeltHeader_NeverClaimsNegativeGil()
  {
    // Knob-dominated pile: mats round to nothing, the skill-ups still speak.
    var s = new PileSummary(2, 150_000, 0);
    var line = BoardLayout.MeltHeaderLine(s, redRows: 1, yellowRows: 2, knobRed: 100_000, knobYellow: 50_000);
    Assert.Equal("Melt - 2 rows, 1 red skill-up + 2 yellow skill-ups", line);
    Assert.DoesNotContain("gil", line); // zero mats says nothing, never a negative
  }

  [Fact]
  public void MeltHeader_NoSkillups_IsThePlainGilLine()
  {
    var s = new PileSummary(3, 27_000, 1);
    Assert.Equal("Melt - 3 rows, ~27,000 gil in mats (1 unpriced)",
      BoardLayout.MeltHeaderLine(s, 0, 0, 100_000, 50_000));
  }

  [Fact]
  public void MeltHeader_SaysHowMuchOfItsPromiseNobodyWeighed()
  {
    // The header sums measurements and band averages into one gil figure, so
    // the figure has to carry the same tell the cells wear (08-03).
    var s = new PileSummary(6, 27_000, 1);
    Assert.Equal("Melt - 6 rows, ~27,000 gil in mats (1 unpriced, 4 estimated)",
      BoardLayout.MeltHeaderLine(s, 0, 0, 100_000, 50_000, estimatedRows: 4));

    // Nothing estimated says nothing - the mark only means something if it's rare.
    Assert.Equal("Melt - 6 rows, ~27,000 gil in mats (1 unpriced)",
      BoardLayout.MeltHeaderLine(s, 0, 0, 100_000, 50_000, estimatedRows: 0));
  }

  // ---- The melt grade's tell: a prior may never wear a measurement's clothes ----

  [Fact]
  public void OnlyAMeltPrior_WearsTheEstimateMark()
  {
    Assert.Equal("~", BoardCalls.GradeMark(RoutingExit.Desynth, MeltGrade.Prior));
    Assert.Equal("", BoardCalls.GradeMark(RoutingExit.Desynth, MeltGrade.Measured));
    Assert.Equal("", BoardCalls.GradeMark(RoutingExit.Desynth, MeltGrade.Skillup));
    Assert.Equal("", BoardCalls.GradeMark(RoutingExit.Desynth, MeltGrade.None));
    // No other column has grades to tell apart.
    Assert.Equal("", BoardCalls.GradeMark(RoutingExit.Vendor, MeltGrade.Prior));
    Assert.Equal("", BoardCalls.GradeMark(RoutingExit.List, MeltGrade.Prior));
  }

  [Fact]
  public void TheTooltipNamesTheGrade_AndNeverGuessesOne()
  {
    Assert.Contains("Estimated", BoardCalls.GradeHint(RoutingExit.Desynth, MeltGrade.Prior));
    Assert.Contains("ilvl band", BoardCalls.GradeHint(RoutingExit.Desynth, MeltGrade.Prior));
    Assert.Contains("Measured", BoardCalls.GradeHint(RoutingExit.Desynth, MeltGrade.Measured));
    Assert.Contains("skill-up knob", BoardCalls.GradeHint(RoutingExit.Desynth, MeltGrade.Skillup));
    // An ungraded score and every other exit stay silent rather than claim a
    // provenance nobody recorded.
    Assert.Equal("", BoardCalls.GradeHint(RoutingExit.Desynth, MeltGrade.None));
    Assert.Equal("", BoardCalls.GradeHint(RoutingExit.Gc, MeltGrade.Prior));
  }

  // ---- Cell-click semantics ----

  [Fact]
  public void ClickingANonWinnerCell_IsActionable()
  {
    var s = BoardCalls.Of(RoutingExit.Gc, false, RoutingExit.Gc, false);
    Assert.True(BoardCalls.IsActionable(s, RoutingExit.Desynth));
  }

  [Fact]
  public void ClickingTheOwnCellOfARuledRow_IsANoOp()
  {
    // The old green button refused its own click; the pressed cell inherits that.
    var s = BoardCalls.Of(RoutingExit.Gc, false, RoutingExit.Gc, false);
    Assert.False(BoardCalls.IsActionable(s, RoutingExit.Gc));
  }

  [Fact]
  public void ClickingTheRoutersCellOnAnOverriddenRow_RevertsToRouter()
  {
    var overridden = BoardCalls.Of(RoutingExit.Gc, false, RoutingExit.Desynth, false);
    Assert.True(BoardCalls.IsActionable(overridden, RoutingExit.Gc));

    var reverted = BoardCalls.Of(RoutingExit.Gc, false, RoutingExit.Gc, false);
    Assert.Equal(CallSource.Router, reverted.Source);
    Assert.Equal("router", BoardCalls.Label(reverted));
  }

  [Fact]
  public void OnAnUnruledRow_EveryCellIsLive_IncludingTheRoutersOwn()
  {
    // This is the whole Review gesture: any cell click graduates the row out.
    var s = BoardCalls.Of(RoutingExit.List, true, RoutingExit.List, needsRuling: true);
    foreach (var exit in BoardCalls.Exits)
      Assert.True(BoardCalls.IsActionable(s, exit));
  }

  // ---- The strip ----

  [Fact]
  public void TheStripOrderIsFixed()
  {
    Assert.Equal(
      new[] { RoutingExit.List, RoutingExit.Desynth, RoutingExit.Gc, RoutingExit.Vendor },
      BoardCalls.Exits);
    Assert.Equal("List", BoardCalls.ColumnLabel(RoutingExit.List));
    Assert.Equal("Melt", BoardCalls.ColumnLabel(RoutingExit.Desynth));
    Assert.Equal("GC", BoardCalls.ColumnLabel(RoutingExit.Gc));
    Assert.Equal("Vend", BoardCalls.ColumnLabel(RoutingExit.Vendor));
  }

  [Fact]
  public void ScoresMapToTheirColumns()
  {
    var scores = new RoutingScores(List: 1000, Gc: 2000, Melt: 3000, Vendor: 4000);
    Assert.Equal(1000, BoardCalls.ScoreOf(scores, RoutingExit.List));
    Assert.Equal(3000, BoardCalls.ScoreOf(scores, RoutingExit.Desynth));
    Assert.Equal(2000, BoardCalls.ScoreOf(scores, RoutingExit.Gc));
    Assert.Equal(4000, BoardCalls.ScoreOf(scores, RoutingExit.Vendor));
  }

  [Fact]
  public void NoScoresAtAll_ReadsNullEverywhere()
  {
    // Pre-value exits (ban, protection, venture panic) carry no comparison. Every
    // cell is a dash - a zero would be a claim the router never made.
    foreach (var exit in BoardCalls.Exits)
      Assert.Null(BoardCalls.ScoreOf(null, exit));
  }

  // ---- Pile grouping + header aggregation ----

  [Fact]
  public void TheGroupOrderIsTheEyesAxisFirst_AndOmitsSilent()
  {
    // Review (withheld) then Defer (acting, flagged), then the exits. One scan
    // down the top of the board is the whole three-state contract.
    Assert.Equal(BoardPile.Review, BoardLayout.GroupOrder[0]);
    Assert.Equal(BoardPile.Defer, BoardLayout.GroupOrder[1]);
    // The third state draws nothing: a confident verdict not to engage has no
    // row, which is what killed the Watch pile.
    Assert.DoesNotContain(BoardPile.Silent, BoardLayout.GroupOrder);
  }

  [Fact]
  public void TheDeferGroupSumsNoColumn()
  {
    // One home means the Defer group holds rows headed for DIFFERENT exits, so
    // there is no single score column its header could honestly total. A plain
    // count is the only true header it can have.
    Assert.Null(BoardLayout.ExitOf(BoardPile.Defer));
  }

  [Fact]
  public void EachExitPileSumsItsOwnColumn()
  {
    Assert.Equal(RoutingExit.List, BoardLayout.ExitOf(BoardPile.List));
    Assert.Equal(RoutingExit.Desynth, BoardLayout.ExitOf(BoardPile.Melt));
    Assert.Equal(RoutingExit.Gc, BoardLayout.ExitOf(BoardPile.Churn));
    Assert.Equal(RoutingExit.Vendor, BoardLayout.ExitOf(BoardPile.PullAndVendor));
    // Review is aimed nowhere yet, so it sums nothing.
    Assert.Null(BoardLayout.ExitOf(BoardPile.Review));
  }

  [Fact]
  public void SummarizeCountsEveryRow_ButOnlySumsThePricedOnes()
  {
    var s = BoardLayout.Summarize(new long?[] { 1000, null, 2500, 0 });
    Assert.Equal(4, s.Count);
    Assert.Equal(3500, s.ExpectedGil);
    Assert.Equal(2, s.Unpriced); // the null AND the zero
  }

  [Fact]
  public void HeaderLine_SaysTheCount_TheGil_AndWhatItCouldNotPrice()
  {
    var s = BoardLayout.Summarize(new long?[] { 12_000, null });
    Assert.Equal("GC turn-in - 2 rows, ~12,000 gil (1 unpriced)",
      BoardLayout.HeaderLine(BoardPile.Churn, s));
  }

  [Fact]
  public void HeaderLine_OmitsTheGilClauseWhenNothingIsPriced()
  {
    var s = BoardLayout.Summarize(new long?[] { null });
    Assert.Equal("Review - 1 row (1 unpriced)", BoardLayout.HeaderLine(BoardPile.Review, s));
  }

  [Fact]
  public void EmptySummaryIsClean()
  {
    var s = BoardLayout.Summarize(new List<long?>());
    Assert.Equal("Melt - 0 rows", BoardLayout.HeaderLine(BoardPile.Melt, s));
  }

  // ---- Shared reasoning, said once ----

  [Fact]
  public void TheSealDiscountClauseLeavesTheRowAndLandsOnTheHeader()
  {
    var rate = SealRunway.Effective(baseRate: 25, ventureStock: 2_200, fullBelow: 1_000, zeroAbove: 3_000);
    Assert.True(rate.Discounted);

    var rowReason = $"Turn in: 300 seals (~1,125 gil at 3.75 gil/seal, rough).{rate.Narration}";
    var stripped = BoardNarration.Strip(rowReason, rate.Narration);

    Assert.Equal("Turn in: 300 seals (~1,125 gil at 3.75 gil/seal, rough).", stripped);
    // The header note that used to re-say the clause died in 3b-6 (dark-mode rule);
    // the narration itself still rides the GC option line in the detail pane.
    Assert.Contains("Seal value reduced", rate.Narration);
  }

  [Fact]
  public void AnUndiscountedBatchHasNoHeaderNote_AndLeavesRowsAlone()
  {
    var rate = SealRunway.Effective(25, ventureStock: 400, fullBelow: 1_000, zeroAbove: 3_000);
    Assert.False(rate.Discounted);
    Assert.Equal("", rate.Narration);

    const string reason = "Turn in: 300 seals (~7,500 gil at 25 gil/seal, rough).";
    Assert.Equal(reason, BoardNarration.Strip(reason, rate.Narration));
  }

  [Fact]
  public void TheSkillupParentheticalLeavesTheRowWithoutStrandingItsFullStop()
  {
    const string reason =
      "Skillup: red desynth at ilvl 640 — worth 100,000 gil to you (skillups are scarce).";
    Assert.Equal("Skillup: red desynth at ilvl 640 — worth 100,000 gil to you.",
      BoardNarration.Strip(reason, BoardNarration.SkillupClause));
  }

  [Fact]
  public void StrippingBothFragmentsAtOnceCollapsesTheSeam()
  {
    var rate = SealRunway.Effective(25, 2_200, 1_000, 3_000);
    var reason = $"Turn in: 300 seals.{rate.Narration} Borderline vs Desynth.";
    Assert.Equal("Turn in: 300 seals. Borderline vs Desynth.",
      BoardNarration.Strip(reason, rate.Narration, BoardNarration.SkillupClause));
  }

  // ---- The run log's copy-out stutter ----

  [Fact]
  public void ACopyLineNeverSaysTheActTwice()
  {
    Assert.Equal("Desynthed: Aetherial Ring",
      RunLogLines.CopyLine("Desynthed", "Aetherial Ring", "desynthed"));
  }

  [Fact]
  public void TheQualifierSurvivesTheDedupe()
  {
    Assert.Equal("Desynthed: Aetherial Ring — (HQ)",
      RunLogLines.CopyLine("Desynthed", "Aetherial Ring", "desynthed (HQ)"));
  }

  [Fact]
  public void EveryOtherOutcomeIsUntouched()
  {
    Assert.Equal("Vendor-sold: Cotton Yarn — 42 gil",
      RunLogLines.CopyLine("Vendor-sold", "Cotton Yarn", "42 gil"));
    Assert.Equal("No data: Cotton Yarn", RunLogLines.CopyLine("No data", "Cotton Yarn", ""));
  }

  // ---- Exit doors (strings-three): which exits EXIST, not which have evidence ----

  [Fact]
  public void EveryDoorMapsToItsOwnExit()
  {
    Assert.True(new ExitDoors(true, false, false, false).Has(RoutingExit.List));
    Assert.True(new ExitDoors(false, true, false, false).Has(RoutingExit.Desynth));
    Assert.True(new ExitDoors(false, false, true, false).Has(RoutingExit.Gc));
    Assert.True(new ExitDoors(false, false, false, true).Has(RoutingExit.Vendor));
    var untradable = new ExitDoors(false, true, true, true);
    Assert.False(untradable.Has(RoutingExit.List));
  }

  [Fact]
  public void AllOpenOpensAllFourDoors()
  {
    foreach (var exit in BoardCalls.Exits)
      Assert.True(ExitDoors.AllOpen.Has(exit));
  }

  [Fact]
  public void NonStripExitsAreNeverDoorBlocked()
  {
    // Hold/Ban have no cells and no doors - a door model that blocked them
    // would break IsActionable paths that never consult a cell.
    var closed = new ExitDoors(false, false, false, false);
    Assert.True(closed.Has(RoutingExit.Hold));
    Assert.True(closed.Has(RoutingExit.Ban));
  }

  [Fact]
  public void ClosedHintsSpeakPlayerLanguage()
  {
    foreach (var exit in BoardCalls.Exits)
    {
      var hint = ExitDoors.ClosedHint(exit);
      Assert.False(string.IsNullOrEmpty(hint));
      Assert.DoesNotContain("door", hint);      // the metaphor is ours, not the player's
      Assert.DoesNotContain("exit", hint);
      Assert.DoesNotContain("eligib", hint);
    }
    Assert.Contains("market board", ExitDoors.ClosedHint(RoutingExit.List));
    Assert.Contains("desynth", ExitDoors.ClosedHint(RoutingExit.Desynth), StringComparison.OrdinalIgnoreCase);
  }

  // ========================================================================
  // The column model (item 9): the layout IS the enum
  // ========================================================================

  [Fact]
  public void ColumnAt_MapsTheTableIndexToItsName()
  {
    // The sort used to be arithmetic on a bare index ("1..4 are the scores"),
    // which held until the flag and ilvl columns went in front of them. If this
    // ever drifts, a gil column silently starts ordering by item level.
    Assert.Equal(BoardColumn.Flag, BoardLayout.ColumnAt(0));
    Assert.Equal(BoardColumn.Item, BoardLayout.ColumnAt(1));
    Assert.Equal(BoardColumn.Ilvl, BoardLayout.ColumnAt(2));
    Assert.Equal(BoardColumn.List, BoardLayout.ColumnAt(3));
    Assert.Equal(BoardColumn.Vend, BoardLayout.ColumnAt(6));
    Assert.Equal(BoardColumn.Call, BoardLayout.ColumnAt(7));
  }

  [Fact]
  public void ColumnAt_AnUnplaceableSpecFallsBackToTheNameSort()
  {
    Assert.Equal(BoardColumn.Item, BoardLayout.ColumnAt(-1));
    Assert.Equal(BoardColumn.Item, BoardLayout.ColumnAt(99));
  }

  [Fact]
  public void ExitOfColumn_TheFourScoreColumnsCarryTheStripsOwnOrder()
  {
    // The strip's order and the table's must be the same list read twice.
    var columns = new[] { BoardColumn.List, BoardColumn.Melt, BoardColumn.Gc, BoardColumn.Vend };
    Assert.Equal(BoardCalls.Exits, columns.Select(c => BoardLayout.ExitOfColumn(c)!.Value));
  }

  [Fact]
  public void ExitOfColumn_TheColumnsThatAreNotScoresSaySo()
  {
    Assert.Null(BoardLayout.ExitOfColumn(BoardColumn.Flag));
    Assert.Null(BoardLayout.ExitOfColumn(BoardColumn.Item));
    Assert.Null(BoardLayout.ExitOfColumn(BoardColumn.Ilvl));
    Assert.Null(BoardLayout.ExitOfColumn(BoardColumn.Call));
  }

  [Fact]
  public void AttentionRank_ARowWaitingOnAHumanOutranksEveryTier()
  {
    // The flag column exists to stack the work at the top of one click. What is
    // in the way of tonight's round is the launch control's definition of it -
    // needing a ruling - not the evidence tier, which is a different question.
    Assert.Equal(0, BoardLayout.AttentionRank(ConfidenceTier.Unanimous, needsRuling: true));
    Assert.True(BoardLayout.AttentionRank(ConfidenceTier.Unanimous, true)
      < BoardLayout.AttentionRank(ConfidenceTier.Contradicted, false));
  }

  [Fact]
  public void AttentionRank_TheSettledRowsOrderByHowThinTheEvidenceIs()
  {
    Assert.True(BoardLayout.AttentionRank(ConfidenceTier.Contradicted, false)
      < BoardLayout.AttentionRank(ConfidenceTier.Mixed, false));
    Assert.True(BoardLayout.AttentionRank(ConfidenceTier.Mixed, false)
      < BoardLayout.AttentionRank(ConfidenceTier.Unanimous, false));
  }

  [Fact]
  public void AttentionRank_DeferSitsBetweenTheTwoContracts()
  {
    // The three states, in the one column that sorts on them. Rank 0 is "I
    // won't move without you", rank 1 is "I'm moving, and I'd like you to
    // look", and everything below is "I'm moving." A deferring row is usually
    // Mixed, so ranking it by tier would bury it under Contradicted rows that
    // already have their answer.
    var waiting = BoardLayout.AttentionRank(ConfidenceTier.Mixed, needsRuling: true);
    var defers = BoardLayout.AttentionRank(ConfidenceTier.Mixed, false, deferred: true);
    var contradicted = BoardLayout.AttentionRank(ConfidenceTier.Contradicted, false);

    Assert.True(waiting < defers);
    Assert.True(defers < contradicted);
  }

  [Fact]
  public void AttentionRank_TheDeferSourceLabelsTheVerbItIsAbout()
  {
    // One home means the row is drawn away from its exit pile, so "defer"
    // alone would leave the player reading four cells to find out what is
    // about to happen to the thing.
    var call = BoardCalls.Of(RoutingExit.Desynth, routerWasReview: false,
      RoutingExit.Desynth, needsRuling: false, deferred: true);

    Assert.Equal(CallSource.Deferred, call.Source);
    Assert.Equal("defer: melt", BoardCalls.Label(call));
    // And the planned verb is still PRESSED - the strip never hides what the
    // round is about to do, which is what makes one home an honest trade.
    Assert.Equal(RoutingExit.Desynth, call.Pressed);
  }

  [Fact]
  public void TheDeferSourceYieldsToThePlayersOwnHand()
  {
    // A row he moved is his call whatever ice we were on. Dressing his
    // decision in our doubt would be the board editorializing about the human.
    var call = BoardCalls.Of(RoutingExit.Desynth, routerWasReview: false,
      RoutingExit.Gc, needsRuling: false, deferred: true);

    Assert.Equal(CallSource.Player, call.Source);
    Assert.Equal("YOU (router: melt)", BoardCalls.Label(call));
  }

  // ---- The nulls-last ordering every numeric column shares ----

  private sealed record SortRow(string Name, long? Value);

  [Fact]
  public void NullsLast_ParksTheAbsencesAtTheBottomOfBothDirections()
  {
    var rows = new List<SortRow>
    {
      new("carbon", 50), new("apple", null), new("beryl", 900), new("zinc", null),
    };

    var desc = BoardLayout.NullsLast(rows, r => r.Value, r => r.Name, ascending: false);
    Assert.Equal(new[] { "beryl", "carbon", "apple", "zinc" }, desc.Select(r => r.Name));

    // "No number" is not a small number: the blanks do NOT float to the top when
    // the direction flips, they stay parked - and they keep their own name order.
    var asc = BoardLayout.NullsLast(rows, r => r.Value, r => r.Name, ascending: true);
    Assert.Equal(new[] { "carbon", "beryl", "apple", "zinc" }, asc.Select(r => r.Name));
  }

  [Fact]
  public void NullsLast_TiesBreakOnNameCaseInsensitively()
  {
    var rows = new List<SortRow> { new("Beryl", 7), new("apple", 7) };
    Assert.Equal(new[] { "apple", "Beryl" },
      BoardLayout.NullsLast(rows, r => r.Value, r => r.Name, ascending: false).Select(r => r.Name));
  }
}
