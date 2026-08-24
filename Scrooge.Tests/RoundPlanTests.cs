using System;
using System.Collections.Generic;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The one-button round's cursor (v2.18). The contract: stages fire in
/// workflow order, empty stages are skipped but not buried (work appearing
/// late is picked up), done is done, and the round completes when nothing
/// with work remains.
/// </summary>
public class RoundPlanTests
{
  private static bool All(RoundStage _) => true;

  /// <summary>
  /// A work predicate for the ACT HALF ALONE - the four stages whose executors are
  /// runs. It used to be the test project's copy of RoundPlan.HasExecutor, the
  /// campaign's interim register of what this build could run; unit 5 deleted that
  /// register (every stage has an executor now, the hinge's being a human press), and
  /// this survives as what it always actually was in these tests: a way to walk the
  /// cursor over the irreversible half without the Look half's two stages arming on
  /// every assertion. Tests that care about the hinge use <see cref="All"/>.
  /// </summary>
  private static bool Built(RoundStage s)
    => s is RoundStage.Pinch or RoundStage.Desynth or RoundStage.BellRun or RoundStage.TurnIn;


  [Fact]
  public void Order_IsTheRuledSentence_MeltFeedsTheBell()
  {
    // Drift, 07-25: "figure out what to melt, melt it, THEN post gained mats +
    // other sellables." The 07-24 lap ran bell-before-melt and 18 melts' worth
    // of yields sat in the bags with no prompt. And there is no Reprice stage:
    // the bell absorbed it - the bell is everything that needs a retainer.
    // Rounds (08-10) wrapped that sentence in the Look/Act waist without
    // disturbing it: the melt still precedes the bell.
    Assert.Equal(new[]
    {
      RoundStage.Pinch, RoundStage.Recon, RoundStage.Triage,
      RoundStage.Desynth, RoundStage.BellRun, RoundStage.TurnIn,
    }, RoundPlan.Order);
  }

  [Fact]
  public void Order_ReconPrecedesEveryIrreversibleStage_TheInterlock()
  {
    // THE ONE ORDERING RULE THAT IS NOT ABOUT EFFICIENCY. The melt destroys the
    // item; a melt that ran before its List door saw a real board is answering
    // "worth more melted than listed?" off banked guesses, and the wrong answer
    // cannot be undone the way a bad price can. So the read half finishes before
    // anything irreversible starts.
    //
    // Asserted positionally rather than by eyeballing the array above, because
    // the failure mode is a future edit that reorders the list for some good
    // local reason and costs real items for a bad global one.
    var recon = Array.IndexOf(RoundPlan.Order, RoundStage.Recon);
    var triage = Array.IndexOf(RoundPlan.Order, RoundStage.Triage);

    Assert.True(recon >= 0, "Recon is in the order");
    Assert.True(triage > recon, "the hinge judges what recon banked, so it follows it");

    foreach (var irreversible in new[]
      { RoundStage.Desynth, RoundStage.BellRun, RoundStage.TurnIn })
    {
      var at = Array.IndexOf(RoundPlan.Order, irreversible);
      Assert.True(recon < at, $"recon must precede {irreversible} - nothing spends before the look");
      Assert.True(triage < at, $"the hinge must precede {irreversible} - nothing spends unruled");
    }

    // And the Look half is genuinely a half: both its stages come before both
    // halves of the waist-and-act remainder.
    Assert.True(Array.IndexOf(RoundPlan.Order, RoundStage.Pinch) < recon,
      "pinch and recon are one errand at one stop, pinch first");
  }


  [Fact]
  public void ReLook_ReOpensTheWaist_TheCursorGoesBackThroughTheHinge()
  {
    // THE RE-LOOK RULING (Drift, addendum 2): "Re-Look returns the Round to the Look
    // half, and the waist re-asserts." The cursor half of that needs no new machinery -
    // un-marking Recon and Triage is enough, because Next already stops at the first
    // unfinished stage with work. This asserts the shape the window relies on.
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    plan.MarkDone(RoundStage.Recon);
    plan.MarkDone(RoundStage.Triage);   // the human pressed Continue

    // Mid-act: the melt is the cursor's business now.
    Assert.Equal(RoundStage.Desynth, plan.Next(All));

    // The Re-Look, as the window performs it.
    plan.Unmark(RoundStage.Recon);
    plan.Unmark(RoundStage.Triage);

    // Recon first - the reads come before the ruling, which is the Look/Act waist.
    Assert.Equal(RoundStage.Recon, plan.Next(All));
    plan.MarkDone(RoundStage.Recon);

    // And then the hinge again, BLOCKING the act half exactly as it did the first
    // time. This is the whole ruling: the act half cannot resume until a human presses
    // Continue against the fresh reads.
    Assert.Equal(RoundStage.Triage, plan.Next(All));
    Assert.False(plan.IsDone(RoundStage.Triage));
  }

