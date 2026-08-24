using System;
using System.Collections.Generic;
using ECommons.DalamudServices;

namespace Scrooge;

/// <summary>
/// THE COMPLETION EVENT HUB (WALK unit 6) - the wiring half of <see cref="FlowPlan"/>.
/// Every executor calls <see cref="Report"/> exactly once when its run ends, complete
/// or aborted; every consequence of "a run ended" subscribes to <see cref="Completed"/>.
/// One mechanism, six executors, four fixes hanging off it.
///
/// <para><b>Why a pump and not a direct invoke.</b> Completions are reported from
/// wherever the run happened to die: inside a TaskManager task, from a framework
/// watchdog tick, and - for the Cancel buttons - from inside an ImGui Draw. A
/// subscriber's job is to re-read the world (bag scans, storage reads, round
/// persistence), and doing that inline from a Draw would mutate the very collections
/// the frame is iterating. So a report only ENQUEUES; <see cref="Pump"/> drains the
/// queue once per framework tick, outside any ImGui frame and on the thread the
/// game's own state is safe to read from. The cost is at most one frame of latency
/// on a refresh nobody was watching yet.</para>
///
/// <para><b>THE COMPLETION CARRIES ITS FACTS</b> (review ruling S1/S2, 2026-08-10).
/// Because the pump is a tick late, a subscriber has no live run to interrogate: the
/// executor that reported has already torn its run down. So the report takes the run
/// itself and snapshots what it did (see <see cref="RunFacts"/>). Nothing downstream
/// of here may read <c>Plugin.CurrentRun</c> - by the time it runs, that is somebody
/// else's run or nobody's.</para>
///
/// <para><b>The first law.</b> Nothing here fires a run. Subscribers narrate,
/// re-read, persist, and advance a cursor's OFFER; the round's next stage still
/// waits on the human's press. A completion is an observation, not a trigger.</para>
/// </summary>
internal static class RunFlow
{
  private static readonly List<RunCompletion> _queue = [];
  private static bool _draining;

  /// <summary>Raised once per completed run, on the framework tick after it was reported.</summary>
  internal static event Action<RunCompletion>? Completed;

  /// <summary>
  /// One run ended. Safe from anywhere: a task, a watchdog, a Draw, a dispose path.
  /// Callers never guard on "did anyone subscribe" - an unheard completion is a
  /// no-op, and that is the point of a hub.
  /// </summary>
  internal static void Report(RunCompletion completion)
  {
    lock (_queue) _queue.Add(completion);
  }

  /// <summary>
  /// Convenience for the common shapes - and the seam where the completion picks up
  /// its facts (review ruling S1/S2).
  ///
  /// <para><b><paramref name="run"/> is required, and that is the fix.</b> It is the
  /// executor's OWN run, handed over while it is still alive; the reporter answers
  /// "whose run was this" at the call, instead of a handler guessing a tick later off
  /// a <c>Plugin.CurrentRun</c> the teardown has already nulled. Explicit
  /// <c>run: null</c> is the honest answer for a refusal on the road - a stage that
  /// never owned a queue processed nothing, and reading the facts off whatever run
  /// happened to be live at that moment (a pinch, say, refusing recon its turn) would
  /// credit one stage's work to another's tally.</para>
  /// </summary>
  internal static void ReportDone(RunKind kind, RunData? run)
    => Report(RunCompletion.Done(kind, FactsOf(run)));

  /// <summary>Convenience for the common shapes. See <see cref="ReportDone"/> on <paramref name="run"/>.</summary>
  internal static void ReportDied(RunKind kind, string reason, RunData? run)
    => Report(RunCompletion.Died(kind, reason, FactsOf(run)));

  /// <summary>
  /// The snapshot itself: the two facts the round's subscribers need after the run is
  /// gone. Taken by value, so a teardown on the very next line cannot move them.
  /// </summary>
  private static RunFacts FactsOf(RunData? run)
    => run is null ? RunFacts.None : new RunFacts(run.DesynthRunId, run.ItemsProcessed);

  /// <summary>
  /// Drains the queue and raises <see cref="Completed"/> for each entry. Called once
  /// per framework tick from the plugin. A subscriber that throws is logged and
  /// skipped - one bad handler must never swallow the completion for the others (the
  /// refresh and the halt are independent consequences of the same fact).
  /// Re-entrancy is refused rather than recursed: a handler that somehow reports
  /// another completion has it picked up on the following tick.
  /// </summary>
  internal static void Pump()
  {
    if (_draining) return;

    RunCompletion[] batch;
    lock (_queue)
    {
      if (_queue.Count == 0) return;
      batch = _queue.ToArray();
      _queue.Clear();
    }

    _draining = true;
    try
    {
      foreach (var completion in batch)
      {
        foreach (var handler in Completed?.GetInvocationList() ?? [])
        {
          try { ((Action<RunCompletion>)handler)(completion); }
          catch (Exception ex)
          {
            Svc.Log.Error(ex, $"[Flow] A run-completion subscriber threw on {completion.Kind}/{completion.Outcome}");
          }
        }
      }
    }
    finally { _draining = false; }
  }

  /// <summary>Drops anything still queued. Plugin dispose - a torn-down world has no consequences to run.</summary>
  internal static void Clear()
  {
    lock (_queue) _queue.Clear();
  }
}
