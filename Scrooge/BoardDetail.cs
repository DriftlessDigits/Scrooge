using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// One line of the detail pane's four-score table: the exit, the number the
/// router weighed, and WHAT FED IT. The three are separate fields rather than
/// one sentence because the pane draws them as columns - the numbers have to
/// line up with each other the same way the board's strip does, or the pane is
/// just the strip again in prose.
/// </summary>
/// <param name="Label">The exit's column name, same spelling the board uses.</param>
/// <param name="Value">The gil as weighed, the board's own dash, or the board's own cross.</param>
/// <param name="Source">Where that number came from. Empty when there is nothing honest to say.</param>
internal readonly record struct ScoreLine(string Label, string Value, string Source);

/// <summary>
/// Where an item's Universalis community ask stands. The pane's old blank line
/// ("no evidence yet") covered four different truths - never asked, still in
/// flight, answered-with-nothing, too stale to trust - and the reader could not
/// tell which one he was looking at (Drift, 08-06: "hard to know when it has
/// worked, when it hasn't"). One state each, one sentence each.
/// </summary>
internal enum CommunityFetchState
{
  /// <summary>Never queued this session - the next score opens the door.</summary>
  NotAsked,
  /// <summary>Asked - the answer is still in flight.</summary>
  Pending,
  /// <summary>Asked, the round failed - queued again behind the back-off.</summary>
  Retrying,
  /// <summary>Universalis answered: the DC has no settled sales on its books.</summary>
  NoTape,
  /// <summary>Answered, but older than the trust window - treated as unknown.</summary>
  Stale,
  /// <summary>Fresh evidence is banked - a blank score predates its landing.</summary>
  HasTape,
  /// <summary>Universalis off, or no data-center scope yet.</summary>
  Unavailable,
}

/// <summary>
/// One banked decision receipt, projected for the pane's trail. A narrower read
/// than <see cref="ReceiptLine"/> on purpose: the trail is about what happened
/// to an ask, not about the queue coordinates the On Market tab explains.
/// </summary>
internal readonly record struct TrailReceipt(
  long Id,
  long CreatedAt,
  string Retainer,
  long? DecidedPrice,
  string OutcomeState,
  // The A9 stamps, where the grader reached them. Null = never graded, which is
  // unambiguous by construction (SILENCE is stamped, see ReceiptGrading).
  string? InterimGrade,
  string? FinalGrade,
  int? TimeToClearDays,
  // THE STAKE the verdict is about: the receipt's queue_position, the spot this
  // ask took at decision time. A verdict without it is a word with no subject -
  // "CHASED" says the market walked down through us, and the reader's next
  // question is always "down through WHERE". Null = held (never listed), or a
  // receipt written before the queue frame.
  int? QueuePosition = null,
  // An un-adopted recon receipt (arm_id 'recon', 08-23): a Look, not an ask -
  // nothing was listed. The trail keeps the row (the decision happened and its
  // price is real evidence) but must not speak ask grammar over it.
  bool IsLook = false);

/// <summary>
/// THE RICH DETAIL PANE's pure core (item 9, 08-06 - the LanePricing/RowMemoir
/// mold: no game, no storage, no ImGui; linked into Scrooge.Tests). The window
/// gathers the operands once per selection and this composes them; nothing here
/// decides anything, and nothing here re-derives a price.
///
/// <para><b>It explains numbers, it does not re-explain decisions.</b> The
/// router's own narration is the row's story and the pane draws it verbatim -
/// see <see cref="BoardNarration.Strip"/>. What was missing was the ARITHMETIC
/// under the four cells: a cell shows "1,474" and a tooltip shows one clause,
/// and neither says that the seal number is 22 seals at a curve-discounted rate
/// or that the melt number is a band average nobody measured. Every sentence
/// below names an operand the decision actually read. If a fact is not on the
/// row, the line stays silent rather than hedging.</para>
/// </summary>
internal static class BoardDetail
{
  // ==========================================================================
  // The four-score math
  // ==========================================================================

