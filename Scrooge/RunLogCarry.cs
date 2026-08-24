using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// THE TRANSCRIPT'S CEILING, in one place because it now has two copies to bound.
///
/// <para>The in-memory carry and the banked <c>round_log</c> hold the same lines and
/// must drop the same ones - a live window showing 4,000 lines over a table holding
/// 8,000 would make "the transcript survived the reload" false in the only way that
/// matters, quietly. One const, two readers.</para>
/// </summary>
internal static class RunLogCap
{
  internal const int MaxEntries = 4000;
}

/// <summary>
/// THE ROUND'S ONE TRANSCRIPT (Drift, 2026-07-26: <i>"each stage seems to reset the run
/// log"</i>).
///
/// <para>Thirty-seven melt lines vanished the moment the bell stage started, and the
/// window retitled itself from "Desynth Run Log" to "Triage Run Log" while it did.
/// Nothing was clearing anything: each run builds a fresh <c>RunData</c> with its own
/// entry list, and the log window simply follows whichever run is current. That is
/// exactly right for a standalone run and exactly wrong for a round, because a round
/// is ONE errand - the stages are its chapters, not four separate books.</para>
///
/// <para>So the carry sits beside the current run rather than inside it: when the log
/// is HOLDING, each finished run's entries are adopted into a transcript the window
/// renders in front of the live run's own. The per-run objects stay untouched, which
/// matters more than it looks - every run summary counts over its OWN
/// <c>LogEntries</c> (skipped, no-data, lane outcomes), and seeding the next run's
/// list with the last one's would have double-counted every one of them.</para>
///
/// <para>Generic in the entry type only so it can stay pure: the log item types live
/// in the ImGui window file and cannot link into the test project.</para>
/// </summary>
internal sealed class RunLogCarry<T>
{
  /// <summary>
  /// The transcript's hard ceiling, oldest lines dropped first.
  ///
  /// <para>A round's real transcript is hundreds of lines and this is far above that.
  /// It exists for the case the round model actually permits: a round is held across
  /// halts and resumes with no bound on how many, and a persisted round survives
  /// reloads for up to the staleness ceiling (hours). Without a cap, "leave the round
  /// active and keep resuming" is an unbounded list. With it, the window's worst case
  /// is a fixed and unremarkable amount of memory, and the only thing lost is the top
  /// of a transcript nobody scrolls to.</para>
  /// </summary>
  internal const int MaxEntries = RunLogCap.MaxEntries;

  private readonly List<T> _carried = new();

  /// <summary>The log is accumulating across runs - a round is underway.</summary>
  internal bool Holding { get; private set; }

  /// <summary>Everything carried from finished runs, oldest first.</summary>
  internal IReadOnlyList<T> Entries => _carried;

  /// <summary>
  /// THE WRITE-THROUGH (Rounds unit 4). Every batch this carry adopts is handed to
  /// the sink as well, so the transcript is banked at the same instant it is
  /// carried - never on a timer, never at round end.
  ///
  /// <para>The adoption points ARE the checkpoints, which is why this hangs off
  /// <see cref="Adopt"/> rather than off a save call of its own: a stage's entries
  /// are adopted exactly when that stage finishes, so "state and run log bank at the
  /// checkpoint" is a fact about where the call sits rather than a rule someone has
  /// to remember at every stage boundary. The Look half completing is the big one,
  /// and it needs no special case here at all.</para>
  ///
  /// <para>Null when nothing is banking (a standalone run, a session with storage
  /// down), which costs the transcript nothing it had before this unit.</para>
  /// </summary>
  internal Action<IReadOnlyList<T>>? WriteThrough { get; set; }

  /// <summary>A round started (or was restored): begin one transcript.</summary>
  internal void Hold()
  {
    _carried.Clear();
    Holding = true;
  }

  /// <summary>
  /// A restored Round's transcript, read back from the bank. Replaces whatever is
  /// carried (a rehydrate happens on a fresh session, so that is nothing) and puts
  /// the carry into Holding without a Hold - which would have cleared the very lines
  /// being restored.
  ///
  /// <para>NOT written through. These lines came FROM the bank; handing them back to
  /// it would append the whole transcript to itself on every reload, and a Round
  /// held across three restarts would read its own first stage four times.</para>
  ///
  /// <para>The cap applies here too, and from the same end: a banked transcript
  /// should never be over-full, but reading one back is the cheapest possible place
  /// to be sure of it.</para>
  /// </summary>
  internal void Rehydrate(IReadOnlyList<T> banked)
  {
    _carried.Clear();
    _carried.AddRange(banked);
    Trim();
    Holding = true;
  }

  /// <summary>
  /// The round ended - cancelled, or the player pressed done. Stops carrying; does
  /// NOT wipe what was carried (review ruling S11, 2026-08-12).
  ///
  /// <para>The wipe was the bug. A round's whole point is that its stages are one
  /// book, and the moment the round ended this method emptied that book - so the
  /// player pressed done and read nothing about the errand he had just run. The
  /// banked copy survived and said so in a comment, but the only surface that reads
  /// it is the restore, and a round that has ended is not a round that restores.</para>
  ///
  /// <para>What supersedes the standing transcript is the next thing written: a new
  /// round's <see cref="Hold"/> clears it, and outside a round the next run's own
  /// start does (the log window's rule, unchanged since the pinch log). Holding goes
  /// false here so nothing further is ADOPTED into a book whose round is over.</para>
  /// </summary>
  internal void Release() => Holding = false;

  /// <summary>The Clear button - always wipes, round or no round.</summary>
  internal void Clear() => _carried.Clear();

  /// <summary>
  /// Fold a finished run's entries into the transcript. A no-op when not holding, so
  /// the standalone path costs nothing and cannot accumulate by accident.
  /// </summary>
  internal void Adopt(IReadOnlyList<T> entries)
  {
    if (!Holding || entries.Count == 0) return;

    _carried.AddRange(entries);
    Trim();
    // Banked AFTER the in-memory fold, deliberately: the transcript the player is
    // looking at is the product, and a sink that throws must not be able to cost him
    // a chapter of it. The sink's own guard decides what a failed bank costs.
    WriteThrough?.Invoke(entries);
  }

  private void Trim()
  {
    var overflow = _carried.Count - MaxEntries;
    if (overflow > 0)
      _carried.RemoveRange(0, overflow);
  }
}
