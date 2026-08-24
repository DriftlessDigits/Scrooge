using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>Where one stage stands, as the rail draws it.</summary>
internal enum RailState
{
  /// <summary>Finished this round.</summary>
  Done,
  /// <summary>The stage the deck is offering right now.</summary>
  Current,
  /// <summary>It died. The round is frozen here until the player resumes.</summary>
  Halted,
  /// <summary>Has work, waiting its turn.</summary>
  Pending,
  /// <summary>Nothing to do - skipped silently, not buried (work can still appear).</summary>
  Empty,
}

/// <summary>
/// WHERE A ROW'S ETA CAME FROM (SF-P2, 2026-08-15). The rail used to have exactly one
/// answer - banked per-stage pace times what the stage is holding - and the live shake
/// caught it saying "pinch - no timing yet" on the same screen as the pinch's own
/// countdown ("2/75 ~8m47s"). One number, three provenances, so the row can say which
/// one it is instead of every surface guessing.
/// </summary>
internal enum EtaBasis
{
  /// <summary>
  /// The banked estimate: this stage's own measured per-item pace times its load. A
  /// null ETA under this basis means UNMEASURED - the stage has never run, and says so.
  /// The default, because it is what every stage answers unless something better exists.
  /// </summary>
  Measured,

  /// <summary>
  /// The run in flight's OWN remaining countdown, handed in by the caller. Strictly
  /// better evidence than the banked estimate: it is calibrated against the items this
  /// very run has already finished, and it does not care what the stage is still holding.
  /// </summary>
  LiveRun,

  /// <summary>
  /// There is no duration here to measure - the stage is waiting on the human. Not a
  /// missing measurement; an absent one, permanently. See <see cref="StageRail.Hinge"/>.
  /// </summary>
  WaitsOnYou,
}

/// <summary>
/// One rail row: a stage, where it stands, how much it holds, how long it should take,
/// and on whose word (<see cref="EtaBasis"/>). The basis defaults to
/// <see cref="EtaBasis.Measured"/> so a row built by hand reads exactly as every row did
/// before SF-P2 - pace times load, absent when unmeasured.
/// </summary>
internal readonly record struct RailRow(
  RoundStage Stage, RailState State, int Count, long? EtaMs,
  EtaBasis Basis = EtaBasis.Measured);

