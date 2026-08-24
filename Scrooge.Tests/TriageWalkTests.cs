using System;
using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE TRIAGE WALK's plan layer (Task 2, spec ruled 2026-08-15). The hinge's forced
/// walk: which page a row lands on, how the cursor moves through pages that may be
/// empty, and what survives a recompose.
///
/// <para>The load-bearing pin is the FIRST one: every row lands on exactly one page, or
/// on none at all if it is Silent. Everything else the walk does is only correct on top
/// of that.</para>
/// </summary>
public class TriageWalkTests
{
  private static TriageRowInput Row(
    string key,
    BoardPile pile = BoardPile.List,
    ConfidenceTier tier = ConfidenceTier.Unanimous,
    bool standing = false,
    bool resolved = false,
    bool deferred = false)
    => new(key, standing, pile, tier, resolved, deferred, key);

  private static TriagePage PageOf(TriageWalk walk, PageKind kind)
    => walk.Pages.First(p => p.Kind == kind);

  private static List<string> KeysOn(TriageWalk walk, PageKind kind)
    => PageOf(walk, kind).Rows.Select(r => r.Key).ToList();

  // ==========================================================================
  // The invariant: exactly one page, or none
  // ==========================================================================

  [Fact]
  public void PageFor_EveryCombinationLandsOnExactlyOnePageOrNowhere()
  {
    // The whole cross product the board can produce. The assertion is not about WHERE
    // each one goes - the named tests below own that - it is that the assignment is a
    // total function with no row falling between two clauses and no row falling out
    // the bottom into nothing (Silent excepted, which draws nowhere BY DESIGN).
    var piles = Enum.GetValues<BoardPile>();
    var tiers = Enum.GetValues<ConfidenceTier>();
    var seen = 0;

    foreach (var pile in piles)
      foreach (var tier in tiers)
        foreach (var standing in new[] { false, true })
          foreach (var resolved in new[] { false, true })
            foreach (var deferred in new[] { false, true })
            {
              var row = Row("k", pile, tier, standing, resolved, deferred);
              var page = TriageWalk.PageFor(row);
              seen++;

              if (pile == BoardPile.Silent)
              {
                Assert.Null(page);
                continue;
              }
              Assert.NotNull(page);

              // And the composed walk agrees with the predicate - one derivation, not
              // two (the lesson BoardConfidence.Rides exists to enforce).
              var walk = TriageWalk.Compose(new[] { row });
              var drawn = walk.Pages.Where(p => p.Rows.Count > 0).ToList();
              Assert.Single(drawn);
              Assert.Equal(page, drawn[0].Kind);
            }

    Assert.Equal(piles.Length * tiers.Length * 2 * 2 * 2, seen);
  }

  [Fact]
  public void PageFor_SilentDrawsNowhere()
  {
    // The third eyes state (08-06): "I got this, and what I got is: don't." It has no
    // board presence, so the walk - a RE-presentation of the same board - has none to
    // give it.
    Assert.Null(TriageWalk.PageFor(Row("k", BoardPile.Silent)));
    var walk = TriageWalk.Compose(new[] { Row("k", BoardPile.Silent) });
    Assert.All(walk.Pages, p => Assert.True(p.IsEmpty));
  }

  [Fact]
  public void PageFor_ReviewIsACaseEvenOnAStandingRow()
  {
    // Review always wins, the same way it wins in BoardPiles.ForRoutingExit. A standing
    // lane the pinch could not price (PricingResult.NoData -> Review) is a question, not
    // a verb, and the verb page has no way to ask it.
    Assert.Equal(PageKind.BagDecisions,
      TriageWalk.PageFor(Row("k", BoardPile.Review, standing: true)));
  }

