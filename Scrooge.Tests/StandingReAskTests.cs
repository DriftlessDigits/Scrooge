using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE RE-ASK (Drift, ruled 2026-08-10, addendum 3): <i>"if we have useful new
/// information, we should present it."</i>
///
/// <para>A staged verb answers a CALL. When a re-read moves the call, carrying the verb
/// spends the human's answer on a question nobody asked him - so the verb is withdrawn
/// and the row goes back into the rulings the hinge waits on. One rule for both halves
/// of the board: bag gear has re-asked on a changed verdict since the ruled board, and
/// this is that, for lanes.</para>
/// </summary>
public class StandingReAskTests
{
  /// <summary>
  /// The window's carry-forward, driven through the REAL re-present the window calls -
  /// staged verbs held up against the calls a fresh pinch produced. Rows are named by
  /// their lane here; the window keys them by item reference, which is the only
  /// difference between this and SetRun.
  /// </summary>
  private static (Dictionary<string, StandingAction> Carried, Dictionary<string, string> ReAsked)
    Represent(
      IEnumerable<(string Lane, StandingVerbFixture Staged)> staged,
      IReadOnlyDictionary<string, BoardPile> callsNow)
  {
    var (carriedRows, withdrawn) = StandingReAsk.Represent(
      staged.Select(s => new StandingReAsk.Staged<string, string>(
        s.Lane, s.Lane, new StagedVerb(s.Staged.Action, s.Staged.Against), s.Staged.IsContest)),
      callsNow);

    return (carriedRows.ToDictionary(r => r.Lane, r => r.Verb.Action),
      withdrawn.ToDictionary(w => w.Lane, w => w.Note));
  }

  internal readonly record struct StandingVerbFixture(
    StandingAction Action, BoardPile Against, bool IsContest = false);

  // ---- The rule -------------------------------------------------------------

  [Fact]
  public void SameCall_TheVerbCarries()
  {
    // The re-read said the same thing. He already answered it; asking again would be
    // the board forgetting a conversation it had.
    var verb = new StagedVerb(StandingAction.Reprice, BoardPile.Reprice);

    Assert.True(StandingReAsk.Survives(verb, BoardPile.Reprice));
    Assert.Equal("", StandingReAsk.Note(verb, BoardPile.Reprice));
  }

  [Fact]
  public void ChangedCall_TheVerbIsWithdrawn()
  {
    // He staged Vend against a board that said pull-and-vendor. The re-read says
    // reprice. Spending the Vend now would sell an item off an answer to a question
    // that no longer exists.
    var verb = new StagedVerb(StandingAction.Vendor, BoardPile.PullAndVendor);

    Assert.False(StandingReAsk.Survives(verb, BoardPile.Reprice));
  }

  [Fact]
  public void APriceWiggleInsideTheSameCall_Carries()
  {
    // THE NO-THRESHOLD ENCODING, asserted as an absence: there is no operand here that
    // could hold a price, a delta or a percentage. Two reads that both say "reprice"
    // are the same call however far the number moved between them - which is why this
    // rule needs no knob, and why it cannot grow one by accident.
    var verb = new StagedVerb(StandingAction.Reprice, BoardPile.Reprice);

    // Every PricingResult that maps to Reprice is one call. The lane's price is not an
    // operand of Survives at all - it is not in the signature.
    foreach (var result in new[]
      { PricingResult.CapBlocked, PricingResult.UndercutTooDeep })
      Assert.True(StandingReAsk.Survives(verb, BoardPiles.ForStanding(result)));
  }

  [Fact]
  public void TheReasonMayChangeWhileTheCallDoesNot()
  {
    // CapBlocked -> UndercutTooDeep is a different REASON for one instruction. The
    // human was told "reprice this" and he answered it; re-asking because the
    // machinery's internal reason moved would be a question the board has not got.
    var verb = new StagedVerb(StandingAction.Reprice, BoardPiles.ForStanding(PricingResult.CapBlocked));

    Assert.True(StandingReAsk.Survives(verb, BoardPiles.ForStanding(PricingResult.UndercutTooDeep)));
  }

