using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// One row waiting on a human ruling, and the round stage that ruling would feed.
/// The stage is what makes the entry a DECISION rather than backlog: it is the
/// part of this run that changes shape if the row is ruled.
/// </summary>
internal readonly record struct JudgmentEntry<T>(T Row, RoundStage Stage);

/// <summary>
/// The FRONT-LOAD GATE (WALK unit 5, Drift 07-23): <i>"if there are decisions,
/// front-load them."</i> Press -&gt; the advisor presents every ruling that would
/// change THIS RUN's stages -&gt; Drift rules them -&gt; the run flows uninterrupted
/// to the end. No mid-run wizard stops. No silently carrying rows past. One
/// judgment session at the top, then pure flow.
///
/// <para>THE QUEUE IS A DERIVED QUANTITY, NOT A LIST. The gate never asks "what
/// is on my list of things to ask about" - it asks "which rows would ride a stage
/// of this run if ruled, but currently will not, because they are waiting on a
/// human click." That is the exact inverse of the confidence gate every bulk
/// button already obeys (<see cref="BoardConfidence.BulkSet{T}(IEnumerable{ValueTuple{T, ConfidenceTier, bool}})"/>):
/// a row rides when it is Unanimous or the player ruled it, so a row is a
/// DECISION when it is neither. This is the whole maturation vector. As the
/// confidence machinery improves - receipts ripen, assent clears dissent,
/// overrides grade the classes - fewer rows fail that bar, and the gate's queue
/// shrinks with ZERO changes to the gate. A mature round front-loads nothing most
/// nights; the gate does not disappear, it just runs out of things to say.</para>
///
/// <para>THE QUEUE'S SOURCES (the caller supplies the candidates; this core only
/// decides which of them are still open questions):</para>
/// <list type="bullet">
///   <item>bag rows in an action pile whose stage is confidence-gated - List and
///     Pull &amp; Vendor (the bell), Churn (the turn-in);</item>
///   <item>bag rows demoted into Review, attributed to the stage the router's own
///     proposal would land them in;</item>
///   <item>listed (triage) rows in Reprice and in Pull &amp; Vendor - both are
///     retainer work, so both are the BELL's now, except on a night the pinch's
///     vendor rider will reach the pull-and-vendor rows first;</item>
///   <item>NOT the Melt pile - the melt run takes the whole pile regardless of
///     tier, so a melt row rides already and is not a decision. Said to the
///     predicate now (<c>ridesWholePile</c>) rather than by dropping the rows
///     early, so the board's drawing inherits the same answer;</item>
///   <item>NOT the DEFER rows, and NOT the broader ledgerbook backlog. Defer's
///     whole contract is that the round acts on them unasked (the pile
///     formerly called Watch was excluded here too, for the opposite reason -
///     nothing was ever going to happen to those rows at all).</item>
///   <item>the SEAL-RUNWAY DISCOUNT advisory (built 07-25, see
///     <see cref="SealDiscountAdvisory"/>) - the gate's first entry that is an
///     ADVISORY rather than a row. It does NOT ask for a ruling: the knobs ARE
///     the ruling surface, so the advisory only states that the discount is in
///     force and on what numbers, and the player answers it in the config if he
///     disagrees. It is not part of the queue and never counts toward the
///     headline or the skip note - it is a sentence about the run, not a
///     decision waiting on a human.</item>
/// </list>
///
/// Pure and Dalamud-free (linked into the test project): the window feeds it
/// candidates with their tiers and staging, it answers "what still needs a human,
/// and what does the run lose if he skips."
/// </summary>
internal static class GatePlan
{
  /// <summary>
  /// The one derived quantity: a row is a DECISION exactly when it would NOT ride
  /// its stage as things stand. The inverse of the bulk gate, deliberately - if
  /// these two ever disagree, the gate is either asking about rows that already
  /// ride (noise) or carrying rows past in silence (the thing the ruling forbids).
  ///
  /// <para>THIS IS THE ONLY DEFINITION OF "UNRULED" (08-02). The board's drawing,
  /// the launch refusal, the completion banner, and this queue all read the same
  /// predicate - a second definition is how six Mixed-tier List rows once drew as
  /// pressed "router" calls while the bell silently refused every one of them.</para>
  ///
  /// <para><paramref name="inReview"/>: a Review VERDICT needs eyes whatever its
  /// evidence tier says - the router declined to call it, so nobody has.</para>
  ///
  /// <para><paramref name="ridesWholePile"/>: THE OTHER HALF OF THE DEFINITION
  /// (item 9, 08-06). A stage that takes its pile ENTIRE - the melt run - spends
  /// a Mixed row and a Unanimous row identically, so there is no ruling for
  /// either of them to be waiting on. The queue always knew this and dropped
  /// those rows before it ever asked the predicate; the BOARD did not, so a
  /// Mixed melt row drew an amber "unruled" Call and four neutral cells over a
  /// launch strip that counted zero rulings and a melt run that took the row
  /// anyway. That is the same two-definitions drift the 08-02 shake caught in
  /// the other direction, and it is why the fact belongs HERE rather than in
  /// the caller's <c>continue</c>: noise and silence are one bug seen from two
  /// sides, and one predicate is what makes both impossible.</para>
  ///
  /// <para>NOT part of this predicate, deliberately: whether the row's stage is
  /// one THIS RUN will visit. A deferred stage's rows are genuinely unruled and
  /// the board must keep saying so - the queue narrows to tonight's stages on
  /// its own (see the note on <see cref="Queue"/>), because the launch control
  /// refuses over that count and refusing over a stage the player just skipped
  /// would make the skip a trap.</para>
  ///
  /// <para><paramref name="deferred"/>: THE HEADLINER'S AMENDMENT (08-06). A
  /// row that DEFERS is not waiting on anybody - the round spends it on
  /// schedule, flagged, and the door to overrule it stays open before and
  /// after. So it must not sit in the judgment queue, must not draw "unruled",
  /// and must not be counted by the launch refusal. Note the shape: this is
  /// the same disjunct <see cref="BoardConfidence.Rides"/> gained on the
  /// other side, which is what keeps the inverse an inverse. Review membership
  /// - <paramref name="inReview"/>, the router declining to call it - still
  /// blocks, and a row with no scored winner never arrives here deferred at
  /// all (see <see cref="DeferPlan.State"/>).</para>
  /// </summary>
  internal static bool NeedsRuling(ConfidenceTier tier, bool playerResolved,
    bool inReview = false, bool ridesWholePile = false, bool deferred = false)
    => !ridesWholePile && !playerResolved && !deferred
       && (inReview || !BoardConfidence.IsBulkEligible(tier));

