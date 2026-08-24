using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// THE PER-RUN SKIPS (ruled ledger, stage 2a). Four checkboxes beside the launch
/// control, one per round stage, all checked. Unchecking one DEFERS that stage for
/// this run: the round flows past it, and nothing about the row is touched -
/// verdicts stand, piles stand, staged verbs stand. A deferred stage is a stage
/// the errand does not visit tonight, not a re-route.
///
/// <para><b>The skip lands at PLAN level, never in the flow.</b> A deferred stage
/// answers "no work" to the cursor, which is a sentence the cursor already knows
/// how to read (an empty stage is skipped silently and is NOT marked done - see
/// <see cref="RoundPlan.Next"/>). Nothing in the round engine learns a new word:
/// no new branch in the fire path, no new state on the halt, no new persisted
/// field. The one seam is <see cref="HasWork"/>, wrapped around the deck's own
/// answer.</para>
///
/// <para><b>Never sticky.</b> The set is per-run and per-session by construction -
/// the window clears it when a round ends and never persists it - so skipping is
/// always a fresh deliberate act. A skip that survived the night would be a stage
/// quietly missing from every round after the one the player meant it for, which
/// is the shape of bug nobody ever finds by looking at a round.</para>
///
/// <para>THE TAXONOMY IS THE ROUND'S, NOT THE BOARD'S. The board has four exit
/// columns (List / Melt / GC / Vend) and the round has four STAGES
/// (<see cref="RoundPlan.Order"/>), and they are not the same four. The bell is one
/// stop with three verbs - it lists, it reprices and it pulls-and-vendors - so
/// "List" and "Vendor" are legs of ONE stage and cannot be deferred apart without
/// splitting the one-door bell back into the side doors WALK unit 4 deleted. The
/// chips name it honestly: three of the player's four words map straight through,
/// and the fourth box says "List/Vendor" because that is what the stop does.</para>
///
/// Pure and Dalamud-free (linked into the test project).
/// </summary>
internal static class RoundSkips
{
  /// <summary>
  /// A stage as its checkbox says it - and as the completion banner names it back.
  /// ONE vocabulary for both: the sentence "4 GC turn-in skipped" has to point at
  /// the box the player unchecked, so it wears that box's exact words.
  /// </summary>
  internal static string Chip(RoundStage stage) => stage switch
  {
    RoundStage.Pinch => "Pinch",
    RoundStage.Desynth => "Desynth",
    RoundStage.BellRun => "List/Vendor",
    RoundStage.TurnIn => "GC turn-in",
    RoundStage.Recon => "Recon",
    RoundStage.Triage => "Triage",
    _ => "?",
  };

  /// <summary>What the box's tooltip says the stage actually is.</summary>
  internal static string Hint(RoundStage stage) => stage switch
  {
    RoundStage.Pinch => "Read the board and reprice standing listings (and vendor the pull-and-vendor rows, if the rider is armed).",
    RoundStage.Desynth => "Open the coffers, then the salvage window with the melt pile pre-selected.",
    RoundStage.BellRun => "The retainer errand entire - list, reprice, pull-and-vendor. One stop, three verbs, so they defer together.",
    RoundStage.TurnIn => "Expert Delivery at your Grand Company.",
    RoundStage.Recon => "Read the real board for every listable item and bank the decision - no price is written and nothing is posted. Skip it to spend the act half on what was already banked.",
    RoundStage.Triage => "The hinge: rule the decisions recon banked before anything irreversible runs. Skipping it commits the act half on the calls that already stand.",
    _ => "",
  };

  /// <summary>
  /// The deck's work answer, with this run's skips folded in. A deferred stage has
  /// no work THIS RUN however much is sitting in its pile - which is exactly the
  /// answer that makes the cursor walk past it without marking it done.
  /// </summary>
  internal static bool HasWork(RoundStage stage, bool rawHasWork, IReadOnlySet<RoundStage> skipped)
    => rawHasWork && !skipped.Contains(stage);

