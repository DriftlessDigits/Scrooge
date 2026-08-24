using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The board mirror (08-22): badge geometry reconstructed from the walk's own
/// banked counts, and the two headers that carry each table's age. The badges are
/// receipts - so these tests pin the partition arithmetic (prefix crashers,
/// suffix dreamers, own rows seated but uncounted) and the honesty guard that
/// refuses to badge a board the census was not taken against.
/// </summary>
public class BoardMirrorTests
{
  private const long Now = 1_000_000_000;

  private static MarketEvents.BoardListing Row(long price, bool own = false, bool hq = false,
    string retainer = "Foreign", int qty = 1)
    => new(retainer, qty, hq, price, own);

  private static SaleHistorySchema.BankedSale Sale(long price, long saleTime, bool hq = false,
    string buyer = "Buyer")
    => new(price, 1, hq, saleTime, buyer);

  /// <summary>
  /// A decision whose banked counts describe a walk over n foreign rows. Since the
  /// geometry guard (docket #2, 08-23) a step must bank its span and a listing
  /// anchor its row - a decision that banked neither only clears the guard when it
  /// stepped nothing and priced no live row.
  /// </summary>
  private static LaneDecision Decision(int sellers, int skipped, int dreamers,
    long? crasherFloor = null, long? crasherCeiling = null, long? anchor = null)
    => new()
    {
      Outcome = LaneOutcome.Undercut,
      CraziesSkipped = skipped,
      CrasherFloor = crasherFloor,
      CrasherCeiling = crasherCeiling,
      Anchor = anchor,
      AnchorIsListing = anchor is not null,
      Census = new LaneCensus(
        Sellers: sellers, Seat: 1, PriorSeat: 0, Competitors: sellers - skipped - dreamers,
        CompetitorFloor: null, CompetitorCeiling: null, AboveCeiling: dreamers,
        BandLow: null, BandHigh: null, SaleCount: 5, PerDay: null, Median: null),
      Evidence = "test walk",
    };

  [Fact]
  public void Badges_PartitionTheQueue_PrefixCrashers_SuffixDreamers()
  {
    var board = new[]
    {
      Row(100), Row(120), Row(5_000), Row(5_200), Row(30_000), Row(31_000),
    };
    var m = BoardMirror.Compose(board, Now - 7_200, [], itemIsHq: false,
      Decision(sellers: 6, skipped: 2, dreamers: 2,
        crasherFloor: 100, crasherCeiling: 120, anchor: 5_000), Now);

    Assert.Equal(
      new[]
      {
        BoardMirror.MirrorCall.Crasher, BoardMirror.MirrorCall.Crasher,
        BoardMirror.MirrorCall.Competitor, BoardMirror.MirrorCall.Competitor,
        BoardMirror.MirrorCall.Dreamer, BoardMirror.MirrorCall.Dreamer,
      },
      m.Rows.Select(r => r.Call).ToArray());
    Assert.Equal("Board as of 2h ago - 6 rows", m.BoardHeader);
  }

  [Fact]
  public void OwnRow_KeepsItsSeatInTheLine_ButNeverCountsAgainstTheCensus()
  {
    var board = new[]
    {
      Row(100), Row(120), Row(5_000),
      Row(5_100, own: true, retainer: "Kif"),
      Row(5_200), Row(30_000), Row(31_000),
    };
    // The census counted 6 FOREIGN rows; the own row rides between the
    // competitors without shifting anyone's badge.
    var m = BoardMirror.Compose(board, Now - 3_600, [], itemIsHq: false,
      Decision(sellers: 6, skipped: 2, dreamers: 2,
        crasherFloor: 100, crasherCeiling: 120, anchor: 5_000), Now);

    Assert.Equal(BoardMirror.MirrorCall.Own, m.Rows[3].Call);
    Assert.Equal("Kif", m.Rows[3].Retainer);
    Assert.Equal(BoardMirror.MirrorCall.Competitor, m.Rows[2].Call);
    Assert.Equal(BoardMirror.MirrorCall.Competitor, m.Rows[4].Call);
    Assert.Equal(BoardMirror.MirrorCall.Dreamer, m.Rows[5].Call);
  }

