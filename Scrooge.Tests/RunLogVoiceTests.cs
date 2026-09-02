using Xunit;
using Scrooge.Windows;

namespace Scrooge.Tests;

/// <summary>
/// MOVEMENT 1 - THE VOICE (ruled 2026-08-13). "State first, options second, verbs
/// named, tense honest, evidence on hover."
///
/// <para>The anchor Drift wrote is the fixture these tests are held against:
/// <i>"Repricing from 680 to 441. You are 3rd in line. First 2 are low balls at
/// 150"</i>. Everything below either pins one of the five rules or pins the
/// specimen line that rule came out of.</para>
/// </summary>
public class RunLogVoiceTests
{
  /// <summary>The player's own minimum as an effective floor - the shape the fold's
  /// tests read (the one floor law, 2026-08-21).</summary>
  private static EffectiveFloor MinimumFloor(int minimum)
    => PriceFloor.Effective(PriceFloorMode.None, vendorPrice: 0, minimumListingPrice: minimum);

  /// <summary>A census with the seats and counts filled in - the board, already counted.</summary>
  private static LaneCensus Census(
    int sellers = 0, int seat = 0, int priorSeat = 0, int competitors = 0,
    long? floor = null, long? ceiling = null, int aboveCeiling = 0,
    double? bandLow = null, double? bandHigh = null, int sales = 0, double? perDay = null)
    => new(sellers, seat, priorSeat, competitors, floor, ceiling, aboveCeiling,
           bandLow, bandHigh, sales, perDay);

  // ==========================================================================
  // Rule 1: the verb prefix names the act
  // ==========================================================================

  [Fact]
  public void EveryLine_OpensWithTheVerbThatNamesTheAct()
  {
    Assert.StartsWith("Reprice: ", RunLogVoice.Priced(680, 441, Census(), 0, null, null, null).Line);
    Assert.StartsWith("List: ", RunLogVoice.Priced(null, 441, Census(), 0, null, null, null).Line);
    Assert.StartsWith("Skip: ", RunLogVoice.Skip(75, "Below your minimum.").Line);
    Assert.StartsWith("Recon: ", RunLogVoice.Recon(13_989, Census(), "", null).Line);
    Assert.StartsWith("Vendor: ", RunLogVoice.Vendor(14, 7_000, "The line sits under what the vendor pays.").Line);
  }

  [Fact]
  public void AFirstListingIsAList_AChangedAskIsAReprice()
  {
    // The verb follows the ACT, not the caller: nothing was repriced when nothing
    // was standing there.
    Assert.StartsWith("List: ", RunLogVoice.Priced(0, 900, Census(), 0, null, null, null).Line);
    Assert.StartsWith("Reprice: ", RunLogVoice.Priced(1_000, 900, Census(), 0, null, null, null).Line);
  }

  // ==========================================================================
  // Rule 2: tense is honest
  // ==========================================================================

  [Fact]
  public void GilThatMoved_SpeaksInThePast()
  {
    Assert.Equal("Reprice: From 680 to 441.", RunLogVoice.Priced(680, 441, Census(), 0, null, null, null).Line);
    // A kept ask is its own verb (Drift, 08-15): "Reprice: Kept at X" primed a move
    // and took it back. Held is past where recon's "Hold." is present - the tense
    // rule telling the reader whether anyone acted.
    Assert.Equal("Held: 999 stands.", RunLogVoice.Priced(999, 999, Census(), 0, null, null, null).Line);
    Assert.Equal("List: Posted at 17,787.", RunLogVoice.Priced(null, 17_787, Census(), 0, null, null, null).Line);
    Assert.Equal("Skip: Left at 75. No legal ask - honest price 44/ea sits under your 75 minimum. "
      + "List sits out; the other exits compete.",
      RunLogVoice.Skip(75, RunLogVoice.Reasons.BelowFloor(MinimumFloor(75), 44)).Line);
  }

  [Fact]
  public void ADecisionNobodyActedOn_SpeaksAsAVerdict()
  {
    // "Reprice happened so should speak in those terms. Recon is what could happen."
    var priced = RunLogVoice.Recon(13_989, Census(sellers: 34, seat: 1), "", null).Line;
    Assert.Equal("Recon: Would ask 13,989. You'd be 1st in line - 34 sellers behind you.", priced);

    var held = RunLogVoice.Recon(null, Census(), RunLogVoice.Reasons.NothingWorthStandingBehind, null).Line;
    Assert.Equal("Recon: Hold. The board makes no sense right now - nothing worth standing behind.", held);
  }

  [Fact]
  public void ReconNeverClaimsTheActHappened()
  {
    var line = RunLogVoice.Recon(500, Census(sellers: 3, seat: 2), "", null).Line;
    Assert.Contains("Would ask", line);
    Assert.Contains("You'd be", line);
    Assert.DoesNotContain("Posted", line);
    Assert.DoesNotContain("You are", line);
  }

  // ==========================================================================
  // Rule 3: the destination gets an ordinal, the prior position never does
  // ==========================================================================

  [Fact]
  public void Ordinal_HandlesTheTeensAndTheTens()
  {
    Assert.Equal("1st", RunLogVoice.Ordinal(1));
    Assert.Equal("2nd", RunLogVoice.Ordinal(2));
    Assert.Equal("3rd", RunLogVoice.Ordinal(3));
    Assert.Equal("4th", RunLogVoice.Ordinal(4));
    Assert.Equal("11th", RunLogVoice.Ordinal(11));
    Assert.Equal("12th", RunLogVoice.Ordinal(12));
    Assert.Equal("13th", RunLogVoice.Ordinal(13));
    Assert.Equal("21st", RunLogVoice.Ordinal(21));
    Assert.Equal("112th", RunLogVoice.Ordinal(112));
    Assert.Equal("", RunLogVoice.Ordinal(0));
  }

