using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The rich detail pane's core (item 9, 08-06). Two contracts are worth pinning:
/// the four score lines say what fed each number and NEVER invent one where the
/// row has no operand for it, and the receipt trail orders asks the same way the
/// On Market tab picks a standing one - a reprice and the ask it superseded can
/// never swap places between two surfaces reading one table.
/// </summary>
public class BoardDetailTests
{
  private static BoardDetail.ScoreOperands Row(
    long? list = null, long? melt = null, long? gc = null, long? vend = null,
    MeltGrade grade = MeltGrade.None,
    ExitDoors? doors = null,
    long ownSale = 0, long communityMedian = 0, int communitySamples = 0, bool fallback = false,
    CommunityFetchState state = CommunityFetchState.NotAsked,
    int? seals = null, SealRate rate = default,
    DesynthSkillupColor? color = null, int yellow = 0, int red = 0,
    long? floorRefusedAsk = null)
    => new(new[] { list, melt, gc, vend }, grade, doors ?? ExitDoors.AllOpen,
      ownSale, communityMedian, communitySamples, fallback, state, seals, rate, color, yellow, red,
      floorRefusedAsk);

  private static string SourceFor(BoardDetail.ScoreOperands o, RoutingExit exit)
    => BoardDetail.Scores(o).First(l => l.Label == BoardCalls.ColumnLabel(exit)).Source;

  private static string ValueFor(BoardDetail.ScoreOperands o, RoutingExit exit)
    => BoardDetail.Scores(o).First(l => l.Label == BoardCalls.ColumnLabel(exit)).Value;

  // ========================================================================
  // The four-score math
  // ========================================================================

  [Fact]
  public void Scores_AreAlwaysFour_InTheStripsOwnOrder()
  {
    // The pane is read against the row it was opened from. Reordering or
    // dropping an exit would make the reader hunt for the cell he just clicked.
    var lines = BoardDetail.Scores(Row());
    Assert.Equal(BoardCalls.Exits.Select(BoardCalls.ColumnLabel), lines.Select(l => l.Label));
  }

  [Fact]
  public void Scores_BorrowTheBoardsOwnTwoGlyphs()
  {
    // A dash is "no evidence yet", a cross is "no such door, ever" - the same
    // two facts the cells wear. A pane that spelled a closed door as a dash
    // would contradict the row two inches above it.
    var closed = Row(doors: new ExitDoors(List: false, Melt: true, Gc: true, Vendor: true));
    Assert.Equal("x", ValueFor(closed, RoutingExit.List));
    Assert.Equal("-", ValueFor(closed, RoutingExit.Desynth));
    Assert.Equal(ExitDoors.ClosedHint(RoutingExit.List), SourceFor(closed, RoutingExit.List));
  }

  [Fact]
  public void Scores_AnEstimatedMeltKeepsItsTellHere_Too()
  {
    // The "~" is the whole reason a band average can't pass for a measurement.
    // It travels with the number onto every surface that draws one.
    var priced = Row(melt: 1_474, grade: MeltGrade.Prior);
    Assert.Equal("~1,474", ValueFor(priced, RoutingExit.Desynth));
    Assert.Contains("nobody has ever melted one", SourceFor(priced, RoutingExit.Desynth));
  }

  [Fact]
  public void Scores_AMeasuredMeltSaysSo()
    => Assert.Equal("Measured: your own desynths of this item.",
      SourceFor(Row(melt: 1_064, grade: MeltGrade.Measured), RoutingExit.Desynth));

  [Fact]
  public void Scores_ASkillupNamesTheColourAndTheKnobItWasPricedAt()
  {
    var line = SourceFor(
      Row(melt: 100_000, grade: MeltGrade.Skillup, color: DesynthSkillupColor.Red,
        yellow: 50_000, red: 100_000),
      RoutingExit.Desynth);
    Assert.Contains("skill-up knob", line);   // the board's own sentence, verbatim
    Assert.Contains("red", line);
    Assert.Contains("100,000", line);
  }

  [Fact]
  public void Scores_TheGcLineShowsTheMULTIPLICATION_AtTheRateItRanAt()
  {
    // The bug class this exists for: a curve-discounted GC score rendering like
    // a full-rate one. The pane shows the seals, the rate, and the discount
    // clause SealRate carries with it - never a bare product.
    var discounted = SealRunway.Effective(baseRate: 25, ventureStock: 2_000, fullBelow: 1_000, zeroAbove: 3_000);
    var line = SourceFor(Row(gc: 275, seals: 22, rate: discounted), RoutingExit.Gc);

    Assert.Contains("22 seals", line);
    Assert.Contains("12.5 gil/seal", line);
    Assert.Contains("2,000 ventures", line);  // SealRate.Narration, carried whole
  }

