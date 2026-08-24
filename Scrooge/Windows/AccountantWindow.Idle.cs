using Dalamud.Bindings.ImGui;
using System;
using System.Linq;
using System.Numerics;

using Scrooge.Rounds;

namespace Scrooge.Windows;

/// <summary>
/// THE IDLE SCREEN AND THE DASHBOARD'S STRIP - the two surfaces a player reads when no
/// Round is walking. One is the pre-flight checklist the Round's own window opens on; the
/// other is the world's readouts on the gil dashboard, plus a door.
///
/// <para>They share <see cref="DrawDashboardReadouts"/> deliberately: the plan line and
/// the fit check are only as good as the read they were scored against, and the age of
/// that read is the first thing worth knowing before a press. One implementation, so the
/// two screens cannot date the same board differently.</para>
/// </summary>
internal sealed partial class AccountantWindow
{
  /// <summary>
  /// The dashboard's strip: the world readouts, and a DOOR to the Round.
  ///
  /// <para><b>The launch left this strip (Movement 3).</b> It used to carry Make the
  /// Rounds, the step boxes, the plan line and the resume line - so the only way to
  /// start an errand was through the dashboard, and the only way to read what is on
  /// the market was to start one. Both halves of that are fixed by the same move: the
  /// launch preview went to the Round's own window (it IS the idle screen there), and
  /// the standing asks came here. What is left is what the strip was always for -
  /// what the board was scored against - plus one button that opens a window and
  /// starts nothing.</para>
  /// </summary>
  internal void DrawDashboardStrip()
  {
    var deck = DashboardDeck();
    DrawDashboardReadouts(deck);

    // The errand's own state, in one line, because a dashboard that said nothing
    // about a Round already underway would be the reader's blind spot. It STATES; the
    // Resume it might tempt is on the wizard, with the halt it answers.
    if (_conductor.Plan.Active)
      ImGui.TextColored(ScroogeColors.Amber,
        _conductor.Plan.Halted ? "A Round is holding." : "A Round is underway.");

    if (ImGui.Button($"{AccountantPlan.DoorLabel(_conductor.Plan.Active)}###roundDoor"))
      Plugin.OpenRoundDoor();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip(_conductor.Plan.Active
        ? "Opens the Round you are in - the rail, the step you are on, and its verbs."
        : "Opens the Round's own screen: the plan, the fit check, and Make the Rounds. Nothing starts until you press it there.");
  }

