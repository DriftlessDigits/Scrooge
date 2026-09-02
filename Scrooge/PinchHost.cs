using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using Scrooge.Windows;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// WHAT THE DECK AND THE WINDOWS TALK TO. The retainer bell is one place with four
/// runs in it - the pinch, the bell (hawk), recon, and the vendor rider riding the
/// pinch - and they share four things: one task manager (which is what makes
/// "something else is already working" a single true fact rather than four flags),
/// one pricing pipeline, one jitter, and one Talk-dialog listener pair.
///
/// <para>This owns those four and hands them to the executors. It decides nothing
/// itself: every member below is a door onto an executor, and the one piece of real
/// behaviour here - <see cref="AbortEverything"/> - exists because the draw path's
/// last-resort catch must take down all three runs and cannot be trusted to remember
/// which ones exist.</para>
///
/// <para>It used to be a Dalamud <c>Window</c> called AutoPinch with the ImGui
/// overlay, the pinch chain, the rider and the TTS probe all inside it. The window is
/// <see cref="AutoPinchOverlayWindow"/> now and holds a reference to this; the plugin
/// owns this and registers that.</para>
/// </summary>
internal sealed class PinchHost : IDisposable
{
  private readonly TaskManager _taskManager;
  private readonly Random _random = new();

  private readonly ItemPricingPipeline _pricing;
  private readonly VendorRiderExecutor _rider;
  private readonly PinchRunExecutor _pinch;
  private readonly HawkRunOrchestrator _hawkOrchestrator;

  /// <summary>
  /// The recon executor (Rounds unit 2). Seated beside the hawk rather than inside
  /// it because it needs the same four collaborators - the shared task manager, the
  /// pricing pipeline, the jitter, and the Talk-dialog listeners - and every one of
  /// those is this host's private. Sharing the ONE task manager is what makes
  /// "another run is still working" true for recon without a second busy gate.
  /// </summary>
  private readonly ReconRunOrchestrator _reconOrchestrator;

  /// <summary>The pricing pipeline, for the standing orchestrator's chained reprices.</summary>
  internal ItemPricingPipeline Pricing => _pricing;

  /// <summary>The pinch executor, for the overlay's two buttons.</summary>
  internal PinchRunExecutor Pinch => _pinch;

  internal PinchHost()
  {
    _taskManager = new TaskManager
    {
      TimeLimitMS = 10000,   // per-task timeout (individual steps, not the whole run)
      AbortOnTimeout = true
    };
    _pricing = new ItemPricingPipeline(_taskManager, ApplyJitter);
    _rider = new VendorRiderExecutor(_taskManager);
    _pinch = new PinchRunExecutor(_taskManager, _pricing, _rider, ApplyJitter);
    // No jitter func: the bell's only jittered gap was the flat keep-open wait, and
    // the await ladder that replaced it applies the jitter itself (unit 3).
    _hawkOrchestrator = new HawkRunOrchestrator(
      _taskManager, _pricing, _pinch.SkipRetainerDialog, _pinch.RemoveTalkAddonListeners);
    _reconOrchestrator = new ReconRunOrchestrator(
      _taskManager, _pricing, ApplyJitter, _pinch.SkipRetainerDialog, _pinch.RemoveTalkAddonListeners);

  }

  public void Dispose()
  {
    _pinch.Dispose();
    // A live BELL must not outlive the plugin either (stability sweep, 2026-08-16).
    // This Dispose tore down recon and the pricing pipeline and walked straight past
    // the hawk - so a plugin unload mid-bell left the run's SelectYesno listener
    // registered against a dead delegate and the lifecycle still IsRunning. Both
    // orchestrators are IDisposable now so the omission cannot recur by forgetting.
    _hawkOrchestrator.Dispose();
    // A live recon must not outlive the plugin: its Abort is the fail-closed
    // teardown (queue dropped, Talk listeners removed, CurrentRun cleared).
    _reconOrchestrator.Dispose();
    _pricing.Dispose();
  }

  /// <summary>
  /// TAKES DOWN EVERY RUN AT THIS BELL, for the draw path's last-resort catch
  /// (review ruling S20, 2026-08-12: "we CANNOT crash the game. Period.").
  ///
  /// <para>All three doors, in one place, because the catch that calls this got them
  /// wrong twice by having to remember them: it tore down the hawk and left recon
  /// standing (which kept the round frozen on a stage nothing was working until the
  /// 60s stall watchdog noticed, and then named the market board rather than the
  /// error), and it reported neither death for the plain pinch, which has no
  /// orchestrator to speak for it.</para>
  ///
  /// <para>The live run is captured BEFORE the aborts: each of them nulls
  /// <c>Plugin.CurrentRun</c> as part of its own teardown whether or not the run was
  /// theirs, so that reference is the only way to still know whose run just died. The
  /// mode guard on the pinch report keeps this from double-reporting a run one of the
  /// aborts just spoke for - a standing run is a pinch by pricing mode, not by errand,
  /// and its teardown belongs to its own orchestrator.</para>
  /// </summary>
  internal void AbortEverything(string reason)
  {
    _taskManager.Abort();
    Plugin.Ledger.CancelRun();
    var live = Plugin.CurrentRun;
    _hawkOrchestrator.Abort();
    _reconOrchestrator.Abort(reason);
    if (live is { Mode: RunMode.Pinch, IsStandingRun: false })
      RunFlow.ReportDied(RunKind.Pinch, reason, live);
    _pinch.RemoveTalkAddonListeners();
    _rider.Cleanup();
  }

  /// <summary>Whether the pinch task chain is mid-flight (round deck's busy gate).</summary>
  internal bool PinchBusy => _pinch.Busy;

