using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;

namespace Scrooge.Windows;

/// <summary>
/// THE SCORE CELL - one widget, both boards (code-shine 3-3b). The cells ARE the control
/// surface of a row: the number they show is the whole of what a reader compares, and a
/// click on one IS the ruling.
///
/// <para>It lived twice - once in the board's table, once in the On Market strip - and
/// the two copies drifted onto the one thing a cell is FOR. The board's List cell had
/// learned to show the standing ask (Movement 4) and On Market's had not, so the same
/// lane read two different gil figures under two columns wearing the same header. A
/// shared widget does not fix that by itself; feeding it one
/// <see cref="BoardAskCell.Number"/> at both call sites does, and the widget is what
/// makes there be one place to feed.</para>
/// </summary>
internal sealed partial class AccountantWindow
{
  /// <summary>
  /// ONE SCORE CELL, PAINTED. The full-width invisible hit target, the hover, the
  /// right-aligned number and the colour that says what the number IS - the sequence
  /// every score cell in the plugin performs, written once.
  ///
  /// <para><paramref name="hint"/> is deferred rather than composed: a cell's sentence
  /// costs string work and only the hovered cell has a reader. <paramref name="onClick"/>
  /// null is a DEAD cell - the hit target survives so a closed door can still say why
  /// on hover, and the click means nothing.</para>
  /// </summary>
  private static void ScoreCell(string id, long? shown, string text, bool pressed,
    Vector4? tint, Func<string> hint, Action? onClick)
  {
    if (pressed)
      ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(PressedCellBg));

    var width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
    var start = ImGui.GetCursorPos();
    if (ImGui.InvisibleButton(id, new Vector2(width, ImGui.GetTextLineHeight()))
        && onClick is { } click)
      click();
    if (ImGui.IsItemHovered() && hint() is { Length: > 0 } tip)
      ImGui.SetTooltip(tip);

