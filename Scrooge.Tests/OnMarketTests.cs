using System.Collections.Generic;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// Tests for the On Market tab's pure core (ruled ledger, stage 3): which banked
/// receipts speak for a standing ask, the relative-time grammar, and the queue
/// story a receipt's V35 spans earn. Everything here is banked data - no game
/// reads, no fetches, and nothing in this file can cause one.
/// </summary>
public class OnMarketTests
{
  private const long Now = 1_000_000;

  private static ReceiptLine R(
    long id = 1, long createdAt = Now, uint item = 100, bool hq = false,
    string name = "Thing", string retainer = "Karen", int qty = 1,
    long? price = 1000, int? queuePos = 0, string state = "open", string? interim = null,
    int cluster = 0, long? crashFloor = null, long? crashCeil = null,
    long? lineFloor = null, long? lineCeil = null, int? crowd = null)
    => new(id, createdAt, item, hq, name, retainer, qty, price, queuePos, state, interim,
      cluster, crashFloor, crashCeil, lineFloor, lineCeil, crowd);

  // ---- Standing: open, newest per lane ----

  [Fact]
  public void Standing_KeepsOnlyOpenReceipts()
  {
    var standing = OnMarket.Standing(new[]
    {
      R(id: 1, item: 100, state: "open"),
      R(id: 2, item: 200, state: "cleared"),
      R(id: 3, item: 300, state: "never_cleared"),
      // The pinch reconciler's close (08-02): the lane was absent from a full
      // sell-list read. The ghost must leave the tab - this exact shape once
      // drew 14d-old dashes as "currently listed".
      R(id: 4, item: 400, state: "gone_unobserved"),
    });

    Assert.Single(standing);
    Assert.Equal(100u, standing[0].ItemId);
  }

  [Fact]
  public void Standing_NewestReceiptSpeaksForTheLane()
  {
    // A reprice writes a new receipt without closing the old one. One listing,
    // not two - counting both would inflate the tab.
    var standing = OnMarket.Standing(new[]
    {
      R(id: 1, createdAt: Now - 7200, price: 5000),
      R(id: 2, createdAt: Now - 60, price: 4800),
    });

    Assert.Single(standing);
    Assert.Equal(4800, standing[0].DecidedPrice);
  }

  [Fact]
  public void Standing_TiedTimestampsBreakOnId()
  {
    var standing = OnMarket.Standing(new[]
    {
      R(id: 7, createdAt: Now, price: 100),
      R(id: 8, createdAt: Now, price: 200),
    });

    Assert.Single(standing);
    Assert.Equal(200, standing[0].DecidedPrice);
  }

  [Fact]
  public void Standing_SeparatesLanesByQualityAndRetainer()
  {
    var standing = OnMarket.Standing(new[]
    {
      R(id: 1, item: 100, hq: false, retainer: "Karen"),
      R(id: 2, item: 100, hq: true, retainer: "Karen"),
      R(id: 3, item: 100, hq: false, retainer: "Dave"),
    });

    Assert.Equal(3, standing.Count);
  }

  [Fact]
  public void Standing_OrdersNewestFirst()
  {
    var standing = OnMarket.Standing(new[]
    {
      R(id: 1, item: 100, createdAt: Now - 86400),
      R(id: 2, item: 200, createdAt: Now - 60),
    });

    Assert.Equal(200u, standing[0].ItemId);
    Assert.Equal(100u, standing[1].ItemId);
  }

  // ---- Relative time ----

  [Theory]
  [InlineData(0, "just now")]
  [InlineData(59, "just now")]
  [InlineData(60, "1m ago")]
  [InlineData(7200, "2h ago")]
  [InlineData(86400, "1d ago")]
  [InlineData(259200, "3d ago")]
  public void Relative_UsesOneCoarseningGrammar(long secondsAgo, string expected)
    => Assert.Equal(expected, OnMarket.Relative(secondsAgo));

  [Fact]
  public void RelativeAt_FutureStampReadsAsJustNow()
  {
    // Clock skew or a restored DB. The one thing it certainly is not is old.
    Assert.Equal("just now", OnMarket.RelativeAt(Now + 5000, Now));
  }

  // ---- The queue story: numbers, never a novel ----

  [Fact]
  public void QueueStory_SpeaksTheCrasherSpanAndTheLine()
  {
    // Drift, 08-02: "I do want to know what the crashers were." The 16-crasher
    // receipt that provoked it was a board bug - and only the PRICES could have
    // said so at a glance.
    var r = R(queuePos: 16, crashFloor: 795, crashCeil: 999,
      cluster: 24, lineFloor: 1000, lineCeil: 1500);
    Assert.Equal("stepped over 16 at 795-999 - the line: 24 at 1,000-1,500",
      OnMarket.QueueStory(r));
  }

