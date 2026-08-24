using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// ONE ROW OF THE IDLE PLAN - a stage, its state (which is the colour), whether this
/// run has deferred it, and the sentence it says. The state and the stage ride along
/// so the ImGui layer can colour and hover without re-deriving anything: the wizard's
/// rail learned that lesson already (see AccountantWindow.DrawVerticalRail), and an
/// idle rail that re-asked the deck per row would be a second answer to a question
/// the deck already answered this frame.
/// </summary>
internal readonly record struct IdleRow(
  RoundStage Stage, RailState State, bool Deferred, string Line);

/// <summary>
/// WHAT THE IDLE ROUNDS SCREEN SAYS (SF-P1, live shake 08-14/15, ruled 2026-08-15).
///
/// <para>Movement 3 made the launch preview the idle Rounds screen - one
/// implementation, hosted on the Gil Dashboard strip and in the Round's own window
/// (<see cref="AccountantPlan.ScreenFor"/> keys it). The strip's composition is
/// horizontally shaped, which is right for a strip and wrong for a window: in the
/// full "Scrooge - Rounds" window the plan was one wrapped arrow-line
/// ("pinch -&gt; 12 recon -&gt; hinge -&gt; ...") over an empty body. Drift's shake
/// receipt: the idle screen wastes its vertical space.</para>
///
/// <para><b>The plan verticalizes into the rail the wizard already draws.</b> Not a
/// new surface - the SAME <see cref="StageRail"/> rows, the same
/// <see cref="AccountantPlan.RailBody"/> wording (the stage's enable checkbox sits in
/// the glyph's seat here - ruled 08-15 shake, the strip and the plan were one list
/// twice), the same
/// <see cref="StageRail.EtaSuffix"/> vocabulary SF-P2 built the day before: an armed
/// pinch prices the listed asks it will visit (<c>DeckState.PinchAsks</c>), the hinge
/// says "waits on you", an unmeasured stage says "no timing yet". A second spelling of
/// a stage's plan is exactly how two surfaces come to describe one errand
/// differently, and this file exists so there is not one.</para>
///
/// <para><b>THE TENSE RULE IS STRUCTURAL HERE, not procedural.</b> The round log
/// speaks acts in the past tense; a plan speaks in the present-future, because the
/// round it describes has not run. So <see cref="Plan"/> never reaches for
/// <see cref="AccountantPlan.DoneTally"/> - the one past-tense sentence family the
/// rail owns - whatever state a row arrives in. Nothing about an idle deck should ever
/// produce a Done row, and if something ever does, the idle screen still cannot claim
/// work nobody has done.</para>
///
/// <para>Pure and Dalamud-free (linked into the test project).</para>
/// </summary>
internal static class IdlePlan
{
  /// <summary>
  /// What a deferred stage wears on the idle screen. The wizard's rail says "(skipped
  /// this run)" over a round in flight; before the press the run is the one the boxes
  /// are describing, so the idle screen keeps the word the old arrow-line used -
  /// "(skipped)", the same word on the same screen as the checkbox that caused it.
  /// </summary>
  internal const string DeferredNote = "(skipped)";

  /// <summary>
  /// THE PLAN, VERTICALIZED - one row per stage of <see cref="RoundPlan.Order"/>, in
  /// order, saying what that stage will do with what it is holding.
  ///
  /// <para><paramref name="rows"/> is <see cref="StageRail.Build"/>'s own output, built
  /// by the caller off the deck it already derived this frame (no round is underway, so
  /// nothing is current, nothing is halted and nothing is done - the states here are
  /// Pending and Empty). <paramref name="skipped"/> is this run's deferrals, which the
  /// deck has already folded into the row states; it is read again here only for the
  /// note, because "empty" and "deferred" look identical from a row and read completely
  /// differently to a player.</para>
  ///
  /// <para><paramref name="sealFitNote"/> is <c>SealFit.PlanSuffix</c> - "(~23 fit)",
  /// empty unless the wallet overflows - and it rides the TURN-IN row because that is
  /// the step it is about. It came off the old arrow-line, where it hung on the turn-in
  /// leg for the same reason; dropping it in the move would have quietly deleted the
  /// one sensor that says a 52-item turn-in is really a 23-item one.</para>
  ///
  /// <para>Every stage draws, including the empty ones (the rail's own ruling: a rail
  /// that silently shortened itself would make "did it skip the melt?" a question the
  /// player answers by counting). The caller dithers them.</para>
  /// </summary>
  internal static List<IdleRow> Plan(
    IReadOnlyList<RailRow> rows, IReadOnlySet<RoundStage> skipped, string sealFitNote = "")
  {
    var plan = new List<IdleRow>(rows.Count);
    foreach (var row in rows)
    {
      var deferred = skipped.Contains(row.Stage);
      // The tally slot carries the deferral note, the wallet note, or NOTHING. It is the
      // slot the wizard fills with DoneTally, and this screen may never: see the tense
      // rule on the type.
      var notes = new List<string>(2);
      if (deferred) notes.Add(DeferredNote);
      if (row.Stage == RoundStage.TurnIn && sealFitNote.Trim() is { Length: > 0 } seals)
        notes.Add(seals);

      // The BODY, not the full rail line: the idle screen leads each row with the
      // stage's enable checkbox in the glyph's seat (ruled 08-15 shake - the strip
      // and the plan were the same list twice, and an all-pending rail is a column
      // of dots). Same sentence after the mark; see AccountantPlan.RailBody.
      plan.Add(new IdleRow(row.Stage, row.State, deferred,
        AccountantPlan.RailBody(row, GatePlan.StageNoun(row.Stage), string.Join(" ", notes))));
    }
    return plan;
  }