  /// <summary>
  /// Everything the four score lines are composed from - the same operands the
  /// router scored with, carried on the row (or read beside it) rather than
  /// recomputed here.
  /// </summary>
  /// <param name="Scores">The four numbers in the board's strip order.</param>
  /// <param name="MeltGrade">What the Melt number IS - measured, band prior, or the knob.</param>
  /// <param name="Doors">Which exits exist at all; a closed door explains its own blank.</param>
  /// <param name="OwnSalePrice">The row's own last sale, 0 when the tape never saw one.</param>
  /// <param name="CommunityMedian">The DC-scope median, 0 when there is none.</param>
  /// <param name="CommunitySampleCount">How many DC sales back that median.</param>
  /// <param name="CommunityFallback">The List score leaned on the community read, not an own sale.</param>
  /// <param name="CommunityState">Where the DC ask stands - what a blank List cell actually means.</param>
  /// <param name="SealValue">Expert Delivery seals for one of these, null when the GC won't take it.</param>
  /// <param name="Seals">The rate the GC score was actually multiplied by, discount and all.</param>
  /// <param name="SkillupColor">The skill-up color the melt scorer priced in, null when none.</param>
  /// <param name="SkillupYellow">The yellow knob, in gil.</param>
  /// <param name="SkillupRed">The red knob, in gil.</param>
  internal readonly record struct ScoreOperands(
    long?[] Scores,
    MeltGrade MeltGrade,
    ExitDoors Doors,
    long OwnSalePrice = 0,
    long CommunityMedian = 0,
    int CommunitySampleCount = 0,
    bool CommunityFallback = false,
    CommunityFetchState CommunityState = CommunityFetchState.NotAsked,
    int? SealValue = null,
    SealRate Seals = default,
    DesynthSkillupColor? SkillupColor = null,
    int SkillupYellow = 0,
    int SkillupRed = 0,
    // The ask the floor refused (PricingResult.BelowFloor rows only) - the pane's
    // verdict line quotes this number, and the List cell's blank must agree with
    // it instead of guessing at causes (Drift, 08-23, the Adamantite Ingot).
    long? FloorRefusedAsk = null);

  /// <summary>
  /// The four lines, in the board's strip order - always four, always the same
  /// order, for the same reason the strip is: the pane is read against the row
  /// it was opened from, and a pane that reordered or dropped exits would make
  /// the reader hunt for the cell he just clicked.
  /// </summary>
  internal static List<ScoreLine> Scores(in ScoreOperands o)
  {
    var lines = new List<ScoreLine>(BoardCalls.Exits.Length);
    for (var i = 0; i < BoardCalls.Exits.Length; i++)
    {
      var exit = BoardCalls.Exits[i];
      var value = o.Scores is { Length: > 0 } s && i < s.Length ? s[i] : null;
      var open = o.Doors.Has(exit);
      // The board's own two glyphs, so the pane and the cell it explains never
      // disagree about what a blank means: a dash is "no evidence yet", a cross
      // is "no such door, ever".
      var shown = value is long gil
        ? BoardCalls.GradeMark(exit, o.MeltGrade) + $"{gil:N0}"
        : open ? "-" : "x";
      lines.Add(new ScoreLine(BoardCalls.ColumnLabel(exit), shown, SourceOf(exit, value, open, o)));
    }
    return lines;
  }

  /// <summary>
  /// What fed one number. A closed door explains itself with the cell's own
  /// hint; an open door with nothing behind it says so plainly; a real number
  /// names its operands and nothing else.
  /// </summary>
  private static string SourceOf(RoutingExit exit, long? value, bool doorOpen, in ScoreOperands o)
  {
    if (!doorOpen) return ExitDoors.ClosedHint(exit);
    // A blank List cell forks on the DC ask's actual state - "never asked" and
    // "asked, the answer was nothing" call for opposite amounts of patience,
    // and one sentence covering both is how the pipeline reads as a black box.
    // THE THIRD STATE (SF-B1, 2026-08-13): the answer can be IN HAND and still
    // not decide the cell - the rules declined it (a fresh local sale outranks
    // the DC; some lanes the spine refuses to price). The fetch-state hint said
    // "Refresh to let the evidence in" over exactly that blank, Drift pressed
    // Refresh, and nothing could ever change - the hint must fork on whether
    // the scorer already HELD the evidence before it talks about fetching.
    if (value is null)
      return exit != RoutingExit.List
        ? "No evidence yet - the door is open, evidence could arrive."
        // THE FLOOR-REFUSED BLANK (Drift, 08-23: the ingot's evidence priced it at
        // 40, the 75 minimum refused it, and this hint answered with two guesses
        // that were both wrong). When the refused ask is in hand, the blank has
        // ONE cause and the sentence states it.
        : o.FloorRefusedAsk is long refused
          ? $"It would list at {refused:N0}/ea - the floor refuses that ask, so List sits out. "
            + "Refreshing changes nothing here."
          : o.CommunityMedian > 0 && o.CommunitySampleCount > 0
            ? $"You have the DC's read: ~{o.CommunityMedian:N0} across {o.CommunitySampleCount} "
              + $"sale{(o.CommunitySampleCount == 1 ? "" : "s")}. It didn't set this price - "
              // One of the two old guesses is checkable against an operand already
              // on the row, so check it rather than reciting both (08-23).
              + (o.OwnSalePrice > 0
                ? "your own recent sale outranks it. "
                : "this is a row the round refuses to price. ")
              + "Refreshing changes nothing here."
            : CommunityBlankHint(o.CommunityState);

    switch (exit)
    {
      case RoutingExit.List:
        // Which witness the router actually weighed. The community read is the
        // one that has to say so out loud: it is the DC's price, not ours, and
        // it stands in only where our own tape is silent.
        if (o.CommunityFallback && o.CommunityMedian > 0)
          return $"The DC pays ~{o.CommunityMedian:N0} - {o.CommunitySampleCount} settled "
               + $"{(o.CommunitySampleCount == 1 ? "sale" : "sales")}, Universalis community. "
               + "You have never sold one.";
        if (o.OwnSalePrice > 0)
          return $"Your own last sale of one, at {o.OwnSalePrice:N0}.";
        return "";

      case RoutingExit.Desynth:
        // The grade sentence is the board's, verbatim - the cells and the pile
        // header already say it this way, and a second phrasing of the same
        // fact is how one of them ends up subtly wrong.
        var grade = BoardCalls.GradeHint(exit, o.MeltGrade);
        if (o.MeltGrade == MeltGrade.Skillup && o.SkillupColor is { } color)
        {
          var knob = color == DesynthSkillupColor.Red ? o.SkillupRed : o.SkillupYellow;
          grade += $" A {color.ToString().ToLowerInvariant()} one, at your {knob:N0} gil knob.";
        }
        return grade;

      case RoutingExit.Gc:
        if (o.SealValue is not int seals) return "";
        var rate = $"{seals:N0} seals at {o.Seals.EffectiveRate:0.##} gil/seal.";
        // The discount clause is SealRate's own, carried with the rate it
        // describes - a discounted score that reads like a full-rate one is a
        // display lie, and this is the surface that shows the multiplication.
        return o.Seals.Narration is { Length: > 0 } note ? rate + note : rate;

      case RoutingExit.Vendor:
        return "What the NPC counter pays for one.";

      default:
        return "";
    }
  }

