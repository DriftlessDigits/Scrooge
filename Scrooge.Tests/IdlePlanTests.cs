using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// WHAT THE IDLE ROUNDS SCREEN SAYS (SF-P1, ruled 2026-08-15). Three sentences, and
/// every one of them is about state: the plan verticalized into stage rows, the ONE
/// count of decisions waiting, and what the last round did.
///
/// <para>The specimens are verbatim on purpose. This screen is the first thing a
/// player sees when he presses Round at the bell, and its wording is the ruled voice -
/// pinned here so a red-pen pass has one place to happen and one place to be caught.</para>
/// </summary>
public class IdlePlanTests
{
  private static readonly HashSet<RoundStage> None = new();

  /// <summary>
  /// The idle deck's own answer, built the way the window builds it: no round, so
  /// nothing is done, current or halted, and no run is in flight.
  /// </summary>
  private static List<RailRow> IdleRows(
    Func<RoundStage, int> countOf, Func<RoundStage, bool> hasWork,
    Func<RoundStage, float> pace, int pinchAsks = 0)
    => StageRail.Build(countOf, hasWork, _ => false, current: null, halted: null,
      pace, liveRunEtaMs: null, pinchAsks);

  private static string LineFor(IEnumerable<IdleRow> plan, RoundStage stage)
    => plan.First(r => r.Stage == stage).Line;

  // ---- The plan, verticalized -----------------------------------------------

  [Fact]
  public void Plan_DrawsEveryStepInRoundOrder()
  {
    // The rail's own ruling, inherited: an empty or deferred step is dithered, never
    // dropped. A plan that silently shortened itself would make "is it going to melt?"
    // a question the player answers by counting rows.
    var rows = IdleRows(_ => 0, _ => false, _ => 0f);
    var plan = IdlePlan.Plan(rows, None);

    Assert.Equal(RoundPlan.Order, plan.Select(r => r.Stage).ToArray());
  }

  [Fact]
  public void Plan_TheArmedPinchPricesTheAsksItWillVisit()
  {
    // SF-P2's ruling, on the screen that comes BEFORE the run: the pinch stages
    // nothing, so pace x holding is zero and the row used to say "no timing yet" over
    // an errand the fit check two lines down had already costed. The asks it will walk
    // are its load, and they are the deck's own PinchAsks - the fit line's operand.
    var rows = IdleRows(_ => 0, s => s == RoundStage.Pinch, _ => 7000f, pinchAsks: 75);
    var plan = IdlePlan.Plan(rows, None);

    Assert.Equal("pinch - ~9m", LineFor(plan, RoundStage.Pinch));
  }

  [Fact]
  public void Plan_AStepSaysWhatItIsHoldingAndWhatItWillCost()
  {
    // Recon's honest operand is the STALE half of the listable bag - what the pass
    // actually walks - and its ETA is that count times recon's own banked pace.
    var rows = IdleRows(s => s == RoundStage.Recon ? 12 : 0, s => s == RoundStage.Recon,
      _ => 20000f);
    var plan = IdlePlan.Plan(rows, None);

    Assert.Equal("recon (12) - ~4m", LineFor(plan, RoundStage.Recon));
  }

  [Fact]
  public void Plan_TheHingeWaitsOnYou()
  {
    // Not "no timing yet" - there is no measurement coming, ever, because the executor
    // is a human reading his own board (StageRail.Hinge). The count is the deck's
    // RulingsNeeded, the same number the Continue refuses over.
    var rows = IdleRows(s => s == StageRail.Hinge ? 3 : 0, s => s == StageRail.Hinge,
      _ => 20000f);
    var plan = IdlePlan.Plan(rows, None);

    Assert.Equal("triage (3) - waits on you", LineFor(plan, StageRail.Hinge));
  }

  [Fact]
  public void Plan_AnEmptyStepSaysNothingToDo()
  {
    // The one honest string (StageRail.EmptyNote): the stage exists, it looked, there
    // was nothing there. No ETA, because there is no time left to spend.
    var rows = IdleRows(_ => 0, _ => false, _ => 20000f);
    var plan = IdlePlan.Plan(rows, None);

    Assert.Equal("melt  (nothing to do)", LineFor(plan, RoundStage.Desynth));
  }

  [Fact]
  public void Plan_ADeferredStepKeepsItsCountAndSaysItIsSkipped()
  {
    // The count is what the step is HOLDING, which the deferral does not change - the
    // player unchecked a box, he did not empty a pile. The word is the one the old
    // arrow-line used, on the same screen as the checkbox that caused it.
    var skipped = new HashSet<RoundStage> { RoundStage.BellRun };
    var rows = IdleRows(s => s == RoundStage.BellRun ? 30 : 0,
      s => s == RoundStage.BellRun && !skipped.Contains(s), _ => 20000f);
    var plan = IdlePlan.Plan(rows, skipped);

    Assert.Equal("bell (30)  (skipped)", LineFor(plan, RoundStage.BellRun));
    Assert.True(plan.First(r => r.Stage == RoundStage.BellRun).Deferred);
  }

