using System;

namespace Scrooge;

/// <summary>
/// THE FAIL-CLOSED DEATH, written once - the sequence the bell run and the recon run
/// both spelled out by hand, character for character apart from their own extra
/// cleanup and their own name for the run.
///
/// <para>The order in it is the whole point, and it is load-bearing twice over:
/// <c>wasLive</c> is read BEFORE the lifecycle is cancelled (afterwards there is no
/// way left to tell an already-dead run from one this call just killed, and a second
/// death report overwrites the round's halt banner with the wrong gap); and
/// <c>Plugin.CurrentRun</c> is handed over BEFORE it is cleared, because a run that
/// died halfway still did everything up to the press and the rail's tally is about
/// work done, not about how the run ended (RunFacts).</para>
/// </summary>
internal static class RunTeardown
{
  /// <summary>
  /// Cancels <paramref name="run"/>, cancels the ledger's row, hands the dying run
  /// over to the flow, and reports the death - but only if the run was still live,
  /// so a second caller down a racing path stays silent.
  /// </summary>
  /// <param name="run">The lifecycle to drive to Cancelled.</param>
  /// <param name="kind">Whose death this is, for the round's rail.</param>
  /// <param name="reason">
  /// The executor's own words, verbatim - it becomes the named gap on the halt banner.
  /// </param>
  /// <param name="extraCleanup">
  /// Everything the caller holds that this class knows nothing about: its queue, its
  /// catchall block, its addon listeners. Runs after the handover and before the
  /// report, so the death is announced over a torn-down executor.
  /// </param>
  internal static void Die(RunLifecycle run, RunKind kind, string reason, Action extraCleanup)
  {
    var wasLive = run.IsRunning;
    run.Cancel(DateTime.UtcNow);
    Plugin.Ledger.CancelRun();
    var dying = Plugin.CurrentRun;
    Plugin.CurrentRun = null;
    extraCleanup();
    if (wasLive)
      RunFlow.ReportDied(kind, reason, dying);
  }
}