  [Fact]
  public void ReLook_BeforeTheHingeEverCommitted_IsUnchanged()
  {
    // Pre-commit the extra un-mark is a no-op: Triage was never marked, so the cursor
    // lands where it always did. The ruling widened the verb without changing it.
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    plan.MarkDone(RoundStage.Recon);

    Assert.Equal(RoundStage.Triage, plan.Next(All));

    plan.Unmark(RoundStage.Recon);
    plan.Unmark(RoundStage.Triage);

    Assert.Equal(RoundStage.Recon, plan.Next(All));
    Assert.False(plan.IsDone(RoundStage.Triage));
  }

  [Fact]
  public void StageValues_AreFrozen_SoAPersistedRoundStillMeansWhatItSaid()
  {
    // The persisted Done list stores these as numbers. Renumbering the survivors
    // when Reprice was deleted would have re-read a saved "Desynth" as "TurnIn"
    // on the next reload; 2 is a retired hole forever.
    Assert.Equal(0, (int)RoundStage.Pinch);
    Assert.Equal(1, (int)RoundStage.BellRun);
    Assert.Equal(3, (int)RoundStage.Desynth);
    Assert.Equal(4, (int)RoundStage.TurnIn);
    // The Rounds stages took the next free numbers, NOT the retired 2 - a
    // persisted round from before this build carries 2 meaning "Reprice", and
    // Recon inheriting it would resurrect a dead mark under a live name.
    Assert.Equal(5, (int)RoundStage.Recon);
    Assert.Equal(6, (int)RoundStage.Triage);
    Assert.NotEqual(2, (int)RoundStage.Recon);
    Assert.NotEqual(2, (int)RoundStage.Triage);
  }

  [Fact]
  public void PlaceOf_ThreeStops()
  {
    Assert.Equal(RoundPlace.Bell, RoundPlan.PlaceOf(RoundStage.Pinch));
    Assert.Equal(RoundPlace.Bell, RoundPlan.PlaceOf(RoundStage.BellRun));
    Assert.Equal(RoundPlace.Anywhere, RoundPlan.PlaceOf(RoundStage.Desynth));
    Assert.Equal(RoundPlace.ExpertDelivery, RoundPlan.PlaceOf(RoundStage.TurnIn));
    // Recon is the pinch's other half at the pinch's own stop.
    Assert.Equal(RoundPlace.Bell, RoundPlan.PlaceOf(RoundStage.Recon));
    // The hinge is a conversation, not an errand: naming it a place would send the
    // player on a walk to read his own board.
    Assert.Equal(RoundPlace.Anywhere, RoundPlan.PlaceOf(RoundStage.Triage));
  }

  [Fact]
  public void Next_WalksTheOrderAsStagesComplete()
  {
    var plan = new RoundPlan();
    plan.Start();
    Assert.Equal(RoundStage.Pinch, plan.Next(All));
    plan.MarkDone(RoundStage.Pinch);
    Assert.Equal(RoundStage.Recon, plan.Next(All));
    plan.MarkDone(RoundStage.Recon);
    Assert.Equal(RoundStage.Triage, plan.Next(All));
    plan.MarkDone(RoundStage.Triage);
    Assert.Equal(RoundStage.Desynth, plan.Next(All));
    plan.MarkDone(RoundStage.Desynth);
    Assert.Equal(RoundStage.BellRun, plan.Next(All));
  }

  // ---- The triage hinge: contract B, and what it costs the cursor ----

  [Fact]
  public void Triage_FiredButNotDone_BlocksTheCursor_ContractB()
  {
    // THE HINGE'S WHOLE POINT. Triage is contract B (the melt precedent, 07-26):
    // offered and fired like any stage, but marked done ONLY on the human's
    // Continue press - never at fire time. Presenting the page is not ruling it.
    //
    // What that buys is enforced by machinery that already existed: Next stops at
    // the first unfinished stage that has work, so a Triage that declines to mark
    // itself is a wall. No act stage can be offered around it, however much work
    // the act half is holding and however long the player stares at the page.
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    plan.MarkDone(RoundStage.Recon);

    // The hinge is presented. That is the FIRE - and it marks nothing.
    Assert.Equal(RoundStage.Triage, plan.Next(All));
    Assert.False(plan.IsDone(RoundStage.Triage));

    // Ask again on the next frame, and the next, and the next. Every act stage
    // below is armed and every one of them stays behind the wall.
    Assert.Equal(RoundStage.Triage, plan.Next(All));
    Assert.Equal(RoundStage.Triage, plan.Next(All));

    // The human presses Continue. THAT is the only thing that opens it.
    plan.MarkDone(RoundStage.Triage);
    Assert.Equal(RoundStage.Desynth, plan.Next(All));
  }