    ImGui.SetCursorPos(start);
    ImGui.SetCursorPosX(start.X + Math.Max(0f, width - ImGui.CalcTextSize(text).X));
    if (shown is null) ImGui.TextDisabled(text);
    else if (pressed) ImGui.TextColored(ScroogeColors.Earned, text);
    else if (tint is Vector4 colour) ImGui.TextColored(colour, text);
    else ImGui.Text(text);
  }

  /// <summary>The pressed cell's wash - "this is the call", said in the table's own language.</summary>
  private static readonly Vector4 PressedCellBg = new(0.20f, 0.36f, 0.20f, 0.55f);

  /// <summary>
  /// WHAT A CELL'S CLICK WOULD DO, in the row's own grammar - the only thing the two
  /// boards' hovers are allowed to differ by. The number's own sentence is the head and
  /// is composed once for both; this picks the tail.
  /// </summary>
  private enum CellVoice
  {
    /// <summary>No such door on this item. The cross's own sentence, and no head at all.</summary>
    Closed,
    /// <summary>This IS the call - a bag row's pressed cell.</summary>
    Call,
    /// <summary>This is the staged CONTEST against a standing ask - clicking again withdraws it.</summary>
    Contest,
    /// <summary>Nobody has called this one; the click is the ruling.</summary>
    Unruled,
    /// <summary>The router's own cell on a row the player moved - the click hands it back.</summary>
    RoutersOwn,
    /// <summary>An ordinary overrule toward this exit.</summary>
    Overrule,
    /// <summary>A standing lane's non-List exit: the round pulls it, the exit fires at its own stop.</summary>
    PullFor,
  }

  /// <summary>
  /// ONE CELL'S HOVER, for both boards. The head says what the number IS - the standing
  /// ask when the lane has one (<see cref="BoardAskCell.Hint"/>, which carries the relist
  /// preview behind it), otherwise the scored worth and what was on the scale - and the
  /// tail says what a click would do about it.
  ///
  /// <para>ONE COMPOSER because the head is where the two surfaces drifted (3-3b). On
  /// Market wrote its own ("The round would write ~X" / "No List number") and only wore
  /// the scored-worth line when a melt grade happened to exist, so the same lane could be
  /// described two ways depending on which tab it was read from. The tails are genuinely
  /// different sentences - a bag row is a CALL, a standing lane is a CONTEST - and stay
  /// so; <see cref="CellVoice"/> is the whole of that difference.</para>
  ///
  /// <para><b>"As weighed" now says WHAT WAS ON THE SCALE</b> (08-22).
  /// <paramref name="weighed"/> is the operand line the detail pane already draws for
  /// this exact exit (<see cref="BoardDetail.Scores"/>) - which witness the List score
  /// used, how many seals at what rate the GC score multiplies out to, whether the melt
  /// figure was measured or borrowed. The numbers existed; the hover was the one surface
  /// that had the reader's attention and did not show them. It SUBSUMES the melt-grade
  /// line this method used to append on its own: the pane's Desynth operand is
  /// <c>GradeHint</c> plus the skill-up knob, so appending both would say the grade
  /// twice.</para>
  /// </summary>
  private static string CellHint(RoutingExit exit, long? value, MeltGrade grade,
    long? ask, CellVoice voice, string weighed = "")
  {
    // Dead, not mute: a closed door answers the one question it is being asked and
    // nothing else. There is no worth to report about an exit that does not exist.
    if (voice == CellVoice.Closed) return ExitDoors.ClosedHint(exit);

    // MOVEMENT 4: a standing row's List cell is showing the ask, so its hover
    // leads with the ask and carries the relist preview behind it - the number
    // the cell used to show, one layer down, said in the voice.
    var worth = ask is long standing && standing > 0
      ? BoardAskCell.Hint(standing, value)
      : value is long gil
        ? $"{BoardCalls.ColumnLabel(exit)}: ~{gil:N0} gil as weighed."
        : $"No {BoardCalls.ColumnLabel(exit)} evidence for this one.";
    // "As weighed" says nothing about what was on the scale, so the scale rides
    // here: the pane's own operand line for this exit where the caller supplied one,
    // and the melt grade alone where it did not (On Market's cells pass none).
    if (ask is null && value is not null)
    {
      var scale = weighed.Length > 0 ? weighed : BoardCalls.GradeHint(exit, grade);
      if (scale.Length > 0) worth += $"\n{scale}";
    }

    return voice switch
    {
      CellVoice.Call => $"{worth}\nThis is the call. Click another cell to overrule it.",
      CellVoice.Contest =>
        $"{worth}\nStaged: {BoardCalls.ShortName(exit)}. Click again to withdraw the contest - the ask goes back to standing.",
      // An unruled List cell with no reprice number invites a ruling toward a number
      // the line above just said doesn't exist (ruled 08-16 walk). Its tail says the
      // two options this cell actually holds - do nothing, or send the bell back -
      // and only those two: Dismiss is the pane's job (ruled same walk). Same
      // no-proposal test Hint's own middle line uses, so tail and line agree.
      CellVoice.Unruled => ask is long kept && BoardAskCell.ShowsAsk(kept)
          && (value is not long proposal || proposal <= 0)
        ? $"{worth}\nLeft alone, the book keeps {RunLogVoice.Gil(kept)}. Click to stage a reprice - the bell re-reads the lane at the retainer, and holds again if nothing's changed."
        : $"{worth}\nClick to rule it here - that IS the ruling.",
      CellVoice.RoutersOwn => $"{worth}\nThe router's own call. Click to hand it back.",
      CellVoice.PullFor =>
        $"{worth}\nPull for {BoardCalls.ShortName(exit)} - the round pulls it; the exit fires at its own stop.",
      _ => $"{worth}\nClick to overrule to {BoardCalls.ShortName(exit)}.",
    };
  }

  /// <summary>
  /// ONE score cell - and the whole control surface of a row. The number is the
  /// gil the router weighed for that exit, discounts already applied, right-aligned
  /// with separators; the winner's cell is drawn pressed. Clicking a cell IS the
  /// gesture: on a called row it overrules (or, on the router's own cell, hands the
  /// call back); on an unruled row it rules.
  /// </summary>
  private void DrawScoreCell(BoardRow row, int index)
  {
    var exit = BoardCalls.Exits[index];
    var value = row.Scores[index];
    var pressed = row.Call is { } c && c.Pressed == exit;
    var routersOwn = row.Call is { Source: CallSource.Player } p && !p.RouterWasReview && p.RouterExit == exit;
    // Bag rows have always been live; listed rows joined the contract in unit 4
    // - their cells stage the verb the exit maps to (List = reprice-to-honest,
    // the rest = pull-for-that-exit). A doorless cell is dead whatever the row:
    // an exit the game doesn't offer is not a thing a click may rule toward.
    var doorOpen = row.Doors.Has(exit);
    var clickable = doorOpen
      && (row.Routed is not null || (row.Standing is not null && row.Call is not null));

    // Two glyphs, two facts (Drift, 08-02: a hover-only hint is not enough
    // notice): a DASH is "no evidence yet - the door is open, evidence could
    // arrive"; a CROSS is "no such door on this item, ever". The cross reads
    // at a glance; the tooltip still carries the sentence.
    // The prior's tell (08-03): an estimated melt wears a "~" so it can never
    // sit in the Melt column looking exactly like a number we measured.
    // MOVEMENT 4: on a standing row the List cell shows THE ASK - the state of
    // the state, in the column named after it - and the relist preview moves to
    // the hover. One call decides the number, and the column sorts on the same
    // one (see SortBoard), so the cell and the sort arrow cannot disagree.
    // A CLOSED door keeps its cross: a lane the game offers no List door for is
    // a fact about the exit, and painting an ask over it would answer a question
    // the cross is not being asked.
    var ask = exit == RoutingExit.List && doorOpen ? row.Ask : null;
    var shown = BoardAskCell.Number(ask, value);
    var text = shown is long gil
      ? BoardCalls.GradeMark(exit, row.MeltGrade) + Format.Gil(gil)
      : doorOpen ? "—" : "×";

    var voice = !doorOpen ? CellVoice.Closed
      : pressed ? CellVoice.Call
      : row.Call is { Source: CallSource.Unruled } ? CellVoice.Unruled
      : routersOwn ? CellVoice.RoutersOwn
      : CellVoice.Overrule;

    ScoreCell($"##cell{row.Key}:{index}", shown, text, pressed,
      // The ask is STATE, not a call, and it wears the colour every other reading of
      // the world wears on this board. It outranks the router's amber here on
      // purpose: on a standing row the List cell has stopped being a proposal.
      BoardAskCell.ShowsAsk(ask) ? ScroogeColors.Info
        : routersOwn ? ScroogeColors.Amber
        : null,
      // The operand line is composed INSIDE the deferred hint - only a hovered cell
      // pays for it, which is the whole reason the hint is a lambda.
      () => CellHint(exit, value, row.MeltGrade, ask, voice, WeighedLine(row, index)),
      clickable
        ? () => { _selectedKey = row.Key; RuleRow(row, exit); }
        : null);
  }

  /// <summary>
  /// What was on the scale for one cell, in the DETAIL PANE'S OWN WORDS. Same
  /// composer, same operands, same sentence the reader sees when he opens the pane -
  /// so the hover and the pane cannot come to explain one number two ways, which is
  /// the drift this whole file exists to prevent.
  ///
  /// <para>Empty on any failure to gather: a hover that cannot name the scale says
  /// nothing about it rather than guessing at it.</para>
  /// </summary>
  private string WeighedLine(BoardRow row, int index)
  {
    var lines = BoardDetail.Scores(ScoreOperandsFor(row));
    return index >= 0 && index < lines.Count ? lines[index].Source : "";
  }

  /// <summary>
  /// Applies a cell click. The SAME ruling the move buttons always wrote - the
  /// teaching signal first, then the move - so nothing about routing semantics
  /// changes with the gesture that triggers it.
  /// </summary>
  private void RuleRow(BoardRow row, RoutingExit exit)
  {
    if (row.Call is not { } call) return;
    if (!row.Doors.Has(exit)) return; // the seam's own guard, not just the cell's
    if (!BoardCalls.IsActionable(call, exit)) return;

    if (row.Routed is { } item)
    {
      RecordRoutedSignal(item, exit); // confirmation or disagreement - both teach
      item.Pile = exit;
      item.InReview = false;
      item.PlayerResolved = true;
      return;
    }

    // A LISTED row's cell (unit 4): the click stages the triage verb the exit
    // maps to - the same staging the pane's toggles write, through the same
    // dictionary, with the same teaching signal. The round spends it.
    if (row.Standing is { } standing && BoardCalls.ActionOfExit(exit) is var action
        && action != StandingAction.None)
    {
      _actions[standing.Item] = new StagedVerb(action, CallClassOf(standing.Item));
      Answered(standing.Item);
      RecordStandingSignal(standing.Item, BoardPiles.ForStanding(standing.Item.Result), action);
    }
  }
}
