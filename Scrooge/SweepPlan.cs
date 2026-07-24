using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>Where a sweep stage physically happens - no button teleports the player.</summary>
internal enum SweepPlace
{
  Bell,
  Anywhere,
  ExpertDelivery,
}

/// <summary>
/// The full sweep in workflow order (Sam's endgame sentence, 2026-07-19).
/// PUBLIC because the persisted sweep state (<see cref="SweepState"/>) carries
/// these values through the config JSON - a public property cannot expose an
/// internal enum. Widening only; nothing about the stage list changed.
/// </summary>
public enum SweepStage
{
  /// <summary>Read the board and reprice standing listings (the pinch).</summary>
  Pinch,
  /// <summary>List and vendor the routed bag gear - the bell run.</summary>
  BellRun,
  /// <summary>Fix standing listings the guards held (cap / undercut / upward).</summary>
  Reprice,
  /// <summary>Melt the skillup/yield pile at the desynthesis window.</summary>
  Desynth,
  /// <summary>Churn the rest to seals at the GC's Expert Delivery.</summary>
  TurnIn,
}

/// <summary>
/// The named gap a HALT holds over a dead stage (spec 2026-07-23,
/// "Failure semantics: HALT-NAME-RESUME"). A mid-flow death does not silently
/// un-mark and roll on; it stops the sweep and names what died so the deck can
/// say it out loud. The message is composed at halt time and persisted verbatim
/// - the two factories are the two spec-sanctioned ways to phrase it.
/// </summary>
public sealed record SweepHalt(SweepStage Stage, string Message)
{
  /// <summary>
  /// The halt named PLAINLY - for a death that is not a spine facet (a server
  /// timeout, a watchdog kill): what died, and what would clear it. Progress is
  /// folded into <paramref name="reason"/> when the caller has it ("aborted at
  /// 6/13").
  /// </summary>
  internal static SweepHalt Plainly(SweepStage stage, string what, string reason, string wouldClear)
    => new(stage, $"{what} halted - {reason}. {wouldClear}");

  /// <summary>
  /// The halt named in the SPINE's vocabulary - for a death whose reason maps to
  /// a declared expectation (occupancy, view, place). Borrows the evaluation's
  /// "expected X, but Y" message shape verbatim so a halt reads exactly like the
  /// pre-fire refusal that would have named the same gap.
  /// </summary>
  internal static SweepHalt FromSpine(SweepStage stage, SpineEvaluation eval)
    => new(stage, eval.Message);
}

/// <summary>
/// The serialization-friendly snapshot of a sweep's HELD PLACE (spec: what
/// survives a reload is exactly the held place - stage cursor, completion marks,
/// halted-or-not + the named gap, and the start timestamp for the staleness
/// guard). Pure data with public get/set + a parameterless ctor so the config
/// JSON serializer round-trips it; <see cref="SweepPlan.Export"/> /
/// <see cref="SweepPlan.Restore"/> are the only things that build or consume it.
/// </summary>
public sealed class SweepState
{
  /// <summary>A sweep was underway when this was written.</summary>
  public bool Active { get; set; }

  /// <summary>Stages marked done at fire time (completion marks).</summary>
  public List<SweepStage> Done { get; set; } = new();

  /// <summary>The halted stage, or null if the sweep was flowing normally.</summary>
  public SweepStage? HaltStage { get; set; }

  /// <summary>The named gap over the halted stage (verbatim halt message).</summary>
  public string? HaltMessage { get; set; }

  /// <summary>When the sweep started, unix seconds. 0 = unknown (retire on restore).</summary>
  public long StartedAtUnix { get; set; }
}

/// <summary>
/// The one-button sweep's cursor: pinch -&gt; bell run -&gt; reprice -&gt;
/// desynth -&gt; turn in, one press per stage, walking between stops. ZERO
/// intelligence by design: every stage fires only on a player press, a stage
/// with no work is skipped silently, and a stage is marked done AT FIRE TIME.
///
/// WALK unit 2 adds the RUN MODEL on top of that dumb cursor:
/// <list type="bullet">
///   <item><b>Halt</b> - a mid-flow stage death does not un-mark and roll on; it
///     HALTS the sweep, holding the cursor and the completion marks in place and
///     naming the gap. The deck must never offer the next stage past a corpse,
///     so <see cref="Next"/> returns null while halted.</item>
///   <item><b>Resume</b> - re-offers the HALTED stage as current (its executor
///     rescans at fire time), never a replay from the top and never a re-fire of
///     a completed stage.</item>
///   <item><b>Persistence</b> - <see cref="Export"/> / <see cref="Restore"/> carry
///     the held place across a reload; <see cref="IsStale"/> retires a sweep too
///     old to trust.</item>
/// </list>
/// The distinction from <see cref="Unmark"/> is deliberate: Unmark is the quiet
/// fire-time revert (the stage goes straight back onto the cursor); Halt is the
/// loud stop (the sweep freezes at the corpse until the player Resumes).
/// Pure and Dalamud-free (linked into the test project): the window feeds it
/// work counts, location, timestamps, and abort reasons; it answers "what's next,
/// where, and is anything blocking."
/// </summary>
internal sealed class SweepPlan
{
  internal static readonly SweepStage[] Order =
  {
    SweepStage.Pinch,
    SweepStage.BellRun,
    SweepStage.Reprice,
    SweepStage.Desynth,
    SweepStage.TurnIn,
  };