  [Fact]
  public void ThePriorSeatIsNeverNumbered()
  {
    // A number on the old seat reads as a fact about where you WERE, which is the
    // one thing the reprice just made untrue.
    var line = RunLogVoice.Priced(680, 441, Census(sellers: 53, seat: 3, priorSeat: 54), 0, null, null, null).Line;
    Assert.Contains("from the back of the line to 3rd", line);
    Assert.DoesNotContain("54th", line);
    Assert.DoesNotContain("54", line);
  }

  [Fact]
  public void BehindNamesTheBackOfTheLine_OnlyWhenNobodyIsBehindYou()
  {
    Assert.Equal("the back of the line", RunLogVoice.Behind(21, 20));
    Assert.Equal("further back", RunLogVoice.Behind(9, 20));
    Assert.Null(RunLogVoice.Behind(0, 20));
  }

  [Fact]
  public void AnUnlistedItemSaysWhereItLanded_NotWhereItCameFrom()
  {
    var line = RunLogVoice.Priced(null, 900, Census(sellers: 4, seat: 2), 0, null, null, null).Line;
    Assert.Equal("List: Posted at 900, 2nd in line.", line);
  }

  [Fact]
  public void AnAskThatDidNotMoveKeepsItsSeat()
  {
    var line = RunLogVoice.Priced(999, 999, Census(sellers: 8, seat: 4, priorSeat: 4), 0, null, null, null).Line;
    Assert.Equal("Held: 999 stands, still 4th in line.", line);
  }

  [Fact]
  public void TheRuledHeldSpecimen_ReadsExactlyAsRuled()
  {
    // Drift, 08-15, verbatim: "Held: 16,750 stands, still 1st in line."
    var line = RunLogVoice.Priced(16_750, 16_750, Census(sellers: 12, seat: 1, priorSeat: 1), 0, null, null, null).Line;
    Assert.Equal("Held: 16,750 stands, still 1st in line.", line);
  }

  [Fact]
  public void AnAskThatWentUpSaysItDropped()
  {
    // The move clause reports the direction honestly, including the unflattering one.
    var line = RunLogVoice.Priced(100, 900, Census(sellers: 8, seat: 6, priorSeat: 2), 0, null, null, null).Line;
    Assert.Equal("Reprice: From 100 to 900, dropping you to 6th in line.", line);
  }

  [Fact]
  public void NoSeat_MeansNothingIsSaidAboutTheLine()
  {
    // Position unknown is not the same statement as "front of the line", and must
    // never render as one.
    var line = RunLogVoice.Priced(680, 441, Census(sellers: 12), 0, null, null, null).Line;
    Assert.Equal("Reprice: From 680 to 441.", line);
    Assert.DoesNotContain("in line", line);
  }

  // ==========================================================================
  // Rule 4: plain words - and rule 5: second person
  // ==========================================================================

  [Fact]
  public void TheFlagshipSpecimen_ComesOutVerbatim()
  {
    var line = RunLogVoice.Priced(
      680, 441,
      Census(sellers: 53, seat: 3, priorSeat: 54, competitors: 32, floor: 442, ceiling: 1_500,
             aboveCeiling: 6, bandLow: 825, bandHigh: 2_895, perDay: 6),
      lowBalls: 2, lowBallFloor: 150, lowBallCeiling: 150, evidence: null);

    Assert.Equal(
      "Reprice: From 680 to 441, moving you from the back of the line to 3rd. Front 2 are low balls at 150.",
      line.Line);
  }