  /// <summary>
  /// What the skips are LEAVING - each deferred stage that actually had rows, with
  /// its count, in round order. A deferred stage with nothing in it is absent: the
  /// player skipped a stage that was going to do nothing anyway, and a banner that
  /// reported "0 pinch skipped" would be counting the absence of work as work.
  /// </summary>
  internal static List<(RoundStage Stage, int Count)> Deferred(
    IReadOnlySet<RoundStage> skipped, Func<RoundStage, int> count)
    => RoundPlan.Order
      .Where(skipped.Contains)
      .Select(s => (Stage: s, Count: count(s)))
      .Where(d => d.Count > 0)
      .ToList();
}

/// <summary>Why the one launch control cannot fire. <see cref="None"/> = it can.</summary>
internal enum LaunchBlock
{
  /// <summary>Nothing is in the way - press it.</summary>
  None,
  // RulingsNeeded retired 2026-08-12 (the minors batch). The rulings gate moved to
  // the HINGE when the board did (AccountantPlan.Continue owns it), and the launch's
  // one caller had been passing zero ever since - a refusal arm no press could reach,
  // certified by tests that were the only thing keeping it alive.
  /// <summary>No stage of the round has anything to do.</summary>
  NothingStaged,
  /// <summary>The turn-in is the only work there is, and the wallet holds none of it.</summary>
  SealWalletFull,
}

/// <summary>The launch control's whole state: can it fire, and what does it say beside itself.</summary>
internal readonly record struct LaunchState(LaunchBlock Block, string Companion)
{
  internal bool CanFire => Block == LaunchBlock.None;
}

/// <summary>
/// THE ONE LAUNCH CONTROL (ruled ledger, stage 2a). Every per-pile fire button and
/// the header's Go are gone; one button starts the round - on the Round's own idle
/// screen since Movement 3, on the gil dashboard before that - and when it cannot
/// start one it says why instead of pretending.
///
/// <para><b>THE RULINGS ARE NOT THIS BUTTON'S BUSINESS ANY MORE.</b> They were: the
/// launch refused while any row still owed a call, on <see cref="GatePlan"/>'s derived
/// count. Unit 5 moved the board to the hinge, and with it the gate - the round now
/// runs its whole Look half before anybody rules anything, so a launch that demanded
/// rulings up front would refuse to start the very pass that produces them.
/// <see cref="AccountantPlan.Continue"/> holds the same arithmetic at the seat it
/// belongs to, and this control is left with the two refusals that are still about
/// STARTING.</para>
///
/// <para><b>The wallet blocks NARROWLY.</b> A full seal wallet stops the turn-in and
/// nothing else, so it only refuses the launch when the turn-in is the only work
/// there is - a round with a bell to run still has an errand worth walking, and
/// refusing it over a stage that is one of four would be the sensor becoming a gate
/// (see <see cref="SealFit"/>: it warns, the run halts honestly, the halt clears
/// itself). The one case it must refuse is the one where pressing does nothing.</para>
///
/// Pure and Dalamud-free (linked into the test project).
/// </summary>
internal static class LaunchControl
{
  /// <summary>
  /// The button's state. <paramref name="armedStages"/> is how many stages have
  /// work after this run's skips; <paramref name="turnInIsTheOnlyWork"/> is the
  /// narrow wallet case.
  ///
  /// <para>Both remaining refusals are the same shape: pressing would do nothing.
  /// The rulings arm that used to lead this list is gone with the gate it belonged
  /// to - see the note on the type.</para>
  /// </summary>
  internal static LaunchState Assess(int armedStages, bool turnInIsTheOnlyWork,
    bool sealWalletFull)
  {
    if (armedStages <= 0)
      return new LaunchState(LaunchBlock.NothingStaged, "nothing staged");
    if (turnInIsTheOnlyWork && sealWalletFull)
      return new LaunchState(LaunchBlock.SealWalletFull, "seal wallet full");
    return new LaunchState(LaunchBlock.None, "");
  }
}

