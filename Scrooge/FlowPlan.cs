using System;

namespace Scrooge;

/// <summary>
/// Which executor just finished. One value per RUN SHAPE, not per orchestrator
/// class: the reprice stage and the header's Go button are the same triage
/// executor and report the same kind, because the flow cares what the world
/// looks like afterwards, not which button started it.
/// </summary>
internal enum RunKind
{
  /// <summary>The pinch executor's board pass (with its vendor rider).</summary>
  Pinch,
  /// <summary>The Hawk run - the one-door bell.</summary>
  Bell,
  /// <summary>
  /// The STANDING-LISTING executor: reprices, pulls, vendors (the bell's first leg
  /// OR the header Go). Named for the rows it works - the ones already on the
  /// market - since the Rounds re-seating (unit 5). It was called Triage, which
  /// was the same word <see cref="RoundStage.Triage"/> uses for the human hinge, and
  /// one word meaning two things in one wiring table is how a stage gets mapped to
  /// the wrong executor by somebody reading carefully.
  /// </summary>
  Standing,
  /// <summary>The desynthesis melt.</summary>
  Melt,
  /// <summary>GC Expert Delivery churn.</summary>
  TurnIn,
  /// <summary>The Venture Coffer rider - a bag-unlock pass ahead of the bell.</summary>
  Coffer,
  /// <summary>
  /// The recon pass (Rounds unit 2): the hawk's per-item loop run to the spine and
  /// then CANCELLED instead of priced. A separate kind from <see cref="Bell"/>
  /// despite walking the same navigation, because the flow's question is what the
  /// world looks like afterwards - and afterwards, recon has changed nothing about
  /// the world except what we know about it. Reported by
  /// <see cref="ReconRunOrchestrator"/> since unit 2 (recon-run).
  /// </summary>
  Recon,
}

/// <summary>How a run ended. Two values, because the flow only ever asks one question.</summary>
internal enum RunOutcome
{
  /// <summary>It finished its work.</summary>
  Complete,
  /// <summary>It died - cancelled, timed out, watchdogged, refused mid-flight.</summary>
  Aborted,
}

/// <summary>
/// WHAT THE RUN DID, carried by the completion that announces it (review ruling
/// S1/S2, 2026-08-10).
///
/// <para>THE BUG THIS SHAPE EXISTS TO MAKE UNWRITABLE. A completion only ENQUEUES;
/// the handler runs a tick later, on the framework pump. Every executor tears its
/// run down as it ends - <c>Plugin.CurrentRun = null</c> - so by the time the
/// handler asks the live run what it processed, there is no live run to ask. Two
/// subscribers were written against that null and both were dead code the day they
/// shipped: the rail's per-step tallies never rendered, and the melt's run id never
/// joined the set the bell admits yields from, which quietly re-created the exact
/// "arrived after the hinge" refusal the one-door bell was built to end.</para>
///
/// <para>So the facts travel WITH the completion, snapshotted by the executor while
/// its run is still alive. A handler that reads these can be wrong about the world -
/// it cannot be reading a run that no longer exists.</para>
///
/// <para><see cref="None"/> is a completion with no run behind it: a refusal on the
/// road, a stage that never got a queue. It is not "we don't know" - it is zero
/// items processed and no melt, which is exactly what happened.</para>
/// </summary>
internal readonly record struct RunFacts(long? DesynthRunId, int ItemsProcessed)
{
  /// <summary>A completion with no run behind it - a refusal, not a silence.</summary>
  internal static RunFacts None => default;
}

/// <summary>
/// ONE run completion. Every executor reports exactly this when it ends, and
/// every consequence of "a run ended" hangs off it (see <see cref="FlowPlan"/>).
/// <paramref name="Reason"/> is the executor's own words for an abort, verbatim -
/// it becomes the named gap on the halt banner, so it must say what died, never
/// a category. <paramref name="Facts"/> is what the run DID, stamped at report
/// time - see <see cref="RunFacts"/> for why it cannot be read off the live run.
/// </summary>
internal readonly record struct RunCompletion(
  RunKind Kind, RunOutcome Outcome, string? Reason = null, RunFacts Facts = default)
{
  internal static RunCompletion Done(RunKind kind, RunFacts facts = default)
    => new(kind, RunOutcome.Complete, null, facts);

  internal static RunCompletion Died(RunKind kind, string reason, RunFacts facts = default)
    => new(kind, RunOutcome.Aborted, reason, facts);
}