  [Fact]
  public void Scores_AnUndiscountedGcLineCarriesNoClause()
  {
    var full = SealRunway.Effective(baseRate: 25, ventureStock: 500, fullBelow: 1_000, zeroAbove: 3_000);
    Assert.Equal("22 seals at 25 gil/seal.", SourceFor(Row(gc: 550, seals: 22, rate: full), RoutingExit.Gc));
  }

  [Fact]
  public void Scores_TheListLineNamesWHICHWitnessWasWeighed()
  {
    // Our own sale and the DC's median are different claims about different
    // markets, and the second one has to admit we have never sold one.
    Assert.Contains("Your own last sale", SourceFor(Row(list: 20_803, ownSale: 20_803), RoutingExit.List));

    var community = SourceFor(
      Row(list: 48_997, communityMedian: 48_997, communitySamples: 7, fallback: true),
      RoutingExit.List);
    Assert.Contains("The DC pays", community);
    Assert.Contains("7 settled sales", community);
    Assert.Contains("never sold one", community);
  }

  [Fact]
  public void Scores_SayNothingRatherThanHedge()
  {
    // A number whose witness is not on the row gets NO source line. A hedge
    // ("probably your sale history") would be the pane inventing a pricing
    // explanation, which is the one thing it is not allowed to do.
    Assert.Equal("", SourceFor(Row(list: 9_000), RoutingExit.List));
    Assert.Equal("", SourceFor(Row(gc: 550), RoutingExit.Gc));
  }

  // ========================================================================
  // The blank List cell - four truths, told apart (08-06)
  // ========================================================================

  [Fact]
  public void Scores_ABlankListCell_SaysWhichBlankItIs()
  {
    // The old line covered never-asked, in-flight, answered-with-nothing and
    // too-stale with ONE sentence - the black box Drift called out. Each state
    // now owns a sentence, and no two of them can be mistaken for each other.
    Assert.Contains("has not been asked",
      SourceFor(Row(state: CommunityFetchState.NotAsked), RoutingExit.List));
    Assert.Contains("in flight",
      SourceFor(Row(state: CommunityFetchState.Pending), RoutingExit.List));
    Assert.Contains("failed to answer",
      SourceFor(Row(state: CommunityFetchState.Retrying), RoutingExit.List));
    Assert.Contains("the answer was nothing",
      SourceFor(Row(state: CommunityFetchState.NoTape), RoutingExit.List));
    Assert.Contains("older than your trust window",
      SourceFor(Row(state: CommunityFetchState.Stale), RoutingExit.List));
    Assert.Contains("Refresh to let the evidence in",
      SourceFor(Row(state: CommunityFetchState.HasTape), RoutingExit.List));
    Assert.Contains("Universalis is off",
      SourceFor(Row(state: CommunityFetchState.Unavailable), RoutingExit.List));
  }

  // ---- SF-B1 (2026-08-13): the blank cell's third state ----

  [Fact]
  public void BlankList_WithEvidenceInHand_NamesTheRuleNotTheFetch()
  {
    // THE LABRYS EXHIBIT: the DC's 9 sales at ~7,780 were in the scorer's hand
    // and the rules declined them (a fresh local sale outranks the DC) - but the
    // hint read the fetch state, saw HasTape, and said "Refresh to let the
    // evidence in". Drift pressed Refresh; nothing could ever change. When the
    // median was HELD at scoring, the blank is a rule, and the hint must say so.
    var o = Row(communityMedian: 7_780, communitySamples: 9,
      state: CommunityFetchState.HasTape);
    var hint = SourceFor(o, RoutingExit.List);
    Assert.Contains("7,780", hint);
    Assert.Contains("It didn't set this price", hint);
    Assert.Contains("Refreshing changes nothing", hint);
    Assert.DoesNotContain("Refresh to let the evidence in", hint);
  }

  [Fact]
  public void BlankList_TheFloorRefusedIt_SaysSoAndNeverGuesses()
  {
    // THE ADAMANTITE INGOT (Drift, 08-23: "this text is NOT helpful at all"). The
    // evidence priced the HQ side at 40, the 75 minimum refused it, and the blank
    // answered with two guesses - both wrong. With the refused ask in hand the
    // blank has one cause, and it must agree with the verdict line that quotes it.
    var hint = SourceFor(Row(communityMedian: 39, communitySamples: 3,
      state: CommunityFetchState.HasTape, floorRefusedAsk: 40), RoutingExit.List);
    Assert.Contains("would list at 40/ea", hint);
    Assert.Contains("the floor refuses that ask", hint);
    Assert.Contains("Refreshing changes nothing", hint);
    Assert.DoesNotContain("didn't set this price", hint);
    Assert.DoesNotContain("refuse", hint.Replace("the floor refuses that ask", ""));
  }