  /// <summary>
  /// THE WAITING DECISIONS - ONE SENTENCE, AND THE CAP IS THE RULING (SF-P1, ruled
  /// 2026-08-15).
  ///
  /// <para>The count is the deck's own <c>RulingsNeeded</c> - the same operand
  /// <see cref="AccountantPlan.Continue"/> refuses over at the hinge and the same one
  /// <see cref="RoundBanner"/> counts as unruled when a round runs dry, so all three
  /// surfaces quote one arithmetic and none of them derives it.</para>
  ///
  /// <para><b>It is a count, not a queue.</b> No rows, no items, no buttons - the idle
  /// screen is not a second judgment surface, and a list here would hand back exactly
  /// the standalone desk ruling Q4 deleted ("why would I judge something outside a
  /// round?"). You rule at the hinge, against boards recon read minutes earlier.</para>
  ///
  /// <para><b>Zero says nothing.</b> Absence, not "0 waiting" - a night with nothing
  /// owed has nothing to report, which is the same honesty
  /// <see cref="GatePlan.Headline"/> and <see cref="RoundBanner.Count"/> already keep.</para>
  /// </summary>
  internal static string WaitingDecisions(int rulingsNeeded)
    => rulingsNeeded <= 0
      ? ""
      : $"{rulingsNeeded} decision{(rulingsNeeded == 1 ? "" : "s")} waiting on you";

  /// <summary>
  /// THE RETIREMENT, SAID AT THE DOOR (B1.4, ruled 08-21). A parked round past the
  /// staleness ceiling is dropped on restore, and until now it was dropped SILENTLY -
  /// the player opened the Round door, found a fresh launch screen where he had left
  /// an errand mid-flight, and had nothing to read about it. The ceiling has no config
  /// row and is not getting one; the SILENCE was the problem, not the invisibility.
  ///
  /// <para>Said where the round was lost: the idle seat is the screen he opens
  /// expecting to find it, and a line there is read by exactly the person who needs
  /// it. Chat is not the seat - the retirement happens on the first draw after a
  /// reload, which is minutes or hours before he goes looking.</para>
  ///
  /// <para><paramref name="ceilingHours"/> is the ceiling that actually retired it, so
  /// the sentence cannot drift from the number it describes. Empty when nothing was
  /// retired: a door that reports a retirement every night reports nothing.</para>
  /// </summary>
  internal static string RetiredRoundLine(bool retired, int ceilingHours)
    => !retired
      ? ""
      : $"Yesterday's Round retired - parked past {Math.Max(1, ceilingHours)}h. "
        + "Its banked decisions survive; this door starts fresh.";

  /// <summary>
  /// THE LAST ROUND'S TALLY - one line, what the previous errand did, or nothing.
  ///
  /// <para><paramref name="tally"/> is null until a round has ended under a build that
  /// banks one, and the line is simply absent until then - the idle screen has no
  /// history to report and does not pretend to (see <see cref="LastRoundTally"/> for
  /// what survives the idle boundary and what does not).</para>
  ///
  /// <para><b>The clauses are the RAIL's, verbatim.</b> Each stage's number goes through
  /// <see cref="AccountantPlan.DoneTally"/> - "87 lanes banked", "30 worked at the bell"
  /// - which is the sentence the wizard's rail said about that same stage while the
  /// round was walking. Past tense is correct here and only here: this round RAN.</para>
  ///
  /// <para><paramref name="zone"/> is the player's UTC offset, handed in rather than
  /// read, for <see cref="RoundResume.Line"/>'s reason: a sentence composed against the
  /// machine's clock is a sentence no test can pin and no reader can see. The age
  /// parenthetical is <see cref="RoundResume.Ago"/>, the same ladder the resume line
  /// and the run log speak - a bare wall-clock time on an idle screen cannot tell
  /// tonight's round from Tuesday's.</para>
  /// </summary>
  internal static string LastRoundLine(LastRoundTally? tally, DateTimeOffset now, TimeSpan zone)
  {
    if (tally is not { EndedAtUnix: > 0 } last) return "";

    var ended = DateTimeOffset.FromUnixTimeSeconds(last.EndedAtUnix).ToOffset(zone);
    var ago = RoundResume.Ago((long)Math.Max(0, (now - ended).TotalSeconds));
    var did = string.Join(", ", Clauses(last));
    return did.Length > 0
      ? $"Last round ended {ended:H:mm} ({ago}) - {did}."
      : $"Last round ended {ended:H:mm} ({ago}).";
  }