/// <summary>
/// THE STAGE RAIL (WALK unit 6) - the round's face, in the run log.
///
/// <para>07-24 named the gap: "same errand, half narrated, half invisible". The
/// run log showed whichever single run happened to be in flight and knew nothing
/// about the errand around it, so a player mid-round had no one surface that said
/// where the whole thing stood. The deck knew, but the deck lives in the Ledger,
/// which is not the window you watch while a run works.</para>
///
/// <para>The ETA is MEASURED or absent. Each stage quotes its own persisted
/// per-item pace (Configuration.AvgMsPerItemByStage, blended from that stage's own
/// finished runs) times what it is actually holding. A stage that has never run
/// has no rate and says so - it does not borrow a neighbour's number, because a
/// rate nobody measured reads like a rule.</para>
///
/// <para><b>SF-P2 (live shake 08-14/15): pace times HOLDING is the wrong second
/// operand three times over, and the rail said "no timing yet" in all three.</b>
/// The shake caught the rail claiming the pinch had no timing while the pinch's own
/// progress line, on the same screen, counted down "2/75 ~8m47s". Three separate
/// fixes, one shape - the count was never the honest operand:
/// <list type="bullet">
///   <item><b>A stage with a run in flight quotes the RUN.</b> A walking pinch
///     freshens the board retainer by retainer, so mid-run it holds nothing (the
///     SF3 self-extinguishing-work shape) - pace x 0 is null, while the run itself
///     knew its own remaining time all along. The caller hands that value in
///     (<c>RunLifecycle.Eta</c>, the same derivation the progress line renders) rather
///     than the rail re-deriving it: two calculations of one countdown is how the two
///     surfaces came to disagree in the first place.</item>
///   <item><b>An armed pinch quotes its LISTED ASKS.</b> The pinch's holding is
///     structurally zero - it does not stage rows, it re-reads the board - so before
///     it fires there is nothing for the pace to multiply. Its load is the listed asks
///     it will visit, which is the same operand the fit check already prices the round
///     with; the pace is still the pinch's own banked one.</item>
///   <item><b>The hinge says "waits on you".</b> See <see cref="Hinge"/>.</item>
/// </list></para>
///
/// Pure and Dalamud-free (linked into the test project): every live operand - the
/// run's countdown, the ask count, the banked paces - is handed in by the caller.
/// </summary>
internal static class StageRail
{
  /// <summary>
  /// THE HUMAN HINGE - the one stage whose duration is not the plugin's to measure,
  /// and therefore the one stage that must never promise a measurement.
  ///
  /// <para>Identified here rather than assumed from the name, and the receipt is the
  /// pace bank: <c>LedgerWindow.StageForRate</c> maps every run kind to the stage whose
  /// key it blends into, and there is no arm that returns this one. A triage-flavoured
  /// run is the bell's standing-listing leg and banks into <see cref="RoundStage.BellRun"/>
  /// by design (07-25, when the bell absorbed the reprice stage), so
  /// <c>AvgMsPerItemByStage["Triage"]</c> is a key nothing in this codebase can ever
  /// write. "No timing yet" on that row was a promise of a number that would never
  /// arrive - the fence nobody was ever going to come back and fix. What the stage is
  /// actually waiting for is a human reading his own board, so the row says that
  /// instead (SF-P2, 2026-08-15).</para>
  /// </summary>
  internal const RoundStage Hinge = RoundStage.Triage;

  /// <summary>
  /// Builds the rail. <paramref name="msPerItem"/> returns 0 for a stage with no
  /// measurement yet - which becomes a null ETA, never a zero one.
  ///
  /// <para><paramref name="liveRunEtaMs"/> is the CURRENT stage's own run countdown in
  /// milliseconds, or null when nothing is in flight (or the run has no honest estimate
  /// yet - "gathering data" arrives here as null and the row falls back to the banked
  /// answer, which is the same answer it gave before SF-P2). It applies to the current
  /// row only: a pending stage has no run to quote.</para>
  ///
  /// <para><paramref name="pinchListedAsks"/> is how many listed asks an armed pinch
  /// would visit. Used only when the pinch is holding nothing, which is its normal
  /// state - if a caller ever does give the pinch a real load, the load wins, because
  /// a count of things staged outranks a forecast of things to visit.</para>
  /// </summary>
  internal static List<RailRow> Build(
    Func<RoundStage, int> countOf,
    Func<RoundStage, bool> hasWork,
    Func<RoundStage, bool> isDone,
    RoundStage? current,
    RoundStage? halted,
    Func<RoundStage, float> msPerItem,
    long? liveRunEtaMs = null,
    int pinchListedAsks = 0)
  {
    var rows = new List<RailRow>(RoundPlan.Order.Length);
    foreach (var stage in RoundPlan.Order)
    {
      var state = halted == stage ? RailState.Halted
        : isDone(stage) ? RailState.Done
        : current == stage ? RailState.Current
        : hasWork(stage) ? RailState.Pending
        : RailState.Empty;

      var count = countOf(stage);
      var (eta, basis) = EtaOf(stage, state, count, msPerItem, liveRunEtaMs, pinchListedAsks);
      rows.Add(new RailRow(stage, state, count, eta, basis));
    }
    return rows;
  }