  [Fact]
  public void QueueStory_ALoneLowballIsNamedAsOne()
  {
    var r = R(queuePos: 1, crashFloor: 200, crashCeil: 200,
      cluster: 4, lineFloor: 1200, lineCeil: 1222);
    Assert.Equal("stepped over a lone lowball at 200 - nobody near it - the line: 4 at 1,200-1,222",
      OnMarket.QueueStory(r));
  }

  [Fact]
  public void QueueStory_ALoneLowballAndALoneCompetitor_AreDifferentStories()
  {
    // The two loners shared a sentence shape in one visual slot (08-03), and
    // they are opposite acts: one row we refused to price against, one row we
    // priced under. The words have to disagree, because the reader's next move
    // does.
    var lowball = OnMarket.QueueStory(R(queuePos: 1, crashFloor: 750, crashCeil: 750))!;
    var competitor = OnMarket.QueueStory(R(queuePos: 0, cluster: 1, lineFloor: 750, lineCeil: 750))!;

    Assert.Equal("stepped over a lone lowball at 750 - nobody near it", lowball);
    Assert.Equal("undercut the lone competitor at 750 - alone, but not nonsense", competitor);
    // Same price, same "one row, no company" - and no shared verb to confuse them.
    Assert.DoesNotContain("stepped over", competitor);
    Assert.DoesNotContain("undercut", lowball);
  }

  [Fact]
  public void QueueStory_FrontOfTheQueueStillNamesItsLine()
  {
    // Pos 0 with a cluster: nothing stepped, but the line we undercut is still
    // the row's context.
    var r = R(queuePos: 0, cluster: 12, lineFloor: 2871, lineCeil: 3000);
    Assert.Equal("the line: 12 at 2,871-3,000", OnMarket.QueueStory(r));

    var loner = R(queuePos: 0, cluster: 1, lineFloor: 5000, lineCeil: 5000);
    Assert.Equal("undercut the lone competitor at 5,000 - alone, but not nonsense",
      OnMarket.QueueStory(loner));
  }

  [Fact]
  public void QueueStory_NamesTheCrowdThatWonTheOutnumberingTest()
  {
    // The Golden Silk misread (Drift, 08-03): the cell said "stepped over 4 - the
    // line: 3" while the crowd that actually convicted the pack was twenty deep.
    // The cluster is not the count the test read; the crowd is.
    var r = R(queuePos: 4, crashFloor: 1, crashCeil: 60, crowd: 20,
      cluster: 3, lineFloor: 1200, lineCeil: 1222);
    Assert.Equal("stepped over 4 at 1-60, outnumbered by the 20 behind them - the line: 3 at 1,200-1,222",
      OnMarket.QueueStory(r));
  }

  [Fact]
  public void QueueStory_NoCrowdBankedSaysNothingAboutOne()
  {
    // Pre-V37 receipts, and every lone-crazy step (which convicts on aloneness,
    // not on a headcount). A missing crowd is silence, never a zero.
    var r = R(queuePos: 2, crashFloor: 500, crashCeil: 600, cluster: 3,
      lineFloor: 900, lineCeil: 950);
    Assert.DoesNotContain("outnumbered", OnMarket.QueueStory(r));
  }

  [Fact]
  public void QueueStory_NoBankedSpansMeansNoStory_NeverAHedge()
  {
    // Pre-V35 receipts and held rows banked no spans. No numbers, no story.
    Assert.Null(OnMarket.QueueStory(R(queuePos: 16)));
    Assert.Null(OnMarket.QueueStory(R(queuePos: null, price: null)));
  }

  [Fact]
  public void QueueStory_OnePriceSpansCollapse()
  {
    var r = R(queuePos: 2, crashFloor: 500, crashCeil: 500,
      cluster: 3, lineFloor: 900, lineCeil: 950);
    Assert.Equal("stepped over 2 at 500 - the line: 3 at 900-950",
      OnMarket.QueueStory(r));
  }

  // ---- Build ----

  [Fact]
  public void Build_StampsTheBankedFacts()
  {
    var rows = OnMarket.Build(
      new[] { R(createdAt: Now - 7200, price: 48997, queuePos: 2, qty: 3,
        crashFloor: 500, crashCeil: 600, cluster: 5, lineFloor: 48998, lineCeil: 49500) },
      Now);

    var row = Assert.Single(rows);
    Assert.Equal(48997, row.Price);
    Assert.Equal("2h ago", row.ListedLabel);
    Assert.Equal(2, row.QueuePosition);
    Assert.Equal(3, row.Quantity);
    Assert.Equal("stepped over 2 at 500-600 - the line: 5 at 48,998-49,500", row.QueueStory);
  }