  /// <summary>
  /// What each stage did, in round order. A stage with no processed count is absent
  /// rather than zeroed: the round skipped it, deferred it, or found nothing there, and
  /// "0 melted" would count the absence of work as work (<see cref="RoundSkips.Deferred"/>'s
  /// rule, applied to the other end of the errand). The hinge is absent by construction -
  /// its executor is a human press and it processes nothing.
  /// </summary>
  private static IEnumerable<string> Clauses(LastRoundTally tally)
    => RoundPlan.Order
      .Select(s => AccountantPlan.DoneTally(RailState.Done, s, tally.Processed(s)))
      .Where(c => c.Length > 0);
}

/// <summary>
/// WHAT THE LAST ROUND DID, PERSISTED (SF-P1, 2026-08-15) - the one struct that makes
/// the idle screen's tally line sayable across the boundary it has to cross.
///
/// <para><b>Nothing else survived, and that is why this exists.</b> The audit: the
/// report screen's banner (<see cref="RoundBanner"/>) counts what is still WAITING and
/// derives it live from the board every frame - it says nothing about what the round
/// did, and it dies with the dismissal. The wizard rail's per-stage tallies are an
/// in-memory dictionary, cleared by the next round's start, gone on a reload. The
/// banked transcript (<c>round_log</c>) survives the round but is a prose transcript,
/// capped and superseded, and reading a tally back out of it would mean parsing the
/// sentences the log writes. <c>round_runs</c> persists three TIMESTAMPS and no counts.
/// So an honest "what the last round did" needed a home, and this is the smallest one:
/// the counts the round already banked, plus when it ended.</para>
///
/// <para><b>The counts are not new arithmetic.</b> They are the per-stage processed
/// totals the wizard's rail already renders - each one a finished run's own
/// <c>RunFacts.ItemsProcessed</c>, banked at the completion event, which is the one
/// moment it is true (a stage's live count is what it has LEFT). This type gives them
/// somewhere to live past the errand instead of a second way to count them.</para>
///
/// <para><b>Keyed by stage NAME, following <c>Configuration.AvgMsPerItemByStage</c>.</b>
/// Same reason: a persisted dictionary keyed on an enum ties the file to today's
/// spelling of the enum's serialization, and this plugin has already ruled that a
/// persisted identity is the dangerous thing to move (see the receipts on
/// <see cref="RoundState"/>). A key nothing recognises reads back as no count at all,
/// which is exactly the fail-quiet this line wants.</para>
///
/// <para>Public with get/set and a parameterless ctor so the config's JSON serializer
/// round-trips it, the <see cref="RoundState"/> mold.</para>
/// </summary>
public sealed class LastRoundTally
{
  /// <summary>
  /// When the round ended, unix seconds. 0 = unknown, and the line refuses to draw -
  /// a tally with no time on it would be history with no date, which on an idle screen
  /// is indistinguishable from a claim about tonight.
  /// </summary>
  public long EndedAtUnix { get; set; }

  /// <summary>
  /// What each stage processed, keyed by <see cref="RoundStage"/> name. Absent key =
  /// that stage did nothing this round (deferred, empty, or never reached), and the
  /// line simply omits it.
  /// </summary>
  public Dictionary<string, int> ProcessedByStage { get; set; } = new();

  /// <summary>What one stage processed - 0 for a stage the round never reported on.</summary>
  internal int Processed(RoundStage stage)
    => ProcessedByStage.TryGetValue(stage.ToString(), out var n) ? n : 0;

  /// <summary>
  /// Banks a finished round's per-stage tallies. Takes the rail's own dictionary so
  /// there is one derivation of "what this stage did" and this type never counts
  /// anything itself.
  /// </summary>
  internal static LastRoundTally From(
    IReadOnlyDictionary<RoundStage, int> stageTally, long endedAtUnix)
  {
    var banked = new LastRoundTally { EndedAtUnix = endedAtUnix };
    foreach (var (stage, processed) in stageTally)
      if (processed > 0)
        banked.ProcessedByStage[stage.ToString()] = processed;
    return banked;
  }
}
