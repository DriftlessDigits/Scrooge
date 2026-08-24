using System;

namespace Scrooge;

/// <summary>What the Round's own window is showing, keyed to the state of the errand.</summary>
internal enum RoundScreen
{
  /// <summary>No Round, nothing owed: the launch preview - the plan line, the fit advisory, and Make the Rounds.</summary>
  Preview,
  /// <summary>A Round exists - flowing or held: the wizard.</summary>
  Wizard,
  /// <summary>The last Round ended and still owes its report: the tally, over the preview for the next one.</summary>
  Report,
}

/// <summary>What the Accountant's right pane is showing this frame.</summary>
internal enum StepPane
{
  /// <summary>A run stage: the progress readout and whatever the stage is waiting on.</summary>
  Run,
  /// <summary>The hinge: the board, its detail pane, and the staged verbs.</summary>
  Board,
  /// <summary>The Round ran dry: the final tally.</summary>
  Completion,
  /// <summary>The Round is frozen over a dead stage: the named gap.</summary>
  Halt,
}

/// <summary>Whether the hinge's Continue may be pressed, and what it says when it may not.</summary>
internal readonly record struct HingeGate(bool CanContinue, string Companion);

/// <summary>
/// THE ACCOUNTANT'S PURE HALF (Rounds unit 5) - the wizard's three decisions that do
/// not need a game, a window, or an ImGui frame to be right.
///
/// <para>The window they belong to is 5,000 lines of Dalamud, which is exactly why
/// these three are here: the screen selector decides what the Round's window shows a
/// player who just walked in, the hinge gate decides whether the round's one
/// irreversible press is allowed, and the pane selector decides what the player is
/// looking at mid-errand. Every one of them is a sentence about state, and a sentence
/// about state is testable.</para>
///
/// Pure and Dalamud-free (linked into the test project).
/// </summary>
internal static class AccountantPlan
{
  /// <summary>
  /// WHAT THE ROUND'S ONE WINDOW IS SHOWING (Movement 3, ruled 2026-08-13). The door
  /// no longer ROUTES - it opens the Round's window, every time, and this decides
  /// what is inside it.
  ///
  /// <para><b>This supersedes unit 5's routing ruling</b> ("the Round door routes to
  /// the dashboard when idle", ruled Q4 2026-08-10). That was honest routing to the
  /// only idle surface that existed: the launch preview lived on the gil dashboard,
  /// so an idle door had nowhere else to land. The preview IS the idle screen now, so
  /// the door opens it directly - one press at the bell lands on Make the Rounds
  /// instead of on the dashboard's everything. Drift's receipt: "why should I open up
  /// the gil dashboard, which has a lot of stuff in it, just to click the 'Rounds'
  /// button again? I just clicked it on the retainer screen".</para>
  ///
  /// <para>Both halves of <paramref name="roundLive"/> are a Round - one underway and
  /// one HELD (banked, restored, waiting on a Resume) - and both get the wizard, which
  /// is where the Resume line and its button live. <paramref name="reportOwed"/> is
  /// the last errand's undismissed tally; it outranks the preview because a report
  /// nobody has read yet is the thing the player came back for. It does not replace
  /// it - the report screen carries the preview under it, so the next Round is one
  /// press away from the last one's report.</para>
  /// </summary>
  internal static RoundScreen ScreenFor(bool roundLive, bool reportOwed)
    => roundLive ? RoundScreen.Wizard
      : reportOwed ? RoundScreen.Report
      : RoundScreen.Preview;

  /// <summary>
  /// The gil dashboard's door to the Round, in words. The dashboard READS - it never
  /// starts an errand any more - so both labels open a window and neither one fires
  /// anything: the press that starts a Round is on the Round's own screen.
  /// </summary>
  internal static string DoorLabel(bool roundLive)
    => roundLive ? "Open the Round" : "Open the Rounds";

  /// <summary>
  /// What the right pane draws. Order of precedence is the order the player can do
  /// something about it: a halt outranks everything (the round is frozen and the
  /// only live verb is Resume), the hinge outranks the ordinary run stages (it is
  /// the one stage whose executor is looking at the screen), and no offered stage
  /// at all means the errand is over.
  /// </summary>
  internal static StepPane PaneFor(bool halted, RoundStage? offered)
    => halted ? StepPane.Halt
      : offered is null ? StepPane.Completion
      : offered == RoundStage.Triage ? StepPane.Board
      : StepPane.Run;