  // ---- The note -------------------------------------------------------------

  [Fact]
  public void TheNoteSaysWhatHeSaid_AndWhatMovedUnderIt()
  {
    var note = StandingReAsk.Note(
      new StagedVerb(StandingAction.Vendor, BoardPile.PullAndVendor), BoardPile.Reprice);

    Assert.Contains("Vend", note);       // his own button's word
    Assert.Contains("Vendor", note);     // the pile he staged against, as the board titles it
    Assert.Contains("Reprice", note);    // where it moved
    Assert.Contains("asking again", note);
  }

  [Fact]
  public void ASurvivingVerbHasNoNote()
  {
    // A note about a verb that still stands would be the board narrating its own
    // steadiness at a player who is trying to read a worklist.
    Assert.Equal("", StandingReAsk.Note(
      new StagedVerb(StandingAction.Pull, BoardPile.PullAndVendor), BoardPile.PullAndVendor));
  }

  // ---- The re-present, end to end ------------------------------------------

  [Fact]
  public void ReadPresent_CarriesTheUnchanged_AndReAsksTheMoved()
  {
    var staged = new[]
    {
      ("steady", new StandingVerbFixture(StandingAction.Reprice, BoardPile.Reprice)),
      ("moved", new StandingVerbFixture(StandingAction.Vendor, BoardPile.PullAndVendor)),
    };
    var callsNow = new Dictionary<string, BoardPile>
    {
      ["steady"] = BoardPile.Reprice,
      ["moved"] = BoardPile.Reprice,
    };

    var (carried, reAsked) = Represent(staged, callsNow);

    Assert.Equal(new[] { "steady" }, carried.Keys);
    Assert.Equal(StandingAction.Reprice, carried["steady"]);
    Assert.Equal(new[] { "moved" }, reAsked.Keys);
    Assert.NotEmpty(reAsked["moved"]);
  }

  [Fact]
  public void AWithdrawnVerbRejoinsTheRulingsTheHingeWaitsOn()
  {
    // The count is the point. The judgment queue calls a lane RULED when a verb is
    // staged on it, so dropping the verb is what puts the row back in front of the
    // human - and what stops Continue until he answers. Modelled through GatePlan the
    // way the window composes it: playerResolved is "_actions holds this lane".
    var staged = new[]
    {
      ("moved", new StandingVerbFixture(StandingAction.Vendor, BoardPile.PullAndVendor)),
    };
    var callsNow = new Dictionary<string, BoardPile> { ["moved"] = BoardPile.Reprice };

    var (carried, _) = Represent(staged, callsNow);

    var open = GatePlan.Queue(new[]
    {
      ((object)"moved", RoundStage.BellRun, ConfidenceTier.Unanimous,
        PlayerResolved: carried.ContainsKey("moved"),
        InReview: true, RidesWholePile: false, Deferred: false),
    }).Count;

    Assert.Equal(1, open);
    Assert.False(AccountantPlan.Continue(open, actHasWork: true).CanContinue);
  }

  [Fact]
  public void ALaneThatIsNoLongerListed_SimplyDropsOut()
  {
    // It sold, or he pulled it by hand. There is nothing left to reprice and nothing
    // to ask about - a re-ask note for an item that is gone would be a question with
    // no row under it.
    var staged = new[]
    {
      ("gone", new StandingVerbFixture(StandingAction.Reprice, BoardPile.Reprice)),
    };

    var (carried, reAsked) = Represent(staged, new Dictionary<string, BoardPile>());

    Assert.Empty(carried);
    Assert.Empty(reAsked);
  }