  /// <summary>
  /// The blank List cell's sentence, one per state. Every line says which leg
  /// of the pipeline the row is standing on and what (if anything) will move it
  /// - the reader's next question is always "do I wait, refresh, or accept it".
  /// </summary>
  private static string CommunityBlankHint(CommunityFetchState state) => state switch
  {
    CommunityFetchState.Pending =>
      "No evidence yet - Universalis has been asked and the answer is in flight. Refresh once the header count clears.",
    CommunityFetchState.Retrying =>
      "No evidence yet - Universalis failed to answer. The ask is queued again and retries after the back-off.",
    CommunityFetchState.NoTape =>
      "Universalis answered: no settled sales on the DC's books. Not unasked - asked, and the answer was nothing.",
    CommunityFetchState.Stale =>
      "Universalis has a record, but it is older than your trust window - treated as no evidence until a fresher one lands.",
    CommunityFetchState.HasTape =>
      "The DC has answered since this row was scored - Refresh to let the evidence in.",
    CommunityFetchState.Unavailable =>
      "No evidence coming - Universalis is off, or no data center is known yet.",
    _ =>
      "No evidence yet - the DC has not been asked. Scoring this row queues the ask.",
  };

  // ==========================================================================
  // The receipt trail
  // ==========================================================================

  /// <summary>
  /// The trail for one (item, quality): the asks we wrote for it, newest first.
  /// Ties on created_at break on id - the later insert is the later decision,
  /// the same rule <see cref="OnMarket.Standing"/> picks a standing ask with, so
  /// a reprice and the ask it superseded can never swap places between surfaces.
  ///
  /// <para>Bounded by <paramref name="keep"/> rather than by age: receipt
  /// retention already keeps a handful per lane, and a variant standing on two
  /// retainers holds two lanes' worth. The pane wants the recent shape of this
  /// item's asks, not its whole history.</para>
  /// </summary>
  internal static List<TrailReceipt> Trail(IEnumerable<TrailReceipt> receipts, int keep = 5)
    => receipts
      .OrderByDescending(r => r.CreatedAt)
      .ThenByDescending(r => r.Id)
      .Take(Math.Max(0, keep))
      .ToList();