  [Fact]
  public void NoInternalVocabularyEverReachesTheLine()
  {
    var line = RunLogVoice.Priced(
      680, 441, Census(sellers: 53, seat: 3, priorSeat: 54, aboveCeiling: 6),
      lowBalls: 2, lowBallFloor: 150, lowBallCeiling: 150, evidence: null).Line;

    foreach (var jargon in new[] { "crasher", "bait", "dreamer", "lane", "anchor", "cluster", "[", "]" })
      Assert.DoesNotContain(jargon, line, System.StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void OneLowBallIsNotPluralised()
  {
    var line = RunLogVoice.Priced(680, 441, Census(seat: 2, priorSeat: 3, sellers: 2),
      lowBalls: 1, lowBallFloor: 150, lowBallCeiling: 150, evidence: null).Line;
    Assert.Contains("The one in front is a low ball at 150.", line);
  }

  [Fact]
  public void LowBallsThatDisagreeGetASpan_NotOneNumberStandingForTwo()
  {
    var line = RunLogVoice.Priced(680, 441, Census(seat: 4, priorSeat: 9, sellers: 8),
      lowBalls: 3, lowBallFloor: 3, lowBallCeiling: 150, evidence: null).Line;
    Assert.Contains("Front 3 are low balls from 3 to 150.", line);
  }

  // ==========================================================================
  // Rule 6: evidence on hover - and nothing is deleted
  // ==========================================================================

  [Fact]
  public void TheHoverCarriesTheCensus_InSamsOrder()
  {
    var voice = RunLogVoice.Priced(
      680, 441,
      Census(sellers: 53, seat: 3, priorSeat: 54, competitors: 32, floor: 442, ceiling: 1_500,
             aboveCeiling: 6, bandLow: 825, bandHigh: 2_895, perDay: 6),
      lowBalls: 2, lowBallFloor: 150, lowBallCeiling: 150, evidence: null);

    // V19: the multiple names its base. "6 above the 3x ceiling" was 3x of nothing -
    // the one fact in the census with no operand behind it. The 3x here is the
    // fixture's untouched default rendering, not a literal in the voice.
    Assert.Equal("53 sellers; 32 from 442-1,500; 6 asking past 3x the going rate; sells 825-2,895; ~6/day",
      voice.Hover);
  }

  [Fact]
  public void Census_TheCeilingMultipleIsQuotedLive()
  {
    // Ruled 08-22: the knob is a 1.5-10 slider, so the sentence reads the census's
    // own operand - drag it to 2.5 and the line says 2.5x, never a fossilized 3x.
    var voice = RunLogVoice.Priced(
      680, 441,
      Census(sellers: 10, aboveCeiling: 6) with { CeilingMult = 2.5 },
      lowBalls: 0, lowBallFloor: null, lowBallCeiling: null, evidence: null);
    Assert.Contains("6 asking past 2.5x the going rate", voice.Hover);
  }

  [Fact]
  public void TheOldFullGrammarProseReLayersUnderTheCensus()
  {
    // Nothing was deleted when the line got short - it moved.
    var voice = RunLogVoice.Priced(680, 441, Census(sellers: 53, seat: 3, priorSeat: 54),
      0, null, null, "undercut the cheapest cluster on the board - 3 sellers from 442 to 460. Sells 825-2,895, 41 sales, they disagree.");

    Assert.StartsWith("53 sellers", voice.Hover);
    Assert.Contains("\n", voice.Hover);
    Assert.EndsWith("41 sales, they disagree.", voice.Hover);
  }

  [Fact]
  public void NoCensusAndNoProse_MeansNoHoverAtAll()
  {
    Assert.Equal("", RunLogVoice.Priced(680, 441, Census(), 0, null, null, null).Hover);
    Assert.Equal("", RunLogVoice.Priced(680, 441, Census(), 0, null, null, "   ").Hover);
    Assert.Equal("", RunLogVoice.Vendor(14, 7_000, "The line sits under what the vendor pays.").Hover);
  }

  [Fact]
  public void ProseWithNoCensusStandsAlone()
  {
    var voice = RunLogVoice.Skip(75, RunLogVoice.Reasons.BelowFloor(MinimumFloor(75), 44), Census(),
      "nothing on the board and no sales in window, need 3 to judge the board.");
    Assert.StartsWith("nothing on the board", voice.Hover);
    Assert.DoesNotContain("\n", voice.Hover);
  }

  [Fact]
  public void ZeroSellersCensusesNothing()
  {
    // An empty board has no census to show, so the hover carries only the prose.
    var voice = RunLogVoice.Priced(null, 1_995, Census(seat: 1, bandLow: 900, bandHigh: 1_995),
      0, null, null, null);
    Assert.Equal("sells 900-1,995", voice.Hover);
    Assert.Equal("List: Posted at 1,995, 1st in line.", voice.Line);
  }

  // ==========================================================================
  // The rest of the verb family
  // ==========================================================================

  [Fact]
  public void TheCappedClimbSaysWhereItIsHeadedAndWhyItIsNotThereYet()
  {
    // V20: the line states the BEHAVIOUR of this price - it moved one step and is
    // still climbing. "Big jumps are capped" taught the rule at the row; how big a
    // step, and whether the brake is armed, are the cap knob's tooltip's business.
    var line = RunLogVoice.RepriceCapped(197, 591, 1_698, Census(), null).Line;
    Assert.Equal("Reprice: From 197 to 591, climbing toward 1,698 - capped, one step per pinch.",
      line);
  }

  [Fact]
  public void TheCappedClimbClaimsNoSeat_BecauseItLandedWhereTheBrakePutIt()
  {
    var line = RunLogVoice.RepriceCapped(197, 591, 1_698, Census(sellers: 9, seat: 2, priorSeat: 9), null).Line;
    Assert.DoesNotContain("in line", line);
  }

  [Fact]
  public void ASkipWithNothingStanding_SaysSo()
  {
    // Ruled 08-16 stop 4: the comparand is the price the round wanted to write, and
    // "The vendor pays more" against a shown ask read as the vendor beating it. The one
    // floor law (08-21) keeps that discipline and adds the binding floor by name.
    Assert.Equal("Skip: Nothing posted. No legal ask - honest price 900/ea sits under "
      + "the vendor's 1,159. List sits out; the other exits compete.",
      RunLogVoice.Skip(null, RunLogVoice.Reasons.BelowFloor(
        PriceFloor.Effective(PriceFloorMode.Vendor, 1_159, 0), 900)).Line);
    Assert.Equal("Skip: Nothing posted. No legal ask - honest price 900/ea sits under "
      + "the Enclave's 2,318. List sits out; the other exits compete.",
      RunLogVoice.Skip(0, RunLogVoice.Reasons.BelowFloor(
        PriceFloor.Effective(PriceFloorMode.DomanEnclave, 1_159, 0), 900)).Line);
  }

  [Fact]
  public void TheVendorLineSaysWhatLeftAndWhatItFetched()
  {
    Assert.Equal("Vendor: Sold 14 for 7,000. The line sits under what the vendor pays.",
      RunLogVoice.Vendor(14, 7_000, RunLogVoice.Reasons.BelowWhatTheBoardPays).Line);
  }

  [Fact]
  public void ThePostedFromReconLineNamesItsProvenance()
  {
    Assert.Equal("List: Posted at 17,787, 1st in line. Priced from your Look, 23 minutes old.",
      RunLogVoice.Posted(17_787, seat: 1, RunLogVoice.Reasons.FromTheLook(RunLogVoice.Age(23 * 60))).Line);
  }

  // ==========================================================================
  // SF-P6: the pre-filled panel is not a prior ask (live shake, 2026-08-15)
  // ==========================================================================

  /// <summary>
  /// WHAT THE SHAKE HEARD: "Reprice: From 7 to 395, dropping you to 2nd" about an item
  /// that had never been on the board. The 7 was RetainerSell's PRE-FILLED suggestion,
  /// adopted as a standing ask on a hawk run, and off that one phantom operand the
  /// sentence grew a verb, a transition and a move - none of which happened. Gate the
  /// operand and the composer already knows the right verb; nothing here changed.
  /// </summary>
  [Fact]
  public void AHawkPostingFromTheBags_ListsRatherThanRepricingFromAPhantom()
  {
    var census = Census(sellers: 4, seat: 2, priorSeat: 1);

    Assert.Equal("Reprice: From 7 to 395, dropping you to 2nd in line.",
      RunLogVoice.Priced(ListingAccounting.StandingAsk(7, listingFromBags: false), 395,
        census, 0, null, null, null).Line);

    Assert.Equal("List: Posted at 395, 2nd in line.",
      RunLogVoice.Priced(ListingAccounting.StandingAsk(7, listingFromBags: true), 395,
        census, 0, null, null, null).Line);
  }

  /// <summary>
  /// The pinch is untouched, because there the pre-fill IS the item's real ask and the
  /// move it names is a move that happened. Drift's anchor line, composed through the gate.
  /// </summary>
  [Fact]
  public void APinchStillRepricesFromTheAskThePanelShows()
  {
    Assert.Equal("Reprice: From 680 to 441, moving you from the back of the line to 3rd.",
      RunLogVoice.Priced(ListingAccounting.StandingAsk(680, listingFromBags: false), 441,
        Census(sellers: 53, seat: 3, priorSeat: 54), 0, null, null, null).Line);
  }

  /// <summary>
  /// The skip lines carry the same operand and caught the same disease: "Left at 7"
  /// says an ask is sitting on the board right now. A hawk skip has nothing sitting
  /// anywhere, and the composer's other branch has always said exactly that.
  /// </summary>
  [Fact]
  public void AHawkSkipNeverLeavesTheItemAtAPriceItNeverHad()
  {
    Assert.StartsWith("Skip: Nothing posted.",
      RunLogVoice.Skip(ListingAccounting.StandingAsk(7, listingFromBags: true),
        RunLogVoice.Reasons.BelowFloor(MinimumFloor(75), 44)).Line);

    Assert.StartsWith("Skip: Left at 7.",
      RunLogVoice.Skip(ListingAccounting.StandingAsk(7, listingFromBags: false),
        RunLogVoice.Reasons.BelowFloor(MinimumFloor(75), 44)).Line);
  }

  [Fact]
  public void ReasonsGetExactlyOneTrailingPeriod()
  {
    Assert.EndsWith("minimum.", RunLogVoice.Skip(75, "Below your minimum").Line);
    Assert.EndsWith("minimum.", RunLogVoice.Skip(75, "Below your minimum.").Line);
    Assert.DoesNotContain("..", RunLogVoice.Skip(75, "Below your minimum.").Line);
  }

  [Fact]
  public void AgeSpeaksInWholeWords_AndKnowsItsSingulars()
  {
    Assert.Equal("just now", RunLogVoice.Age(0));
    Assert.Equal("just now", RunLogVoice.Age(59));
    Assert.Equal("1 minute old", RunLogVoice.Age(60));
    Assert.Equal("23 minutes old", RunLogVoice.Age(23 * 60));
    Assert.Equal("1 hour old", RunLogVoice.Age(3600));
    Assert.Equal("2 days old", RunLogVoice.Age(172800));
  }
}

/// <summary>
/// The seats the voice speaks are the WALK'S OWN - counted off the board it already
/// had in hand at decision time, never re-derived by a narrator that could disagree
/// with the price. These pin the census against real Decide walks.
/// </summary>
public class LaneCensusTests
{
  [Fact]
  public void TheSeatIsTheAnchorsSeat_BecauseWeLandInFrontOfIt()
  {
    // Two low balls stepped over, then the cheapest real cluster: we take 3rd.
    var board = L.Board(150, 150, 442, 460, 1_500);
    var d = LanePricing.Decide(board, L.Banded(825, 1_400, 2_895, 41), 6, L.Cfg(), currentPrice: 680);

    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(3, d.Census.Seat);
    Assert.Equal(5, d.Census.Sellers);
  }

  [Fact]
  public void ThePriorSeatCountsTheRowsThatWereAlreadyAheadOfUs()
  {
    var board = L.Board(150, 200, 442, 460);
    // Standing at 500: every foreign row is cheaper, so we were the back of the line.
    var d = LanePricing.Decide(board, L.Banded(825, 1_400, 2_895, 41), null, L.Cfg(), currentPrice: 500);
    Assert.Equal(5, d.Census.PriorSeat);
    Assert.Equal(4, d.Census.Sellers);
    Assert.Equal("the back of the line", RunLogVoice.Behind(d.Census.PriorSeat, d.Census.Sellers));
  }

  [Fact]
  public void AnItemNotOnTheBoardHasNoPriorSeat()
  {
    var d = LanePricing.Decide(L.Board(442, 460), L.Banded(825, 1_400, 2_895, 41), null, L.Cfg());
    Assert.Equal(0, d.Census.PriorSeat);
    Assert.Null(RunLogVoice.Behind(d.Census.PriorSeat, d.Census.Sellers));
  }

  /// <summary>
  /// SF-P6 at the walk, one step before the sentence: the prior seat is derived from
  /// the price we hand Decide, so a pre-fill adopted on a hawk run does not just
  /// mis-word a line - it manufactures a position in the queue. Gated, the walk counts
  /// no prior seat, which is the truth about an item that was in the bags.
  /// </summary>
  [Fact]
  public void AGatedPreFillNeverReachesTheWalk_SoAHawkItemHasNoPriorSeat()
  {
    var board = L.Board(150, 442, 460, 1_500);
    var lane = L.Banded(825, 1_400, 2_895, 41);

    var hawk = LanePricing.Decide(board, lane, 6, L.Cfg(),
      currentPrice: ListingAccounting.StandingAsk(7, listingFromBags: true));
    Assert.Equal(0, hawk.Census.PriorSeat);
    Assert.Null(RunLogVoice.Behind(hawk.Census.PriorSeat, hawk.Census.Sellers));

    // The same 7 on a pinch is a real ask at the front of the line, and stays one.
    var pinch = LanePricing.Decide(board, lane, 6, L.Cfg(),
      currentPrice: ListingAccounting.StandingAsk(7, listingFromBags: false));
    Assert.Equal(1, pinch.Census.PriorSeat);
  }

  [Fact]
  public void AHoldTakesNoSeatAtAll()
  {
    // Genuine silence: nothing on the board and nothing on the tape.
    var d = LanePricing.Decide(L.Board(), null, null, L.Cfg(), currentPrice: 900);
    Assert.Equal(LaneOutcome.HeldThinHistory, d.Outcome);
    Assert.Equal(0, d.Census.Seat);
    Assert.Equal(0, d.Census.Sellers);
  }

  [Fact]
  public void RowsAboveTheCeilingAreCountedEvenWhenTheWalkNeverReachesThem()
  {
    // 3x of a 1,000 going rate is 3,000: the two 55M dreams are unreachable.
    var d = LanePricing.Decide(L.Board(1_200, 1_300, 55_000_000, 55_000_001),
      L.Banded(900, 1_000, 1_100, 12), null, L.Cfg(), currentPrice: 2_000);

    Assert.Equal(2, d.Census.AboveCeiling);
    Assert.Equal(4, d.Census.Sellers);
    Assert.Equal(1, d.Census.Seat);
    // The competitor span is the reachable line from our seat up, never the dreams.
    Assert.Equal(2, d.Census.Competitors);
    Assert.Equal(1_200, d.Census.CompetitorFloor);
    Assert.Equal(1_300, d.Census.CompetitorCeiling);
  }

  [Fact]
  public void AnEmptyBoardListingIsFirstInLine()
  {
    var d = LanePricing.Decide(L.Board(), L.Banded(900, 1_400, 1_995, 12), null, L.Cfg());
    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(1, d.Census.Seat);
  }

  [Fact]
  public void AnAbsolutePriceTakesTheSeatItBuys_NotAutomaticallyTheFront()
  {
    // Every row on the board was a lone lowball, so the tape prices it - but the
    // stepped-over rows are still standing, and the band top sits behind them.
    var d = LanePricing.Decide(L.Board(3), L.Banded(900, 1_400, 1_995, 12), null, L.Cfg());
    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(2, d.Census.Seat);
  }

  [Fact]
  public void TheBandAndThePaceRideTheCensus_SoTheHoverNeedsNoSecondSource()
  {
    var d = LanePricing.Decide(L.Board(442, 460), L.Banded(825, 1_400, 2_895, 41), 6.0, L.Cfg());
    Assert.Equal(825, d.Census.BandLow);
    Assert.Equal(2_895, d.Census.BandHigh);
    Assert.Equal(41, d.Census.SaleCount);
    Assert.Equal(6.0, d.Census.PerDay);
  }

  [Fact]
  public void TheCommunityClampDropsTheSeat_BecauseItWasCountedForAPriceThatIsGone()
  {
    var d = LanePricing.Decide(L.Board(), L.Banded(1_800, 1_900, 2_500, 12, LaneSource.Community), null, L.Cfg());
    Assert.Equal(1, d.Census.Seat);

    var clamped = LanePricing.ClampToLocalClearing(d, LaneSource.Community, 1_550);
    Assert.Equal(1_550, clamped.Anchor);
    Assert.Equal(0, clamped.Census.Seat);
  }
}

/// <summary>
/// SF-P7 - THE HOVER'S PROSE NITS (Drift's live shake, 2026-08-15). Four sentences
/// that each claimed a hair more than their operands could stand behind: a count
/// of one taking the plural verb, a span whose two edges were the same number, an
/// "undercut" written over a price that only ever matched, and a "from" opening a
/// range of exactly one price. None of them changed a decision; all four told the
/// reader something that was not so, which is the whole thing the evidence layer
/// exists not to do. Verbatim specimens, because the finding was verbatim.
/// </summary>
public class HoverProseAgreementTests
{
  private static LaneCensus Census(
    int sellers = 0, int seat = 0, int priorSeat = 0, int competitors = 0,
    long? floor = null, long? ceiling = null, int aboveCeiling = 0,
    double? bandLow = null, double? bandHigh = null, int sales = 0, double? perDay = null)
    => new(sellers, seat, priorSeat, competitors, floor, ceiling, aboveCeiling,
           bandLow, bandHigh, sales, perDay);

  private static readonly LaneModel Lane = L.Banded(900, 1_000, 1_100, 12); // 3x ceiling = 3,000

  // ==========================================================================
  // 1. One listing SITS
  // ==========================================================================

  [Fact]
  public void OneListingAboveTheCeiling_Sits()
  {
    var d = LanePricing.Decide(L.Board(900, 5_000), Lane, null, L.Cfg());
    Assert.Contains("1 listing at 5,000+ sits above the 3x ceiling and cannot be cut in front of", d.Evidence);
  }

  [Fact]
  public void TwoListingsAboveTheCeiling_StillSit()
  {
    // The plural was never wrong - only the singular borrowed its verb.
    var d = LanePricing.Decide(L.Board(900, 5_000, 6_000), Lane, null, L.Cfg());
    Assert.Contains("2 listings at 5,000+ sit above the 3x ceiling and cannot be cut in front of", d.Evidence);
  }

  // ==========================================================================
  // 2. A span whose edges agree is not a span
  // ==========================================================================

  [Fact]
  public void TheCensusBandCollapsesToOnePrice_WhenItsEdgesAgree()
  {
    // "sells 200-200" was the shake's own specimen: one sale draws its band on
    // top of itself, and the census already owns "at X" for a collapsed pair.
    var voice = RunLogVoice.Priced(null, 200, Census(seat: 1, bandLow: 200, bandHigh: 200),
      0, null, null, null);
    Assert.Equal("sells at 200", voice.Hover);
  }

  [Fact]
  public void TheCensusBandStaysASpan_WhenItsEdgesDisagree()
  {
    var voice = RunLogVoice.Priced(null, 1_995, Census(seat: 1, bandLow: 900, bandHigh: 1_995),
      0, null, null, null);
    Assert.Equal("sells 900-1,995", voice.Hover);
  }

  [Fact]
  public void TheBandLineCollapsesToOnePrice_WhenTheTapeAgreesWithItself()
  {
    // The agreement clause drops with the collapse (ruled 08-15): a
    // zero-width band has no disagreement to characterize, and "they agree" there
    // would report an agreement among witnesses that barely exist.
    var d = LanePricing.Decide(L.Board(), L.Banded(200, 200, 200, 3), null, L.Cfg());
    Assert.Contains("Sells at 200, 3 sales.", d.Evidence);
    Assert.DoesNotContain("they agree", d.Evidence);
    Assert.DoesNotContain("they disagree", d.Evidence);
  }

  [Fact]
  public void TheClusterCollapsesToOnePrice_WhenEverySellerAsksTheSame()
  {
    // "2 sellers from 444 to 444" - two numbers printed to say one number.
    var d = LanePricing.Decide(L.Board(444, 444, 900), L.Banded(400, 500, 600, 12), null, L.Cfg());
    Assert.Contains("undercut the cheapest cluster on the board — 2 sellers at 444", d.Evidence);
  }

  [Fact]
  public void TheClusterStaysASpan_WhenTheSellersDisagree()
  {
    var d = LanePricing.Decide(L.Board(442, 460), L.Banded(825, 1_400, 2_895, 41), 6.0, L.Cfg());
    Assert.Contains("undercut the cheapest cluster on the board — 2 sellers from 442 to 460", d.Evidence);
  }

  // ==========================================================================
  // 3. Standing level with the cheapest is a MATCH
  // ==========================================================================

  private const string TheWalksProse =
    "undercut the cheapest competitor (444). Sells 400-600, 12 sales, they agree.";

  private static LaneCensus At444 =>
    Census(sellers: 3, seat: 1, priorSeat: 1, competitors: 3, floor: 444, ceiling: 900);

  [Fact]
  public void AnAskLevelWithTheCheapestCompetitor_MatchedIt_AndSaysSo()
  {
    // The shake's row: Held at 444 with the cheapest competitor at 444, hovering
    // "undercut the cheapest competitor (444)". Gentleman's Match copies the
    // price exactly, and a copy is not a cut.
    var voice = RunLogVoice.Priced(444, 444, At444, 0, null, null, TheWalksProse);
    Assert.Equal("Held: 444 stands, still 1st in line.", voice.Line);
    Assert.EndsWith("matched the cheapest competitor (444). Sells 400-600, 12 sales, they agree.", voice.Hover);
    Assert.DoesNotContain("undercut", voice.Hover);
  }

  [Fact]
  public void AnAskStrictlyBelowTheCheapestCompetitor_ReallyDidUndercutIt()
  {
    var voice = RunLogVoice.Priced(680, 443, At444, 0, null, null, TheWalksProse);
    Assert.EndsWith("undercut the cheapest competitor (444). Sells 400-600, 12 sales, they agree.", voice.Hover);
  }

  [Fact]
  public void ReconsWouldAsk_IsHeldToTheSameVerb()
  {
    // Recon writes nothing, but the ask it names is the one that would go on the
    // board - so the verb has to be true of THAT number.
    var voice = RunLogVoice.Recon(444, At444, "", TheWalksProse);
    Assert.Contains("matched the cheapest competitor (444)", voice.Hover);
    Assert.Contains("undercut the cheapest competitor (444)",
      RunLogVoice.Recon(443, At444, "", TheWalksProse).Hover);
  }

  // ==========================================================================
  // 4. One price is "at", a range is "from"
  // ==========================================================================

  [Fact]
  public void OneSteppedOverLowball_IsAtItsPrice()
  {
    var d = LanePricing.Decide(L.Board(110, 900, 950), Lane, null, L.Cfg());
    Assert.Contains("stepped over 1 lone lowball at 110 with nobody near it;", d.Evidence);
  }

  [Fact]
  public void APileOfSteppedOverRows_StillOpensARangeWithFrom()
  {
    // The seat rail's pile: five cheap rows really are a range starting at 100.
    var d = LanePricing.Decide(
      L.Board(100, 101, 102, 103, 104, 900, 910, 920, 930, 940, 950), Lane, null, L.Cfg());
    Assert.Contains("the crashers have friends — 5 cheap rows from 100,", d.Evidence);
  }
}

/// <summary>
/// The evidence layer has to survive a reload: a transcript that keeps its sentences
/// and loses its receipts is a transcript nobody can check anything against.
/// </summary>
public class RunLogHoverBankingTests
{
  [Fact]
  public void TheHoverRoundTripsThroughTheDetailColumn()
  {
    var entry = new LogEntry(ItemOutcome.CrazySkipped, "Bender", "Tea Brick HQ",
      "Reprice: From 680 to 441, moving you from the back of the line to 3rd.")
      { Hover = "53 sellers; 6 above the 3x ceiling\nundercut the cheapest competitors." };

    var flat = RoundLogEntry.Flatten(entry, "Pinch");
    var restored = Assert.IsType<LogEntry>(RoundLogEntry.Restore(flat!.Value));

    Assert.Equal(entry.Message, restored.Message);
    Assert.Equal(entry.Hover, restored.Hover);
    Assert.Equal(ItemOutcome.CrazySkipped, restored.Outcome);
  }

  [Fact]
  public void ARowWithNoEvidenceBanksNoSeparator()
  {
    var flat = RoundLogEntry.Flatten(
      new LogEntry(ItemOutcome.VendorSold, "Bender", "Tea Brick", "Vendor: Sold 14 for 7,000."), "Bell");
    Assert.DoesNotContain(RoundLogEntry.HoverSeparator, flat!.Value.Detail);
  }

  [Fact]
  public void ARowBankedBeforeTheEvidenceLayerExistedRestoresWithoutOne()
  {
    var legacy = new RoundLogLine(1, 1_800_000_000, "Pinch", RoundLogKind.Entry,
      nameof(ItemOutcome.EmptyBoard), "Tea Brick", "Tea Brick: listed at 1,995 [empty board] - an empty board.");
    var restored = Assert.IsType<LogEntry>(RoundLogEntry.Restore(legacy));

    Assert.Null(restored.Hover);
    Assert.EndsWith("an empty board.", restored.Message);
  }

  [Fact]
  public void EverythingPastTheFirstSeparatorIsEvidence()
  {
    var (message, hover) = RoundLogEntry.Unpack(
      RoundLogEntry.Pack("Reprice: From 680 to 441.", "53 sellers\nsells 825-2,895"));
    Assert.Equal("Reprice: From 680 to 441.", message);
    Assert.Equal("53 sellers\nsells 825-2,895", hover);
  }

  // ---- The one floor law's voice, and the crasher-guard's question ----

  /// <summary>The player's own minimum as an effective floor - the shape the fold's
  /// tests read (the one floor law, 2026-08-21).</summary>
  private static EffectiveFloor MinimumFloor(int minimum)
    => PriceFloor.Effective(PriceFloorMode.None, vendorPrice: 0, minimumListingPrice: minimum);

  [Fact]
  public void TheFloorClause_NamesTheRuleThatActuallyBound()
  {
    Assert.Equal("your 75 minimum", RunLogVoice.Reasons.FloorClause(MinimumFloor(75)));
    Assert.Equal("the vendor's 1,159",
      RunLogVoice.Reasons.FloorClause(PriceFloor.Effective(PriceFloorMode.Vendor, 1_159, 0)));
    Assert.Equal("the Enclave's 2,318",
      RunLogVoice.Reasons.FloorClause(PriceFloor.Effective(PriceFloorMode.DomanEnclave, 1_159, 0)));
    // The higher of the two rules binds, and it is the one the sentence names.
    Assert.Equal("your 5,000 minimum",
      RunLogVoice.Reasons.FloorClause(PriceFloor.Effective(PriceFloorMode.Vendor, 1_159, 5_000)));
  }

  [Fact]
  public void TheFloorClause_NoneBorrowsNobodyElsesWording()
  {
    // THE PIN THAT KEEPS IT UNFOLDED (the mechanical pile, 3b). A floor that bound
    // nothing has no number worth naming and must never speak another binding's
    // sentence. Unreachable today - the verdicts this feeds only fire when
    // EffectiveFloor.Refuses did, which needs a floor above zero - and that is exactly
    // why nothing but a test would notice it folding.
    var none = PriceFloor.Effective(PriceFloorMode.Vendor, vendorPrice: null, minimumListingPrice: 0);
    Assert.Equal(FloorBinding.None, none.Binding);
    Assert.Equal("the floor", RunLogVoice.Reasons.FloorClause(none));
    Assert.DoesNotContain("vendor", RunLogVoice.Reasons.FloorClause(none));
    Assert.DoesNotContain("Enclave", RunLogVoice.Reasons.FloorClause(none));
  }

  [Fact]
  public void TheFloorVerdictSaysListSitsOut_NotThatTheItemWasPricedUp()
  {
    var line = RunLogVoice.Reasons.BelowFloor(MinimumFloor(75), 44);
    Assert.Contains("No legal ask", line);
    Assert.Contains("44/ea", line);
    Assert.Contains("List sits out; the other exits compete.", line);
    // Nothing here may suggest the ask was raised to reach the floor.
    Assert.DoesNotContain("75/ea", line);
  }

  // The crasher-guard voice tests died with the guard (3.1 sweep).

  [Fact]
  public void TheVendorFallbackNamesTheFloorItSoldUnder()
  {
    Assert.Equal("The ask sits under your 75 minimum.",
      RunLogVoice.Reasons.SoldUnderFloor(MinimumFloor(75)));
  }

  // ========================================================================
  // The held roll-up reads its own rows (V12, ruled B7, built 3b-7)
  // ========================================================================

  [Fact]
  public void HeldRollup_SplitsByTheReasonTheRowsThemselvesGave()
  {
    // The old line asserted "(not enough sales)" over a class PricingVoice already
    // splits per row - a summary overwriting the thing it counts. The split is derived
    // from the held rows' own spoken lines, so the two cannot disagree.
    var lines = new[]
    {
      RunLogVoice.Skip(500, RunLogVoice.Reasons.TooFewSales).Line,
      RunLogVoice.Skip(500, RunLogVoice.Reasons.TooFewSales).Line,
      RunLogVoice.Skip(null, RunLogVoice.Reasons.BoardSilent).Line,
    };
    Assert.Equal("3 held - 2 thin tape, 1 board silent", RunLogVoice.HeldRollup(lines));
  }

  [Fact]
  public void HeldRollup_GoesReasonNeutralWhenTheClassIsUniform()
  {
    // One reason across the whole class is a repetition, not a split - "3 held - 3 thin
    // tape" teaches nobody anything, so the fallback is the bare count.
    var thin = new[]
    {
      RunLogVoice.Skip(500, RunLogVoice.Reasons.TooFewSales).Line,
      RunLogVoice.Skip(500, RunLogVoice.Reasons.TooFewSales).Line,
    };
    Assert.Equal("2 held", RunLogVoice.HeldRollup(thin));

    var silent = new[] { RunLogVoice.Skip(null, RunLogVoice.Reasons.BoardSilent).Line };
    Assert.Equal("1 held", RunLogVoice.HeldRollup(silent));
  }

  [Fact]
  public void HeldRollup_NeverAssertsTheReasonTheRowsDidNotGive()
  {
    // The whole defect in one assertion: a run whose only held row says the board never
    // answered must not be summarized as "not enough sales".
    var silent = new[] { RunLogVoice.Skip(null, RunLogVoice.Reasons.BoardSilent).Line };
    Assert.DoesNotContain("sales", RunLogVoice.HeldRollup(silent));
  }

  // ==========================================================================
  // The melt run's reachability line (the Rattan Sofa defect, 2026-08-29):
  // three routed-Melt items sat invisible under the desynthesis window's
  // category filter and the run reported plain success, round after round.
  // The summary must name the gap when the pile outnumbers the window.
  // ==========================================================================

  [Fact]
  public void MeltUnreachable_SilentWhenTheWindowShowsTheWholePile()
  {
    Assert.Null(RunLogVoice.MeltUnreachable(routed: 16, reachable: 16));
    Assert.Null(RunLogVoice.MeltUnreachable(routed: 0, reachable: 0));
    // The window showing MORE than the pile (hand-added selections) hides nothing.
    Assert.Null(RunLogVoice.MeltUnreachable(routed: 3, reachable: 5));
  }

  [Fact]
  public void MeltUnreachable_NamesTheMissingCountAndTheBagsOnlyReach()
  {
    // Re-worded with the decision walk (08-30): the walk covers every bag
    // category now, so a pile item this line fires for is in NO bag - retainer
    // stock is the known case, and "cycle the filter" would be wrong advice.
    var line = RunLogVoice.MeltUnreachable(routed: 16, reachable: 13);
    Assert.NotNull(line);
    Assert.Contains("3 of the 16", line);
    Assert.Contains("bags", line);
    // The cure rides the sentence: pull retainer-held stock to the bags.
    Assert.Contains("retainer", line);
    Assert.DoesNotContain("filter", line);
  }

  [Fact]
  public void MeltUnreachable_SpeaksSingularForOneMissingItem()
  {
    var line = RunLogVoice.MeltUnreachable(routed: 4, reachable: 3);
    Assert.NotNull(line);
    Assert.Contains("1 of the 4", line);
    Assert.DoesNotContain("items aren't", line);
  }

  // ==========================================================================
  // Phase B (2026-08-30): the run cycles the filter ITSELF and picks up the
  // hidden pile items. The pickup line states the decision and its operands -
  // which category, how many - per the dark-mode rule.
  // ==========================================================================

  [Fact]
  public void MeltWalkPickup_NamesTheCategoryAndTheCount()
  {
    var line = RunLogVoice.MeltWalkPickup(3, "Housing");
    Assert.NotNull(line);
    Assert.Contains("Housing", line);
    Assert.Contains("3 more", line);
    Assert.Contains("melt pile", line);
  }

  [Fact]
  public void MeltWalkPickup_SpeaksSingularForOneItem()
  {
    var line = RunLogVoice.MeltWalkPickup(1, "Housing");
    Assert.NotNull(line);
    Assert.Contains("1 more", line);
    Assert.Contains("item", line);
    Assert.DoesNotContain("items", line);
  }

  [Fact]
  public void MeltWalkPickup_SilentWhenTheCategoryHoldsNothing()
  {
    // A walk that finds nothing says nothing - the residual MeltUnreachable
    // line at run end is the honest report for a pile still out of reach.
    Assert.Null(RunLogVoice.MeltWalkPickup(0, "Housing"));
  }
}