  [Fact]
  public void Plan_TheTurnInCarriesTheSealWalletNote()
  {
    // SealFit.PlanSuffix hung on the turn-in leg of the arrow-line, and it rides the
    // turn-in ROW now. Losing it in the move would have quietly deleted the sensor
    // that says a 52-item turn-in is really a 23-item one.
    var rows = IdleRows(s => s == RoundStage.TurnIn ? 52 : 0, s => s == RoundStage.TurnIn,
      _ => 10000f);
    var plan = IdlePlan.Plan(rows, None, sealFitNote: " (~23 fit)");

    Assert.Equal("turn in (52) - ~9m  (~23 fit)", LineFor(plan, RoundStage.TurnIn));
  }

  [Fact]
  public void Plan_TheWalletNoteAndTheDeferralBothFitOnTheTurnIn()
  {
    var skipped = new HashSet<RoundStage> { RoundStage.TurnIn };
    var rows = IdleRows(s => s == RoundStage.TurnIn ? 52 : 0, _ => false, _ => 10000f);
    var plan = IdlePlan.Plan(rows, skipped, sealFitNote: " (~23 fit)");

    Assert.Equal("turn in (52)  (skipped) (~23 fit)", LineFor(plan, RoundStage.TurnIn));
  }

  [Fact]
  public void Plan_NeverClaimsWorkNobodyHasDone()
  {
    // THE TENSE RULE, STRUCTURALLY. The round log speaks acts in the past tense; a
    // plan speaks present-future, because the round it describes has not run. Nothing
    // about an idle deck should produce a Done row - and if something ever does, the
    // idle screen still may not say "87 lanes banked" about a round with no runs
    // behind it. The tally slot is the deferral note's, never DoneTally's.
    var done = new List<RailRow>
    {
      new(RoundStage.Recon, RailState.Done, 87, null),
    };

    var line = IdlePlan.Plan(done, None).Single().Line;

    Assert.DoesNotContain("banked", line);
    Assert.DoesNotContain(AccountantPlan.DoneTally(RailState.Done, RoundStage.Recon, 87), line);
  }

  // ---- The waiting decisions: ONE sentence ----------------------------------

  [Fact]
  public void WaitingDecisions_SaysTheCountInOneSentence()
  {
    Assert.Equal("3 decisions waiting on you", IdlePlan.WaitingDecisions(3));
  }

  [Fact]
  public void WaitingDecisions_OneIsSingular()
  {
    Assert.Equal("1 decision waiting on you", IdlePlan.WaitingDecisions(1));
  }

  [Fact]
  public void WaitingDecisions_NothingOwedSaysNothing()
  {
    // Absence, not "0 waiting". A night with nothing owed has nothing to report - the
    // same honesty GatePlan.Headline and RoundBanner.Count already keep.
    Assert.Equal("", IdlePlan.WaitingDecisions(0));
    Assert.Equal("", IdlePlan.WaitingDecisions(-1));
  }

  // ---- The last round's tally -----------------------------------------------

  private static readonly DateTimeOffset Ended =
    new(2026, 8, 15, 20, 14, 0, TimeSpan.Zero);

  private static LastRoundTally Banked(params (RoundStage Stage, int Processed)[] stages)
    => LastRoundTally.From(stages.ToDictionary(s => s.Stage, s => s.Processed),
      Ended.ToUnixTimeSeconds());

  [Fact]
  public void LastRoundLine_AbsentUntilARoundHasEnded()
  {
    // A fresh config has no last round, and the idle screen says nothing about one.
    Assert.Equal("", IdlePlan.LastRoundLine(null, Ended, TimeSpan.Zero));
  }

  [Fact]
  public void LastRoundLine_ATallyWithNoTimeRefusesToDraw()
  {
    // History with no date on it, on a screen about tonight, is indistinguishable from
    // a claim about tonight.
    var undated = new LastRoundTally { EndedAtUnix = 0 };
    undated.ProcessedByStage["Recon"] = 87;

    Assert.Equal("", IdlePlan.LastRoundLine(undated, Ended, TimeSpan.Zero));
  }

  [Fact]
  public void LastRoundLine_SaysWhatThePreviousRoundDid()
  {
    // Past tense is correct HERE and only here: this round ran. Every clause is the
    // rail's own DoneTally, in that step's own units - the same sentence its rail said
    // while the round was walking.
    var tally = Banked(
      (RoundStage.Pinch, 75), (RoundStage.Recon, 87), (RoundStage.BellRun, 30),
      (RoundStage.Desynth, 4), (RoundStage.TurnIn, 4));

    Assert.Equal(
      "Last round ended 20:14 (42 minutes ago) - 75 lanes read, 87 lanes banked, 4 melted, 30 worked at the bell, 4 turned in.",
      IdlePlan.LastRoundLine(tally, Ended.AddMinutes(42), TimeSpan.Zero));
  }