  /// <summary>
  /// The judgment queue: the candidates that still need a human, in round order
  /// (the order the run will spend them). Ordering is stable inside a stage, so
  /// the gate reads top-to-bottom like the run itself.
  ///
  /// <para>The caller supplies only candidates whose stage is part of THIS run -
  /// that scoping is the queue's, not the predicate's, and it is the one thing
  /// the board deliberately does not share (see <see cref="NeedsRuling"/>).</para>
  /// </summary>
  internal static List<JudgmentEntry<T>> Queue<T>(
    IEnumerable<(T Row, RoundStage Stage, ConfidenceTier Tier, bool PlayerResolved, bool InReview,
      bool RidesWholePile, bool Deferred)> candidates)
    => candidates
      .Where(c => NeedsRuling(c.Tier, c.PlayerResolved, c.InReview, c.RidesWholePile, c.Deferred))
      .Select(c => new JudgmentEntry<T>(c.Row, c.Stage))
      .OrderBy(e => Array.IndexOf(RoundPlan.Order, e.Stage))
      .ToList();

  /// <summary>The stage named the way the gate says it out loud.</summary>
  internal static string StageNoun(RoundStage stage) => stage switch
  {
    RoundStage.Pinch => "pinch",
    RoundStage.BellRun => "bell",
    RoundStage.Desynth => "melt",
    RoundStage.TurnIn => "turn in",
    RoundStage.Recon => "recon",
    RoundStage.Triage => "triage",
    _ => "?",
  };