  [Fact]
  public void CensusMismatch_RefusesEveryBadge()
  {
    // The walk saw 4 foreign rows; the snapshot now holds 3 - the board moved.
    // A guessed badge is worse than no badge.
    var board = new[] { Row(100), Row(5_000), Row(30_000) };
    var m = BoardMirror.Compose(board, Now - 60, [], itemIsHq: false,
      Decision(sellers: 4, skipped: 1, dreamers: 1), Now);

    Assert.All(m.Rows, r => Assert.Equal(BoardMirror.MirrorCall.Unjudged, r.Call));
  }

  [Fact]
  public void GeometryMismatch_RefusesEveryBadge_TheDyeBoard()
  {
    // THE PASTEL GREEN DYE RECEIPT (docket #2, 08-23): the afternoon walk banked
    // 8 sellers, cluster from 440 - the evening board still held 8 foreign rows,
    // but shuffled, with a 2-gil seat at the front. The headcount collided and
    // the 2-gil row inherited "competitor" from a walk that never saw it. The
    // banked anchor row (440) no longer fronts the queue - every badge refuses.
    var board = new[]
    {
      Row(2), Row(440), Row(460), Row(475), Row(480), Row(490), Row(500), Row(3_000),
    };
    var m = BoardMirror.Compose(board, Now - 60, [], itemIsHq: false,
      Decision(sellers: 8, skipped: 0, dreamers: 1, anchor: 440), Now);

    Assert.All(m.Rows, r => Assert.Equal(BoardMirror.MirrorCall.Unjudged, r.Call));
  }

  [Fact]
  public void GeometryMatch_TheSameBoard_StillBadges()
  {
    // The control case: identical headcount AND the anchor row where the walk
    // left it - the replay is legitimate and the badges speak.
    var board = new[]
    {
      Row(440), Row(460), Row(475), Row(480), Row(490), Row(500), Row(510), Row(3_000),
    };
    var m = BoardMirror.Compose(board, Now - 60, [], itemIsHq: false,
      Decision(sellers: 8, skipped: 0, dreamers: 1, anchor: 440), Now);

    Assert.Equal(BoardMirror.MirrorCall.Competitor, m.Rows[0].Call);
    Assert.Equal(BoardMirror.MirrorCall.Dreamer, m.Rows[7].Call);
  }

  [Fact]
  public void AStepWithNoBankedSpan_CannotProveItself_FailsClosed()
  {
    // 21 of the book's 105 step receipts banked no geometry (the 08-23 audit) -
    // the lone-lowball class. A step that cannot show its span gets no badge.
    var board = new[] { Row(100), Row(120), Row(5_000) };
    var m = BoardMirror.Compose(board, Now - 60, [], itemIsHq: false,
      Decision(sellers: 3, skipped: 2, dreamers: 0), Now);

    Assert.All(m.Rows, r => Assert.Equal(BoardMirror.MirrorCall.Unjudged, r.Call));
  }

  [Fact]
  public void NoDecision_ReadsUnjudged_NeverGuesses()
  {
    var m = BoardMirror.Compose([Row(100), Row(200)], Now - 60, [], false, null, Now);
    Assert.All(m.Rows, r => Assert.Equal(BoardMirror.MirrorCall.Unjudged, r.Call));
  }

