using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using System;

namespace Scrooge;

/// <summary>
/// THE WEDGE WATCHDOG, written once.
///
/// <para>The ECommons LegacyTaskManager clears its queue when <c>TimeLimitMS</c> fires
/// and tells NOBODY. The executor it was running for then sits with its "running" flag
/// still up over an empty queue - holding every busy gate in the plugin shut, and (worse)
/// letting a round walk on past a stage that never finished. The 23:05 pinch died 3 items
/// into 102 and nothing reported it (shake finding SF1, 2026-08-12); the 07-22 melt leak
/// was the same shape one door over.</para>
///
/// <para><b>The tell is identical in every executor</b>, which is why this is one class
/// and not six: a live run's queue is NEVER empty - each step enqueues its successor -
/// so <c>isLive() &amp;&amp; !isBusy()</c> can only mean the queue died underneath it.
/// What differs between the sites is only what "live" MEANS to them (a flag, a
/// <see cref="RunLifecycle"/>, a mode on <c>Plugin.CurrentRun</c>) and what their
/// teardown does - so those two are the constructor arguments and nothing else is. The
/// framework subscription and the defensive never-double-subscribe guard, which were
/// hand-rolled five times, live here.</para>
/// </summary>
internal sealed class QueueWedgeWatchdog
{
  private readonly Func<bool> _isLive;
  private readonly Func<bool> _isBusy;
  private readonly Action _onWedged;

  /// <summary>
  /// The double-subscribe guard every site used to carry as a defensive
  /// <c>Update -= Tick</c> before its <c>+=</c>. Owned here so no site has to
  /// remember it, and so <see cref="Disarm"/> can be called from anywhere -
  /// including from inside <see cref="_onWedged"/> - without unsubscribing twice.
  /// </summary>
  private bool _armed;

  /// <param name="isLive">Is the run this watchdog guards still in progress?</param>
  /// <param name="isBusy">Is the task queue behind that run still working?</param>
  /// <param name="onWedged">
  /// The site's own teardown, verbatim - it names the death in its own words, because
  /// a stalled melt, a stalled coffer rider and a stalled pinch owe the player
  /// different sentences. Called at most once per arming, with the watchdog already
  /// disarmed.
  /// </param>
  internal QueueWedgeWatchdog(Func<bool> isLive, Func<bool> isBusy, Action onWedged)
  {
    _isLive = isLive;
    _isBusy = isBusy;
    _onWedged = onWedged;
  }

  /// <summary>Subscribes for the run just enqueued. Arming an armed watchdog is a no-op.</summary>
  internal void Arm()
  {
    if (_armed) return;
    _armed = true;
    Svc.Framework.Update += Tick;
  }

  /// <summary>
  /// Unsubscribes. Idempotent, and safe from inside the teardown: the tick disarms
  /// BEFORE it fires <c>onWedged</c>, so a teardown that also disarms (the shared
  /// exit paths of the coffer rider and the GC turn-in both do) lands on a no-op.
  /// </summary>
  internal void Disarm()
  {
    if (!_armed) return;
    _armed = false;
    Svc.Framework.Update -= Tick;
  }

  private void Tick(IFramework _)
  {
    // The run ended through one of its own doors - nothing left to watch. Retiring
    // here is what lets a site arm at run start and never think about the happy path.
    if (!_isLive())
    {
      Disarm();
      return;
    }
    if (_isBusy()) return;

    // Disarm first: the teardown below can re-enter this class (every one of them
    // disarms somewhere), and a wedge must fire exactly once.
    Disarm();
    _onWedged();
  }
}