  [Fact]
  public void BlankList_TheOldHedge_NowChecksTheOperandItAlwaysHeld()
  {
    // "your own recent sale outranks it, OR this is a row we refuse to price" was
    // two guesses in one breath; the first is checkable against OwnSalePrice.
    var withSale = SourceFor(Row(communityMedian: 7_780, communitySamples: 9,
      ownSale: 8_000, state: CommunityFetchState.HasTape), RoutingExit.List);
    Assert.Contains("your own recent sale outranks it", withSale);
    Assert.DoesNotContain("refuses to price", withSale);

    var noSale = SourceFor(Row(communityMedian: 7_780, communitySamples: 9,
      state: CommunityFetchState.HasTape), RoutingExit.List);
    Assert.Contains("the round refuses to price", noSale);
    Assert.DoesNotContain("outranks", noSale);
  }

  [Fact]
  public void BlankList_WithNoEvidenceHeld_StillForksOnTheFetchState()
  {
    // The fetch-state sentences keep their jobs when the scorer truly held
    // nothing - the third state only claims the blanks it can prove.
    Assert.Contains("Refresh to let the evidence in",
      SourceFor(Row(state: CommunityFetchState.HasTape), RoutingExit.List));
  }

  [Fact]
  public void Scores_TheForkIsListsAlone_OtherBlanksKeepTheOneSentence()
  {
    // A blank Melt cell has nothing to do with Universalis - it keeps the
    // generic line no matter what the DC ask is doing.
    Assert.Equal("No evidence yet - the door is open, evidence could arrive.",
      SourceFor(Row(state: CommunityFetchState.Retrying), RoutingExit.Desynth));
  }

  [Fact]
  public void Scores_AFilledListCell_IgnoresTheFetchState()
  {
    // The fork only explains blanks. A List score with its witness on the row
    // keeps naming the witness - state is not allowed to rewrite evidence.
    Assert.Contains("Your own last sale",
      SourceFor(Row(list: 20_803, ownSale: 20_803, state: CommunityFetchState.Pending), RoutingExit.List));
  }

  // ========================================================================
  // The receipt trail
  // ========================================================================

  private static TrailReceipt R(long id, long at, string state = "open",
    long? price = 1_000, string retainer = "Kif", string? interim = null, string? final = null,
    int? ttc = null, int? queuePosition = null)
    => new(id, at, retainer, price, state, interim, final, ttc, queuePosition);

  [Fact]
  public void Trail_IsNewestFirst_AndBreaksTiesOnId()
  {
    // The supersession tie-break, shared with OnMarket.Standing: two receipts
    // written in the same second are ordered by insert, so a reprice never
    // sorts underneath the ask it replaced.
    var trail = BoardDetail.Trail(new[] { R(1, 100), R(3, 100), R(2, 500) });
    Assert.Equal(new long[] { 2, 3, 1 }, trail.Select(t => t.Id));
  }

  [Fact]
  public void Trail_IsBoundedByCount_NotByAge()
  {
    var many = Enumerable.Range(1, 20).Select(i => R(i, i * 100)).ToList();
    Assert.Equal(3, BoardDetail.Trail(many, keep: 3).Count);
    Assert.Equal(20L, BoardDetail.Trail(many, keep: 3)[0].Id);
    Assert.Empty(BoardDetail.Trail(many, keep: 0));
  }

  [Fact]
  public void TrailLine_EveryOutcomeSpeaksInTheMemoirsGrammar()
  {
    const long now = 1_000_000;
    Assert.Contains("sold in 2d",
      BoardDetail.TrailLine(R(1, now - 86_400, "cleared", ttc: 2), now));
    Assert.Contains("sold in under a day",
      BoardDetail.TrailLine(R(1, now - 86_400, "cleared", ttc: 0), now));
    Assert.Contains("came off the board unsold",
      BoardDetail.TrailLine(R(1, now - 86_400, "never_cleared"), now));
    Assert.Contains("left the board while nobody watched",
      BoardDetail.TrailLine(R(1, now - 86_400, "gone_unobserved"), now));
    Assert.Contains("still standing",
      BoardDetail.TrailLine(R(1, now - 86_400, "open"), now));
  }

  [Fact]
  public void TrailLine_CarriesWhenWhereAndWhatWeAsked()
  {
    const long now = 1_000_000;
    var line = BoardDetail.TrailLine(R(1, now - 7_200, "open", price: 48_997, retainer: "Kif"), now);
    Assert.Equal("2h ago @ Kif - asked 48,997 - still standing", line);
  }