  /// <summary>
  /// One row's ETA and whose word it is on. The order of the arms IS the ruling
  /// (SF-P2): a finished stage owes nothing, the hinge owes no number at all, a live
  /// run outranks any banked estimate of the stage it is running, an armed pinch prices
  /// the asks it will visit, and everything else keeps exactly today's behaviour -
  /// measured pace times holding, "no timing yet" when unmeasured.
  /// </summary>
  private static (long? Ms, EtaBasis Basis) EtaOf(
    RoundStage stage, RailState state, int count,
    Func<RoundStage, float> msPerItem, long? liveRunEtaMs, int pinchListedAsks)
  {
    // A finished or empty stage has no time left to spend, whatever it counts.
    if (state is RailState.Done or RailState.Empty) return (null, EtaBasis.Measured);

    // The hinge, before anything else: it has no duration under any basis, and a run
    // could not be in flight for a stage whose executor is a human press.
    if (stage == Hinge) return (null, EtaBasis.WaitsOnYou);

    // The run in flight speaks for its own stage. Strictly better evidence than pace x
    // holding, and on a self-extinguishing stage it is the ONLY evidence.
    if (state == RailState.Current && liveRunEtaMs is long live) return (live, EtaBasis.LiveRun);

    return (Eta(LoadOf(stage, count, pinchListedAsks), msPerItem(stage)), EtaBasis.Measured);
  }

  /// <summary>
  /// THE SECOND OPERAND, once: what a stage's pace should be multiplied by. Normally
  /// its holding, except for the armed pinch, whose holding is structurally zero - it
  /// re-reads the board rather than staging rows - so it prices the listed asks it
  /// will visit instead. A caller that does give the pinch a real load keeps it: a
  /// count of things staged outranks a forecast of things to visit.
  ///
  /// <para>Both estimators need this substitution - the rail's per-row ETA and
  /// <see cref="PlanMachineMs"/>'s round total - and the fit check compares the
  /// second against the rail's own footer. Two spellings of the pinch's operand is
  /// how those two numbers would come to disagree.</para>
  /// </summary>
  private static int LoadOf(RoundStage stage, int count, int pinchListedAsks)
    => stage == RoundStage.Pinch && count <= 0 ? pinchListedAsks : count;

  /// <summary>
  /// A stage's remaining time: its own measured pace times what it holds. Null
  /// when either operand is missing - no rate yet, or nothing to do.
  /// </summary>
  internal static long? Eta(int count, float msPerItem)
    => count > 0 && msPerItem > 0f ? (long)(msPerItem * count) : null;

  /// <summary>
  /// What the whole round has left, when every remaining stage can be costed.
  /// Null the moment ANY armed stage is unmeasured - a total that silently omits
  /// a stage is worse than no total, because it reads as complete.
  ///
  /// <para><b>The live run's countdown PARTICIPATES (SF-P2).</b> It arrives as an
  /// ordinary costed row and is added like any other, which is the whole point: the
  /// running stage used to cost pace x 0 = null and take the total down with it, so the
  /// round header went blank for the length of every run - the one stretch where the
  /// player most wants a number and the one stretch where the plugin has the best one
  /// it will ever have. Better evidence must not read as less evidence.</para>
  ///
  /// <para><b>The hinge still nulls the total, and that is a RULING, not a leftover
  /// (SF-P2).</b> It looks identical to the old accident - the row has no ms, so the
  /// total is null while triage is armed, exactly as it was when triage was merely
  /// unmeasured - and it is kept deliberately, on this file's own doctrine. The two
  /// candidate readings were: exclude the hinge by design (the rail's own row says
  /// "waits on you", so nothing is SILENTLY omitted and the doctrine's stated worry
  /// does not bite), or let it null the total. The second wins because the header's
  /// claim is a DURATION, not a list of what it covers: "Round - ~12m of work left"
  /// over a round with an unbounded human pause sitting in the middle of it is a
  /// number the plugin cannot keep, and the player reading it is not auditing which
  /// stages the sum walked. So the total returns the moment the hinge is behind us -
  /// which is also when the act half, the part that actually has a duration, is all
  /// that is left. See <see cref="Hinge"/> for why no measurement can ever arrive.</para>
  /// </summary>
  internal static long? RemainingMs(IReadOnlyList<RailRow> rows)
  {
    var (total, costed, hingeOpen) = SumRows(rows);
    if (hingeOpen) return null; // armed, and no duration exists
    return costed ? total : null;
  }