  /// <summary>
  /// The gate's one-line headline: how many rulings, and which stages they feed -
  /// "4 rulings before Continue: 3 bell, 1 turn in". Empty on an empty queue, which is
  /// the mature night: nothing to say, so the gate says nothing.
  ///
  /// <para><b>BEFORE CONTINUE, NOT BEFORE THE ROUND</b> (the mechanical pile, 3b). It
  /// read "before this round" from the front-load era, when the queue locked the launch
  /// button. The queue moved to the hinge's Continue gate (ruled Q1): by the time this
  /// line draws, the round is already running - the pinch read boards, recon banked
  /// decisions - and the press these rulings stand in front of is the one that commits
  /// the act half. "Before this round" pointed the reader at a door he had already
  /// walked through.</para>
  /// </summary>
  internal static string Headline(IEnumerable<RoundStage> stages)
  {
    var byStage = new Dictionary<RoundStage, int>();
    var total = 0;
    foreach (var s in stages)
    {
      byStage[s] = byStage.GetValueOrDefault(s) + 1;
      total++;
    }
    if (total == 0) return "";
    var parts = RoundPlan.Order
      .Where(byStage.ContainsKey)
      .Select(s => $"{byStage[s]} {StageNoun(s)}");
    return $"{total} ruling{(total == 1 ? "" : "s")} before Continue: {string.Join(", ", parts)}";
  }

  /// <summary>
  /// THE ADVISORY (07-25): one line, at the top of the gate, when the runway-gated
  /// seal discount is in force for this round - what is discounted, on what runway,
  /// against what line, by how much.
  ///
  /// <para>Informational by construction. Every other gate entry is a question
  /// waiting on a click, and this one is not: the threshold and factor knobs are
  /// the ruling surface, so a player who disagrees answers in the config, not here.
  /// That is why it needs no new verb, no queue entry, and no place in the headline
  /// count - a "4 rulings" headline that included a sentence nobody can rule would
  /// be a lie about how much work the gate is asking for.</para>
  ///
  /// <para>Empty string when nothing is discounted, and the caller draws nothing:
  /// the gate stays quiet on a night the discount never fired, exactly as it stays
  /// quiet on a night with nothing to rule.</para>
  /// </summary>
  // Player language (strings pass, 08-02): what the user needs is the fact
  // (you're stocked on ventures), the consequence (seals are worth less this
  // round, melt wins more often), and the numbers that matter (the two rates).
  // "Runway", "your 1.8wk line", "x0.15" and the config nudge were shop
  // jargon and design commentary leaking into the UI.
  internal static string SealDiscountAdvisory(SealRate rate)
    => rate.Discounted && rate.Stock is int stock
      ? rate.Factor <= 0.0
        ? $"You're sitting on {stock:N0} ventures - past the melt line, so seals score at "
          + $"nothing this round and melt takes everything it can."
        : $"You're stocked on ventures ({stock:N0}), so new seals aren't worth much right now: "
          + $"turn-ins are valued at {rate.EffectiveRate:0.##} gil/seal instead of {rate.BaseRate} "
          + $"this round, and melt will win more rows than usual."
      : "";

  // The SKIP is gone (ruled ledger, stage 2a), and LeavingNote with it. "No
  // silently carrying rows past" used to be honoured by a button that named what it
  // left behind; the launch control now REFUSES over that same count, so there is
  // no skip left to narrate. What the run leaves behind is said at the other end
  // instead - see RoundBanner, which counts the rows still waiting when a round
  // runs out of stages.
}
