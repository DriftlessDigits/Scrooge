using System.Linq;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE TWO DECISION NUMBERS (V24, ruled 08-22). The Hawk window's Route column keeps
/// its one word; the hover leads with what the winning exit was worth and what the
/// best loser was worth, because that pair - not the prose beside it - is the answer
/// to "why that exit and not the other one".
///
/// <para>The pins hold one property and assert it against the router rather than
/// against literals: the pair is READ OFF the verdict the router already produced,
/// never scored a second time. A tag that quoted numbers from its own pass could
/// disagree with the pile it is a map of, which is exactly the guarantee
/// <see cref="RouteTagMap"/> exists to make - so a pin written in hard-coded gil
/// would be testing the rules engine's arithmetic, not this seam.</para>
/// </summary>
public class RouteTagNumbersTests
{
  /// <summary>The router's own four scores for one item, as the tag should read them.</summary>
  private static (long? Winner, long? RunnerUp) Expected(RoutingVerdict v)
  {
    if (v.Scores is not { } s) return (null, null);
    var scored = BoardCalls.Exits
      .Select(e => (Exit: e, Score: BoardCalls.ScoreOf(s, e)))
      .Where(p => p.Score is long)
      .ToList();
    var winner = scored.Where(p => p.Exit == v.Exit).Select(p => p.Score).FirstOrDefault();
    var losers = scored.Where(p => p.Exit != v.Exit).Select(p => p.Score!.Value).ToList();
    return (winner, losers.Count == 0 ? (long?)null : losers.Max());
  }

  private static void AssertReadsOffTheVerdict(RoutingItemInputs item)
  {
    var batch = T.Batch();
    var tag = RouteTagMap.Evaluate(item, batch);
    var (winner, runnerUp) = Expected(RoutingRules.Evaluate(item, batch));

    Assert.Equal(winner, tag.WinnerScore);
    Assert.Equal(runnerUp, tag.RunnerUpScore);
  }

  [Fact]
  public void MeltBeatsTheSale_PairIsTheRoutersOwnTwoNumbers()
    => AssertReadsOffTheVerdict(T.Gear(sale: (3_000, 0, 5), melt: 5_000, vendor: 1_000));

  [Fact]
  public void SealsWin_PairIsTheRoutersOwnTwoNumbers()
    => AssertReadsOffTheVerdict(T.Gear(seals: 200, vendor: 100));

  [Fact]
  public void ReviewDegradation_KeepsThePair()
    // The tag says "?" here, and the two numbers ARE why it could not choose - the
    // one verdict where dropping them would leave the hover with nothing to lead on.
    => AssertReadsOffTheVerdict(T.Gear(melt: 1_400, vendor: 1_000));

  [Fact]
  public void EveryExitShape_ReadsOffTheVerdict()
  {
    AssertReadsOffTheVerdict(T.Gear(sale: (3_000, 0, 5), vendor: 100));
    AssertReadsOffTheVerdict(T.Gear(melt: 900, vendor: 1_000));
    AssertReadsOffTheVerdict(T.Gear(velocity: 0.2));
    AssertReadsOffTheVerdict(T.Gear());
    AssertReadsOffTheVerdict(T.Gear(sale: (3_000, 0, 5), melt: 12_000, seals: 800, vendor: 1_000));
  }

  [Fact]
  public void Numbers_AreNullOnThePreValueEarlyExits()
  {
    // Ban, always-vendor and non-equipment return before any comparison runs, so
    // there is no pair to lead with and the hover falls back to the reason alone.
    foreach (var tag in new[]
    {
      RouteTagMap.Evaluate(T.Gear(banned: true), T.Batch()),
      RouteTagMap.Evaluate(T.Gear(alwaysVendor: true), T.Batch()),
      RouteTagMap.Evaluate(T.Gear(equipment: false), T.Batch()),
    })
    {
      Assert.Equal(RouteTagMap.Verdict.None, tag.Verdict);
      Assert.Null(tag.WinnerScore);
      Assert.Null(tag.RunnerUpScore);
    }
  }

  [Fact]
  public void RunnerUp_IsTheBestLoser_NotTheSecondColumn()
  {
    // The pair must survive whichever exit wins: the loser named is the highest
    // OTHER score, never a fixed neighbour in the strip's order.
    var item = T.Gear(sale: (3_000, 0, 5), melt: 12_000, seals: 800, vendor: 1_000);
    var batch = T.Batch();
    var tag = RouteTagMap.Evaluate(item, batch);
    var verdict = RoutingRules.Evaluate(item, batch);

    if (tag.RunnerUpScore is not long runnerUp) return;
    foreach (var exit in BoardCalls.Exits)
    {
      if (exit == verdict.Exit) continue;
      if (BoardCalls.ScoreOf(verdict.Scores, exit) is long other)
        Assert.True(other <= runnerUp);
    }
  }
}