  /// <summary>
  /// THE READOUTS - what the board was scored against, and what the world is doing
  /// while it was. Was the Ledger window's header; every line of it is status about
  /// the WORLD rather than a decision about a row, which is exactly why it survived
  /// the desk and moved to the dashboard.
  ///
  /// <para><b>THE BLOCK RE-LAY (ruled 08-22, off the player's own screenshot).</b> Every
  /// readout used to chain <c>SameLine</c> + <c>ImGui.Text</c> onto one running sentence,
  /// which broke twice over. <b>It clipped:</b> <c>Text</c> does not wrap, so the live
  /// line ended "retry in 2" - the seconds of a countdown fell off the right edge, and
  /// the one readout whose whole point is a number lost its number. <b>And it welded five
  /// instruments together:</b> the read's age, the board's operands, a fetch fault, a
  /// fleet advisory and the seal overflow, joined by " - " and separated by " - " so the
  /// reader had no way to tell a new instrument from a new clause of the last one.</para>
  ///
  /// <para>So the block is laid out by INSTRUMENT FAMILY now. Line one is the read and
  /// its clocks. Line two is the board's operands - the numbers every verdict below was
  /// scored against. Every advisory below that gets a line of its own and only when it
  /// fires, and every one of them is <c>TextWrapped</c>, so nothing in this block can
  /// clip again. A quiet night renders two lines.</para>
  /// </summary>
  private void DrawDashboardReadouts(DeckState deck)
  {
    // "The board speaks in last-read tense" is the design mantra; the player gets the
    // fact it encodes (strings-two): these prices are from the last scan, not live -
    // and the parenthetical beside it carries THE TWO CLOCKS (ruled 08-21, pen 6):
    // the pinch's read age and recon's, which are the two reads every verdict below
    // was scored against. The market_events tally that used to ride here is gone -
    // it only counted what our own runs saw, so it was self-observation dressed as
    // market news.
    ImGui.Text("prices as of your last read");
    ImGui.SameLine();
    ImGui.TextDisabled($"({RipenessSensors.HeaderClocks(_cache.LastFullScanAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), _cache.ReconLastBankedAt, _cache.ReconBankedCount)})");

    // ------------------------------------------------------------------
    // LINE 2 - THE BOARD'S OPERANDS. The numbers the router was judging when it
    // scored the rows below. Its segments chain, because they are one instrument;
    // nothing else in this method chains onto them.
    // ------------------------------------------------------------------

    // PROTECTED HOLDS ARE A CONFIG FACT, NOT A STATE (ruled 08-06) - and only when
    // there are any, because "0 held" is a sentence about nothing.
    var heldCount = _cache.Items.Count(i => i.ActivePile == BoardPile.Silent
      && i.Pile == RoutingExit.Hold);

    // The curve's verdict CARRIES the stock (V15), so the bare token count stands
    // down when it fires: one telling of one number, whichever instrument owns it.
    var sealsBit = _cache.VentureStock is not null && _cache.SealRate.Discounted;
    var started = false;

    if (_cache.VentureStock is int stock)
    {
      if (!sealsBit)
      {
        // The stock IS the routing operand (the seal S-curve, 2026-08-05): the
        // readout shows the number the router is judging. No projection, no runway
        // - the dial is tokens, the same dial Drift thinks in.
        ImGui.TextWrapped($"{stock:N0} venture tokens");
        started = true;
      }
      else
      {
        // V15: THE VERDICT AND ITS LIVE OPERAND, and nothing else. The curve's
        // anchors are the SealCurve knobs' own business - a readout that recited
        // them would be the configuration standing where the decision belongs.
        ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
        ImGui.TextWrapped(_cache.SealRate.Factor <= 0.0
          ? $"seals at 0% of face - stocked past the melt line - {stock:N0} ventures stocked."
          : $"seals at ~{_cache.SealRate.Factor * 100:0}% of face - {stock:N0} ventures stocked.");
        ImGui.PopStyleColor();
        // ONE plain sentence (ruled 08-16 round walk, red-pen 1): the fact and the
        // consequence, in the rates this round actually multiplied by. No curve
        // talk, no config anchors.
        if (ImGui.IsItemHovered())
          ImGui.SetTooltip(
            $"{_cache.SealRate.EffectiveRate:0.##} gil/seal this round instead of {_cache.SealRate.BaseRate}, so melt wins more rows.");
        started = true;
      }
    }

    if (heldCount > 0)
    {
      if (started) ImGui.SameLine();
      ImGui.TextDisabled($"{(started ? "- " : "")}{heldCount} held");
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip("Gear in a gearset, fully spiritbonded, or wearing materia - never auto-routed, by your own settings. Not a decision waiting on you; the router just leaves it alone.");
    }

    // Data-warming honesty (session 3, item 7; reworded 08-06): while Universalis
    // lookups are out the board is scoring on what it has - say so, and count
    // LOOKUPS, not "pending".
    var statPending = UniversalisStats.PendingCount;
    var histPending = UniversalisHistory.PendingCount;
    var uniPending = statPending + histPending;
    if (uniPending > 0)
    {
      // A HELD queue speaks in the line, not just the tooltip (Drift, 08-06: "not
      // obvious that it is there"). Healthy stays amber; failed goes warning with the
      // countdown.
      // ITS OWN WRAPPED LINE (ruled 08-22). Chained onto the operand line this read
      // "retry in 2" on the player's own screenshot - the countdown's seconds fell off
      // the right edge of a Text that cannot wrap, on the one readout that exists to
      // carry a number.
      var backoff = Math.Max(UniversalisStats.BackoffRemainingSeconds, UniversalisHistory.BackoffRemainingSeconds);
      var noun = uniPending == 1 ? "lookup" : "lookups";
      ImGui.PushStyleColor(ImGuiCol.Text, backoff > 0 ? ScroogeColors.Warning : ScroogeColors.Amber);
      ImGui.TextWrapped(backoff > 0
        ? $"Universalis not answering - {uniPending} {noun} held, retry in {backoff / 60}m {backoff % 60:00}s"
        : $"checking market data - {uniPending} {noun} out");
      ImGui.PopStyleColor();
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip(
          $"Universalis lookups still out: {statPending} world almanac (velocity/recency), {histPending} DC sale history."
          + "\nLookups, not items - one item can owe both kinds, and the count covers every surface the board scores (bag, standing asks, slow movers)."
          + "\nVerdicts marked \"~ community read\" may firm up on the next Refresh."
          + (backoff > 0 ? "\nThe last fetch round failed - nothing is lost, the queue retries after the back-off." : ""));
    }

    // The capacity advisory (gate 9b). STATES, never blocks - the hawk handles a full
    // fleet honestly at the bell, and the observation may be a Round old.
    var obsAge = FleetCapacity.ObservedAtUnix > 0
      ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - FleetCapacity.ObservedAtUnix
      : (long?)null;
    if (FleetCapacity.Advisory(deck.Bell.ListCount, FleetCapacity.FreeSlots, obsAge) is { } capacity)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, capacity.Tight ? ScroogeColors.Amber : ScroogeColors.Muted);
      ImGui.TextWrapped(capacity.Text);
      ImGui.PopStyleColor();
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip("Free sell slots across the fleet, counted off the bell roster the last time a run read it. An advisory, not a gate: a hawk that outnumbers the slots lists what fits and says so at the bell.");
    }

    // The seal-runway advisory banner is GONE from the dashboard (ruled 08-16 round
    // walk, red-pen 1): the readout tag + its hover already say the discount, and a
    // third telling repeated the token count and the rates on the same screen.
    // GatePlan.SealDiscountAdvisory itself survives for any surface that has no tag.

    DrawSealOverflowNote(deck.Seals);
  }

  /// <summary>
  /// MAKE THE ROUNDS - the Round's one entry, and the counts that advertise the next
  /// one. This is the launch strip's arithmetic in its new seat.
  ///
  /// <para><b>What it kept:</b> the per-run step checkboxes (unchecking one DEFERS
  /// that step tonight - see <see cref="RoundSkips"/>), the plan line with the
  /// deferrals folded in, and the fit check's two clocks.</para>
  ///
  /// <para><b>What it LOST, and why:</b> the rulings lock. "N rulings needed" used to
  /// refuse the launch, because the launch was the last press before the act half.
  /// It is not any more - the hinge is (ruled Q1) - and a Look half that reads boards
  /// needs no rulings at all. So the count still renders here, as an ADVERTISEMENT of
  /// what the next hinge will ask for, and the button starts the Round anyway. The
  /// gate did not weaken; it moved to where it bites.</para>
  ///
  /// <para><b>Where it is drawn (Movement 3):</b> the Round's own window, as the idle
  /// screen. It sat on the gil dashboard, which meant every errand started with a trip
  /// through eleven tabs of readouts - and meant a player who only wanted to READ the
  /// dashboard had a launch button in his way. The preview is the screen the door
  /// opens now; the dashboard keeps the world readouts and a door.</para>
  ///
  /// <para><b>The live and ended branches are gone from here.</b> A live Round draws
  /// the WIZARD, and an owed report draws over this preview rather than inside it -
  /// both are <see cref="AccountantPlan.ScreenFor"/>'s call, made once, at the top of
  /// the screen. This method only ever runs with no Round underway.</para>
  /// </summary>
  private void DrawLaunchPreview(DeckState deck)
  {
    // The readouts lead the preview for the same reason they lead the dashboard: the
    // plan line and the fit check below are only as good as the read they were scored
    // against, and the age of that read is the first thing worth knowing before a
    // press. Same method, one implementation - the dashboard hosts it too.
    DrawDashboardReadouts(deck);
    ImGui.Separator();

    // THE RETIREMENT, AT THE DOOR (B1.4, ruled 08-21). If a parked round was dropped
    // on restore for being too old, this is the screen the player opens looking for
    // it - so this is where he is told, above the plan that replaced it. It used to
    // be a chat error fired on the first frame after a reload, which is nowhere near
    // the moment he goes looking.
    if (IdlePlan.RetiredRoundLine(_conductor.RetiredStaleRound,
          Plugin.Configuration.RoundStalenessCeilingHours) is { Length: > 0 } retired)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
      ImGui.TextWrapped(retired);
      ImGui.PopStyleColor();
      ImGui.Separator();
    }

    // The plan, VERTICAL (SF-P1), and the boxes RIDE THE PLAN (ruled 08-15 shake).
    // The checkbox strip and the plan were the same six stages listed twice; now each
    // plan row leads with its own enable checkbox, so the idle screen reads as a
    // pre-flight checklist. Unchecking DEFERS that step for this Round only - the
    // boxes re-check themselves when it ends, so skipping is always a fresh act.
    DrawIdlePlan(deck);

    // The fit check speaks before the press - the two clocks, or why one is blind.
    DrawFitLine(deck.Fit);

    var armed = RoundPlan.Order.Count(deck.HasWork);
    var turnInOnly = armed == 1 && deck.HasWork(RoundStage.TurnIn);
    var walletFull = deck.Seals is { Ready: > 0, Fits: 0 };
    // The rulings gate is the HINGE's, not the launch's - so the launch does not ask
    // about it at all any more (the minors batch retired the parameter that this call
    // site had been passing zero to since unit 5).
    var launch = LaunchControl.Assess(armed, turnInOnly, walletFull);

    // BELOW the checklist (ruled 08-15 shake): you read the plan, you check the fit,
    // THEN you press - a launch button above the list was the trigger before the
    // pre-flight. This preview only draws INSIDE the Round's window, so the press
    // changes the screen under itself (preview -> wizard) rather than opening anything.
    ImGui.BeginDisabled(!launch.CanFire);
    if (ImGui.Button("Make the Rounds###roundStart"))
      _conductor.StartRound();
    ImGui.EndDisabled();
    if (launch.CanFire && ImGui.IsItemHovered())
      ImGui.SetTooltip("Pinch, recon, the hinge, melt, bell, turn in - in order. The Look half runs unblocked; you rule at the hinge, and nothing irreversible happens until you press Continue.");

    if (!launch.CanFire)
    {
      ImGui.SameLine();
      ImGui.TextDisabled(launch.Companion);
      if (launch.Block == LaunchBlock.SealWalletFull && ImGui.IsItemHovered())
        ImGui.SetTooltip("The turn-in is the only work there is and your seal wallet has no room for any of it. Spend some seals, or uncheck GC turn-in and run the rest.");
    }

    // THE PENDING RULINGS, as an advertisement (ruled Q4: this window is not a desk).
    // The number is the hinge's own gate count, so what it says here and what the
    // Continue refuses over are one arithmetic - it just has no button attached. ONE
    // SENTENCE, capped there by ruling (SF-P1): no rows, no items, no buttons.
    if (IdlePlan.WaitingDecisions(deck.RulingsNeeded) is { Length: > 0 } waiting)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
      ImGui.TextWrapped(waiting);
      ImGui.PopStyleColor();
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip("Rows that would ride a step of this Round if you ruled on them. You rule at the hinge, against boards recon read minutes earlier - not here, and not tonight on yesterday's data.");
    }

    // THE LAST ROUND, at the bottom, quiet: history under the plan rather than over it.
    // Absent until a round has ended - see LastRoundTally for what survives to here.
    if (IdlePlan.LastRoundLine(Plugin.Configuration.LastRound,
          DateTimeOffset.Now, DateTimeOffset.Now.Offset) is { Length: > 0 } last)
    {
      ImGui.Separator();
      ImGui.TextDisabled(last);
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip("What the previous Round's steps reported processing, each in that step's own units - the same tallies its rail showed while it walked. It is history: nothing here is about the Round you are about to start.");
    }
  }

  /// <summary>
  /// THE IDLE PLAN, DOWN THE PAGE (SF-P1, 2026-08-15) - the wizard's rail, drawn for a
  /// round that has not started.
  ///
  /// <para>Same <see cref="StageRail.Build"/>, same <see cref="AccountantPlan.RailBody"/>
  /// wording, same dither and the same hover as
  /// <see cref="DrawVerticalRail"/> - so the plan a player reads before the press and
  /// the rail he watches during the run are one sentence per stage, not two spellings of
  /// it. The composition is <see cref="IdlePlan.Plan"/>'s; this only colours it.</para>
  ///
  /// <para><b>The enable checkbox sits in the glyph's seat</b> (ruled 08-15 shake): the
  /// old separate checkbox strip and this plan were the same six stages listed twice,
  /// and an idle rail where every state is Pending was a column of dots. Mid-round the
  /// wizard's rail keeps the glyphs - states are the story there, and the boxes are not
  /// flippable once the Round is walking.</para>
  ///
  /// <para><b>Nothing is current, halted, or done</b>, because no round is underway: the
  /// marks are handed in as constants rather than read off the cursor, which is what
  /// makes the tense rule structural. A row cannot say what a stage DID on a screen that
  /// has no run behind it (see the tense note on <see cref="IdlePlan"/>).</para>
  ///
  /// <para>The armed pinch's forecast rides in the same way it does mid-round - the
  /// deck's own <c>PinchAsks</c>, which is the fit check's operand two lines down, so the
  /// pinch row and the fit line cannot price one errand off two reads (SF-P2).</para>
  /// </summary>
  private void DrawIdlePlan(DeckState deck)
  {
    var rows = StageRail.Build(deck.CountOf, deck.HasWork,
      // No round: nothing done, nothing current, nothing halted, nothing in flight.
      _ => false, current: null, halted: null, RoundConductor.RateFor, liveRunEtaMs: null, deck.PinchAsks);

    foreach (var row in IdlePlan.Plan(rows, deck.Skipped, deck.Seals?.PlanSuffix ?? ""))
    {
      // The box leads the row - the glyph's seat. Its label is empty (the sentence
      // beside it is the label); the ### id keeps the strip-era per-stage identity.
      var run = !_conductor.Skipped.Contains(row.Stage);
      if (ImGui.Checkbox($"###skip{row.Stage}", ref run))
      {
        _conductor.SetStageSkipped(row.Stage, skipped: !run);
      }
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip("Unchecked = deferred THIS ROUND: it flows past the step and nothing about the rows changes - no re-routing, no re-staging, they simply wait. The boxes re-check themselves when the Round ends.");

      var color = row.State == RailState.Empty || row.Deferred
        ? new Vector4(ScroogeColors.Muted.X, ScroogeColors.Muted.Y, ScroogeColors.Muted.Z,
            ScroogeColors.Muted.W * 0.45f)
        : ScroogeColors.Muted;

      ImGui.SameLine();
      ImGui.PushStyleColor(ImGuiCol.Text, color);
      ImGui.TextWrapped(row.Line);
      ImGui.PopStyleColor();
      if (ImGui.IsItemHovered())
        // The bell's breakdown rode the old arrow-line's hover and stays reachable on
        // the step it is about - "the bell is one door" is a fact about the bell.
        ImGui.SetTooltip(row.Stage == RoundStage.BellRun
          ? $"{RoundSkips.Hint(row.Stage)}\n\nOne door - {deck.Bell.Breakdown}: everything that needs a retainer, in one visit chain. The melt runs first so its yields ride the same bell."
          : RoundSkips.Hint(row.Stage));
    }
  }
}
