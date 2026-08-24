using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The front-load gate's derived queue (WALK unit 5). What is worth pinning is
/// not the UI - it is that the queue is the EXACT INVERSE of the bulk gate, so it
/// can never ask about a row that already rides (noise) or stay silent about one
/// that does not (carrying rows past). That contract outlived the gate's own
/// screen: stage 2a deleted the judgment screen and kept this arithmetic, because
/// it is what the one launch control refuses over (see LaunchStripTests).
/// </summary>
public class GatePlanTests
{
  private sealed record Row(string Name);

  private static (Row, RoundStage, ConfidenceTier, bool, bool, bool, bool) At(
    string name, RoundStage stage, ConfidenceTier tier, bool ruled = false, bool inReview = false,
    bool ridesWholePile = false, bool deferred = false)
    => (new Row(name), stage, tier, ruled, inReview, ridesWholePile, deferred);

  // ---- NeedsRuling: the inverse of the bulk gate ----

  [Fact]
  public void NeedsRuling_IsExactlyTheRowsBulkWouldLeaveBehind()
  {
    Assert.False(GatePlan.NeedsRuling(ConfidenceTier.Unanimous, false)); // rides on evidence
    Assert.True(GatePlan.NeedsRuling(ConfidenceTier.Mixed, false));      // waiting on the row click
    Assert.True(GatePlan.NeedsRuling(ConfidenceTier.Contradicted, false));
    // The human already ruled it - the click IS the resolution the gate wanted.
    Assert.False(GatePlan.NeedsRuling(ConfidenceTier.Mixed, true));
    Assert.False(GatePlan.NeedsRuling(ConfidenceTier.Contradicted, true));
    Assert.False(GatePlan.NeedsRuling(ConfidenceTier.Unanimous, true));
  }

  [Fact]
  public void NeedsRuling_AReviewVerdictNeedsEyesWhateverItsTierSays()
  {
    // The router DECLINED to call it - unanimous evidence about a call nobody made
    // is not a call. Without this, a Review row could slip past the gate while the
    // board still (rightly) draws it unruled: the exact two-definitions drift the
    // 08-02 shake caught in the other direction.
    Assert.True(GatePlan.NeedsRuling(ConfidenceTier.Unanimous, false, inReview: true));
    // The human's ruling closes it regardless.
    Assert.False(GatePlan.NeedsRuling(ConfidenceTier.Unanimous, true, inReview: true));
  }

  [Fact]
  public void NeedsRuling_AWholePileRowIsWaitingOnNobody()
  {
    // The melt run takes its pile ENTIRE - tier and all - so a Mixed melt row is
    // already riding. Asking for a ruling on it is noise, and the board asked for
    // exactly that until this fact moved out of the queue and into the predicate
    // (item 9, 08-06). Both halves of the drift, one test: the same row is a
    // decision in a gated pile and not one in a whole-pile stage.
    Assert.True(GatePlan.NeedsRuling(ConfidenceTier.Mixed, false));
    Assert.False(GatePlan.NeedsRuling(ConfidenceTier.Mixed, false, ridesWholePile: true));
    // It outranks the Review flag too: a stage that spends the row regardless
    // leaves nothing for a human to answer, whatever put the row there.
    Assert.False(GatePlan.NeedsRuling(ConfidenceTier.Contradicted, false, inReview: true, ridesWholePile: true));
  }

  [Fact]
  public void Queue_AndTheBoard_AgreeOnTheSameMeltRow()
  {
    // The launch tally is the queue's Count and the Call column is the predicate,
    // so this is the disagreement itself, written down: a Mixed melt row must be
    // absent from the queue AND drawn as a call, not as "unruled".
    var queue = GatePlan.Queue(new[]
    {
      At("melt", RoundStage.Desynth, ConfidenceTier.Mixed, ridesWholePile: true),
      At("bell", RoundStage.BellRun, ConfidenceTier.Mixed),
    });

    Assert.Equal("bell", Assert.Single(queue).Row.Name);
    Assert.False(GatePlan.NeedsRuling(ConfidenceTier.Mixed, false, ridesWholePile: true));
  }