  /// <summary>Whether a Hawk run is mid-flight (round deck's busy gate).</summary>
  internal bool HawkRunning => _hawkOrchestrator.IsRunning;

  /// <summary>Whether a recon run is mid-flight (round deck's busy gate).</summary>
  internal bool ReconRunning => _reconOrchestrator.IsRunning;

  /// <summary>The live recon run, for the standard progress readout.</summary>
  internal RunLifecycle ReconRun => _reconOrchestrator.Run;

  /// <inheritdoc cref="PinchRunExecutor.CancelPinchRun"/>
  internal void CancelPinchRun(string reason) => _pinch.CancelPinchRun(reason);

  /// <summary>
  /// Cancels a live recon run INCLUDING its queue on the shared task manager (SF3-N).
  /// The orchestrator's own Abort tears down run state and reports the death but
  /// leaves the queue to whoever owns the manager - which is this host, so the
  /// round's Abandon needs this door rather than <see cref="AbortReconRun"/> (whose
  /// caller, the pricing pipeline's exception path, is already inside a task).
  /// </summary>
  internal void CancelReconRun(string reason)
  {
    _taskManager.Abort();
    _reconOrchestrator.Abort(reason);
  }

  /// <summary>Cancels a live Hawk run including its queue on the shared task manager (SF3-N, same shape as the recon door).</summary>
  internal void CancelHawkRun()
  {
    _taskManager.Abort();
    _hawkOrchestrator.Abort();
  }

  /// <summary>
  /// Round-deck entry to "pinch all retainers". Self-guarding: a no-op unless
  /// the RetainerList addon is open and ready and no task chain is busy - the
  /// same preconditions the overlay button enforces by only existing there.
  /// </summary>
  internal void StartPinchAllRetainers() => _pinch.PinchAllRetainers();

  internal void NavigateAndStartHawkRun(List<ListableItem> items)
    => _hawkOrchestrator.NavigateAndStartHawkRun(items);

  /// <summary>
  /// Round-deck entry to the recon stage. The work set is composed HERE, at fire
  /// time, from a fresh bag scan and a fresh cache read - never handed in from a
  /// frame the deck drew earlier (the bell's own lesson, and recon's filter is
  /// time-dependent besides).
  ///
  /// <para><paramref name="bypassFreshness"/> is the Re-Look: read the whole listable
  /// set rather than the stale half. It rides through here rather than being a second
  /// entry point because a Re-Look IS the recon stage - same run, same completion,
  /// same halt machinery - differing only in what it decides to walk.</para>
  /// </summary>
  internal void StartReconRun(bool bypassFreshness = false)
    => _reconOrchestrator.NavigateAndStartReconRun(
      ReconRunOrchestrator.ComposeWorkSet(bypassFreshness));

  /// <summary>Recon's stall watchdog, asked once per framework tick.</summary>
  internal void ReconTick() => _reconOrchestrator.Tick();

  /// <summary>
  /// Kills a live recon pass in the executor's own words (review ruling S9). The
  /// pricing pipeline's exception door calls this: the throw happens deep inside a
  /// task the orchestrator cannot see, and a pass whose panel had to be force-cancelled
  /// is not a pass that can go on to the next item.
  /// </summary>
  internal void AbortReconRun(string reason) => _reconOrchestrator.Abort(reason);

  /// <summary>
  /// REACHES FOR THE BELL - one humanized click on the Summoning Bell the player is
  /// standing at, so the round's bell stages no longer stop one act short of the work
  /// they were pressed for (see <see cref="BellReach"/> for whose decision this is;
  /// this method only carries it out). Rides the shared task manager so the beat
  /// before the click is the same jittered pause every other click in the plugin
  /// gets - a reach that snapped instantly would be the one act in the whole plugin
  /// that did not look like a person.
  ///
  /// <para>Returns false when the wheel is taken or no bell is in range; the caller
  /// treats that as "no reach happened" and does not burn its cooldown on it.</para>
  /// </summary>
  internal bool ReachForBell()
  {
    if (_taskManager.IsBusy) return false;
    if (GameSafe.NearestReachableSummoningBell() is null) return false;

    _taskManager.DelayNext(ApplyJitter(BellReach.ReachDelayMs));
    _taskManager.Enqueue(() =>
    {
      // Re-read at click time rather than closing over the object: a task manager
      // beat is several frames, and an object handle from before a zone change is a
      // pointer into a world that no longer exists.
      if (GameSafe.NearestReachableSummoningBell() is { } bell)
        GameSafe.InteractWith(bell.Object);
    }, "ReachForBell");
    return true;
  }

  /// <summary>
  /// Clears the cached price lookup table. Called when price floor settings
  /// change so that affected items are re-queried from the market board.
  /// </summary>
  internal void ClearCachedPrices() => _pricing.ClearCachedPrices();

  /// <summary>
  /// The per-action humanizer every click at this bell shares. Off by default
  /// (<c>Configuration.EnableJitter</c>); the 1000ms floor keeps a hostile jitter
  /// setting from producing an inhumanly fast click.
  /// </summary>
  private int ApplyJitter(int baseMS)
  {
    // Check if jitter is enabled
    if (!Plugin.Configuration.EnableJitter)
      return baseMS;

    var jitterMS = Plugin.Configuration.JitterMS;

    // Guard against weird
    if (jitterMS <= 0)
      return baseMS;

    // Calculate offset
    var offset = (int)(((_random.NextDouble() * 2.0) - 1.0) * jitterMS);

    return Math.Max(1000, baseMS + offset);
  }
}