  [Fact]
  public void NoRePresent_NoReAsk_ThePreCommitPathIsUntouched()
  {
    // Nothing here fires until a run reports standing items and the board rebuilds. A
    // player staging verbs at the first hinge, with no run in between, keeps every one
    // of them - the rule is about NEW information, and no new information arrived.
    var staged = new[]
    {
      ("a", new StandingVerbFixture(StandingAction.Vendor, BoardPile.PullAndVendor)),
      ("b", new StandingVerbFixture(StandingAction.Reprice, BoardPile.Reprice)),
    };
    var unchanged = new Dictionary<string, BoardPile>
    {
      ["a"] = BoardPile.PullAndVendor,
      ["b"] = BoardPile.Reprice,
    };

    var (carried, reAsked) = Represent(staged, unchanged);

    Assert.Equal(2, carried.Count);
    Assert.Empty(reAsked);
  }

  // ---- The door: a pinch reports even when it raised nothing (review ruling S5) ----

  [Fact]
  public void APinchReports_EvenWithZeroStandingRows()
  {
    // THE GHOST-VERB FIX. The hand-off used to be gated on "this run produced rows",
    // so the pass that supersedes verbs only spoke when it also had rows to hand over.
    // A clean pinch is news: the lanes those verbs answered are not flagged any more.
    Assert.True(StandingReAsk.Reports(isPinchRun: true, standingCount: 0));
    Assert.True(StandingReAsk.Reports(isPinchRun: true, standingCount: 7));
  }

  [Fact]
  public void EveryOtherRunReportsOnlyWhatItRaised()
  {
    // Recon, GC, desynth, coffer, the triage batch: none of them stamps a lane's call,
    // so re-presenting on their account would withdraw verbs against nothing.
    Assert.False(StandingReAsk.Reports(isPinchRun: false, standingCount: 0));
    Assert.True(StandingReAsk.Reports(isPinchRun: false, standingCount: 3));
  }

  [Fact]
  public void AZeroRowPinch_DropsTheGhostsAndKeepsTheContests()
  {
    // The two halves of a clean pinch, in one pass. The pinch-raised verb answered a
    // flag that is gone, so it drops; the contest was never the pinch's to raise and
    // nothing moved under it, so it stands.
    var staged = new[]
    {
      ("ghost", new StandingVerbFixture(StandingAction.Vendor, BoardPile.PullAndVendor)),
      ("mine", new StandingVerbFixture(StandingAction.Reprice, BoardPile.Reprice, IsContest: true)),
    };

    var (carried, reAsked) = Represent(staged, new Dictionary<string, BoardPile>());

    Assert.Equal(new[] { "mine" }, carried.Keys);
    Assert.Empty(reAsked);
  }

  // ---- The contest half, through the same door (review ruling S17) ----

  [Fact]
  public void AContestAgainstAMovedCall_IsReAsked()
  {
    // He clicked a score cell to say "reprice this myself". Tonight's pinch says the
    // lane is below floor - pull and vendor. His answer was to a different question,
    // and the frozen PlayerContest result used to make this comparison a tautology.
    var staged = new[]
    {
      ("mine", new StandingVerbFixture(StandingAction.Reprice, BoardPile.Reprice, IsContest: true)),
    };
    var callsNow = new Dictionary<string, BoardPile> { ["mine"] = BoardPile.PullAndVendor };

    var (carried, reAsked) = Represent(staged, callsNow);

    Assert.Empty(carried);
    Assert.Contains("asking again", reAsked["mine"]);
  }

  [Fact]
  public void AContestAgainstAnUnmovedCall_Carries()
  {
    var staged = new[]
    {
      ("mine", new StandingVerbFixture(StandingAction.Reprice, BoardPile.Reprice, IsContest: true)),
    };
    var callsNow = new Dictionary<string, BoardPile> { ["mine"] = BoardPile.Reprice };

    var (carried, reAsked) = Represent(staged, callsNow);

    Assert.Equal(StandingAction.Reprice, carried["mine"]);
    Assert.Empty(reAsked);
  }

  // ---- One lane, one ruling - the contest wins (review ruling S10) ----