/// <summary>What a completion does to a live round's held place.</summary>
internal enum FlowReaction
{
  /// <summary>Nothing - no round, no matching stage, or the stage already settled.</summary>
  None,
  /// <summary>HALT the round over the dead stage and name the gap.</summary>
  Halt,
}

/// <summary>What the flow does with the stage the cursor is currently offering.</summary>
internal enum FlowAdvance
{
  /// <summary>Nothing fires and nothing is named - no live round, a halt, a run in
  /// flight, or no stage left to offer.</summary>
  Nothing,
  /// <summary>Fire it now - the player is standing where it happens.</summary>
  Fire,
  /// <summary>Name the walk (and the port, where one is offered) and wait for arrival.</summary>
  NameWalk,
}

/// <summary>
/// RUN COMPLETION IS A FLOW EVENT (WALK unit 6, from the 07-24 lap). One root
/// cause wore four faces that night, and every one of them was the same sentence:
/// <i>an executor finished and the world stayed on pre-run data until a human
/// intervened.</i> The window sat on a 4.6h-stale header after a pinch and
/// proposed a RE-PINCH off it; a Go run cleared three sections invisibly; a melt
/// that died while the Ledger was closed came back reading "done" after a reload;
/// and the standing book waited for the next pinch to re-learn our own listings.
///
/// The fix is ONE mechanism, not four timers: every executor reports a
/// <see cref="RunCompletion"/> when its run ends - complete OR aborted - and the
/// consequences subscribe. This file is the pure half: the stage map and the
/// reaction rule, Dalamud-free so it links into the test project. The wiring half
/// (the event hub, the pump, the subscribers) is RunFlow.
///
/// <para>THE FIRST LAW HOLDS HERE, STRUCTURALLY - and moved one level up on 07-25.
/// A completion still only narrates, re-reads and persists: <see cref="Reaction"/>
/// has no value meaning "fire". Firing lives in <see cref="Advance"/>, whose every
/// path is Nothing unless a round the human PRESSED is live and un-halted. The
/// boundary did not soften; it is now stated in one rule with one operand
/// (roundActive) instead of being spread across every button in the deck.
/// We EARN what we make.</para>
/// </summary>
internal static class FlowPlan
{
  /// <summary>
  /// The round stage a run kind IS, or null when the run is not a stage. The
  /// coffer rider is the null: it fires at the FRONT of the melt stage and hands
  /// off whether it opened many, none, or died, so the melt's own run is what marks
  /// the stage done - a dead coffer loop must never halt the round over a stage
  /// that then proceeds anyway (CofferOrchestrator's standing ruling).
  ///
  /// <para>TWO KINDS MAP TO THE BELL (07-25). The bell stage is a chain of runs -
  /// the standing-listing leg (Standing: reprices and pulls) and then the listing leg
  /// (Bell: the Hawk run) - and either one dying is the bell dying. They report
  /// separately because they ARE separate executors; they answer to one stage
  /// because they are one errand at one stop.</para>
  ///
  /// <para>THE TWO TRIAGES ARE ONE TRIAGE NOW (Rounds unit 5, 08-10). This table used
  /// to carry a warning: <c>RunKind.Triage</c> was the standing-listing executor and
  /// mapped to <see cref="RoundStage.BellRun"/>, while <see cref="RoundStage.Triage"/>
  /// was the human hinge - one word, two meanings, in the one file where confusing
  /// them is a wiring bug. The executor is <see cref="RunKind.Standing"/> now, so the
  /// warning is retired and the fact it was guarding still holds: the hinge has no run
  /// kind at all and never will. Its executor is Drift pressing Continue, and a press is
  /// not a run that reports a completion.</para>
  /// </summary>
  internal static RoundStage? StageOf(RunKind kind) => kind switch
  {
    RunKind.Pinch => RoundStage.Pinch,
    RunKind.Bell => RoundStage.BellRun,
    RunKind.Standing => RoundStage.BellRun,
    RunKind.Melt => RoundStage.Desynth,
    RunKind.TurnIn => RoundStage.TurnIn,
    RunKind.Recon => RoundStage.Recon,
    _ => null,
  };