  [Fact]
  public void Triage_BlocksEvenWhenTheActHalfIsWhereThePlayerIsStanding()
  {
    // The opportunistic cursor (WALK unit 6) exists so a player standing at the
    // GC counter is not told to walk to a bell. It must NOT become a door around
    // the hinge: an armed act stage under the player's feet is still an act stage,
    // and the whole waist is the claim that nothing irreversible runs unruled.
    //
    // Offer only ever reaches its opportunistic loop when the LINEAR cursor's own
    // place is unsatisfied - and the hinge's place is Anywhere, so it is always
    // satisfied and the loop is never entered while Triage stands. That is the
    // structural reason, and this is the receipt for it.
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    plan.MarkDone(RoundStage.Recon);

    // The player is standing at Expert Delivery - the turn-in's place, and not
    // the hinge's problem.
    var offer = plan.Offer(All, s => s == RoundStage.TurnIn || RoundPlan.PlaceOf(s) == RoundPlace.Anywhere);

    Assert.NotNull(offer);
    Assert.Equal(RoundStage.Triage, offer!.Value.Stage);
    Assert.False(offer.Value.Opportunistic);
  }

  [Fact]
  public void Triage_WithNothingToJudge_IsSkippedLikeAnyEmptyStage()
  {
    // The hinge is a checkpoint, not a ceremony. "No decisions and nothing staged"
    // is Empty, and an empty stage is walked past in silence and NOT marked done -
    // so a round that grows work later still stops at the hinge on its way through.
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);