  [Fact]
  public void OneLaneWithBothAPinchRowAndAContest_CarriesTheContestOnly()
  {
    // The executor keys work by row. Two rows on one lane meant the retainer visited it
    // twice, on conflicting verbs, in whichever order a dictionary enumerated. The
    // contest is the most recent and most deliberate answer, so it takes the lane.
    var entries = new[]
    {
      new StandingReAsk.Staged<string, string>(
        "pinch-row", "lane", new StagedVerb(StandingAction.Vendor, BoardPile.PullAndVendor), false),
      new StandingReAsk.Staged<string, string>(
        "contest-row", "lane", new StagedVerb(StandingAction.Reprice, BoardPile.Reprice), true),
    };
    var callsNow = new Dictionary<string, BoardPile> { ["lane"] = BoardPile.Reprice };

    var (carried, withdrawn) = StandingReAsk.Represent(entries, callsNow);

    Assert.Single(carried);
    Assert.Equal("contest-row", carried[0].Row);
    Assert.Equal(StandingAction.Reprice, carried[0].Verb.Action);
    Assert.Empty(withdrawn);
  }

  [Fact]
  public void AWithdrawnContest_DoesNotFallBackToTheVerbUnderneathIt()
  {
    // The contest owns the lane's ruling, so its re-ask governs the lane. Falling back
    // to the older pinch-raised verb would spend an answer the human has already
    // replaced, and do it on the exact press the board just said it was re-asking.
    var entries = new[]
    {
      new StandingReAsk.Staged<string, string>(
        "pinch-row", "lane", new StagedVerb(StandingAction.Vendor, BoardPile.PullAndVendor), false),
      new StandingReAsk.Staged<string, string>(
        "contest-row", "lane", new StagedVerb(StandingAction.Reprice, BoardPile.Reprice), true),
    };
    var callsNow = new Dictionary<string, BoardPile> { ["lane"] = BoardPile.PullAndVendor };

    var (carried, withdrawn) = StandingReAsk.Represent(entries, callsNow);

    Assert.Empty(carried);
    Assert.Single(withdrawn);
    Assert.Equal("lane", withdrawn[0].Lane);
  }

  [Fact]
  public void TheContestWinsFromEitherSideOfTheList()
  {
    // Order in must not decide the ruling: the same board re-presents the same way
    // whichever half of the pair the caller happens to feed first.
    foreach (var contestFirst in new[] { true, false })
    {
      var pinchRow = new StandingReAsk.Staged<string, string>(
        "pinch-row", "lane", new StagedVerb(StandingAction.Vendor, BoardPile.PullAndVendor), false);
      var contestRow = new StandingReAsk.Staged<string, string>(
        "contest-row", "lane", new StagedVerb(StandingAction.Reprice, BoardPile.Reprice), true);
      var entries = contestFirst
        ? new[] { contestRow, pinchRow }
        : new[] { pinchRow, contestRow };

      var (carried, _) = StandingReAsk.Represent(entries,
        new Dictionary<string, BoardPile> { ["lane"] = BoardPile.Reprice });

      Assert.Single(carried);
      Assert.Equal("contest-row", carried[0].Row);
    }
  }

  [Fact]
  public void TwoPinchRowsOnOneLane_ResolveToExactlyOneEntry()
  {
    // A synthetic flag row and a run row can both carry a verb for the same lane. One
    // lane, one ruling still holds - the caller's order decides, and it is stable.
    var entries = new[]
    {
      new StandingReAsk.Staged<string, string>(
        "first", "lane", new StagedVerb(StandingAction.Reprice, BoardPile.Reprice), false),
      new StandingReAsk.Staged<string, string>(
        "second", "lane", new StagedVerb(StandingAction.Vendor, BoardPile.PullAndVendor), false),
    };

    var (carried, withdrawn) = StandingReAsk.Represent(entries,
      new Dictionary<string, BoardPile> { ["lane"] = BoardPile.Reprice });

    Assert.Single(carried);
    Assert.Equal("first", carried[0].Row);
    Assert.Empty(withdrawn);
  }
}