  [Fact]
  public void Build_ProjectsTheLaneScores_AndLeavesUnscoredLanesNull()
  {
    // The four-score strip (walk unit 3): scores ride per lane (item, quality,
    // retainer). A lane the window never scored draws dashes off a null array,
    // never a fabricated zero.
    var scores = new Dictionary<(uint, bool, string), long?[]>
    {
      [(100u, false, "Karen")] = new long?[] { 849, 610, null, 120 },
    };
    var rows = OnMarket.Build(
      new[] { R(item: 100, retainer: "Karen"), R(id: 2, item: 200, retainer: "Dave") },
      Now, scores);

    var scored = rows.Single(r => r.ItemId == 100);
    Assert.Equal(new long?[] { 849, 610, null, 120 }, scored.Scores);
    Assert.Null(rows.Single(r => r.ItemId == 200).Scores);
  }

  [Fact]
  public void Build_CarriesTheInterimGradeWithoutSpendingIt()
  {
    // The A9 column is a fast-follow; the seam is that the row already holds it.
    var rows = OnMarket.Build(new[] { R(interim: "SILENCE") }, Now);

    Assert.Equal("SILENCE", Assert.Single(rows).InterimGrade);
  }

  [Fact]
  public void Build_QuantityFloorsAtOne()
  {
    // A legacy receipt with a zero quantity must not erase the row's stack.
    var rows = OnMarket.Build(new[] { R(qty: 0) }, Now);

    Assert.Equal(1, Assert.Single(rows).Quantity);
  }

  // ---- Headline ----

  [Fact]
  public void Headline_SumsGilAtAskAndIgnoresPricelessRows()
  {
    var rows = OnMarket.Build(new[]
    {
      R(id: 1, item: 100, price: 1000, qty: 3),  // 3000
      R(id: 2, item: 200, price: 500, qty: 1),   // 500
      R(id: 3, item: 300, price: null),          // counts as a row, not as gil
    }, Now);

    var (count, gil) = OnMarket.Headline(rows);
    Assert.Equal(3, count);
    Assert.Equal(3500, gil);
  }

  // ---- The queue tag: the verdict in the cell, the story on the hover ----

  [Fact]
  public void QueueTag_NamesTheSteppedPackInPlainWords()
  {
    // The tag explains the POSITION, which is the number the reader is looking at.
    // Plain words: "low balls", never the crasher/bait vocabulary (RunLogVoice 4).
    var r = R(queuePos: 16, crashFloor: 795, crashCeil: 999,
      cluster: 24, lineFloor: 1000, lineCeil: 1500);

    Assert.Equal("behind 16 low balls", OnMarket.QueueTag(r));
    // Nothing was deleted - the sentence is still there for the hover.
    Assert.Equal("stepped over 16 at 795-999 - the line: 24 at 1,000-1,500",
      OnMarket.QueueStory(r));
  }

  [Fact]
  public void QueueTag_ALoneLowballIsNotPluralised()
  {
    var r = R(queuePos: 1, crashFloor: 200, crashCeil: 200);
    Assert.Equal("behind a lone low ball", OnMarket.QueueTag(r));
  }

  [Fact]
  public void QueueTag_NoLowballsMeansTheTagIsAboutTheLineInstead()
  {
    var line = R(queuePos: 0, cluster: 12, lineFloor: 2871, lineCeil: 3000);
    var loner = R(queuePos: 0, cluster: 1, lineFloor: 750, lineCeil: 750);

    Assert.Equal("under 12 sellers", OnMarket.QueueTag(line));
    Assert.Equal("under one seller", OnMarket.QueueTag(loner));
  }

  [Fact]
  public void QueueTag_NoBankedSpansMeansNoTag_NeverAHedge()
  {
    // Same refusal the story makes: no numbers, nothing to say about them.
    Assert.Null(OnMarket.QueueTag(R(queuePos: 4)));
    Assert.Null(OnMarket.QueueStory(R(queuePos: 4)));
  }

  [Fact]
  public void QueueTag_RidesTheBuiltRowBesideItsStory()
  {
    var rows = OnMarket.Build(new[]
    {
      R(queuePos: 3, crashFloor: 100, crashCeil: 150, cluster: 2, lineFloor: 900, lineCeil: 950),
    }, Now);

    Assert.Equal("behind 3 low balls", rows[0].QueueTag);
    Assert.Contains("stepped over 3", rows[0].QueueStory);
  }
}