  [Fact]
  public void PageFor_StandingRowsAreBoardCalls()
  {
    // The spec's table: reprice, standing pull-and-vendor, the melt-beats-ask contest,
    // player contests, and the withdrawn-verb rows.
    foreach (var pile in new[] { BoardPile.Reprice, BoardPile.PullAndVendor, BoardPile.Melt })
      Assert.Equal(PageKind.BoardDecisions, TriageWalk.PageFor(Row("k", pile, standing: true)));
  }

  [Fact]
  public void PageFor_AStandingRowIsABoardCallEvenWhenItWouldRide()
  {
    // A Unanimous standing reprice could satisfy Rides(), and the riders page would
    // happily take it. It is a BOARD call: its question is a verb against a listing,
    // and the snap grammar that asks it lives on page 1.
    var row = Row("k", BoardPile.Reprice, ConfidenceTier.Unanimous, standing: true);
    Assert.True(BoardConfidence.Rides(row.Tier, false, row.Deferred));
    Assert.Equal(PageKind.BoardDecisions, TriageWalk.PageFor(row));
  }

  [Fact]
  public void PageFor_RidesPutsARowOnTheRiders()
  {
    // Both disjuncts the spec names: Unanimous (bulk-eligible) and Defer.
    Assert.Equal(PageKind.Riders, TriageWalk.PageFor(Row("u", BoardPile.List)));
    Assert.Equal(PageKind.Riders,
      TriageWalk.PageFor(Row("d", BoardPile.Melt, ConfidenceTier.Mixed, deferred: true)));
  }

  [Fact]
  public void PageFor_ARowThatCannotActNeedsEyes()
  {
    // The clause with no entry in the spec's table: a Mixed bag row that neither defers
    // nor sits in Review. By construction it does not exist, and the point of the clause
    // is that if it ever does it is not silently dropped.
    Assert.Equal(PageKind.BagDecisions, TriageWalk.PageFor(Row("k", BoardPile.List, ConfidenceTier.Mixed)));
  }

  // ==========================================================================
  // The riders contract
  // ==========================================================================

  [Fact]
  public void PageFor_AReplayedPriorRulingRides()
  {
    // F5 (ruled 08-22): a PRIOR-round ruling replayed onto this row is a resolved
    // row, not a question - it rides like any other resolved row instead of seating
    // a pre-answered case. The Southern Seas Shirt pin: Mixed tier, not standing,
    // not Review, PlayerResolved by the replay seam.
    Assert.Equal(PageKind.Riders,
      TriageWalk.PageFor(Row("k", BoardPile.List, ConfidenceTier.Mixed, resolved: true)));
    // And it owes the walk nothing - the count never includes furniture.
    var walk = TriageWalk.Compose(new[] { Row("a", BoardPile.List, ConfidenceTier.Mixed, resolved: true) });
    Assert.Equal(0, walk.CallsLeft);
  }

  [Fact]
  public void Decide_AnInWalkRulingKeepsItsCasePage()
  {
    // The F5 split's other half: a ruling made LIVE in this walk stays revisitable
    // on its case page (via Verdicts) - only the NEXT round's replay promotes it to
    // a rider.
    var walk = TriageWalk.Compose(new[] { Row("b", BoardPile.Review) });
    Assert.True(walk.Decide("b", BoardPile.Churn));
    Assert.Equal(new[] { "b" }, KeysOn(walk, PageKind.BagDecisions));
    Assert.Equal(0, walk.CallsLeft);
  }

  [Fact]
  public void Decide_HoldAnswersTheDocketWithoutAnExit()
  {
    // THE MISSING VERB (ruled 2026-08-23, the Hemicyon Hide case): Silent is the
    // hold verdict - it answers the case (CallsLeft drops, IsComplete can open)
    // while staging nothing. The row stays on its case page, revisitable.
    var walk = TriageWalk.Compose(new[] { Row("hide", BoardPile.Review) });
    Assert.Equal(1, walk.CallsLeft);

    Assert.True(walk.Decide("hide", BoardPile.Silent));
    Assert.Equal(0, walk.CallsLeft);
    Assert.Equal(new[] { "hide" }, KeysOn(walk, PageKind.BagDecisions));
    Assert.Equal(BoardPile.Silent, walk.Verdicts["hide"]);

    // And the hold is a verdict like any other: a later exit press overwrites it.
    Assert.True(walk.Decide("hide", BoardPile.List));
    Assert.Equal(BoardPile.List, walk.Verdicts["hide"]);
  }