  [Fact]
  public void NeedsRuling_MirrorsBulkSet_RowForRow()
  {
    // The contract in one assertion: whatever BulkSet takes, the gate leaves; and
    // whatever BulkSet leaves, the gate takes. If these two ever drift, the gate
    // is lying in one direction or the other.
    var rows = new[]
    {
      ("a", ConfidenceTier.Unanimous, false, false),
      ("b", ConfidenceTier.Mixed, false, false),
      ("c", ConfidenceTier.Contradicted, false, false),
      ("d", ConfidenceTier.Mixed, true, false),
      ("e", ConfidenceTier.Contradicted, true, false),
      // The Defer door, on both sides of the mirror at once (08-06): a
      // deferring row must ride AND must not be asked about. This row is the
      // whole reason the two predicates gained the same disjunct on the same
      // day - if only one of them had, the launch would refuse over a row the
      // bell was about to take anyway.
      ("f", ConfidenceTier.Mixed, false, true),
    };

    var rides = BoardConfidence.BulkSet(
      rows.Select(r => (r.Item1, r.Item2, r.Item3, r.Item4))).ToHashSet();
    var asks = rows
      .Where(r => GatePlan.NeedsRuling(r.Item2, r.Item3, deferred: r.Item4))
      .Select(r => r.Item1).ToHashSet();

    Assert.Empty(rides.Intersect(asks));
    Assert.Equal(rows.Length, rides.Count + asks.Count);
    Assert.Contains("f", rides);
  }

  [Fact]
  public void NeedsRuling_ADeferringRowIsWaitingOnNobody()
  {
    // The launch-gate half of the headliner. A Mixed row used to block the
    // round on principle; if it has a scored winner it now DEFERS - it runs,
    // flagged - and the refusal must stop counting it.
    Assert.True(GatePlan.NeedsRuling(ConfidenceTier.Mixed, false));
    Assert.False(GatePlan.NeedsRuling(ConfidenceTier.Mixed, false, deferred: true));
    // Review membership still blocks, and it is not a contradiction: a row that
    // defers is never inReview (see DeferPlan.State - the router declining is
    // exactly what makes a row Review instead of Defer). This asserts the
    // predicate's precedence anyway, because a future caller could pass both.
    Assert.False(GatePlan.NeedsRuling(ConfidenceTier.Mixed, false, inReview: true, deferred: true));
  }

  [Fact]
  public void Queue_ADeferringRowIsNotAnOpenQuestion()
  {
    var queue = GatePlan.Queue(new[]
    {
      At("defers", RoundStage.BellRun, ConfidenceTier.Mixed, deferred: true),
      At("asks", RoundStage.BellRun, ConfidenceTier.Mixed),
    });

    Assert.Equal("asks", Assert.Single(queue).Row.Name);
  }

  // ---- The queue ----

  [Fact]
  public void Queue_MatureNight_IsEmpty()
  {
    // Everything unanimous or already ruled: the press must flow straight through
    // with no gate at all. This is the zero-cost path, and the shape a mature
    // round settles into as the confidence machinery ripens.
    var candidates = new[]
    {
      At("clean", RoundStage.BellRun, ConfidenceTier.Unanimous),
      At("ruled", RoundStage.TurnIn, ConfidenceTier.Contradicted, ruled: true),
    };

    Assert.Empty(GatePlan.Queue(candidates));
  }

  [Fact]
  public void Queue_KeepsOnlyTheOpenQuestions()
  {
    var candidates = new[]
    {
      At("rides", RoundStage.BellRun, ConfidenceTier.Unanimous),
      At("asks", RoundStage.BellRun, ConfidenceTier.Mixed),
      At("demoted", RoundStage.TurnIn, ConfidenceTier.Contradicted),
      At("resolved", RoundStage.Desynth, ConfidenceTier.Mixed, ruled: true),
    };

    var queue = GatePlan.Queue(candidates);

    Assert.Equal(new[] { "asks", "demoted" }, queue.Select(e => e.Row.Name));
  }

  [Fact]
  public void Queue_ReadsInRoundOrder()
  {
    // The gate reads top-to-bottom like the run itself - the player rules the
    // pinch's rows before the bell's, because that is the order they are spent.
    var candidates = new[]
    {
      At("churn", RoundStage.TurnIn, ConfidenceTier.Mixed),
      At("melt", RoundStage.Desynth, ConfidenceTier.Mixed),
      At("bell", RoundStage.BellRun, ConfidenceTier.Mixed),
      At("rider", RoundStage.Pinch, ConfidenceTier.Mixed),
    };

    var queue = GatePlan.Queue(candidates);

    Assert.Equal(new[] { "rider", "melt", "bell", "churn" }, queue.Select(e => e.Row.Name));
  }

  [Fact]
  public void Queue_IsStableWithinAStage()
  {
    var candidates = new[]
    {
      At("first", RoundStage.BellRun, ConfidenceTier.Mixed),
      At("second", RoundStage.BellRun, ConfidenceTier.Contradicted),
      At("third", RoundStage.BellRun, ConfidenceTier.Mixed),
    };

    Assert.Equal(new[] { "first", "second", "third" },
      GatePlan.Queue(candidates).Select(e => e.Row.Name));
  }

