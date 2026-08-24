using System;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// MOVEMENT 2 - THE STATE-FIRST BOARD (ruled 2026-08-13). Drift's diagnosis: the
/// hinge is awkward because you have to work out "the state of the state" before
/// you can rule - <i>is this thing already listed?</i> - and the pane answered
/// that last. These pin the sentence that now answers it first.
///
/// <para>The two specimens from the brief are the fixtures everything else is
/// held against:</para>
/// <code>
/// Standing at Elwyn for 17,787, listed today. You last sold one in April for 13,338.
/// The DC pays ~7,780 lately (9 sales).
/// In your bags - from tonight's melt.
/// </code>
/// </summary>
public class BoardStateLineTests
{
  private static readonly DateTimeOffset Now = new(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);

  private static BoardStateLine.StateFacts Listed(
    string retainer = "Elwyn", long? ask = 17_787, DateTimeOffset? listedAt = null,
    long? lastSale = null, DateTimeOffset? lastSaleAt = null,
    long dc = 0, int samples = 0)
    => new(Standing: true, Retainer: retainer, Ask: ask, ListedAt: listedAt ?? Now,
           LastSalePrice: lastSale, LastSaleAt: lastSaleAt,
           CommunityMedian: dc, CommunitySamples: samples);

  private static BoardStateLine.StateFacts InBags(
    bool fromMelt = false, long? lastSale = null, DateTimeOffset? lastSaleAt = null,
    long dc = 0, int samples = 0)
    => new(Standing: false, FromTonightsMelt: fromMelt,
           LastSalePrice: lastSale, LastSaleAt: lastSaleAt,
           CommunityMedian: dc, CommunitySamples: samples);

  // ==========================================================================
  // The two specimens, verbatim
  // ==========================================================================

  [Fact]
  public void TheStandingSpecimen_ReadsExactlyAsRuled()
  {
    var line = BoardStateLine.Compose(
      Listed(lastSale: 13_338, lastSaleAt: new DateTimeOffset(2026, 4, 9, 0, 0, 0, TimeSpan.Zero),
             dc: 7_780, samples: 9),
      Now);

    Assert.Equal("Standing at Elwyn for 17,787, listed today. You last sold one in April for 13,338.",
      line.Where);
    Assert.Equal("The DC pays ~7,780 lately (9 sales).", line.Market);
  }

  [Fact]
  public void TheBagSpecimen_ReadsExactlyAsRuled()
  {
    var line = BoardStateLine.Compose(InBags(fromMelt: true), Now);
    Assert.Equal("In your bags - from tonight's melt.", line.Where);
    Assert.Equal("", line.Market);
  }

  // ==========================================================================
  // Standing vs in-bags: the question the pane opens on
  // ==========================================================================

  [Fact]
  public void AStandingRowSaysWhereItStands_ABagRowSaysItIsInTheBags()
  {
    Assert.StartsWith("Standing at Elwyn", BoardStateLine.Compose(Listed(), Now).Where);
    Assert.StartsWith("In your bags", BoardStateLine.Compose(InBags(), Now).Where);
  }

  [Fact]
  public void ABagRowWithNoProvenance_ClaimsNone()
  {
    // Every road into the bags but tonight's melt is unrecorded, and an
    // unrecorded road gets no sentence.
    Assert.Equal("In your bags.", BoardStateLine.Compose(InBags(), Now).Where);
  }

  // ==========================================================================
  // Every fact present or absent - a gap drops its clause, never hedges
  // ==========================================================================

  [Fact]
  public void NoAskOnRecord_DropsThePriceAndKeepsTheRest()
  {
    var line = BoardStateLine.Compose(Listed(ask: null), Now);
    Assert.Equal("Standing at Elwyn, listed today.", line.Where);
  }

  [Fact]
  public void AZeroAskIsNotAnAsk()
  {
    Assert.DoesNotContain(" for ", BoardStateLine.Compose(Listed(ask: 0), Now).Where);
  }

  [Fact]
  public void NoListingStamp_SaysNothingAboutAge()
  {
    var line = BoardStateLine.Compose(
      new BoardStateLine.StateFacts(Standing: true, Retainer: "Elwyn", Ask: 17_787), Now);
    Assert.Equal("Standing at Elwyn for 17,787.", line.Where);
  }