  private readonly HashSet<SweepStage> _done = new();
  private SweepHalt? _halt;

  /// <summary>A sweep is underway - the deck shows the cursor.</summary>
  internal bool Active { get; private set; }

  /// <summary>When the current sweep started - the staleness clock's zero. Null when idle.</summary>
  internal DateTimeOffset? StartedAt { get; private set; }

  /// <summary>The sweep is frozen over a dead stage, holding its place.</summary>
  internal bool Halted => _halt != null;

  /// <summary>The named gap the halt holds, or null when flowing normally.</summary>
  internal SweepHalt? CurrentHalt => _halt;

  /// <summary>The halted stage, or null when flowing normally.</summary>
  internal SweepStage? HaltStage => _halt?.Stage;

  internal static SweepPlace PlaceOf(SweepStage stage) => stage switch
  {
    SweepStage.Desynth => SweepPlace.Anywhere,
    SweepStage.TurnIn => SweepPlace.ExpertDelivery,
    _ => SweepPlace.Bell, // pinch, bell run, and reprice all live at a bell
  };

  /// <summary>Start a sweep now (wall clock). The shell's entry point.</summary>
  internal void Start() => Start(DateTimeOffset.UtcNow);

  /// <summary>Start a sweep stamped at an explicit time - the testable seam for the staleness clock.</summary>
  internal void Start(DateTimeOffset startedAt)
  {
    Active = true;
    _done.Clear();
    _halt = null;
    StartedAt = startedAt;
  }

  internal void Cancel()
  {
    Active = false;
    _done.Clear();
    _halt = null;
    StartedAt = null;
  }

  internal void MarkDone(SweepStage stage) => _done.Add(stage);

  /// <summary>
  /// Reverts a fire-time MarkDone after the fired run ABORTS, QUIETLY: the stage
  /// goes straight back onto the cursor with no halt held. The gentle sibling of
  /// <see cref="Halt"/> - use it when the death needs no naming and the sweep can
  /// simply re-offer the stage. (Kept as the pure distinction; the deck now halts
  /// loud on a real mid-flow death.)
  /// </summary>
  internal void Unmark(SweepStage stage) => _done.Remove(stage);

  /// <summary>
  /// HALTS the sweep over a dead stage: the stage's fire-time mark is reverted
  /// (it did not finish) AND the sweep freezes, holding the named gap. Prior
  /// stages keep their completion marks - the held place survives - but
  /// <see cref="Next"/> offers nothing until <see cref="Resume"/>, so the deck
  /// can never flow past the corpse.
  /// </summary>
  internal void Halt(SweepHalt halt)
  {
    _done.Remove(halt.Stage);
    _halt = halt;
  }

  /// <summary>
  /// Clears the halt and re-offers the halted stage as current. The stage was
  /// un-marked by <see cref="Halt"/>, so the very next <see cref="Next"/> returns
  /// it (its executor rescans at fire time) - never a replay from the top, never
  /// a re-fire of a completed stage. If the rescan finds no work, the cursor
  /// skips it forward like any empty stage.
  /// </summary>
  internal void Resume() => _halt = null;

  internal bool IsDone(SweepStage stage) => _done.Contains(stage);

  /// <summary>
  /// The first unfinished stage that has work, in sweep order; null when the
  /// sweep is complete (everything left is done or empty) OR while the sweep is
  /// HALTED (never offer a stage past a corpse - the deck renders the halt banner
  /// and a Resume, not the next fire button). Skipped-empty stages are NOT marked
  /// done - if work appears (a pinch flags reprices), the cursor picks the stage
  /// up on its way through.
  /// </summary>
  internal SweepStage? Next(Func<SweepStage, bool> hasWork)
  {
    if (_halt != null) return null;
    foreach (var stage in Order)
      if (!_done.Contains(stage) && hasWork(stage))
        return stage;
    return null;
  }

  /// <summary>Snapshot the held place for persistence.</summary>
  internal SweepState Export() => new()
  {
    Active = Active,
    Done = _done.ToList(),
    HaltStage = _halt?.Stage,
    HaltMessage = _halt?.Message,
    StartedAtUnix = StartedAt?.ToUnixTimeSeconds() ?? 0,
  };

  /// <summary>
  /// Rehydrate the held place from a persisted snapshot - the deck shows the same
  /// stages done / current / halted as before the reload. The caller is
  /// responsible for the staleness check (<see cref="IsStale"/>) BEFORE restoring;
  /// a stale sweep is retired, not restored.
  /// </summary>
  internal void Restore(SweepState state)
  {
    Active = state.Active;
    _done.Clear();
    foreach (var stage in state.Done) _done.Add(stage);
    _halt = state.HaltStage is SweepStage hs
      ? new SweepHalt(hs, state.HaltMessage ?? "")
      : null;
    StartedAt = state.StartedAtUnix > 0
      ? DateTimeOffset.FromUnixTimeSeconds(state.StartedAtUnix)
      : null;
  }

  /// <summary>
  /// A restored sweep older than the ceiling is history, not a sweep - retire it
  /// loudly instead of trusting a stale world (spec staleness guard, deliberately
  /// dumb: one timestamp vs one ceiling, no cleverer logic). Pure decision; the
  /// shell supplies the wall clock and the config ceiling.
  /// </summary>
  internal static bool IsStale(DateTimeOffset startedAt, DateTimeOffset now, TimeSpan ceiling)
    => now - startedAt > ceiling;
}