  [Fact]
  public void Queue_CarriesTheStageTheRulingWouldFeed()
  {
    var queue = GatePlan.Queue(new[] { At("x", RoundStage.Desynth, ConfidenceTier.Mixed) });
    Assert.Equal(RoundStage.Desynth, Assert.Single(queue).Stage);
  }

  // ---- The sentences ----

  [Fact]
  public void Headline_RollsUpByStageInRoundOrder()
    // BEFORE CONTINUE, not before the round: the queue moved from the launch button's
    // lock to the hinge's Continue gate (ruled Q1), and by the time this line draws the
    // round has already pinched and reconned.
    => Assert.Equal("4 rulings before Continue: 3 bell, 1 turn in",
      GatePlan.Headline(new[]
      {
        RoundStage.TurnIn, RoundStage.BellRun, RoundStage.BellRun, RoundStage.BellRun,
      }));

  [Fact]
  public void Headline_SingularRuling()
    => Assert.Equal("1 ruling before Continue: 1 bell",
      GatePlan.Headline(new[] { RoundStage.BellRun }));

  [Fact]
  public void Headline_EmptyQueueSaysNothing()
    => Assert.Equal("", GatePlan.Headline(System.Array.Empty<RoundStage>()));

  [Fact]
  public void StageNoun_EveryStageSpeaks()
  {
    foreach (var stage in RoundPlan.Order)
      Assert.NotEqual("?", GatePlan.StageNoun(stage));
  }

  // ========================================================================
  // The seal-discount advisory - the gate's first non-row entry
  // ========================================================================

  [Fact]
  public void SealDiscountAdvisory_SaysNothingWhenNothingIsDiscounted()
  {
    var full = SealRunway.Effective(baseRate: 25, ventureStock: 500, fullBelow: 1_000, zeroAbove: 3_000);
    Assert.False(full.Discounted);
    Assert.Equal("", GatePlan.SealDiscountAdvisory(full));
  }

  [Fact]
  public void SealDiscountAdvisory_SaysNothingWhenTheStockIsUnreadable()
  {
    // No stock read = no curve = no discount, and therefore nothing to say.
    var blind = SealRunway.Effective(baseRate: 25, ventureStock: null, fullBelow: 1_000, zeroAbove: 3_000);
    Assert.Equal("", GatePlan.SealDiscountAdvisory(blind));
  }

  [Fact]
  public void SealDiscountAdvisory_SaysTheFactAndTheNumbersThatMatter()
  {
    // Strings-pass discipline (Drift, 08-02) in the curve's grammar: the FACT
    // (stocked on ventures, in TOKENS - the dial Drift thinks in), the
    // CONSEQUENCE (seals valued down, melt wins more), and the two rates.
    // The curve's anchors are shop constants and stay out of the sentence.
    var discounted = SealRunway.Effective(baseRate: 25, ventureStock: 2_000, fullBelow: 1_000, zeroAbove: 3_000);
    Assert.True(discounted.Discounted);

    var line = GatePlan.SealDiscountAdvisory(discounted);
    Assert.Contains("2,000", line);            // how stocked, in tokens
    Assert.Contains("12.5 gil/seal", line);    // what turn-ins are worth now
    Assert.Contains("instead of 25", line);    // what they usually are
    Assert.DoesNotContain("week", line);       // the old dial stays dead
    Assert.DoesNotContain("smoothstep", line); // the jargon stays in the code
    Assert.DoesNotContain("config", line);
  }

  [Fact]
  public void SealDiscountAdvisory_PastTheMeltLine_SaysMeltTakesEverything()
  {
    var zeroed = SealRunway.Effective(baseRate: 25, ventureStock: 3_200, fullBelow: 1_000, zeroAbove: 3_000);
    var line = GatePlan.SealDiscountAdvisory(zeroed);
    Assert.Contains("3,200", line);
    Assert.Contains("nothing", line);
  }

  [Fact]
  public void SealDiscountAdvisory_AsksForNothing()
  {
    // The knobs are the ruling surface. If this line ever grows a question mark,
    // it has become a decision the gate can't take - and the headline count,
    // which never sees it, would start lying about the work in front of the run.
    var discounted = SealRunway.Effective(baseRate: 25, ventureStock: 2_400, fullBelow: 1_000, zeroAbove: 3_000);
    Assert.DoesNotContain("?", GatePlan.SealDiscountAdvisory(discounted));
  }
}