  [Fact]
  public void TrailLine_AReceiptWithNoAskSaysThatInsteadOfPrintingAZero()
    => Assert.Contains("no price on record",
      BoardDetail.TrailLine(R(1, 0, "open", price: null, retainer: ""), 0));

  [Fact]
  public void TrailLine_ALookNeverSpeaksAskGrammar()
  {
    // The Neo-Ishgardian Sword (08-23): "just now @ Elwyn - asked 20,000 - still
    // standing" over a sword still in the bags. An un-adopted recon receipt is a
    // Look - the decision is real evidence, the ask never existed.
    const long now = 1_000_000;
    var look = new TrailReceipt(1, now - 240, "Elwyn", 20_000, "open",
      null, null, null, null, IsLook: true);
    var line = BoardDetail.TrailLine(look, now);
    Assert.Equal("4m ago @ Elwyn - your Look priced it at 20,000 - nothing listed", line);
    Assert.DoesNotContain("asked", line);
    Assert.DoesNotContain("still standing", line);

    var held = new TrailReceipt(2, now - 240, "Elwyn", null, "open",
      null, null, null, null, IsLook: true);
    Assert.Contains("your Look held it - nothing listed", BoardDetail.TrailLine(held, now));
  }

  [Fact]
  public void Stamp_DrawsOnlyWhatTheGraderActuallyWrote()
  {
    // NULL is unambiguous by construction - SILENCE is a stamp - so an empty
    // stamp means "never graded" and nothing else. No placeholder, ever.
    Assert.Equal("", BoardDetail.Stamp(R(1, 0)));
    Assert.Equal("  [CUT_OFF]", BoardDetail.Stamp(R(1, 0, interim: "CUT_OFF")));
    Assert.Equal("  [CHASED]", BoardDetail.Stamp(R(1, 0, final: "CHASED")));
    Assert.Equal("  [PASS / CLEARED]", BoardDetail.Stamp(R(1, 0, interim: "PASS", final: "CLEARED")));
  }

  [Fact]
  public void Stamp_NamesTheSeatTheVerdictIsAbout()
  {
    // The verdict scores a DECISION - which spot in the line we took - so
    // "CHASED" alone is half a sentence. queue_position counts the foreign rows
    // AHEAD of us, and the house's seat grammar is 1-based (LanePricing.SeatOf,
    // the run log, the census), so three rows ahead is seat 4. Off-by-one here
    // would put the trail a seat away from every other surface saying the word.
    Assert.Equal("  [CHASED from seat 4]",
      BoardDetail.Stamp(R(1, 0, final: "CHASED", queuePosition: 3)));
    Assert.Equal("  [PASS / CLEARED from seat 1]",
      BoardDetail.Stamp(R(1, 0, interim: "PASS", final: "CLEARED", queuePosition: 0)));
  }

  [Fact]
  public void Stamp_AHeldReceiptHasNoSeatToName()
  {
    // Null queue_position means we never listed. A seat invented for it would be
    // a claim about a spot that never existed.
    Assert.Equal("  [SILENCE]", BoardDetail.Stamp(R(1, 0, interim: "SILENCE")));
  }

  [Fact]
  public void Stamp_LegacyGradesPassThroughVerbatimAndWearNoSeat()
  {
    // Pre-reframe stamps scored the PRICE, not the spot. They stay exactly as
    // they were written - history reads as what it was - and hanging a seat off
    // "UNDERSOLD" would dress an old verdict in a frame it was never made under.
    Assert.Equal("  [MISS]", BoardDetail.Stamp(R(1, 0, interim: "MISS", queuePosition: 3)));
    Assert.Equal("  [ON_TRACK / UNDERSOLD]",
      BoardDetail.Stamp(R(1, 0, interim: "ON_TRACK", final: "UNDERSOLD", queuePosition: 3)));
    // A legacy interim beside a current final still earns the seat: the seat
    // belongs to the receipt, and the current verdict is about it.
    Assert.Equal("  [MISS / CHASED from seat 4]",
      BoardDetail.Stamp(R(1, 0, interim: "MISS", final: "CHASED", queuePosition: 3)));
  }

  [Fact]
  public void TrailCaveat_RefusesTheScoreboardReading()
  {
    // The survivorship caveat, in the new frame. A clean trail must not be read
    // as "optimal" - that reading is structurally wrong, and the surface is where
    // it has to be refused. The full argument lives once, on ReceiptGrading.
    var caveat = BoardDetail.TrailCaveat();
    Assert.Contains("PASS / CLEARED", caveat);
    Assert.Contains("CHASED", caveat);
    Assert.Contains("never that they were the best seats", caveat);
    // The counterweight is a measurement, not a stamp - and it is not drawn here.
    Assert.Contains("measured in gil on the receipt, not shown here", caveat);
  }
}