  [Fact]
  public void LastRoundLine_AStepThatDidNothingIsAbsentNotZeroed()
  {
    // "0 melted" would count the absence of work as work - RoundSkips.Deferred's rule,
    // applied to the other end of the errand.
    var tally = Banked((RoundStage.Recon, 87));

    Assert.Equal("Last round ended 20:14 (2 minutes ago) - 87 lanes banked.",
      IdlePlan.LastRoundLine(tally, Ended.AddMinutes(2), TimeSpan.Zero));
  }

  [Fact]
  public void LastRoundLine_ARoundThatProcessedNothingIsStillATime()
  {
    // The honest subset: a round that ran dry did happen, and the player who is looking
    // at this screen wondering whether he already ran one tonight is answered.
    Assert.Equal("Last round ended 20:14 (3 hours ago).",
      IdlePlan.LastRoundLine(Banked(), Ended.AddHours(3), TimeSpan.Zero));
  }

  [Fact]
  public void LastRoundLine_TheClockIsThePlayersNotTheMachines()
  {
    // The zone is handed in (RoundResume.Line's reason): a sentence composed against
    // whatever clock the process happens to run on is one no test can pin.
    var tally = Banked((RoundStage.Recon, 1));

    Assert.StartsWith("Last round ended 15:14",
      IdlePlan.LastRoundLine(tally, Ended.AddMinutes(1), TimeSpan.FromHours(-5)));
  }

  // ---- What survives the idle boundary --------------------------------------

  [Fact]
  public void LastRoundTally_BanksOnlyStepsThatDidSomething()
  {
    // The dictionary it copies is the rail's, which carries whatever the completion
    // events reported - including a zero from a run that processed nothing.
    var banked = LastRoundTally.From(
      new Dictionary<RoundStage, int> { [RoundStage.Recon] = 87, [RoundStage.Desynth] = 0 },
      Ended.ToUnixTimeSeconds());

    Assert.Equal(new[] { "Recon" }, banked.ProcessedByStage.Keys.ToArray());
    Assert.Equal(87, banked.Processed(RoundStage.Recon));
    Assert.Equal(0, banked.Processed(RoundStage.Desynth));
  }

  [Fact]
  public void LastRoundTally_RoundTripsThroughTheConfigSerializer()
  {
    // It lives in the player's config file, so it has to survive the trip Dalamud makes
    // it take (TypeNameHandling.Objects, Simple assembly format - see ConfigShapeTests
    // for why a persisted identity is the dangerous thing to move).
    var settings = new JsonSerializerSettings
    {
      TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
      TypeNameHandling = TypeNameHandling.Objects,
    };
    var tally = Banked((RoundStage.Recon, 87), (RoundStage.BellRun, 30));

    var back = JsonConvert.DeserializeObject<LastRoundTally>(
      JsonConvert.SerializeObject(tally, settings), settings);

    Assert.NotNull(back);
    Assert.Equal(Ended.ToUnixTimeSeconds(), back!.EndedAtUnix);
    Assert.Equal("Last round ended 20:14 (1 minute ago) - 87 lanes banked, 30 worked at the bell.",
      IdlePlan.LastRoundLine(back, Ended.AddMinutes(1), TimeSpan.Zero));
  }

  // --- THE RETIREMENT, SAID AT THE DOOR (B1.4, ruled 08-21) ---

  [Fact]
  public void RetiredRoundLine_SaysWhatWentAndWhatSurvived()
    // The banked decisions surviving is the half the player actually needs: the
    // errand is gone, the rulings he made inside it are not.
    => Assert.Equal(
      "Yesterday's Round retired - parked past 4h. Its banked decisions survive; this door starts fresh.",
      IdlePlan.RetiredRoundLine(retired: true, ceilingHours: 4));

  [Fact]
  public void RetiredRoundLine_QuotesTheCeilingThatActuallyRetiredIt()
    // The sentence reads the configured ceiling, so it cannot drift from the
    // number that did the retiring (the knob has no row, but it is reachable).
    => Assert.Contains("parked past 12h", IdlePlan.RetiredRoundLine(true, 12));

  [Fact]
  public void RetiredRoundLine_FloorsWithTheRestorePath()
    // RestoreRound clamps the ceiling at 1h; the line clamps the same way or it
    // would quote a ceiling nothing enforces.
    => Assert.Contains("parked past 1h", IdlePlan.RetiredRoundLine(true, 0));

  [Fact]
  public void RetiredRoundLine_SilentWhenNothingRetired()
    // A door that reports a retirement every night reports nothing.
    => Assert.Equal("", IdlePlan.RetiredRoundLine(retired: false, ceilingHours: 4));
}
