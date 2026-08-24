using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE SEAL S-CURVE (Drift, 2026-08-05, replacing the 07-25 runway step). The
/// rule under test: seal value slides smoothly from full at/below the low
/// anchor to nothing at/above the high anchor, half at the center - "a generic
/// S-type curve, centered on 2k ventures." Stock is the only operand; an
/// unreadable stock never discounts blind. Every discounted answer has to be
/// able to SAY it was discounted, in tokens, never weeks.
/// </summary>
public class SealCurveTests
{
  private static SealRate Rate(int? stock, int baseRate = 25,
    int fullBelow = 1_000, int zeroAbove = 3_000)
    => SealRunway.Effective(baseRate, stock, fullBelow, zeroAbove);

  // ---- The ends: turn in everything / melt everything ----

  [Fact]
  public void AtOrBelowTheFullLine_RateUntouched()
  {
    var r = Rate(1_000);
    Assert.False(r.Discounted);
    Assert.Equal(25, r.EffectiveRate);
    Assert.Equal(1.0, r.Factor);
    Assert.Equal("", r.Narration);
  }

  [Fact]
  public void WayBelow_StillFullValue()
    => Assert.False(Rate(200).Discounted);

  [Fact]
  public void AtOrAboveTheMeltLine_SealsScoreAtNothing()
  {
    // "if I am above 3k ventures, melt everything, don't turn in anything" -
    // achieved arithmetically: a zero factor loses every fight it should lose.
    var r = Rate(3_000);
    Assert.True(r.Discounted);
    Assert.Equal(0.0, r.EffectiveRate);
    Assert.Equal(0.0, r.Factor);
    Assert.Contains("nothing", r.Narration);
  }

  // ---- The S between: centered on 2k ----

  [Fact]
  public void TheCenter_IsExactlyHalfValue()
  {
    var r = Rate(2_000);
    Assert.True(r.Discounted);
    Assert.Equal(12.5, r.EffectiveRate, 6);
    Assert.Equal(0.5, r.Factor, 6);
  }

  [Fact]
  public void TheCurveIsAnS_SteepMiddleFlatEnds()
  {
    // Smoothstep''s signature: the first quarter costs less value than the
    // second quarter. 1,500 keeps ~84% while 2,000 holds 50% - the fall is
    // gentle near the ends and steep through the center.
    Assert.Equal(0.84375, Rate(1_500).Factor, 6);
    Assert.Equal(0.15625, Rate(2_500).Factor, 6);
    Assert.True(Rate(1_500).Factor - Rate(1_750).Factor
              < Rate(1_750).Factor - Rate(2_000).Factor);
  }

  [Fact]
  public void TheCurveOnlyEverFalls()
  {
    var prev = 1.1;
    for (var stock = 500; stock <= 3_500; stock += 100)
    {
      var f = Rate(stock).Factor;
      Assert.True(f <= prev, $"factor rose at {stock}");
      prev = f;
    }
  }

  [Fact]
  public void TonightsStock_ScoresNearTheMeltLine()
  {
    // The convicting night: 2,612 tokens. The curve says seals are worth ~10%
    // of face - which is "my mind shifts into melt mode" as arithmetic.
    var r = Rate(2_612);
    Assert.True(r.Discounted);
    Assert.InRange(r.Factor, 0.05, 0.15);
  }

  // ---- Honesty ----

  [Fact]
  public void DiscountedRate_NarratesInTokens_NeverWeeks()
  {
    var r = Rate(2_612);
    Assert.Contains("2,612 ventures", r.Narration);
    Assert.DoesNotContain("week", r.Narration);
    Assert.DoesNotContain("0.1", r.Narration); // the factor is a shop constant; the % prints instead
  }

  [Fact]
  public void FullRate_NarratesNothing()
    => Assert.Equal("", Rate(900).Narration);

  [Fact]
  public void UnknownStock_NeverDiscountsBlind()
  {
    var r = Rate(null);
    Assert.False(r.Discounted);
    Assert.Equal(25, r.EffectiveRate);
    Assert.Null(r.Stock);
    Assert.Equal("", r.Narration);
  }

  // ---- Config anchors are respected, and broken anchors fail safe ----

  [Fact]
  public void AnchorsAreConfigurable()
  {
    Assert.False(Rate(2_200, fullBelow: 2_500, zeroAbove: 4_000).Discounted);
    Assert.True(Rate(2_200, fullBelow: 500, zeroAbove: 2_100).Discounted);
  }

  [Fact]
  public void CrossedOrEqualAnchors_CollapseToACliff_NeverAPremium()
  {
    // A broken config degrades to a hard step at the melt line: full below,
    // nothing at or above - never a value above face.
    Assert.Equal(1.0, Rate(999, fullBelow: 1_000, zeroAbove: 1_000).Factor);
    Assert.Equal(0.0, Rate(1_000, fullBelow: 1_000, zeroAbove: 1_000).Factor);
    Assert.Equal(0.0, Rate(1_500, fullBelow: 2_000, zeroAbove: 1_000).Factor);
  }

  [Fact]
  public void BaseRateIsRespected()
  {
    var r = Rate(2_000, baseRate: 40);
    Assert.Equal(40, r.BaseRate);
    Assert.Equal(20.0, r.EffectiveRate, 6);
  }

  // ---- Through the rules engine ----

  [Fact]
  public void RulesEngine_StockedUp_MeltBeatsCheapenedSeals()
  {
    // 300 seals = 7,500 gil at full rate, beating a 5,000 melt. At 2,612
    // stock the curve leaves ~750 gil of seal value - the melt wins.
    var item = T.Gear(seals: 300, melt: 5000);
    var v = RoutingRules.Evaluate(item, T.Batch(stock: 2_612, cfg: T.Cfg()));
    Assert.Equal(RoutingExit.Desynth, v.Exit);
  }

  [Fact]
  public void RulesEngine_BelowTheFullLine_TurnInWinsOnValue()
  {
    var item = T.Gear(seals: 300, melt: 5000);
    var v = RoutingRules.Evaluate(item, T.Batch(stock: 1_000, cfg: T.Cfg()));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.DoesNotContain("reduced", v.Reason);
  }

  [Fact]
  public void RulesEngine_CurvedTurnIn_SaysSoInTheReason()
  {
    // A seal pile big enough to win even at half value still has to narrate
    // the rate it actually scored at - never a full-rate sentence.
    var item = T.Gear(seals: 4000, melt: 5000);
    var v = RoutingRules.Evaluate(item, T.Batch(stock: 2_000, cfg: T.Cfg()));
    Assert.Equal(RoutingExit.Gc, v.Exit);
    Assert.Contains("reduced", v.Reason);
    Assert.Contains("12.5 gil/seal", v.Reason);
    Assert.Equal(50_000, v.Scores!.Value.Gc);
  }
}