  [Fact]
  public void HqItem_NqRowsAreOffLane_AndTheHqPartitionIgnoresThem()
  {
    // HQ item: the judged line is HQ-only (A12). Two NQ rows sit on the board
    // cheaper than everything - off-lane, not crashers.
    var board = new[]
    {
      Row(50), Row(60),
      Row(5_000, hq: true), Row(5_200, hq: true), Row(30_000, hq: true),
    };
    var m = BoardMirror.Compose(board, Now - 60, [], itemIsHq: true,
      Decision(sellers: 3, skipped: 0, dreamers: 1), Now);

    Assert.Equal(BoardMirror.MirrorCall.OffLane, m.Rows[0].Call);
    Assert.Equal(BoardMirror.MirrorCall.OffLane, m.Rows[1].Call);
    Assert.Equal(BoardMirror.MirrorCall.Competitor, m.Rows[2].Call);
    Assert.Equal(BoardMirror.MirrorCall.Competitor, m.Rows[3].Call);
    Assert.Equal(BoardMirror.MirrorCall.Dreamer, m.Rows[4].Call);
  }

  [Fact]
  public void NqItem_HqRowAtTheSameMoney_StandsAheadInTheLine()
  {
    // Decide's tie-break: at equal money the better quality is genuinely ahead.
    var board = new[] { Row(5_000), Row(5_000, hq: true) };
    var m = BoardMirror.Compose(board, Now - 60, [], itemIsHq: false, null, Now);
    Assert.True(m.Rows[0].IsHq);
    Assert.False(m.Rows[1].IsHq);
  }

  [Fact]
  public void TheCap_ShowsTwentyAndSaysWhatItDropped()
  {
    var board = Enumerable.Range(1, 25).Select(i => Row(i * 1_000L)).ToArray();
    var m = BoardMirror.Compose(board, Now - 60, [], false, null, Now);
    Assert.Equal(20, m.Rows.Count);
    Assert.Equal("Board as of 1m ago - 25 rows, showing the first 20", m.BoardHeader);
  }

  [Fact]
  public void EmptyBoard_NamesBothPossibleSilences()
  {
    var m = BoardMirror.Compose([], 0, [], false, null, Now);
    Assert.Equal(
      "No board rows banked - the last scan saw an empty board, or this board has never been read.",
      m.BoardHeader);
    Assert.Empty(m.Rows);
  }

  [Fact]
  public void TheTape_RidesNewestFirst_AndSaysWhatItHolds()
  {
    var sales = new[]
    {
      Sale(9_000, Now - 200_000, buyer: "Old Buyer"),
      Sale(10_000, Now - 3_600, buyer: "Fresh Buyer"),
    };
    var m = BoardMirror.Compose([], 0, sales, false, null, Now);
    Assert.Equal("Fresh Buyer", m.Sales[0].Buyer);
    Assert.Equal("1h ago", m.Sales[0].WhenText);
    Assert.Equal("2d ago", m.Sales[1].WhenText);
    Assert.Equal("Sale tape - 2 sales banked", m.TapeHeader);
  }

  [Fact]
  public void TheTapeCap_SaysTheRingIsDeeper()
  {
    var sales = Enumerable.Range(1, 30).Select(i => Sale(1_000, Now - i * 1_000L)).ToArray();
    var m = BoardMirror.Compose([], 0, sales, false, null, Now);
    Assert.Equal(20, m.Sales.Count);
    Assert.Equal("Sale tape - newest 20 of 30 banked", m.TapeHeader);
  }

  [Fact]
  public void EmptyTape_SaysSo()
  {
    var m = BoardMirror.Compose([], 0, [], false, null, Now);
    Assert.Equal("No sales banked - the tape has never spoken for this item.", m.TapeHeader);
  }

  [Fact]
  public void CallWords_ArePinned()
  {
    Assert.Equal("yours", BoardMirror.CallWord(BoardMirror.MirrorCall.Own));
    Assert.Equal("crasher", BoardMirror.CallWord(BoardMirror.MirrorCall.Crasher));
    Assert.Equal("competitor", BoardMirror.CallWord(BoardMirror.MirrorCall.Competitor));
    Assert.Equal("dreamer", BoardMirror.CallWord(BoardMirror.MirrorCall.Dreamer));
    Assert.Equal("off-lane", BoardMirror.CallWord(BoardMirror.MirrorCall.OffLane));
    Assert.Equal("", BoardMirror.CallWord(BoardMirror.MirrorCall.Unjudged));
  }
}
