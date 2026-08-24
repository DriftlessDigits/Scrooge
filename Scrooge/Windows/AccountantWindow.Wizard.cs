using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Numerics;

using Scrooge.Board;
using Scrooge.Rounds;

namespace Scrooge.Windows;

/// <summary>
/// THE LIVE ROUND'S SCREEN (Rounds unit 5, the Allagan-Market shape). The rail down the
/// left, the current step's own surface on the right, that step's verbs along the bottom -
/// and nothing else, because the window IS the errand once one is underway.
///
/// <para>Composition, not new UI: every pane here hands off to something that already
/// exists and already knows how to draw itself. The one rule the file keeps for itself is
/// that the rail and the bar are told the SAME offer, asked once - a rail pointing at one
/// stage while the bar offers another is the unit-6 bug drawn closer together.</para>
/// </summary>
internal sealed partial class AccountantWindow
{
  /// <summary>
  /// THE ROUND'S ONE SCREEN (Movement 3). Dual-pane once a Round is underway: the
  /// stage rail down the left, the current step's own surface on the right, that
  /// step's verbs along the bottom. Idle, it is the LAUNCH PREVIEW - the plan line,
  /// the fit check, Make the Rounds - because that is what a player who just pressed
  /// Round at the bell came for.
  ///
  /// <para><b>Idle is a page, not a desk.</b> The standalone judgment desk died with
  /// ruling Q4 - "why would I judge something outside a round?" - so a Round window
  /// with no Round has nothing to offer but the last one's report and the way to
  /// start the next. Both of those are HERE now: unit 5 kept them on the gil
  /// dashboard, which made starting an errand cost a trip through the dashboard's
  /// everything (Drift, 08-13), and Movement 3 moved the preview to the door it is
  /// reached through.</para>
  /// </summary>
  private void DrawRoundScreen(List<InboxRow> standingRows, DeckState? deck, bool nothingHere)
  {
    // The empty page is for an empty BOARD with no errand over it. A live Round keeps
    // its wizard whatever the piles hold - see the deck derivation in Draw.
    if (nothingHere && !_conductor.Plan.Active)
    {
      ImGui.TextDisabled("Nothing on the board - no routable gear in your bags and no open flags.");
      if (ImGui.Button("Refresh")) RefreshAll();
      return;
    }

    var state = deck!.Value;

    switch (AccountantPlan.ScreenFor(_conductor.Plan.Active, _conductor.RoundEnded))
    {
      case RoundScreen.Wizard:
        DrawWizard(standingRows, state);
        return;
      case RoundScreen.Report:
        DrawCompletionReport(WaitingWork(state), inline: true);
        ImGui.Separator();
        DrawLaunchPreview(state);
        return;
      default:
        DrawLaunchPreview(state);
        return;
    }
  }

  /// <summary>
  /// THE WIZARD (Rounds unit 5, the Allagan-Market shape). Three regions, and each
  /// one answers a different question the player actually asks mid-errand:
  /// <list type="bullet">
  ///   <item><b>Left rail</b> - where does the whole thing stand? The same
  ///     <see cref="StageRail"/> the run log renders horizontally, drawn down the
  ///     side with live per-step tallies.</item>
  ///   <item><b>Right pane</b> - what am I looking at right now? The current step's
  ///     own surface, re-hosted rather than rebuilt: the board at the hinge, the
  ///     salvage preview at the melt, the run-host progress everywhere else.</item>
  ///   <item><b>Bottom bar</b> - what can I press? The current step's verbs, and
  ///     only the current step's.</item>
  /// </list>
  ///
  /// <para>The rail and the pane never disagree, because both are told the SAME
  /// offer: <see cref="RoundPlan.Offer"/> is asked once here and handed to both. That
  /// is the unit-6 lesson applied to one window instead of two - a rail pointing at
  /// one stage while the bar offers another is the same bug, drawn closer together.</para>
  /// </summary>
  private void DrawWizard(List<InboxRow> standingRows, DeckState deck)
  {
    var offer = _conductor.Plan.Offer(deck.HasWork, RoundConductor.LocationSatisfied);
    var current = _conductor.CurrentDisplayStage(offer);
    var pane = AccountantPlan.PaneFor(_conductor.Plan.Halted, current);
    // The step the wizard is ON. A halt outranks the cursor: the round is frozen over
    // that stage, and it is the stage the player has to do something about.
    var step = _conductor.Plan.HaltStage ?? current;

    // The bar's height is reserved BEFORE the panes claim the rest, so a long board
    // can never push the round's own verbs off the bottom of the window.
    var barHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing() * 3;
    var body = Math.Max(96f, ImGui.GetContentRegionAvail().Y - barHeight);
    var railWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.32f, 170f, 280f);