  /// <summary>
  /// What the round does about this completion. The rule that matters is the
  /// negative one: a completion NEVER advances the cursor and never fires
  /// anything (the cursor moves because <see cref="MarksStageDone"/> marked the
  /// stage and the next flow tick reads the moved cursor); this event exists to
  /// catch the case where the fired run died.
  ///
  /// <para>Halt when, and only when: a round is live, it is not already halted
  /// over some other corpse, this run maps to a round stage, the round is the
  /// thing that FIRED it (a stray manual run is ignored), and the run aborted.
  /// "Fired" means the stage is IN FLIGHT (SF2 - stages mark done at completion,
  /// so a dead run's stage is never Done while the round's own run is live) or the
  /// staged melt (contract B, 07-26: staged at fire, marked at completion) - a
  /// melt dying under the player's own Run Desynth press is still the round's
  /// stage dying. A death over a stage that IS Done can only be a stray manual
  /// run, and the predicate answers false for it.</para>
  ///
  /// <para>This is deliberately evaluated at EVENT time rather than at draw time.
  /// The old abort-epoch draw-poll had a reload hole: a melt that died while the
  /// Ledger window was closed, followed by a plugin reload before the next deck
  /// draw, restored a round whose Desynth stage still read done. The persisted
  /// state is now written when the run dies, so there is no window in which the
  /// truth exists only in a counter nobody has read yet.</para>
  /// </summary>
  internal static FlowReaction Reaction(
    RunCompletion completion, bool roundActive, bool roundHalted, Func<RoundStage, bool> firedByRound)
  {
    if (!roundActive || roundHalted) return FlowReaction.None;
    if (completion.Outcome != RunOutcome.Aborted) return FlowReaction.None;
    if (StageOf(completion.Kind) is not RoundStage stage) return FlowReaction.None;
    return firedByRound(stage) ? FlowReaction.Halt : FlowReaction.None;
  }

  /// <summary>
  /// THE COMPLETION MARK (shake finding SF2, 2026-08-13): whether this completion is
  /// the moment its round stage becomes DONE. Stages used to mark at fire time, and
  /// the mark lied for the length of the run - the 08-12 pinch died 3 items into 102
  /// with "x pinch" already on the rail, and the round offered the hinge over an
  /// unpinched board. A stage is done when its run says Complete, exactly as contract
  /// B always held for the melt.
  ///
  /// <para>Three refusals, each its own reason: an ABORT never marks (the halt
  /// machinery owns deaths); a run whose stage is not the one in flight never marks
  /// (a manual run outside the round's claim is not the round's stage finishing);
  /// and the bell's STANDING leg never marks even in flight - it is the first leg of
  /// a chain, and the chain's own settle logic (the listing leg's completion, or the
  /// honest decline when there is nothing to list) decides when that stage is over.
  /// The melt needs no arm here because contract B's fire site never sets it in
  /// flight - its own completion block marks it.</para>
  /// </summary>
  internal static bool MarksStageDone(RunCompletion completion, RoundStage? stageInFlight)
    => completion.Outcome == RunOutcome.Complete
       && completion.Kind != RunKind.Standing
       && StageOf(completion.Kind) is RoundStage stage
       && stage == stageInFlight;