// THE STAGE-ALL BUTTON IS RETIRED (Task 4, ruled 08-15). StageAll and StageAllState
// existed for ONE caller: the bulk-stage verb inside the board's Reprice and
// Pull-and-Vendor group headers, plus the zero-state costume Movement 4 gave it so a
// dead button would stop wearing an imperative verb. The pile groups died with the
// triage panel and the walk's riders page is the bulk affordance now, so the button has
// no seat and this had no caller. A pure module kept alive for nobody is exactly the
// tech debt the panel died to avoid; its LaunchControl sibling below is untouched,
// because the one launch control still fires the errand.

/// <summary>
/// Everything still waiting on the player when a round runs out of stages. Three
/// sources, one number - see <see cref="RoundBanner"/> for why they are one number.
/// </summary>
internal readonly record struct WaitingTally(
  int Unruled, int Staged, IReadOnlyList<(RoundStage Stage, int Count)> Deferred)
{
  internal static readonly WaitingTally Clean =
    new(0, 0, Array.Empty<(RoundStage, int)>());

  internal int DeferredRows => Deferred.Sum(d => d.Count);

  internal int Total => Unruled + Staged + DeferredRows;

  /// <summary>The book is clean - and only then may the banner say so.</summary>
  internal bool BookIsClean => Total == 0;
}

/// <summary>
/// THE COMPLETION BANNER, HONEST (ruled ledger, stage 2a). "Round complete - the
/// board is worked" was true of the STAGES and false of the BOARD: a round can run
/// every stage it has and still leave rows sitting there - rows nobody ruled, rows
/// staged to a verb that never got handed off, rows in a stage the player deferred.
/// The banner said the errand was finished over a board that still had a night's
/// work on it.
///
/// <para><b>One number, from the counts that already exist.</b> The unruled count is
/// <see cref="GatePlan"/>'s queue - the same arithmetic that refuses the launch - so
/// a round cannot end saying "clean" about rows the next launch will refuse to start
/// over. The deferred counts are the deck's own per-stage counts through
/// <see cref="RoundSkips.Deferred"/>. Nothing here counts anything twice, because
/// nothing here counts anything itself.</para>
///
/// <para><b>THE CLICK TARGET IS GONE (Rounds unit 5), and its absence is the ruling.</b>
/// The number used to be clickable - a Focus method answered which pile the standalone
/// desk should open and scroll to - on the reasoning that a count of work you cannot
/// reach is a count, not a hand-off. Ruling Q4 dissolved the premise: the desk is gone,
/// the board renders only at a Round's hinge, and the rows this number names are the
/// NEXT Round's hinge queue. A click that landed on a board judging hours-old data
/// would hand back the exact habit the ruling deleted, so the tally states and the next
/// Look re-reads.</para>
///
/// Pure and Dalamud-free (linked into the test project).
/// </summary>
internal static class RoundBanner
{
  /// <summary>The banner's own sentence, minus the count that carries the numbers.</summary>
  internal static string Lead(in WaitingTally t)
    => t.BookIsClean ? "Round complete - the board is worked." : "Round complete -";

  /// <summary>
  /// The count itself: "7 rows still waiting on you". Empty on a clean book -
  /// there is nothing waiting and nothing to say.
  /// </summary>
  internal static string Count(in WaitingTally t)
    => t.BookIsClean ? "" : $"{t.Total} row{(t.Total == 1 ? "" : "s")} still waiting on you";

  /// <summary>
  /// What the number is made of: "(3 unruled, 2 staged, 4 GC turn-in skipped)". The
  /// skipped parts wear the CHECKBOX's words, so the sentence points back at the box
  /// the player unchecked rather than at a stage name he never saw.
  /// </summary>
  internal static string Breakdown(in WaitingTally t)
  {
    if (t.BookIsClean) return "";
    var parts = new List<string>();
    if (t.Unruled > 0) parts.Add($"{t.Unruled} unruled");
    if (t.Staged > 0) parts.Add($"{t.Staged} staged");
    foreach (var (stage, count) in t.Deferred)
      parts.Add($"{count} {RoundSkips.Chip(stage)} skipped");
    return parts.Count > 0 ? $"({string.Join(", ", parts)})" : "";
  }
}