  /// <summary>
  /// One trail line: when, where, what we asked, and how it ended. The outcome
  /// grammar is <see cref="RowMemoir"/>'s - same four states, same words - so
  /// the memoir's one-line summary and the trail beneath it read as one voice.
  /// </summary>
  internal static string TrailLine(in TrailReceipt r, long now)
  {
    var when = OnMarket.RelativeAt(r.CreatedAt, now);
    var where = r.Retainer.Length > 0 ? $" @ {r.Retainer}" : "";
    // A Look speaks its own grammar (08-23, the Neo-Ishgardian Sword: "just now
    // @ Elwyn - asked 20,000 - still standing" over a sword still in the bags).
    // The decision is real evidence; the ask never existed.
    if (r.IsLook)
      return r.DecidedPrice is long look
        ? $"{when}{where} - your Look priced it at {look:N0} - nothing listed"
        : $"{when}{where} - your Look held it - nothing listed";
    var ask = r.DecidedPrice is long p ? $"asked {p:N0}" : "no price on record";
    var end = r.OutcomeState switch
    {
      "cleared" => r.TimeToClearDays is int d
        ? d == 0 ? "sold in under a day" : $"sold in {d}d"
        : "sold",
      "never_cleared" => "came off the board unsold",
      "gone_unobserved" => "left the board while nobody watched",
      _ => "still standing",
    };
    return $"{when}{where} - {ask} - {end}{Stamp(r)}";
  }

  /// <summary>The verdict spellings the A9 reframe writes (ruled 2026-08-15).
  /// Anything else on a row is a legacy stamp - MISS, ON_TRACK, UNDERSOLD,
  /// WELL_TIMED, UNGRADEABLE - and passes through verbatim.</summary>
  private static readonly HashSet<string> CurrentVerdicts = new(StringComparer.Ordinal)
  {
    "PASS", "LINE_MOVING", "CUT_OFF", "SILENCE",
    "CLEARED", "CHASED", "OUTGROWN", "NEVER_TESTED",
  };

  /// <summary>
  /// The A9 stamps, with the seat they are about. Bare spellings, never a
  /// sentence: a verdict is a judgement on the evidence that existed when it was
  /// made, and dressing it up would invite reading it as a recommendation. Empty
  /// when nothing was stamped - and NULL there is unambiguous, because SILENCE is
  /// a stamp too.
  ///
  /// <para><b>The seat is the stake.</b> Since the reframe the verdict is about
  /// a DECISION - which spot in the line we took - so "CHASED" alone is half a
  /// sentence. The receipt's queue_position is that spot, and it is rendered in
  /// the house's 1-based seat grammar (LanePricing.SeatOf, the run log, the
  /// census): queue_position counts the foreign rows still AHEAD of us, so seat 1
  /// is the front of the line and the displayed seat is that count plus one.
  /// Getting this conversion wrong would put the trail one seat off from every
  /// other surface that says the word.</para>
  ///
  /// <para><b>Legacy stamps pass through verbatim and wear no seat</b> - they
  /// scored the price, not the spot, and hanging a seat off "UNDERSOLD" would
  /// dress an old verdict in a frame it was never made under. History reads as
  /// what it was.</para>
  /// </summary>
  internal static string Stamp(in TrailReceipt r)
  {
    var parts = new List<string>(2);
    if (!string.IsNullOrEmpty(r.InterimGrade)) parts.Add(r.InterimGrade!);
    if (!string.IsNullOrEmpty(r.FinalGrade)) parts.Add(r.FinalGrade!);
    if (parts.Count == 0) return "";

    var seat = "";
    if (r.QueuePosition is int ahead && ahead >= 0 && parts.Exists(CurrentVerdicts.Contains))
      seat = $" from seat {ahead + 1}";
    return $"  [{string.Join(" / ", parts)}{seat}]";
  }

  /// <summary>
  /// THE SURVIVORSHIP CAVEAT, on the surface rather than in a comment nobody
  /// reads. The full argument lives once, at the seam that owns it - read
  /// <see cref="ReceiptGrading"/> before reading a trail of CLEAREDs as a
  /// scoreboard. In one line: a sold ask always reads PASS/CLEARED, because
  /// whether we could have waited out a race is unknowable, so a clean trail
  /// proves the asks SURVIVED and says nothing about whether their seats were the
  /// best ones on offer.
  ///
  /// <para>The counterweight is no longer a stamp. It is the margin measurement
  /// banked beside these verdicts (margin_donated) - gil, not a grade, and the
  /// only thing in A9 that can say the market paid more right after we left. It
  /// is deliberately not drawn here: its two ruled readers are the Phase-4
  /// revival trigger's frequency and magnitude, and a number nobody is meant to
  /// act on per-row does not belong on a per-row line.</para>
  /// </summary>
  internal static string TrailCaveat()
    => "Verdicts score the spot each ask took, not the price. A sold ask always reads "
     + "PASS / CLEARED - whether you could have held out for more is unknowable, so it "
     + "isn't scored - so a clean trail means these asks survived, never that they were "
     + "the best seats you could have taken. CHASED means your next ask went lower: the "
     + "market walked down through your spot. OUTGROWN means it went higher. What the "
     + "market paid right after you left is measured in gil on the receipt, not shown here.";
}