  /// <summary>
  /// THE ADVANCE RULE (WALK unit 9, Drift 07-25): <i>"stages fire automatically when
  /// the predecessor completes and the player is standing where the stage needs
  /// them. Prompts exist only for the port and for loud failures."</i>
  ///
  /// <para>The 07-24 lap wanted a press per stage, and every one of those presses
  /// was the player telling the advisor something the advisor already knew. So the
  /// rule is exactly the sentence above, and nothing more: given a live round and
  /// the stage the cursor offers, fire it or name the walk.</para>
  ///
  /// <para><b>THE FIRST LAW, STRUCTURALLY.</b> Every path out of here is Nothing
  /// unless <paramref name="roundActive"/> - and a round is Active only after the
  /// human pressed Start. There is no operand that could be true on an idle deck,
  /// no timer that ages into a fire, and no "we noticed you're at a bell" branch
  /// outside a round. Halted is Nothing too: the round froze over a corpse and the
  /// human's Resume is the press that unfreezes it. We EARN what we make.</para>
  ///
  /// <para><b>Why it is polled rather than raised by the completion.</b> This is
  /// asked on the framework tick, not (only) when a run reports done - because the
  /// ruling has two triggers, "the predecessor completed" and "the player arrived",
  /// and arrival is not an event anything raises. One rule asked repeatedly covers
  /// both; a completion-only path plus a separate arrival path would be two rules
  /// that can disagree about the same stage on the same frame, which is precisely
  /// the class of bug unit 6 was spent on. The completion's job is to refresh what
  /// the cursor is reading; this decides what happens next.</para>
  ///
  /// <para><paramref name="offered"/> is the cursor's own answer (RoundPlan.Offer),
  /// so "has work" and "which stage" arrive already decided - null means the round
  /// is complete or holds nothing, and nothing is what an empty round does.
  /// <paramref name="runBusy"/> is the flow's one concession to the world: a stage
  /// whose predecessor is still in flight has not had a predecessor complete.</para>
  /// </summary>
  internal static FlowAdvance Advance(
    bool roundActive, bool roundHalted, bool runBusy,
    RoundStage? offered, bool locationSatisfied)
  {
    if (!roundActive || roundHalted) return FlowAdvance.Nothing;
    if (runBusy) return FlowAdvance.Nothing;
    if (offered is null) return FlowAdvance.Nothing;
    return locationSatisfied ? FlowAdvance.Fire : FlowAdvance.NameWalk;
  }

  /// <summary>
  /// THE ROUND ENDS ITSELF (2026-07-26, the zombie round). A round with nothing
  /// left to offer and nothing in flight is FINISHED - and must say so in its own
  /// persisted state, not wait for a human to click a banner. The old shape kept
  /// Active=true after the last stage, so a completed round sat armed in config;
  /// a reload inside the staleness ceiling then restored it as live, and the flow
  /// picked up a three-hour-old errand over tonight's fresh rows without a press.
  ///
  /// <para>Not while halted (a corpse is not a finish line) and not while a run is
  /// busy (the last stage marks done at FIRE time - ending under its own live run
  /// would cancel the errand out from under it).</para>
  /// </summary>
  internal static bool ErrandOver(bool roundActive, bool roundHalted, bool runBusy, bool hasOffer)
    => roundActive && !roundHalted && !runBusy && !hasOffer;

  /// <summary>
  /// The halt this completion holds, in the HALT-NAME-RESUME vocabulary: what
  /// died, the executor's own reason, and what would clear it. Each stage names
  /// its own clearing action honestly - every one of these executors RESCANS at
  /// fire time, so "Resume" always means "look again from where the world is
  /// now", never "replay what it was about to do".
  /// </summary>
  internal static RoundHalt HaltFor(RoundStage stage, string? reason)
  {
    var said = string.IsNullOrWhiteSpace(reason) ? "the run stopped" : reason!;
    var (what, wouldClear) = stage switch
    {
      RoundStage.Pinch => ("Pinch",
        "Clear it and Resume - the pinch re-reads the board from the roster."),
      RoundStage.BellRun => ("Bell run",
        "Clear it and Resume - the bell re-reads each retainer's standing listings and recomposes its rows."),
      RoundStage.Desynth => ("Melt",
        "Clear it and Resume - the melt rescans the bags from where they are now."),
      RoundStage.TurnIn => ("Turn-in",
        "Clear it and Resume - the turn-in re-reads the delivery list at the counter."),
      RoundStage.Recon => ("Recon",
        "Clear it and Resume - recon re-derives its work set, so anything it already banked is skipped and only the stale half is walked again."),
      // The hinge can only halt on something OUTSIDE itself dying while it stood
      // open (there is no recon-the-hinge to abort). It still owes a sentence:
      // a stage with no halt line falls to "The stage", which is the advisor
      // shrugging at the one stage the player was standing in front of.
      RoundStage.Triage => ("Triage",
        "Clear it and Resume - the hinge re-reads the decisions from where they are now; nothing you ruled is lost."),
      _ => ("The stage", "Clear it and Resume."),
    };
    return RoundHalt.Plainly(stage, what, said, wouldClear);
  }
}