  // ==========================================================================
  // Navigation
  // ==========================================================================

  [Fact]
  public void Cursor_StartsOnTheFirstPageWithAnythingToSay()
  {
    var walk = TriageWalk.Compose(new[] { Row("case", BoardPile.Review) });
    Assert.Equal(PageKind.BagDecisions, walk.Pages[walk.Cursor].Kind);
    Assert.False(walk.CanBack);
  }

  [Fact]
  public void Next_SkipsEmptyPages()
  {
    // No riders at all: page 1 -> page 3, one press. An empty page the player has to
    // press through is a page that taught him nothing.
    var walk = TriageWalk.Compose(new[]
    {
      Row("board", BoardPile.Reprice, standing: true),
      Row("case", BoardPile.Review),
    });

    Assert.Equal(PageKind.BoardDecisions, walk.Pages[walk.Cursor].Kind);
    walk.Next();
    Assert.Equal(PageKind.BagDecisions, walk.Pages[walk.Cursor].Kind);
  }

  [Fact]
  public void Back_SkipsEmptyPagesAndStopsAtTheFirstOne()
  {
    var walk = TriageWalk.Compose(new[]
    {
      Row("board", BoardPile.Reprice, standing: true),
      Row("case", BoardPile.Review),
    });
    walk.Next();

    Assert.True(walk.CanBack);
    walk.Back();
    Assert.Equal(PageKind.BoardDecisions, walk.Pages[walk.Cursor].Kind);

    // Disabled at the first page, NOT hidden - and pressing it anyway moves nothing.
    Assert.False(walk.CanBack);
    walk.Back();
    Assert.Equal(PageKind.BoardDecisions, walk.Pages[walk.Cursor].Kind);
  }

  [Fact]
  public void Back_FromTheWalksEndReturnsToTheLastRealPage()
  {
    var walk = TriageWalk.Compose(new[] { Row("board", BoardPile.Reprice, standing: true) });
    walk.Next();
    Assert.Equal(walk.Pages.Count, walk.Cursor);

    walk.Back();
    Assert.Equal(PageKind.BoardDecisions, walk.Pages[walk.Cursor].Kind);
  }

  [Fact]
  public void Next_PastTheLastPageIsTheWalksEndAndStaysThere()
  {
    var walk = TriageWalk.Compose(new[] { Row("rider") });
    walk.Next();
    Assert.Equal(walk.Pages.Count, walk.Cursor);
    Assert.False(walk.CanNext);
    walk.Next();
    Assert.Equal(walk.Pages.Count, walk.Cursor);
  }

  [Fact]
  public void Compose_AnEmptyBoardIsAlreadyWalked()
  {
    var walk = TriageWalk.Compose(Array.Empty<TriageRowInput>());
    Assert.Equal(walk.Pages.Count, walk.Cursor);
    Assert.True(walk.IsComplete);
    Assert.Equal("", walk.Rail());
  }

  // ==========================================================================
  // The walk's end
  // ==========================================================================

  [Fact]
  public void IsComplete_NeedsBothTheWalkAndTheAnswers()
  {
    var walk = TriageWalk.Compose(new[] { Row("case", BoardPile.Review) });
    Assert.False(walk.IsComplete);

    // Walked, but the case is still unanswered - Bag Calls IS the launch refusal's
    // former job, and this is that refusal.
    walk.Next();
    Assert.Equal(walk.Pages.Count, walk.Cursor);
    Assert.False(walk.IsComplete);

    Assert.True(walk.Decide("case", BoardPile.Melt));
    Assert.True(walk.IsComplete);
  }