    if (ImGui.BeginChild("##accRail", new Vector2(railWidth, body), true))
      DrawVerticalRail(deck, current);
    ImGui.EndChild();

    ImGui.SameLine();

    if (ImGui.BeginChild("##accPane", new Vector2(0, body), false))
      DrawStepPane(pane, step, standingRows, deck);
    ImGui.EndChild();

    ImGui.Separator();
    DrawVerbBar(pane, step, deck, offer);
  }

  /// <summary>
  /// THE RAIL, VERTICAL. Same rows, same glyphs, same measured ETAs as the run log's
  /// horizontal one - <see cref="StageRail.Build"/> is asked once and the wording
  /// comes from <see cref="AccountantPlan.RailLine"/>, so the two surfaces cannot
  /// word one stage differently.
  ///
  /// <para><b>Dithered, not hidden.</b> An empty or deferred step draws at half alpha
  /// rather than dropping out of the list: the player's mental model of a Round is
  /// six steps in a fixed order, and a rail that silently shortened itself would make
  /// "did it skip the melt?" a question he has to answer by counting.</para>
  ///
  /// <para><b>ONE HEADER COMPOSER</b> (ruled 08-21). This seat used to spell its own
  /// "Round - ~12m left" beside the run log's "Round - ~12m of work left" - one clock
  /// quoted in two dialects, which is the two-surfaces bug wearing a synonym.
  /// <see cref="StageRail.HeaderText"/> owns the sentence now and both seats ask it.
  /// The total is recomputed from the rows every frame, so it drains as the work
  /// does rather than freezing at the press.</para>
  /// </summary>
  private void DrawVerticalRail(DeckState deck, RoundStage? current)
  {
    var rows = StageRail.Build(deck.CountOf, deck.HasWork, _conductor.Plan.IsDone,
      current, _conductor.Plan.HaltStage, RoundConductor.RateFor, RoundConductor.LiveRunEtaMs(), deck.PinchAsks);

    ImGui.TextColored(ScroogeColors.Header, StageRail.HeaderText(StageRail.RemainingMs(rows)));
    ImGui.Separator();

    foreach (var row in rows)
    {
      var deferred = deck.Skipped.Contains(row.Stage);
      var color = row.State switch
      {
        RailState.Halted => ScroogeColors.Spent,
        RailState.Done => ScroogeColors.Earned,
        RailState.Current => ScroogeColors.Amber,
        _ => ScroogeColors.Muted,
      };
      // The dither. Empty and deferred steps keep their colour and lose their weight,
      // which is what "skipped, and you can still see it" looks like.
      if (row.State == RailState.Empty || deferred)
        color = new Vector4(color.X, color.Y, color.Z, color.W * 0.45f);

      // THE HINGE'S RAIL ROW SAYS WHERE IN THE WALK YOU STAND (ruled 08-15): "triage -
      // page 2/3, 4 calls left". It rides the TALLY seat rather than a new one, because
      // the tally seat is already "what this stage has to say about itself right now" -
      // and the hinge is the one stage with no run to report, so the seat was empty.
      // Only while the walk is live and the hinge is the current step: a walk narrating
      // its position from three stages away would be describing a room nobody is in.
      var tally = deferred
        ? "(skipped this run)"
        : row.Stage == RoundStage.Triage && current == RoundStage.Triage
          && _walk is { } walk && walk.Rail() is { Length: > 0 } line
          ? line
          : AccountantPlan.DoneTally(row.State, row.Stage, _conductor.StageTally.GetValueOrDefault(row.Stage));

      ImGui.PushStyleColor(ImGuiCol.Text, color);
      ImGui.TextWrapped(AccountantPlan.RailLine(row, GatePlan.StageNoun(row.Stage), tally));
      ImGui.PopStyleColor();
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip(RoundSkips.Hint(row.Stage));
    }

    // THE TWO CLOCKS, under the rail (ruled 08-15 shake): the return deadline AND the
    // round's machine-time spend against it, in one sentence - naming only the
    // deadline made the reader do the subtraction that is the line's entire point.
    // Composition is StageRail.ReturnClockLine's (pure, pinned); absent when the
    // clock is unreadable (no ventures out, or not at a bell) - the fit line's rule.
    if (GameSafe.SoonestVentureReturnSeconds() is long returnSecs)
    {
      ImGui.Separator();
      // Wrapped, not clipped (Drift, 08-22 lap): the rail is narrow and this line is
      // the round's longest sentence - a comparison cut off mid-clause reads as noise.
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
      ImGui.TextWrapped(StageRail.ReturnClockLine(returnSecs, rows));
      ImGui.PopStyleColor();
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip("The soonest venture completion across your retainers, against the round's own measured machine time - the same two clocks the fit check raced before the press. Time you spend at the hinge spends against the gap.");
    }
  }

  /// <summary>
  /// The right pane: the current step's own surface. Composition, not new UI - every
  /// branch here hands off to something that already existed and already knows how to
  /// draw itself.
  /// </summary>
  private void DrawStepPane(StepPane pane, RoundStage? step,
    List<InboxRow> standingRows, DeckState deck)
  {
    switch (pane)
    {
      case StepPane.Halt:
        DrawHaltPane(step, standingRows, deck);
        break;
      case StepPane.Completion:
        DrawCompletionReport(WaitingWork(deck), inline: false);
        break;
      case StepPane.Board:
        DrawHingePane(standingRows, deck);
        break;
      default:
        DrawRunPane(step!.Value, deck);
        break;
    }
  }

  /// <summary>
  /// The frozen round: what died, in the halt's own words. The verbs (Resume,
  /// Re-Look, End round here) live in the bar with every other step's, so the pane stays
  /// one thing - the story - and the bar stays the one place anything is pressed.
  /// </summary>
  private void DrawHaltPane(RoundStage? step, List<InboxRow> standingRows, DeckState deck)
  {
    if (_conductor.Plan.CurrentHalt is not RoundHalt halt) return;

    ImGui.TextColored(ScroogeColors.Spent, $"{GatePlan.StageNoun(halt.Stage)} - halted");
    ImGui.Separator();
    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Spent);
    ImGui.TextWrapped(halt.Message);
    ImGui.PopStyleColor();

    // A halt over the hinge is the one case where the board is still the thing to
    // look at: nothing about the halt changed what the player was in the middle of
    // ruling, and the halt line says as much ("nothing you ruled is lost"). The
    // frame's OWN rows, handed down rather than rebuilt: a halted hinge that scanned
    // its own board would draw one set of rows against a deck derived from another.
    if (step == RoundStage.Triage)
    {
      ImGui.Spacing();
      DrawHingePane(standingRows, deck);
    }
  }

  /// <summary>
  /// THE ROUND'S OWN HONEST ARITHMETIC (see <see cref="RoundBanner"/>) - lead, count,
  /// breakdown - in the two shapes it is read in. "Round complete - the board is
  /// worked" was a claim about the STAGES wearing the clothes of a claim about the
  /// BOARD: a round can run every stage it has and still leave a night's work sitting
  /// there. It only says "worked" over a clean book.
  ///
  /// <para><paramref name="inline"/> is the LAYOUT, and the only fork: the wizard's
  /// final page (spec section 5) stacks the lines and closes with what the numbers
  /// mean for next time, while the report strip over the idle screen runs them across
  /// one line and carries the dismiss. The sentences are the same sentences.</para>
  ///
  /// <para><b>The count no longer clicks (unit 5).</b> It used to jump to the pile it
  /// was refusing over, on the standalone desk. There is no standalone desk, and the
  /// rows are the NEXT Round's hinge queue - a click that landed on a board judging
  /// hours-old data would hand back exactly the habit ruling Q4 deleted.</para>
  /// </summary>
  private void DrawCompletionReport(WaitingTally waiting, bool inline)
  {
    ImGui.PushStyleColor(ImGuiCol.Text,
      waiting.BookIsClean ? ScroogeColors.Earned : ScroogeColors.Amber);
    if (inline) ImGui.Text(RoundBanner.Lead(waiting));
    else ImGui.TextWrapped(RoundBanner.Lead(waiting));
    ImGui.PopStyleColor();

    if (!waiting.BookIsClean)
    {
      if (inline) ImGui.SameLine();
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Info);
      if (inline) ImGui.Text(RoundBanner.Count(waiting));
      else ImGui.TextWrapped(RoundBanner.Count(waiting));
      ImGui.PopStyleColor();
      if (inline) ImGui.SameLine();
      ImGui.TextDisabled(RoundBanner.Breakdown(waiting));
    }

    if (inline)
    {
      ImGui.SameLine();
      if (ImGui.SmallButton("done###roundDone"))
        _conductor.DismissReport();
      return;
    }

    if (waiting.BookIsClean) return;
    ImGui.Spacing();
    ImGui.TextWrapped("These are the next Round's hinge queue - the Look half will read "
      + "them again before you rule on them.");
  }

  /// <summary>
  /// THE HINGE'S PANE: the context line, the Refresh, and THE WALK (Task 3, spec ruled
  /// 08-15). It used to be the full-board triage panel - every pile, every row, one
  /// screen - and that panel is gone: "short-changing the triage, or not giving the
  /// decisions the room and UI needed to make a good call, is self-defeating."
  ///
  /// <para>What replaced it is a short forced walk of small pages, each shaped for its
  /// decision. Only the two lines above the walk survive from the old pane, and they
  /// survive because they are about the WHOLE hinge rather than about any one row: these
  /// prices are from the last read, and here is how to read again.</para>
  /// </summary>
  private void DrawHingePane(List<InboxRow> standingRows, DeckState deck)
  {
    // The board's own context line: these prices are from the last read, and how
    // long ago that was - the pinch's clock and recon's, and nothing else (ruled
    // 08-21, pen 6). The rest of the old header's readouts moved to the gil
    // dashboard - they were status about the WORLD, not about this decision.
    ImGui.Text("prices as of your last read");
    ImGui.SameLine();
    ImGui.TextDisabled($"({RipenessSensors.HeaderClocks(_cache.LastFullScanAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), _cache.ReconLastBankedAt, _cache.ReconBankedCount)})");
    ImGui.SameLine();
    if (ImGui.SmallButton("Refresh###hingeRefresh"))
      RefreshAll();
    ImGui.Separator();

    DrawWalk(BuildBoardRows(standingRows), deck);
  }

  /// <summary>
  /// A RUN STEP'S PANE: what the stage is, what it holds, and - while its executor is
  /// working - the one standard progress readout (<see cref="RunHostRender"/>), which
  /// is exactly why the re-host is composition rather than new UI.
  ///
  /// <para>The melt is the one step with a surface of its own to host: the salvage
  /// preview's body draws here whenever the game's window is up, so the press contract
  /// B owes ("Run Desynth is yours") is made inside the wizard instead of behind it.</para>
  ///
  /// <para><b>NO FIT LINE HERE</b> (ruled 08-21). The pinch pane used to redraw the
  /// at-press fit quote mid-round, which put a sentence about whether to START the
  /// round on a screen where it has already started - and its "round est." was the
  /// press's frozen number, still quoting minutes the run had since spent. The rail
  /// owns the running story: it has the live countdown, and its header drains. The
  /// fit check speaks once, before the press, on the idle screen
  /// (<see cref="DrawLaunchPreview"/>).</para>
  /// </summary>
  private void DrawRunPane(RoundStage step, DeckState deck)
  {
    var riderCoffers = Plugin.Configuration.OpenVentureCoffers ? _cache.CofferCount : 0;
    ImGui.TextColored(ScroogeColors.Header, StageLabel(step, deck.CountOf(step),
      step == RoundStage.Desynth ? riderCoffers : 0, deck.Bell, deck.Seals));
    ImGui.Separator();

    // The live run, in the one readout every executor drives. GC keeps its own
    // lifecycle (seals, not gil), so it is asked separately - the same split the
    // header always had.
    if (Plugin.GcTurnIn.IsRunning)
      RunHostRender.Progress(Plugin.GcTurnIn.Run, "Turning in");
    else if (Plugin.CurrentRun is { IsComplete: false } live)
      // The two-register line (F9): what the run has listed and what it has
      // vendored, each clause only when nonzero - never a bare "0 gil".
      RunHostRender.ProgressWithRegisters(live.Lifecycle, GatePlan.StageNoun(step),
        live.ListedCount, live.GilAsked);

    if (step == RoundStage.TurnIn)
      DrawSealOverflowNote(deck.Seals);

    if (step == RoundStage.Desynth)
    {
      if (_conductor.MeltStaged)
      {
        ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Info);
        ImGui.TextWrapped("Melt staged - press Run Desynth below. It is the one press the "
          + "round will not make for you: a melted item has no undo.");
        ImGui.PopStyleColor();
      }
      // The re-host. Draws nothing when the game's salvage window is shut, which is
      // the honest answer - the preview is a view OF that window's list.
      ImGui.Spacing();
      Plugin.DesynthPreview.DrawHosted();
    }

    if (RoundConductor.AnyRunBusy())
      ImGui.TextDisabled("run in progress - the round waits...");
  }

  /// <summary>
  /// THE BOTTOM BAR: the current step's verbs, and only the current step's.
  ///
  /// <para>Four of them are the Round's own and ride every step - Re-Look, Pause here,
  /// End round here, and the Look-age label the hinge owes (unit 4's
  /// <see cref="RoundResume.Line"/>). The fifth is whatever THIS step does, and it is
  /// the only one that changes: Start on a run stage the player is standing in,
  /// Continue at the hinge, Resume over a corpse, Done on the final page.</para>
  ///
  /// <para>The fire button asks <see cref="FlowPlan.Advance"/> - the same rule the
  /// framework tick fires on, with the same operands - so the button the player sees
  /// and the auto-fire that happens a tick later can never be about different stages
  /// or disagree about whether he is standing in the right place.</para>
  /// </summary>
  private void DrawVerbBar(StepPane pane, RoundStage? step, DeckState deck, RoundOffer? offer)
  {
    // THE LOOK'S AGE, at the hinge and over a halt (ruled 08-10: AFK-at-the-hinge and
    // resume-after-a-reload are ONE staleness conversation, so they get one sentence).
    // A label, not a threshold - Drift is the threshold, and the panel veto is the
    // actual safety layer.
    if (pane is StepPane.Board or StepPane.Halt && _conductor.LookDoneAt is DateTimeOffset lookDone)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Info);
      ImGui.TextWrapped(RoundResume.Line(lookDone.ToLocalTime(), DateTimeOffset.Now,
        _conductor.CachedDecisionsThisRun, deck.RulingsNeeded));
      ImGui.PopStyleColor();
    }

    switch (pane)
    {
      case StepPane.Halt:
        if (ImGui.Button("Resume###roundResume")) _conductor.ResumeRound();
        ImGui.SameLine();
        break;

      case StepPane.Completion:
        if (ImGui.Button("Done###roundDone"))
          _conductor.DismissReport();
        return; // a finished round has nothing left to pause or re-look

      case StepPane.Board:
        DrawContinueVerb(deck);
        ImGui.SameLine();
        break;

      default:
        DrawFireVerb(step!.Value, deck, offer);
        ImGui.SameLine();
        break;
    }

    // THE RE-LOOK, always present, never recommended (ruled 08-10). The plugin has no
    // honest basis for deciding when a Look has gone stale, so it offers the verb and
    // says nothing about whether to press it.
    if (ImGui.SmallButton("Re-Look###roundReLook")) _conductor.ReLook();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("Read the boards again before acting - the WHOLE listable bag, "
        + "not just the stale half. A Re-Look that respected the freshness window would "
        + "read nothing and tell you it had looked.");

    // PAUSE, and the two states it must not pretend in. A halted Round is ALREADY
    // held - pausing it would be a second press that does nothing, which is the one
    // thing the deck must never do - and a Round with a run in flight cannot be
    // frozen without freezing it around a live executor.
    ImGui.SameLine();
    var alreadyHeld = _conductor.Plan.Halted;
    var runBusy = RoundConductor.AnyRunBusy();
    ImGui.BeginDisabled(alreadyHeld || runBusy);
    if (ImGui.SmallButton("Pause here###roundPause")) _conductor.PauseRound();
    ImGui.EndDisabled();
    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
      ImGui.SetTooltip(alreadyHeld
        ? "Already held - this Round is waiting on your Resume as it is."
        : runBusy
          ? "A run is in flight - let it finish or cancel it, then pause."
          : "Holds the Round exactly here. Close the window, go do something else; the "
            + "cursor, the transcript and everything recon banked are on disk. Resume "
            + "lands you back on this step.");

    // "End round here", not "Abandon" (ruled 08-23): the verb IS the healthy exit -
    // banks the tally, releases holds, loses nothing - and the scary word made the
    // machine's own author hesitate to press it. Pause = same-plan freeze; this = end
    // now, next round replans fresh. The labels carry that distinction.
    ImGui.SameLine();
    if (ImGui.SmallButton("End round here###roundAbandon")) _conductor.CancelRound();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("Ends the Round now and banks its tally - nothing is lost. "
        + "Holds release, the decisions recon banked stay with their items, and the "
        + "next round replans fresh from wherever the bags stand.");
  }

  /// <summary>
  /// THE HINGE'S ONE PRESS (contract B, ruled Q1). Continue is the human's mark: it
  /// calls <see cref="RoundPlan.MarkDone"/> for the hinge and nothing else does, ever,
  /// which is what makes "the act half never runs unasked" structural rather than
  /// procedural - the cursor stops at a fired-not-done stage by its own rules.
  ///
  /// <para>Gated on <see cref="AccountantPlan.Continue"/>, which reads the judgment
  /// queue's count already narrowed to tonight's selected steps. Same arithmetic the
  /// launch button used to refuse over, new seat.</para>
  /// </summary>
  private void DrawContinueVerb(DeckState deck)
  {
    var actHasWork = deck.HasWork(RoundStage.Desynth) || deck.HasWork(RoundStage.BellRun)
      || deck.HasWork(RoundStage.TurnIn);
    // The forced walk is the GATE's rule (ruled 08-15: "Continue itself should refuse
    // until you've walked every page"). A live walk whose cursor is not past the end
    // still has pages the player never saw; no walk means no pages to owe.
    var walkFinished = _walk is not { } liveWalk || liveWalk.Cursor >= liveWalk.Pages.Count;
    var gate = AccountantPlan.Continue(deck.RulingsNeeded, actHasWork, walkFinished);

    ImGui.BeginDisabled(!gate.CanContinue);
    if (ImGui.Button("Continue###hingeContinue"))
    {
      // INVARIANT B (the addendum): the press is the commit, so the press is where the
      // snapshot is taken - and the mark, and the persist, in that one order. The
      // conductor owns all three because the order is the invariant.
      _conductor.CommitHinge();
      // The walk is spent. Its pulls and verdicts went into the commit above, and a
      // walk left standing over a marked hinge would re-offer the same three pages to
      // a player the round has already moved past.
      _walk = null;
    }
    ImGui.EndDisabled();
    if (gate.CanContinue && ImGui.IsItemHovered())
      ImGui.SetTooltip("Commits the act half: melt, bell, turn-in, in order, off the "
        + "decisions standing right now. This is the last press before anything "
        + "irreversible happens.");

    if (gate.CanContinue) return;

    ImGui.SameLine();
    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
    ImGui.Text(gate.Companion);
    ImGui.PopStyleColor();
    // The frame's own queue, off the deck (3-3b): this hover used to rebuild every
    // standing row and re-walk the whole board to name the same stages the deck had
    // already counted a few lines earlier.
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip($"{GatePlan.Headline(deck.JudgmentStages)}. "
        + "Every one of them is a case on Bag decisions - click here to go there.");
    // THE JUMP, RE-SEATED (Task 3). It used to open the Review pile on the full board
    // and scroll to it; there is no full board, and the rows it named are the Bag decisions
    // page by construction - Bag decisions only ever draws the Review pile. Same gesture,
    // same destination, one page instead of a scroll position.
    if (ImGui.IsItemClicked())
      JumpToBagDecisions();
  }

  /// <summary>
  /// A RUN STEP'S PRESS: fire it, or name the walk that will fire it on arrival. The
  /// button is the manual override - the flow fires the same stage through the same
  /// <see cref="FireRoundStage"/> on the framework tick - so pressing it EARLY is
  /// always allowed and never required.
  /// </summary>
  private void DrawFireVerb(RoundStage step, DeckState deck, RoundOffer? offer)
  {
    var anyBusy = RoundConductor.AnyRunBusy();
    var advance = FlowPlan.Advance(_conductor.Plan.Active, _conductor.Plan.Halted, anyBusy,
      step, RoundConductor.LocationSatisfied(step));

    var fireLabel = step == RoundStage.Pinch && deck.Fit.RequiresConfirm
      ? $"Start anyway - {FitCheck.ShortDur((deck.EstRoundMs ?? 0) / 1000)} round vs {FitCheck.ShortDur(deck.VentureSecs ?? 0)} return###roundFire"
      : $"Start: {StageLabel(step, deck.CountOf(step), 0, deck.Bell, deck.Seals)}###roundFire";

    if (anyBusy || (_conductor.MeltStaged && step == RoundStage.Desynth))
    {
      // Nothing to press: the run owns the wheel, or the melt's press is the player's
      // and it lives in the salvage window the pane is already hosting.
      ImGui.BeginDisabled(true);
      ImGui.Button(fireLabel);
      ImGui.EndDisabled();
      return;
    }

    if (advance == FlowAdvance.NameWalk)
    {
      ImGui.BeginDisabled(true);
      ImGui.Button(fireLabel);
      ImGui.EndDisabled();
      ImGui.SameLine();
      ImGui.TextDisabled(step == RoundStage.Desynth
        ? "close the retainer bell first - the game refuses desynth while occupied"
        : $"walk to {PlaceName(step)} - it fires when you get there");
      // WALK unit 7: where the round NAMES the walk is where the port is offered.
      DrawPortOffer(_conductor.TurnInPort(), "wizardPort");
      return;
    }

    if (ImGui.Button(fireLabel))
      _conductor.FireRoundStage(step, deck);
    if (step == RoundStage.BellRun && ImGui.IsItemHovered())
      ImGui.SetTooltip($"One trip to the retainers: {deck.Bell.Breakdown}. Reprices and pulls run first at their own retainers, then the listing run posts your routed gear, your fresh melt yields and everything else the Hawk would list.");

    // An opportunistic offer says WHY it jumped the chain, and what it stepped over -
    // the player must never wonder whether the Round forgot a step.
    if (offer is { Opportunistic: true } && _conductor.Plan.Next(deck.HasWork) is RoundStage waiting)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Info);
      ImGui.TextWrapped(
        $"You're standing where the {GatePlan.StageNoun(step)} happens - taking it now. "
        + $"The {GatePlan.StageNoun(waiting)} is still waiting its turn.");
      ImGui.PopStyleColor();
    }
  }

  // ==========================================================================
  // The port (WALK unit 7): one decision, two surfaces
  // ==========================================================================

  /// <summary>
  /// Casts the offered teleport. Called from a button's click handler and from
  /// nowhere else - that is the whole first-law contract for this feature.
  /// </summary>
  private static void FireTurnInPort()
  {
    if (PortOrchestrator.Destination() is not { } dest)
    {
      Svc.Chat.PrintError("[Scrooge] Can't port - expected a Grand Company to port to, but you haven't joined one.");
      return;
    }
    PortOrchestrator.FirePort(dest);
  }

  /// <summary>
  /// Draws whatever the port decision came to, beside the line that names the
  /// walk. An offer is a BUTTON; a refusal and a nearer-walk are text. The walk
  /// line itself never moves - a failed or unavailable port leaves the player
  /// exactly the instruction he had before.
  /// </summary>
  internal static void DrawPortOffer(PortDecision decision, string id)
  {
    switch (decision.Verdict)
    {
      case PortVerdict.Offer:
        if (PortOrchestrator.Destination() is not { } dest) return;
        // Offers carry no message line (ruled 08-23): the button is the offer.
        if (ImGui.Button($"{PortPlan.ButtonLabel(dest.Name)}###{id}"))
          FireTurnInPort();
        if (ImGui.IsItemHovered())
          ImGui.SetTooltip("Casts Teleport - it costs gil and it moves you. Nothing else in the round does that, which is why this one asks. You still walk the last yards to the counter; arriving is what arms the turn-in.");
        break;

      case PortVerdict.OfferBellClose:
        if (PortOrchestrator.Destination() is not { } bellDest) return;
        if (ImGui.Button($"{PortPlan.BellCloseButtonLabel(bellDest.Name)}###{id}"))
          // Unit 9's own machinery, inside a human press exactly as its first law
          // demands: close the session windows innermost-first, wait for the
          // occupancy to clear, then fire the same port the plain button fires.
          // An unknown occupier or a window that won't close refuses in the
          // spine's own words - and the port is never fired after a refusal.
          OccupancyTransition.ClearThenRun("port", FireTurnInPort,
            err => Svc.Chat.PrintError($"[Scrooge] {err}"));
        if (ImGui.IsItemHovered())
          ImGui.SetTooltip("Closes the retainer window (Teleport can't cast while you're at the bell), then casts the port. It costs gil and it moves you - one click, because you asked for both.");
        break;

      case PortVerdict.WalkIsNearer:
      case PortVerdict.Settling:
        // Settling renders MUTED, not red: "the game is still busy" is a status, and
        // dressing it as a refusal is what made a self-clearing blink read like a
        // wall the player had to do something about.
        ImGui.TextDisabled(decision.Message);
        break;

      case PortVerdict.Refuse:
        ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Spent);
        ImGui.TextWrapped(decision.Message);
        ImGui.PopStyleColor();
        break;
    }
  }

  // ==========================================================================
  // What a stage is called, and the two advisories every stage may owe
  // ==========================================================================

  /// <summary>
  /// What a stage says on the deck's cursor line and on its fire button. The bell
  /// speaks its whole scope - "bell run (4 list + 2 reprice + 1 vendor)" - because
  /// the bell is now three verbs at one stop and a bare total would not tell the
  /// player which of them he is about to authorize.
  /// </summary>
  private static string StageLabel(RoundStage s, int count, int coffers, BellTally bell,
    SealFit? seals = null) => s switch
  {
    RoundStage.Pinch => "pinch the board",
    RoundStage.BellRun => count > 0 ? $"bell run ({bell.Breakdown})" : "bell run (0)",
    // The melt names its coffers: they are work the stage will do (and the reason
    // the stage is offered at all when the row count is zero).
    RoundStage.Desynth => coffers > 0
      ? $"open desynthesis ({count} + {coffers} coffer{(coffers == 1 ? "" : "s")} to open)"
      : $"open desynthesis ({count})",
    // The turn-in's count wears the wallet's answer when the two disagree: a plan
    // that says "52" against a wallet holding 23 of them is the plan lying with a
    // true number (see SealFit).
    RoundStage.TurnIn => $"turn in ({count}){seals?.PlanSuffix ?? string.Empty}",
    // The count is the STALE half, not the bag - see DeckState.CountOf. "12 stale"
    // rather than a bare "(12)" because the number is the answer to a question the
    // player never asked out loud ("why isn't it reading all 87?"), and the one
    // word is cheaper than the tooltip that would otherwise owe it.
    RoundStage.Recon => $"recon the board ({count} stale)",
    // Unit 5 landed, so the "in the order before it is in the build" note this
    // comment used to carry is history: the hinge is the board's one host and this
    // label names a step the player actually stands at.
    RoundStage.Triage => "triage the calls",
    _ => "?",
  };

  private static string PlaceName(RoundStage s) => s switch
  {
    RoundStage.Pinch => "a retainer bell (roster view)",
    RoundStage.TurnIn => "your GC's Expert Delivery",
    // The hinge is a conversation - it happens where you are (RoundPlace.Anywhere).
    RoundStage.Triage => "wherever you are",
    _ => "a retainer bell",
  };

  /// <summary>
  /// The fit check's verdict: amber when the round outlasts the haul, green when it
  /// fits, muted when a clock is missing.
  ///
  /// <para><b>ONE READER</b> (ruled 08-21). It used to be said twice - once on the
  /// launch preview before the press, once again on the pinch pane mid-round - and the
  /// second seat was a press-time sentence read to a player who had already pressed,
  /// quoting an estimate that stopped ticking at the press. The at-press quote is the
  /// idle screen's alone; the rail narrates the round once it is walking.</para>
  /// </summary>
  private static void DrawFitLine(FitCheck fit)
  {
    ImGui.PushStyleColor(ImGuiCol.Text, fit.Verdict switch
    {
      FitVerdict.DoesntFit => ScroogeColors.Amber,
      FitVerdict.Fits => ScroogeColors.Earned,
      _ => ScroogeColors.Muted,
    });
    ImGui.TextWrapped(fit.Message);
    ImGui.PopStyleColor();
  }

  /// <summary>
  /// The seal wallet's warning, said once and in one voice. Nothing when the pile
  /// fits, when the wallet is unreadable, or when there is nothing waiting - a plan
  /// that already matches reality has no news.
  /// </summary>
  private static void DrawSealOverflowNote(SealFit? seals)
  {
    if (seals is not { Overflows: true } fit) return;
    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
    ImGui.TextWrapped(fit.Note);
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("The run still starts and still stops honestly at the cap - this is the plan admitting it up front. Spend seals down (gear, materia, ventures) and the halt clears itself.");
  }
}