    Assert.Equal(RoundStage.Desynth, plan.Next(Built));
    Assert.False(plan.IsDone(RoundStage.Triage));
  }

  [Fact]
  public void Next_SkipsEmptyStages()
  {
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    // Nothing to melt - the cursor lands on the bell.
    Assert.Equal(RoundStage.BellRun,
      plan.Next(s => s is RoundStage.BellRun or RoundStage.TurnIn));
  }

  [Fact]
  public void Next_PicksUpWorkThatAppearsLate()
  {
    // A melt yields listable mats AFTER the cursor skipped past an empty bell:
    // skipped-empty is not marked done, so the stage is picked up in order.
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    Assert.Equal(RoundStage.TurnIn, plan.Next(s => s == RoundStage.TurnIn));
    Assert.Equal(RoundStage.BellRun,
      plan.Next(s => s is RoundStage.BellRun or RoundStage.TurnIn));
  }

  [Fact]
  public void Unmark_HandsAnAbortedStageBackToTheCursor()
  {
    // The 07-22 round lap: melt fired (marked done at fire time), the run died
    // over an open bell, and the deck kept claiming the melt was done. Unmark
    // reverts the fire-time promise so the cursor offers the stage again.
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    plan.MarkDone(RoundStage.Desynth);
    Assert.Equal(RoundStage.TurnIn,
      plan.Next(s => s is RoundStage.Desynth or RoundStage.TurnIn));
    plan.Unmark(RoundStage.Desynth);
    Assert.Equal(RoundStage.Desynth,
      plan.Next(s => s is RoundStage.Desynth or RoundStage.TurnIn));
  }

  [Fact]
  public void Unmark_OfAnUnmarkedStage_IsANoOp()
  {
    var plan = new RoundPlan();
    plan.Start();
    plan.Unmark(RoundStage.Desynth);
    Assert.False(plan.IsDone(RoundStage.Desynth));
    Assert.Equal(RoundStage.Pinch, plan.Next(All));
  }

  [Fact]
  public void Next_NullWhenEverythingLeftIsDoneOrEmpty()
  {
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    plan.MarkDone(RoundStage.Desynth);
    Assert.Null(plan.Next(s => s is RoundStage.Pinch or RoundStage.Desynth));
  }

  [Fact]
  public void StartAndCancel_ResetTheCursor()
  {
    var plan = new RoundPlan();
    Assert.False(plan.Active);
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    plan.Cancel();
    Assert.False(plan.Active);
    plan.Start();
    // A fresh round forgets the last one's progress.
    Assert.Equal(RoundStage.Pinch, plan.Next(All));
  }

  // ---- HALT-NAME-RESUME (WALK unit 2) ----

  [Fact]
  public void Halt_HoldsThePlace_NeverOffersPastACorpse()
  {
    // The melt fires (marked done at fire time) and dies mid-run. Halt holds the
    // place: the completed pinch stays done, but the deck must NOT offer the next
    // stage past the corpse - Next returns null while halted.
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    plan.MarkDone(RoundStage.Desynth);
    plan.Halt(RoundHalt.Plainly(RoundStage.Desynth, "Melt", "the run stopped", "Resume."));

    Assert.True(plan.Halted);
    Assert.Equal(RoundStage.Desynth, plan.HaltStage);
    Assert.True(plan.IsDone(RoundStage.Pinch)); // completion marks held
    Assert.False(plan.IsDone(RoundStage.Desynth)); // the corpse is not "done"
    Assert.Null(plan.Next(All)); // never past the corpse
  }

  [Fact]
  public void Resume_ReOffersTheHaltedStageOnly_NeverFromTheTop()
  {
    // Resume re-fires the dead stage from its own rescan: the halted stage becomes
    // current again, and the completed pinch is NEVER re-offered (a re-running
    // pinch is the market-pressure shape the cadence gate exists to prevent).
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    plan.MarkDone(RoundStage.BellRun);
    plan.MarkDone(RoundStage.Desynth);
    plan.Halt(RoundHalt.Plainly(RoundStage.Desynth, "Melt", "the run stopped", "Resume."));

    plan.Resume();
    Assert.False(plan.Halted);
    // Only the halted stage is re-offered; the done stages stay buried. Asked
    // through Built, because an un-run Look half upstream of the corpse would
    // otherwise answer first - which is the stubs' correct behaviour, not this
    // test's subject.
    Assert.Equal(RoundStage.Desynth, plan.Next(Built));
    Assert.True(plan.IsDone(RoundStage.Pinch));
    Assert.True(plan.IsDone(RoundStage.BellRun));
  }

  [Fact]
  public void Resume_SkipsTheHaltedStageWhenItsRescanFindsNoWork()
  {
    // The melt's own rescan (fire-time work list) is now empty - Resume must not
    // wedge on an empty stage; the cursor skips it forward like any empty stage.
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    plan.MarkDone(RoundStage.Desynth);
    plan.Halt(RoundHalt.Plainly(RoundStage.Desynth, "Melt", "the run stopped", "Resume."));
    plan.Resume();

    // Desynth has no work now, TurnIn does.
    Assert.Equal(RoundStage.TurnIn, plan.Next(s => s == RoundStage.TurnIn));
  }

  [Fact]
  public void HaltVsUnmark_QuietRevertVersusLoudStop()
  {
    // Unmark: quiet revert - stage straight back onto the cursor, no halt.
    var quiet = new RoundPlan();
    quiet.Start();
    quiet.MarkDone(RoundStage.Pinch);
    quiet.MarkDone(RoundStage.BellRun);
    quiet.MarkDone(RoundStage.Desynth);
    quiet.Unmark(RoundStage.Desynth);
    Assert.False(quiet.Halted);
    Assert.Equal(RoundStage.Desynth, quiet.Next(Built)); // offered immediately

    // Halt: loud stop - same un-mark of the stage, but Next is blocked until Resume.
    var loud = new RoundPlan();
    loud.Start();
    loud.MarkDone(RoundStage.Pinch);
    loud.MarkDone(RoundStage.BellRun);
    loud.MarkDone(RoundStage.Desynth);
    loud.Halt(RoundHalt.Plainly(RoundStage.Desynth, "Melt", "the run stopped", "Resume."));
    Assert.True(loud.Halted);
    Assert.False(loud.IsDone(RoundStage.Desynth)); // both revert the stage mark
    Assert.Null(loud.Next(All)); // but Halt blocks the cursor
  }

  [Fact]
  public void Halt_NamesTheGap_PlainlyAndInSpineVocabulary()
  {
    // Plainly: a death that is not a spine facet (server timeout) - what died and
    // what would clear it.
    var plain = RoundHalt.Plainly(RoundStage.Desynth, "Melt",
      "timeout waiting for SalvageResult", "Clear it and Resume.");
    Assert.Equal(RoundStage.Desynth, plain.Stage);
    Assert.Contains("Melt halted", plain.Message);
    Assert.Contains("timeout waiting for SalvageResult", plain.Message);
    Assert.Contains("Resume", plain.Message);

    // FromSpine: a death whose reason maps to a declared expectation borrows the
    // evaluation's "expected X, but Y" message verbatim.
    var eval = SpineEvaluator.Evaluate(
      new ExpectedState("melt",
        new SpineExpectation(Spine.Facet.Occupancy, "an un-occupied player", Spine.Rung.Refuse)),
      new[] { new FacetReading(false, "the retainer bell is open") });
    var spun = RoundHalt.FromSpine(RoundStage.Desynth, eval);
    Assert.Equal(eval.Message, spun.Message);
    Assert.Equal(RoundStage.Desynth, spun.Stage);
  }

  // ---- Persistence: the held place survives a reload ----

  [Fact]
  public void ExportRestore_RoundTripsTheHeldPlace()
  {
    var started = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    var plan = new RoundPlan();
    plan.Start(started);
    plan.MarkDone(RoundStage.Pinch);
    plan.MarkDone(RoundStage.BellRun);
    plan.MarkDone(RoundStage.Desynth);
    plan.Halt(RoundHalt.Plainly(RoundStage.Desynth, "Melt", "the run stopped", "Resume."));

    var state = plan.Export();

    var restored = new RoundPlan();
    restored.Restore(state);

    Assert.True(restored.Active);
    Assert.True(restored.IsDone(RoundStage.Pinch));
    Assert.True(restored.IsDone(RoundStage.BellRun));
    Assert.False(restored.IsDone(RoundStage.Desynth)); // halted, not done
    Assert.True(restored.Halted);
    Assert.Equal(RoundStage.Desynth, restored.HaltStage);
    Assert.Equal(plan.CurrentHalt!.Message, restored.CurrentHalt!.Message);
    Assert.Equal(started, restored.StartedAt);
    // The deck shows the same stages done/current/halted as before the reload.
    Assert.Null(restored.Next(All));
  }

  // ---- The Re-Look latch: spent by the work, and it survives a reload (S6) ----

  /// <summary>
  /// THE PRESS OUTLIVES THE RELOAD. The Re-Look's entire meaning is "ignore the
  /// freshness window", and the window it is overruling is 24 hours wide - so a latch
  /// dropped by a reload does not degrade, it INVERTS: the resumed recon finds every
  /// decision fresh, walks nothing, and the rail checks the stage off as though the
  /// boards had been re-read.
  /// </summary>
  [Fact]
  public void ExportRestore_AnArmedReLookSurvivesTheReload()
  {
    var plan = new RoundPlan();
    plan.Start(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    plan.MarkDone(RoundStage.Pinch);
    plan.ArmReLook();

    var state = plan.Export();
    Assert.True(state.ReLookPending);

    var restored = new RoundPlan();
    restored.Restore(state);

    Assert.True(restored.ReLookArmed);
  }

  /// <summary>
  /// Spent means spent, on both sides of a reload. The opposite failure to the one
  /// above and just as bad: a consumed latch that re-armed itself on restore would
  /// send a plain Resume back over the whole listable bag.
  /// </summary>
  [Fact]
  public void ExportRestore_AConsumedReLookStaysConsumed()
  {
    var plan = new RoundPlan();
    plan.Start(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    plan.ArmReLook();
    plan.ConsumeReLook();

    Assert.False(plan.ReLookArmed);

    var restored = new RoundPlan();
    restored.Restore(plan.Export());

    Assert.False(restored.ReLookArmed);
  }

  /// <summary>
  /// A state written by a build that never had the column reads false - a round with
  /// no Re-Look owed, which is the fail-closed answer (an armed default would widen
  /// the first recon after an upgrade to the whole bag for no reason anyone pressed).
  /// </summary>
  [Fact]
  public void Restore_APreS6HeldPlace_OwesNoReLook()
  {
    var state = new RoundState
    {
      Active = true,
      Done = new List<RoundStage> { RoundStage.Pinch },
      StartedAtUnix = 1_700_000_000,
    };

    var restored = new RoundPlan();
    restored.Restore(state);

    Assert.False(restored.ReLookArmed);
  }

  /// <summary>
  /// The latch belongs to ONE round. A press that outlived its errand would re-read
  /// every board on the next round's first recon - the same "a skip never outlives the
  /// run it was meant for" discipline the stage boxes already have.
  /// </summary>
  [Fact]
  public void StartAndCancel_BothDisarmTheReLook()
  {
    var plan = new RoundPlan();
    plan.Start(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    plan.ArmReLook();

    plan.Cancel();
    Assert.False(plan.ReLookArmed);

    plan.Start(DateTimeOffset.FromUnixTimeSeconds(1_700_001_000));
    plan.ArmReLook();
    plan.Start(DateTimeOffset.FromUnixTimeSeconds(1_700_002_000));
    Assert.False(plan.ReLookArmed);
  }

  [Fact]
  public void ExportRestore_FlowingRoundSurvivesWithoutAHalt()
  {
    var plan = new RoundPlan();
    plan.Start(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    plan.MarkDone(RoundStage.Pinch);

    var restored = new RoundPlan();
    restored.Restore(plan.Export());

    Assert.False(restored.Halted);
    Assert.Null(restored.CurrentHalt);
    Assert.Equal(RoundStage.Desynth, restored.Next(Built));
  }

  [Fact]
  public void Restore_DropsAMarkForARetiredStage_TheRoundReDerives()
  {
    // A round persisted by the old build, mid-run, with the deleted Reprice stage
    // (value 2) marked done. The restore must not crash and must not carry a
    // done-flag no stage can clear: the mark is dropped and the cursor re-derives
    // from the stage list this build actually runs. The reprice rows themselves
    // are not lost - they are part of the bell now.
    var state = new RoundState
    {
      Active = true,
      Done = new List<RoundStage> { RoundStage.Pinch, (RoundStage)2 },
      StartedAtUnix = 1_700_000_000,
    };

    var restored = new RoundPlan();
    restored.Restore(state);

    Assert.True(restored.Active);
    Assert.True(restored.IsDone(RoundStage.Pinch));
    Assert.False(restored.IsDone((RoundStage)2));
    Assert.Equal(RoundStage.Desynth, restored.Next(Built));
  }

  [Fact]
  public void Restore_APreRoundsHeldPlace_LandsCleanOnTheNewOrder()
  {
    // THE UPGRADE ITSELF (08-10). A round persisted by the pre-Rounds build knows
    // four stages; this build runs six. Nothing new is needed to survive that -
    // Restore already keeps only marks for stages in the current Order, and all
    // four old stages are still in it - but "already covered" is a claim, and the
    // new Order is what it is being claimed about.
    var state = new RoundState
    {
      Active = true,
      Done = new List<RoundStage> { RoundStage.Pinch, RoundStage.Desynth },
      StartedAtUnix = 1_700_000_000,
    };

    var restored = new RoundPlan();
    restored.Restore(state);

    // Every old mark survived - none of the four stages was retired by Rounds.
    Assert.True(restored.IsDone(RoundStage.Pinch));
    Assert.True(restored.IsDone(RoundStage.Desynth));

    // And the two NEW stages arrive un-done, which is the honest answer: a round
    // that predates recon never ran one. The cursor picks them up in order, so an
    // upgraded round walks the Look half it never had rather than skipping to the
    // act half on the strength of marks it does not hold.
    Assert.False(restored.IsDone(RoundStage.Recon));
    Assert.False(restored.IsDone(RoundStage.Triage));
    Assert.Equal(RoundStage.Recon, restored.Next(All));

    // With the stubs answering (which is what the live plugin does today), the
    // upgraded round simply resumes where it was: the bell.
    Assert.Equal(RoundStage.BellRun, restored.Next(Built));
  }

  [Fact]
  public void Restore_DropsAMarkForAStageThisBuildDoesNotRun()
  {
    // The Order-membership rule is the general one, not a special case for the
    // retired Reprice. Any value outside the current list is dropped - including
    // one from a FUTURE build a player downgraded from, which is the case nobody
    // writes a migration for and everybody eventually hits.
    var state = new RoundState
    {
      Active = true,
      Done = new List<RoundStage> { RoundStage.Pinch, (RoundStage)99 },
      StartedAtUnix = 1_700_000_000,
    };

    var restored = new RoundPlan();
    restored.Restore(state);

    Assert.True(restored.IsDone(RoundStage.Pinch));
    Assert.False(restored.IsDone((RoundStage)99));
  }

  [Fact]
  public void Restore_DropsAHaltHeldOverARetiredStage()
  {
    // A halt over a stage that no longer exists could never be resumed past -
    // the cursor would offer nothing forever. It goes with the stage.
    var state = new RoundState
    {
      Active = true,
      Done = new List<RoundStage> { RoundStage.Pinch },
      HaltStage = (RoundStage)2,
      HaltMessage = "Reprice halted - the run stopped.",
      StartedAtUnix = 1_700_000_000,
    };

    var restored = new RoundPlan();
    restored.Restore(state);

    Assert.False(restored.Halted);
    Assert.Equal(RoundStage.Desynth, restored.Next(Built));
  }

  // ---- Staleness: a round too old to trust is history, not a round ----

  [Fact]
  public void IsStale_RetiresARoundPastTheCeiling()
  {
    var started = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    var ceiling = TimeSpan.FromHours(4);

    // Within the ceiling: still a round.
    Assert.False(RoundPlan.IsStale(started, started.AddHours(3), ceiling));
    Assert.False(RoundPlan.IsStale(started, started.AddHours(4), ceiling)); // exactly the ceiling holds
    // Past the ceiling: history.
    Assert.True(RoundPlan.IsStale(started, started.AddHours(4).AddSeconds(1), ceiling));
    Assert.True(RoundPlan.IsStale(started, started.AddDays(1), ceiling));
  }

  // ---- The location-aware cursor (WALK unit 6) ----
  //
  // The strict cursor told a player standing at Expert Delivery to walk to a
  // retainer bell, because the pinch re-armed upstream mid-walk (07-24).

  private static Func<RoundStage, bool> At(params RoundStage[] reachable)
  {
    var set = new HashSet<RoundStage>(reachable);
    return s => set.Contains(s);
  }

  private static Func<RoundStage, bool> Nowhere => _ => false;

  [Fact]
  public void Offer_TheLinearCursorWinsWhenItsPlaceIsSatisfied()
  {
    var plan = new RoundPlan();
    plan.Start();

    // Standing at the bell with everything armed: ordinary flow, untouched.
    var offer = plan.Offer(All, At(RoundStage.Pinch, RoundStage.TurnIn));
    Assert.Equal(RoundStage.Pinch, offer!.Value.Stage);
    Assert.False(offer.Value.Opportunistic);
  }

  [Fact]
  public void Offer_TheGcCounterCase_ARearmingPinchDoesNotBlockTheTurnIn()
  {
    // The exact 07-24 shape: bell, reprice and melt are done; the player walked
    // to the GC counter for the turn-in; mid-walk the board crossed the re-pinch
    // floor and the pinch re-armed. The strict cursor snaps back to the pinch.
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.BellRun);
    plan.MarkDone(RoundStage.Desynth);

    Assert.Equal(RoundStage.Pinch, plan.Next(All)); // the strict answer, still true

    // ...but the player is standing at Expert Delivery, and the turn-in is armed.
    var offer = plan.Offer(All, At(RoundStage.TurnIn));
    Assert.Equal(RoundStage.TurnIn, offer!.Value.Stage);
    Assert.True(offer.Value.Opportunistic);
  }

  [Fact]
  public void Offer_OnlyArmedStagesAreOffered()
  {
    // Standing at the GC counter with nothing to turn in: the turn-in has no
    // work, so it is not a candidate and the deck still names the pinch's walk.
    var plan = new RoundPlan();
    plan.Start();
    bool HasWork(RoundStage s) => s != RoundStage.TurnIn;

    var offer = plan.Offer(HasWork, At(RoundStage.TurnIn));
    Assert.Equal(RoundStage.Pinch, offer!.Value.Stage);
    Assert.False(offer.Value.Opportunistic);
  }

  [Fact]
  public void Offer_ADoneStageIsNeverReOffered()
  {
    // Standing where a finished stage happened does not re-fire it.
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.TurnIn);

    var offer = plan.Offer(All, At(RoundStage.TurnIn));
    Assert.Equal(RoundStage.Pinch, offer!.Value.Stage);
    Assert.False(offer.Value.Opportunistic);
  }

  [Fact]
  public void Offer_NowhereReachableStillNamesTheLinearStage()
  {
    // The deck needs a stage to render "walk to X" against.
    var plan = new RoundPlan();
    plan.Start();

    var offer = plan.Offer(All, Nowhere);
    Assert.Equal(RoundStage.Pinch, offer!.Value.Stage);
    Assert.False(offer.Value.Opportunistic);
  }

  [Fact]
  public void Offer_NeverOffersPastACorpse()
  {
    // The halt rule outranks the location rule: standing at the GC counter does
    // not let the round flow past a stage that died.
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Desynth);
    plan.Halt(RoundHalt.Plainly(RoundStage.Desynth, "Melt", "it died", "Resume it."));

    Assert.Null(plan.Offer(All, At(RoundStage.TurnIn)));
  }

  [Fact]
  public void Offer_ACompleteRoundOffersNothing()
  {
    var plan = new RoundPlan();
    plan.Start();
    Assert.Null(plan.Offer(_ => false, At(RoundStage.TurnIn)));
  }

  // ---- Fail-closed restore (2026-07-26, the zombie round) ----

  [Fact]
  public void HoldForRestore_ParksTheCursorOnTheFirstUnfinishedStage()
  {
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    plan.HoldForRestore("restored");

    Assert.True(plan.Halted);
    // The FIRST unfinished stage in Order - which since Rounds is Recon, not the
    // melt. The hold walks the array, so it inherits the waist for free: a
    // restored round parks in the Look half and cannot resume straight into
    // anything irreversible.
    Assert.Equal(RoundStage.Recon, plan.HaltStage);
    // Holding means NO auto-fire - even standing exactly where the stage happens.
    Assert.Null(plan.Offer(All, At(RoundStage.Recon)));
  }

  [Fact]
  public void HoldForRestore_ARealHaltOutranksThePrecaution()
  {
    var plan = new RoundPlan();
    plan.Start();
    plan.Halt(RoundHalt.Plainly(RoundStage.Desynth, "Melt", "it died", "Resume it."));
    plan.HoldForRestore("restored");

    Assert.Contains("died", plan.CurrentHalt!.Message);
  }

  [Fact]
  public void HoldForRestore_DoesNotRevertACompletionMark()
  {
    // Halt() un-marks its stage (a death means "did not finish"); the restore hold
    // is a precaution, not a death, and must leave every mark standing.
    var plan = new RoundPlan();
    plan.Start();
    plan.MarkDone(RoundStage.Pinch);
    plan.HoldForRestore("restored");

    Assert.True(plan.IsDone(RoundStage.Pinch));
  }

  [Fact]
  public void HoldForRestore_AFullyDoneRoundIsLeftForTheErrandOverRule()
  {
    var plan = new RoundPlan();
    plan.Start();
    foreach (var s in RoundPlan.Order) plan.MarkDone(s);
    plan.HoldForRestore("restored");

    Assert.False(plan.Halted);
  }

  [Fact]
  public void HoldForRestore_ResumeIsTheOneDoorOut()
  {
    var plan = new RoundPlan();
    plan.Start();
    plan.HoldForRestore("restored");
    plan.Resume();

    Assert.Equal(RoundStage.Pinch, plan.Offer(All, At(RoundStage.Pinch))!.Value.Stage);
  }

  // ==========================================================================
  // The RunId join (Rounds unit 4): the cursor, the banked transcript and the
  // decision-cache rows agree about WHICH round they belong to
  // ==========================================================================

  [Fact]
  public void RunId_RidesTheRoundFromStartToCancel()
  {
    var plan = new RoundPlan();
    Assert.Equal(0, plan.RunId);

    plan.Start(DateTimeOffset.UnixEpoch.AddHours(1), runId: 77);
    Assert.Equal(77, plan.RunId);

    plan.Cancel();
    Assert.Equal(0, plan.RunId);
  }

  /// <summary>
  /// A round with no banked run (storage down, or a config from before V40) is a
  /// WORKING round: the cursor, the marks and the halt machinery are untouched, and
  /// only the transcript's durability is lost. Nothing here may fail closed on 0.
  /// </summary>
  [Fact]
  public void RunId_ZeroIsAWorkingRound()
  {
    var plan = new RoundPlan();
    plan.Start();

    Assert.Equal(0, plan.RunId);
    Assert.True(plan.Active);
    Assert.Equal(RoundStage.Pinch, plan.Next(All));
  }

  /// <summary>
  /// THE JOIN SURVIVES THE RELOAD. A restored round that lost its run id would go on
  /// writing transcript lines and cache rows under a different identity from the ones
  /// it wrote before the reload - which is the exact failure the column exists to make
  /// impossible.
  /// </summary>
  [Fact]
  public void RunId_RoundTripsThroughExportAndRestore()
  {
    var plan = new RoundPlan();
    plan.Start(DateTimeOffset.UnixEpoch.AddHours(1), runId: 77);
    plan.MarkDone(RoundStage.Pinch);

    var restored = new RoundPlan();
    restored.Restore(plan.Export());

    Assert.Equal(77, restored.RunId);
    Assert.True(restored.IsDone(RoundStage.Pinch));
  }

  /// <summary>
  /// A pre-V40 state carries no id, which reads back as 0 rather than as a throw:
  /// adding a PROPERTY to the persisted class is the safe half of the config-shape
  /// rule (renaming the class is the fatal half - see RoundStage's receipts).
  /// </summary>
  [Fact]
  public void RunId_AStateFromBeforeTheColumnRestoresAsUnbanked()
  {
    var plan = new RoundPlan();
    plan.Restore(new RoundState
    {
      Active = true,
      Done = new List<RoundStage> { RoundStage.Pinch },
      StartedAtUnix = 1_700_000_000,
    });

    Assert.Equal(0, plan.RunId);
    Assert.True(plan.Active);
  }

  /// <summary>The legacy fold carries the round forward unbanked, and says so.</summary>
  [Fact]
  public void RunId_TheLegacyFoldProducesAnUnbankedRound()
  {
    var (round, _, _, migrated) = LegacyRoundConfig.Fold(
      null,
      new SweepState { Active = true, StartedAtUnix = 1_700_000_000 },
      12, null, true, null);

    Assert.True(migrated);
    Assert.Equal(0, round!.RunId);
  }
}