  [Fact]
  public void IsComplete_NotWhileAPageIsStillUnwalked()
  {
    var walk = TriageWalk.Compose(new[] { Row("rider") });
    Assert.Equal(0, walk.CallsLeft);
    Assert.False(walk.IsComplete);
    walk.Next();
    Assert.True(walk.IsComplete);
  }

  [Fact]
  public void Decide_OnlyAppliesToCases()
  {
    var walk = TriageWalk.Compose(new[]
    {
      Row("rider"),
      Row("board", BoardPile.Reprice, standing: true),
    });
    Assert.False(walk.Decide("rider", BoardPile.Melt));
    Assert.False(walk.Decide("board", BoardPile.Melt));
    Assert.Empty(walk.Verdicts);
  }

  [Fact]
  public void ARowAlreadyRuledBeforeTheWalkOwesNothing()
  {
    // A player resolution that arrived with the row is an answer, not a chore.
    var walk = TriageWalk.Compose(new[] { Row("case", BoardPile.Review, resolved: true) });
    Assert.Single(PageOf(walk, PageKind.BagDecisions).Rows);
    Assert.Equal(0, walk.CallsLeft);
  }

  [Fact]
  public void BagCallsControlRefusesUntilEveryCaseIsAnswered()
  {
    var walk = TriageWalk.Compose(new[] { Row("a", BoardPile.Review), Row("b", BoardPile.Review) });
    var control = PageOf(walk, PageKind.BagDecisions).Control;
    Assert.False(control.Enabled);
    Assert.Equal("2 cases still need an answer", control.Refusal);

    walk.Decide("a", BoardPile.List);
    Assert.Equal("1 case still needs an answer", PageOf(walk, PageKind.BagDecisions).Control.Refusal);

    walk.Decide("b", BoardPile.List);
    Assert.True(PageOf(walk, PageKind.BagDecisions).Control.Enabled);
    Assert.Equal("", PageOf(walk, PageKind.BagDecisions).Control.Refusal);
  }

  [Fact]
  public void EveryOtherPagesControlIsAnUnconditionalNext()
  {
    var walk = TriageWalk.Compose(new[]
    {
      Row("board", BoardPile.Reprice, standing: true),
      Row("rider"),
    });
    Assert.True(PageOf(walk, PageKind.BoardDecisions).Control.Enabled);
    Assert.True(PageOf(walk, PageKind.Riders).Control.Enabled);
  }

  // ==========================================================================
  // Recompose
  // ==========================================================================

  [Fact]
  public void Recompose_KeepsTheCursorOnTheSamePageKind()
  {
    // The player is standing on the riders. A re-pinch lands and page 1 grows a row.
    // He must not be teleported.
    var walk = TriageWalk.Compose(new[] { Row("rider") });
    Assert.Equal(PageKind.Riders, walk.Pages[walk.Cursor].Kind);

    walk.Recompose(new[] { Row("board", BoardPile.Reprice, standing: true), Row("rider") });
    Assert.Equal(PageKind.Riders, walk.Pages[walk.Cursor].Kind);
  }

  [Fact]
  public void Recompose_CarriesTheCursorForwardWhenItsPageEmptied()
  {
    var walk = TriageWalk.Compose(new[] { Row("rider"), Row("case", BoardPile.Review) });
    Assert.Equal(PageKind.Riders, walk.Pages[walk.Cursor].Kind);

    walk.Recompose(new[] { Row("case", BoardPile.Review) });
    Assert.Equal(PageKind.BagDecisions, walk.Pages[walk.Cursor].Kind);
  }