  /// <summary>
  /// THE HINGE'S CONTINUE (ruled Q1, 2026-08-10): the launch gate's arithmetic, moved
  /// to the seat where it actually bites.
  ///
  /// <para>The count it refuses over is the SAME <see cref="GatePlan"/> queue the
  /// launch control used to read, narrowed exactly the same way - to the rows the
  /// stages THIS RUN will visit. That narrowing is not re-derived here; it is already
  /// baked into <paramref name="rulingsNeeded"/> by the judgment queue, which drops
  /// any candidate feeding a deferred stage. A ruling that feeds a stage tonight is
  /// not going to run is not a decision about tonight, and refusing the act half over
  /// one would make the skip a trap.</para>
  ///
  /// <para><b>Nothing to act on is not a refusal.</b> When the act half holds no work
  /// at all the hinge has nothing to gate and Continue is simply the way past it - the
  /// cursor would have skipped an empty hinge silently anyway, and a Continue that
  /// refused over rulings feeding stages with nothing in them would be the gate
  /// arguing with the cursor about a stage neither of them is going to run.</para>
  /// </summary>
  /// <param name="rulingsNeeded">Open rulings - the walk's undecided cases while one
  /// is live, the judgment queue otherwise.</param>
  /// <param name="actHasWork">Whether the act half holds anything at all.</param>
  /// <param name="walkFinished">Whether the player has walked every page (ruled 08-15:
  /// "Continue itself should refuse until you've walked every page" - the forced walk
  /// is the GATE's rule, not just the pane's page order; a settled board he never
  /// looked at is not a board he ruled on). True when no walk is live, so every
  /// pre-walk surface keeps its arithmetic unchanged.</param>
  internal static HingeGate Continue(int rulingsNeeded, bool actHasWork, bool walkFinished = true)
  {
    if (!actHasWork) return new HingeGate(true, "");
    if (rulingsNeeded > 0)
      return new HingeGate(false,
        $"{rulingsNeeded} ruling{(rulingsNeeded == 1 ? "" : "s")} needed");
    if (!walkFinished)
      return new HingeGate(false, "pages left to walk");
    return new HingeGate(true, "");
  }

  /// <summary>
  /// One rail row's line: glyph, the stage's noun, what it is holding, and its own
  /// measured estimate. The vertical rail says exactly what the run log's horizontal
  /// one says, in the same words, because it is the same sentence - two spellings of
  /// a stage's state is how two surfaces come to describe one errand differently.
  ///
  /// <para><paramref name="tally"/> is the live per-step count ("14 lanes banked" is
  /// the same 14 the count carries); a stage holding nothing renders its own empty
  /// note instead, which since unit 5 is the one honest string
  /// (<see cref="StageRail.EmptyNote"/>).</para>
  /// </summary>
  internal static string RailLine(in RailRow row, string noun, string tally)
    => $" {StageRail.Glyph(row.State)} {RailBody(row, noun, tally)}";

  /// <summary>
  /// The rail sentence WITHOUT its glyph - for a surface whose leading mark is not a
  /// state glyph. The idle preview leads each row with the stage's enable CHECKBOX
  /// (ruled 08-15 shake: the checkbox strip and the plan listed the stages twice, and
  /// an idle rail where everything is pending is a column of dots that say nothing).
  /// One sentence either way: the wizard's rail is glyph + body, the idle plan is
  /// checkbox + body, and the words between them cannot drift because they are this
  /// method's.
  /// </summary>
  internal static string RailBody(in RailRow row, string noun, string tally)
  {
    var count = row.Count > 0 ? $" ({row.Count})" : "";
    var eta = StageRail.EtaSuffix(row);
    var note = tally.Length > 0 ? $"  {tally}" : StageRail.EmptyNote(row.State, row.Stage);
    return $"{noun}{count}{eta}{note}";
  }

  /// <summary>
  /// A finished stage's TALLY - what its run actually did, in that stage's own units.
  /// The rail's job mid-round is to say where the errand stands, and a done stage that
  /// said only "x" would drop the one fact the player walked away to earn.
  ///
  /// <para><b>The number is the RUN's, not the pile's.</b> A stage's live count is what
  /// it has LEFT (see DeckState.CountOf) and drops to zero the moment the run drains
  /// it, so reading the pile for a finished stage would report "0 lanes banked" over a
  /// recon that banked eighty-seven. The caller hands in what the completed run
  /// reported processing; a stage with no reported run - the hinge, whose executor is a
  /// human press and processes nothing - gets no tally rather than a fabricated one.</para>
  ///
  /// <para>Only DONE stages get one. A pending stage's count is a forecast and is
  /// already rendered as one (count plus ETA); dressing a forecast in the past tense
  /// would be the rail claiming work nobody has done yet.</para>
  /// </summary>
  internal static string DoneTally(RailState state, RoundStage stage, int processed)
  {
    if (state != RailState.Done || processed <= 0) return "";
    return stage switch
    {
      RoundStage.Pinch => $"{processed} lane{(processed == 1 ? "" : "s")} read",
      RoundStage.Recon => $"{processed} lane{(processed == 1 ? "" : "s")} banked",
      RoundStage.BellRun => $"{processed} worked at the bell",
      RoundStage.Desynth => $"{processed} melted",
      RoundStage.TurnIn => $"{processed} turned in",
      // The hinge has no run and processes nothing - its executor is a press.
      _ => "",
    };
  }
}