  /// <summary>
  /// THE ONE WALK over the rail's armed rows: what they cost between them, whether
  /// anything could be costed at all, and whether the hinge is still open. The two
  /// surfaces that quote a round total - <see cref="RemainingMs"/> and
  /// <see cref="ReturnClockLine"/> - walked these same rows separately and reached
  /// the same three facts, which is two places for a skipped row or a dropped
  /// unmeasured stage to hide.
  ///
  /// <para>WHAT THE HINGE MEANS IS NOT DECIDED HERE, deliberately: this reports that
  /// it is open, and each caller applies its own ruling to that flag (the header
  /// nulls, the return-clock line sums past it and says "after you rule"). The
  /// divergence is the whole point of having two surfaces - see the doctrine on
  /// <see cref="RemainingMs"/>. An unmeasured armed stage is not a policy question,
  /// though: it kills the sum here, because a sum with a hole is not an estimate.</para>
  /// </summary>
  private static (long Total, bool Costed, bool HingeOpen) SumRows(IReadOnlyList<RailRow> rows)
  {
    long total = 0;
    var costed = false;
    var hingeOpen = false;
    foreach (var row in rows)
    {
      if (row.State is RailState.Done or RailState.Empty) continue;
      if (row.Basis == EtaBasis.WaitsOnYou) { hingeOpen = true; continue; }
      if (row.EtaMs is not long ms) return (0, false, hingeOpen);
      total += ms;
      costed = true;
    }
    return (total, costed, hingeOpen);
  }

  /// <summary>An ETA said the way the rest of the round says durations.</summary>
  internal static string EtaText(long? etaMs)
    => etaMs is long ms ? $"~{Durations.Span(ms / 1000)}" : "no timing yet";

  /// <summary>
  /// THE PLAN'S MACHINE-TIME TOTAL AT THE PRESS (ruled 08-16 round walk): every
  /// stage's own measured pace times what it is holding, summed across the round.
  /// This is the fit check's estimate operand - Drift's ruling was "will the ENTIRE
  /// round finish before the retainers return", replacing the pinch-only feed that
  /// predated the per-stage pace bank (the fossil quoted 11m under a step list whose
  /// pinch row alone said 15m). The TURN-IN is summed too, as a deliberate
  /// simplification and not a collision claim - it happens at the GC counter, away
  /// from the bell, and including it only pads the total by its own small ETA.
  ///
  /// <para>Same rules as <see cref="ReturnClockLine"/>'s round half: the hinge is
  /// excluded (machine time is the claim, and the hinge has none), the armed pinch
  /// prices the asks it will visit, and an armed stage with no measured pace nulls
  /// the whole answer - a sum with a hole is not an estimate.</para>
  /// </summary>
  internal static long? PlanMachineMs(
    Func<RoundStage, int> countOf, Func<RoundStage, float> msPerItem, int pinchListedAsks = 0)
  {
    long total = 0;
    var costed = false;
    foreach (var stage in RoundPlan.Order)
    {
      if (stage == Hinge) continue;
      var count = LoadOf(stage, countOf(stage), pinchListedAsks);
      if (count <= 0) continue;
      if (Eta(count, msPerItem(stage)) is not long ms) return null;
      total += ms;
      costed = true;
    }
    return costed ? total : null;
  }