  [Fact]
  public void Recompose_KeepsVerdicts()
  {
    var rows = new[] { Row("a"), Row("b", BoardPile.Review) };
    var walk = TriageWalk.Compose(rows);
    walk.Decide("b", BoardPile.Churn);

    // Same rows, a fresh read - b's tier moved, which changes nothing about the ruling.
    walk.Recompose(new[] { Row("a"), Row("b", BoardPile.Review, ConfidenceTier.Contradicted) });

    Assert.Equal(BoardPile.Churn, walk.Verdicts["b"]);
    Assert.Equal(0, walk.CallsLeft);
  }

  [Fact]
  public void Recompose_ARowThatLeftTheBoardTakesItsDecisionWithIt()
  {
    var walk = TriageWalk.Compose(new[] { Row("a"), Row("b", BoardPile.Review) });
    walk.Decide("b", BoardPile.Churn);

    walk.Recompose(new[] { Row("c", BoardPile.Review) });
    Assert.Empty(walk.Verdicts);
  }

  [Fact]
  public void Recompose_ACallThatChangedRelandsOnTheRightPage()
  {
    // The re-ask, seen from the walk: a lane that moved from reprice to review is a
    // different question, and it gets asked on the page that can ask it.
    var walk = TriageWalk.Compose(new[] { Row("lane", BoardPile.Reprice, standing: true) });
    Assert.Equal(new[] { "lane" }, KeysOn(walk, PageKind.BoardDecisions));

    walk.Recompose(new[] { Row("lane", BoardPile.Review, standing: true) });
    Assert.Empty(KeysOn(walk, PageKind.BoardDecisions));
    Assert.Equal(new[] { "lane" }, KeysOn(walk, PageKind.BagDecisions));
  }

  [Fact]
  public void Recompose_PastTheEndStaysPastTheEndAndTheRefusalDoesTheStopping()
  {
    var walk = TriageWalk.Compose(new[] { Row("rider") });
    walk.Next();
    Assert.True(walk.IsComplete);

    walk.Recompose(new[] { Row("rider"), Row("case", BoardPile.Review) });
    Assert.Equal(walk.Pages.Count, walk.Cursor);
    Assert.False(walk.IsComplete);
  }

  // ==========================================================================
  // The rail
  // ==========================================================================

  [Fact]
  public void Rail_SaysPositionAndRemainingWork()
  {
    var walk = TriageWalk.Compose(new[]
    {
      Row("board", BoardPile.Reprice, standing: true),
      Row("rider"),
      Row("c1", BoardPile.Review),
      Row("c2", BoardPile.Review),
      Row("c3", BoardPile.Review),
      Row("c4", BoardPile.Review),
    });

    Assert.Equal("triage - page 1/3, 4 calls left", walk.Rail());
    walk.Next();
    Assert.Equal("triage - page 2/3, 4 calls left", walk.Rail());
  }

  [Fact]
  public void Rail_CountsOnlyThePagesThePlayerWillSee()
  {
    // "page 2/3" over a walk with an empty page promises a page he never reaches.
    var walk = TriageWalk.Compose(new[]
    {
      Row("board", BoardPile.Reprice, standing: true),
      Row("case", BoardPile.Review),
    });
    Assert.Equal("triage - page 1/2, 1 call left", walk.Rail());
    walk.Next();
    Assert.Equal("triage - page 2/2, 1 call left", walk.Rail());
  }

  [Fact]
  public void Rail_ZeroSaysNothing()
  {
    // IdlePlan.WaitingDecisions' rule: a walk with nothing owed does not report "0".
    var walk = TriageWalk.Compose(new[] { Row("rider") });
    Assert.Equal("triage - page 1/1", walk.Rail());
    walk.Next();
    Assert.Equal("triage - walked", walk.Rail());
  }

  [Fact]
  public void Rail_AtTheEndWithWorkStillOwedStillSaysSo()
  {
    var walk = TriageWalk.Compose(new[] { Row("case", BoardPile.Review) });
    walk.Next();
    Assert.Equal("triage - walked, 1 call left", walk.Rail());
  }
}
