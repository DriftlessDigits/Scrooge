using System;
using System.Collections.Generic;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The advisor's spine (2026-07-22 rule: current state vs expected state, the
/// advisor owns the transition). These cover the PURE decision core - the
/// declarative expected-state model and the transition-ladder evaluator. The
/// game-side sensors (SpineSensors) are Dalamud and untested here by design;
/// this proves the vocabulary: all-met fires, an unmet expectation returns its
/// declared rung, the least-actionable gap wins when several are unmet, and
/// every refusal names expected vs current.
/// </summary>
public class SpineTests
{
  private static SpineExpectation Exp(Spine.Facet facet, string expected, Spine.Rung ifUnmet)
    => new(facet, expected, ifUnmet);

  [Fact]
  public void AllMet_Fires()
  {
    var state = new ExpectedState("melt",
      Exp(Spine.Facet.View, "the desynthesis window open", Spine.Rung.Refuse),
      Exp(Spine.Facet.Occupancy, "to be un-occupied", Spine.Rung.Refuse));

    var eval = SpineEvaluator.Evaluate(state, new List<FacetReading>
    {
      new(true, "it's open"),
      new(true, "you're free to act"),
    });

    Assert.True(eval.CanFire);
    Assert.Equal(Spine.Rung.Fire, eval.Rung);
    Assert.Null(eval.Gap);
  }

  [Fact]
  public void SingleUnmet_ReturnsItsDeclaredRung_AndNamesTheGap()
  {
    var state = new ExpectedState("list",
      Exp(Spine.Facet.View, "the retainer sell view open", Spine.Rung.SelfNavigate),
      Exp(Spine.Facet.Place, "to be at a retainer bell", Spine.Rung.WalkThere));

    // At the bell roster (place met) but the sell view is closed -> the advisor
    // can self-navigate the Hawk Wares hop.
    var eval = SpineEvaluator.Evaluate(state, new List<FacetReading>
    {
      new(false, "the sell view isn't open"),
      new(true, "it's open"),
    });

    Assert.False(eval.CanFire);
    Assert.Equal(Spine.Rung.SelfNavigate, eval.Rung);
    Assert.Equal(Spine.Facet.View, eval.Gap!.Facet);
    Assert.Equal(
      "Can't list - expected the retainer sell view open, but the sell view isn't open.",
      eval.Message);
  }

  [Fact]
  public void SeveralUnmet_LeastActionableRungWins()
  {
    // Neither at a bell nor in the sell view: the self-navigable View gap is
    // dominated by the WalkThere Place gap - you cannot navigate from nowhere.
    var state = new ExpectedState("list",
      Exp(Spine.Facet.View, "the retainer sell view open", Spine.Rung.SelfNavigate),
      Exp(Spine.Facet.Place, "to be at a retainer bell", Spine.Rung.WalkThere));

    var eval = SpineEvaluator.Evaluate(state, new List<FacetReading>
    {
      new(false, "the sell view isn't open"),
      new(false, "you're not at a retainer bell"),
    });

    Assert.Equal(Spine.Rung.WalkThere, eval.Rung);
    Assert.Equal(Spine.Facet.Place, eval.Gap!.Facet);
    Assert.Equal(
      "Can't list - expected to be at a retainer bell, but you're not at a retainer bell.",
      eval.Message);
  }

  [Fact]
  public void RefuseOutranksEverySoftGap()
  {
    // A hard Refuse (occupied - the game rejects the command) must win over any
    // gap the advisor could otherwise close itself.
    var state = new ExpectedState("melt",
      Exp(Spine.Facet.View, "the desynthesis window open", Spine.Rung.SelfNavigate),
      Exp(Spine.Facet.Occupancy, "to be un-occupied", Spine.Rung.Refuse));

    var eval = SpineEvaluator.Evaluate(state, new List<FacetReading>
    {
      new(false, "it isn't open"),
      new(false, "the retainer bell is open"),
    });

    Assert.Equal(Spine.Rung.Refuse, eval.Rung);
    Assert.Equal(
      "Can't melt - expected to be un-occupied, but the retainer bell is open.",
      eval.Message);
  }

  [Fact]
  public void TieOnSeverity_BreaksByDeclarationOrder()
  {
    // Two Refuse gaps at once: the first declared is the one reported, so an
    // executor lists its checks in the order it wants them surfaced.
    var state = new ExpectedState("melt",
      Exp(Spine.Facet.View, "the desynthesis window open", Spine.Rung.Refuse),
      Exp(Spine.Facet.Occupancy, "to be un-occupied", Spine.Rung.Refuse));

    var eval = SpineEvaluator.Evaluate(state, new List<FacetReading>
    {
      new(false, "it isn't open (talk to Mutamix first)"),
      new(false, "the retainer bell is open"),
    });

    Assert.Equal(Spine.Facet.View, eval.Gap!.Facet);
    Assert.Equal(
      "Can't melt - expected the desynthesis window open, but it isn't open (talk to Mutamix first).",
      eval.Message);
  }

  [Fact]
  public void SingleFacet_Place_RefusesWhenAway()
  {
    // Mirrors the GC turn-in's declared contract (Place, Refuse) - the
    // orchestrator itself is Dalamud and not linked here, so the shape is
    // rebuilt from the same pure pieces.
    var state = new ExpectedState("turn in",
      Exp(Spine.Facet.Place, "to be at your GC's Expert Delivery", Spine.Rung.Refuse));

    var eval = SpineEvaluator.Evaluate(state, new List<FacetReading>
    {
      new(false, "the Expert Delivery window isn't open"),
    });

    Assert.Equal(Spine.Rung.Refuse, eval.Rung);
    Assert.Equal(
      "Can't turn in - expected to be at your GC's Expert Delivery, but the Expert Delivery window isn't open.",
      eval.Message);
  }

  [Fact]
  public void ReadingCountMismatch_Throws()
  {
    var state = new ExpectedState("melt",
      Exp(Spine.Facet.View, "the desynthesis window open", Spine.Rung.Refuse),
      Exp(Spine.Facet.Occupancy, "to be un-occupied", Spine.Rung.Refuse));

    Assert.Throws<ArgumentException>(() =>
      SpineEvaluator.Evaluate(state, new List<FacetReading> { new(true, "it's open") }));
  }
}