  /// <summary>
  /// THE RAIL FOOTER'S TWO CLOCKS (ruled 08-15 shake: "can't just say 'retainers
  /// return in 5 minutes', we need to say 'retainers return in 5 minutes, and the
  /// round has an estimated 3 minutes in it'"). One number is a deadline, the other
  /// is the spend against it - naming only the deadline made the reader do the
  /// subtraction that is the entire point of the line.
  ///
  /// <para>The round half is MACHINE time and sums past an open hinge (unlike
  /// <see cref="RemainingMs"/>, which nulls the header's total - a header claims a
  /// duration, this line claims a comparison, and the comparison is exactly what the
  /// player at the hinge is spending his unbounded time against). An open hinge says
  /// so: "after you rule". An unmeasured armed stage drops the round half rather than
  /// underclaiming it - a sum with a hole is not an estimate.</para>
  /// </summary>
  internal static string ReturnClockLine(long returnSecs, IReadOnlyList<RailRow> rows)
  {
    if (returnSecs <= 0) return "retainers are back - the haul is waiting";

    var (machine, costed, hingeOpen) = SumRows(rows);

    var lead = $"retainers return in ~{Durations.Span(returnSecs)}";
    if (!costed) return lead;
    var est = $"~{Durations.Span(machine / 1000)}";
    return hingeOpen
      ? $"{lead}, and the round has an estimated {est} in it after you rule"
      : $"{lead}, and the round has an estimated {est} in it";
  }

  /// <summary>
  /// THE ETA SLOT ON A ROW, separator and all - the one place the rail's timing
  /// vocabulary lives (SF-P2). Every surface that draws the rail - the run log's
  /// horizontal rail, its copy-to-clipboard twin, the Accountant's vertical rail
  /// through <see cref="AccountantPlan.RailLine"/> - had hand-rolled this same
  /// conditional, which is three places to teach a new word and three places to forget
  /// one. A done or empty stage says nothing (its state is already the whole story).
  /// </summary>
  internal static string EtaSuffix(in RailRow row)
    => row.State is RailState.Done or RailState.Empty ? ""
      : row.Basis == EtaBasis.WaitsOnYou ? " - waits on you"
      : $" - {EtaText(row.EtaMs)}";

  /// <summary>
  /// The parenthetical a stage wears when it is holding nothing.
  ///
  /// <para>IT COLLAPSED BACK TO ONE STRING IN UNIT 5, exactly as it said it would.
  /// Through the Rounds build this had two sentences, because a stage with no
  /// executor never LOOKED and telling the player its pile was empty would have been
  /// the rail inventing a scan that never happened. Recon got its executor in unit 2
  /// and the hinge got its presenter here, so "(nothing to do)" is now a claim the
  /// rail can always back: the stage exists, it looked, there was nothing there.</para>
  ///
  /// <para><paramref name="stage"/> stays in the signature deliberately - the note is
  /// a fact ABOUT a stage, and a per-stage wording (an empty hinge is not an empty
  /// melt) is one edit away rather than one refactor away.</para>
  /// </summary>
  internal static string EmptyNote(RailState state, RoundStage stage)
    => state != RailState.Empty ? "" : "  (nothing to do)";

  /// <summary>
  /// THE RAIL'S HEADER LINE. The round's total remaining work, or just "Round" when
  /// something in it has no clock (an open hinge waits on a human, and a round with a
  /// human in it has no total).
  /// </summary>
  internal static string HeaderText(long? remainingMs)
    => remainingMs is long ms ? $"Round - {EtaText(ms)} of work left" : "Round";

  /// <summary>
  /// ONE RAIL ROW, WHOLE - glyph, stage, count, eta slot, empty note. The run log drew
  /// this line twice from two copies of the same composition: once into ImGui with a
  /// colour pushed around it, once into the StringBuilder behind Copy All. A pasted log
  /// that reads differently from the window it was copied out of is the two-surfaces
  /// bug in its smallest form, so the STRING is built here and the colour - which is
  /// the only thing the copy has no use for - stays with the window.
  /// </summary>
  internal static string RowText(in RailRow row)
  {
    var count = row.Count > 0 ? $" ({row.Count})" : "";
    return $" {Glyph(row.State)} {GatePlan.StageNoun(row.Stage)}{count}"
      + $"{EtaSuffix(row)}{EmptyNote(row.State, row.Stage)}";
  }

  /// <summary>The rail glyph for a stage's state - the deck's own vocabulary.</summary>
  internal static string Glyph(RailState state) => state switch
  {
    RailState.Halted => "!",
    RailState.Done => "x",
    RailState.Current => ">",
    _ => ".",
  };
}