  [Fact]
  public void NoRetainerNamed_StillAnswersWhereItIs()
  {
    var line = BoardStateLine.Compose(Listed(retainer: ""), Now);
    Assert.Equal("Standing on the board for 17,787, listed today.", line.Where);
  }

  [Fact]
  public void NeverSoldOne_SaysNothingAboutYourSales()
  {
    var line = BoardStateLine.Compose(Listed(), Now);
    Assert.DoesNotContain("You last sold", line.Where);
  }

  [Fact]
  public void SoldOneButNoDateOnIt_KeepsThePriceAndDropsTheDate()
  {
    var line = BoardStateLine.Compose(Listed(lastSale: 13_338), Now);
    Assert.EndsWith("You last sold one for 13,338.", line.Where);
  }

  [Fact]
  public void NoDcAnswer_LeavesTheMarketLineEmpty()
  {
    Assert.Equal("", BoardStateLine.Compose(Listed(), Now).Market);
  }

  [Fact]
  public void ADcMedianWithoutItsSampleCount_OmitsTheCountRatherThanPrintingZero()
  {
    Assert.Equal("The DC pays ~7,780 lately.",
      BoardStateLine.Compose(Listed(dc: 7_780), Now).Market);
  }

  [Fact]
  public void OneDcSale_IsNotPluralised()
  {
    Assert.Equal("The DC pays ~7,780 lately (1 sale).",
      BoardStateLine.Compose(Listed(dc: 7_780, samples: 1), Now).Market);
  }

  [Fact]
  public void ABagRowCarriesTheSameTwoWitnesses()
  {
    // The state fork is about WHERE it is; your book and the DC's are the same
    // evidence either way, and a pane that dropped them for bag rows would be
    // two panes wearing one name.
    var line = BoardStateLine.Compose(
      InBags(lastSale: 900, lastSaleAt: Now, dc: 1_200, samples: 4), Now);
    Assert.Equal("In your bags. You last sold one today for 900.", line.Where);
    Assert.Equal("The DC pays ~1,200 lately (4 sales).", line.Market);
  }

  // ==========================================================================
  // The listing's age, in days and in plain words
  // ==========================================================================

  [Fact]
  public void ListedAgeSpeaksInDays()
  {
    Assert.Equal("listed today", BoardStateLine.ListedWhen(Now.AddHours(-9), Now));
    Assert.Equal("listed yesterday", BoardStateLine.ListedWhen(Now.AddDays(-1), Now));
    Assert.Equal("listed 4 days ago", BoardStateLine.ListedWhen(Now.AddDays(-4), Now));
  }

  [Fact]
  public void AStampFromTheFutureReadsAsToday_NotAsNegativeAge()
  {
    Assert.Equal("listed today", BoardStateLine.ListedWhen(Now.AddDays(3), Now));
  }

  // ==========================================================================
  // The sale's date, on the calendar
  // ==========================================================================

  [Fact]
  public void ASaleIsRememberedAsAMonth()
  {
    Assert.Equal("in April",
      BoardStateLine.SoldWhen(new DateTimeOffset(2026, 4, 9, 0, 0, 0, TimeSpan.Zero), Now));
  }

  [Fact]
  public void ASaleThisMonthIsNotDressedAsAMonthAgo()
  {
    Assert.Equal("earlier this month",
      BoardStateLine.SoldWhen(new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero), Now));
    Assert.Equal("today", BoardStateLine.SoldWhen(Now.AddHours(-2), Now));
  }

  [Fact]
  public void PastTwelveMonths_TheYearHasToBeSaid()
  {
    // "in April" a year and a half on would be a claim about the wrong April.
    Assert.Equal("in April 2025",
      BoardStateLine.SoldWhen(new DateTimeOffset(2025, 4, 9, 0, 0, 0, TimeSpan.Zero), Now));
    Assert.Equal("in September",
      BoardStateLine.SoldWhen(new DateTimeOffset(2025, 9, 9, 0, 0, 0, TimeSpan.Zero), Now));
  }

  [Fact]
  public void LastYearsSameMonthNeverWearsThisMonthsName()
  {
    // Sold last August, read this August: inside the twelve-month window, but a
    // bare "in August" would read as THIS one. The year has to be said.
    Assert.Equal("in August 2025",
      BoardStateLine.SoldWhen(new DateTimeOffset(2025, 8, 20, 0, 0, 0, TimeSpan.Zero), Now));
  }

  [Fact]
  public void ASaleStampedInTheFutureReadsAsToday()
  {
    Assert.Equal("today", BoardStateLine.SoldWhen(Now.AddDays(5), Now));
  }

  // ==========================================================================
  // The voice
  // ==========================================================================

  [Fact]
  public void TheStateLineSpeaksToThePlayer_NotAboutHim()
  {
    var line = BoardStateLine.Compose(
      InBags(fromMelt: true, lastSale: 13_338, lastSaleAt: Now.AddDays(-40)), Now);
    Assert.Contains("your bags", line.Where);
    Assert.Contains("You last sold", line.Where);
  }

  [Fact]
  public void TheOptionsAreFramedAsMovementsFromTheState()
  {
    // Not a menu of what this item could be in the abstract - the moves from
    // where the line above just said it is.
    Assert.Equal("Your moves from here", BoardStateLine.MovesHeader);
  }

  [Fact]
  public void TheEmptyPaneInvitesTheStateFirst_NotTheExits()
  {
    Assert.StartsWith("Pick a row to see where it stands", BoardStateLine.NoSelection);
    Assert.Contains("already listed", BoardStateLine.NoSelection);
  }

  [Fact]
  public void MoneyIsSpeltTheHouseWay()
  {
    // One formatter for gil across the whole voice - RunLogVoice.Gil.
    Assert.Contains(RunLogVoice.Gil(17_787), BoardStateLine.Compose(Listed(), Now).Where);
  }

  // ==========================================================================
  // The ask, in the List column (Movement 4)
  // ==========================================================================

  [Fact]
  public void AskCell_AStandingRowShowsWhatItIsAsking_NotThePreview()
  {
    Assert.Equal(17_787, BoardAskCell.Number(ask: 17_787, proposal: 13_338));
    Assert.True(BoardAskCell.ShowsAsk(17_787));
  }

  [Fact]
  public void AskCell_ABagRowKeepsThePreviewItAlwaysDrew()
  {
    Assert.Equal(13_338, BoardAskCell.Number(ask: null, proposal: 13_338));
    Assert.False(BoardAskCell.ShowsAsk(null));
    Assert.Null(BoardAskCell.Number(ask: null, proposal: null));
  }

  [Fact]
  public void AskCell_ANonPositiveAskIsNoAskAtAll()
  {
    // A lane that banked nothing must not draw a zero in the money column.
    Assert.Equal(13_338, BoardAskCell.Number(ask: 0, proposal: 13_338));
    Assert.False(BoardAskCell.ShowsAsk(0));
    Assert.Null(BoardAskCell.Number(ask: 0, proposal: null));
  }

  [Fact]
  public void AskCell_TheHoverLeadsWithTheAskAndCarriesTheMoveBehindIt()
  {
    var hint = BoardAskCell.Hint(17_787, 13_338);

    Assert.Equal("You're asking 17,787 for this one.\nReprice to 13,338 - the round writes it.", hint);
  }

  [Fact]
  public void AskCell_APreviewThatLandsOnTheAskIsNamedAsANoOp()
  {
    // "Reprice to 17,787" against an ask of 17,787 reads as a move and is not one.
    Assert.Equal("You're asking 17,787 for this one.\nThe reprice lands on the same number - nothing to write.",
      BoardAskCell.Hint(17_787, 17_787));
  }

  [Fact]
  public void AskCell_NoPreviewIsAReportOfTheReadThatKept_NotAForecast()
  {
    // Ruled 08-16: "no reprice number tonight" read as a prediction about the rest
    // of the round; the truth is an attempt already made. "The last read", not
    // "the pinch" - a skipped pinch leaves an older read holding this answer.
    Assert.Equal("You're asking 17,787 for this one.\n"
      + "The last read tried this lane and held your ask - the floors refuse a new number, or the lane's answer was to keep it.",
      BoardAskCell.Hint(17_787, null));
  }
}
