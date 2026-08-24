using ECommons.DalamudServices;
using System;

namespace Scrooge;

/// <summary>
/// THE GRACE WAITER - the Dalamud half of <see cref="TransientGrace"/>, built on the
/// same shape as <see cref="OccupancyTransition"/>: a framework-tick loop that
/// re-asks a sensor and hands back one of two continuations.
///
/// <para>Its whole job is to stop a BLINK from killing a stage. A precondition read
/// false for a handful of frames at a stage boundary is not a world the player needs
/// to fix - it is the previous stage still putting its toys away (see
/// <see cref="TransientGrace"/> for both 07-26 incidents). So the refusal path now
/// goes through here: keep asking, and only say it out loud if the gap is still
/// there when the window closes.</para>
///
/// <para><b>The first law is untouched.</b> Nothing here fires a run. It re-runs the
/// caller's own attempt - the same call that was already in flight when the blink hit
/// - and that attempt still walks the whole spine. A hold is only ever entered from
/// inside an errand the human already authorized.</para>
///
/// <para>ONE hold at a time. Two stages cannot both be mid-fire (the busy gates see
/// to that), and stacking waiters would mean two continuations racing one sensor. A
/// second request is refused and the caller takes its own refusal path, exactly as it
/// did before this file existed.</para>
/// </summary>
internal static class SpineGrace
{
  private static TransientGrace? _window;
  private static Func<bool>? _met;
  private static Action? _onReturned;
  private static Action? _onExpired;

  /// <summary>A grace hold is running - the tick is re-asking a sensor.</summary>
  internal static bool IsHolding { get; private set; }

  /// <summary>
  /// Hold a transient-class gap open for <paramref name="graceMs"/> instead of
  /// believing it on the first read. <paramref name="met"/> is re-asked every
  /// framework tick; the first tick that reads TRUE runs
  /// <paramref name="onReturned"/>, and the window closing on an unbroken absence
  /// runs <paramref name="onExpired"/>. Exactly one of them ever runs.
  ///
  /// <para>Returns false when a hold is already in flight - the caller must then take
  /// its own refusal path, because a hold that quietly swallowed the second request
  /// would leave a stage neither started nor reported.</para>
  /// </summary>
  internal static bool Hold(string what, int graceMs, Func<bool> met, Action onReturned, Action onExpired)
  {
    if (IsHolding) return false;

    // Ask once before committing to a tick loop: the common case is that the
    // caller's read and this one straddle the transition and it is already over.
    if (met())
    {
      onReturned();
      return true;
    }

    Svc.Log.Debug($"[Spine] {what} read blocked - holding {graceMs}ms before believing it.");

    _window = new TransientGrace(graceMs);
    _window.Observe(false, Environment.TickCount64);
    _met = met;
    _onReturned = onReturned;
    _onExpired = onExpired;
    IsHolding = true;
    Svc.Framework.Update -= HoldTick; // defensive: never double-subscribe
    Svc.Framework.Update += HoldTick;
    return true;
  }

  private static void HoldTick(Dalamud.Plugin.Services.IFramework framework)
  {
    bool met;
    try { met = _met?.Invoke() ?? false; }
    catch (Exception ex)
    {
      // An unreadable sensor is not evidence that the gap closed - fail toward the
      // honest refusal rather than firing into a world we could not read.
      Svc.Log.Error(ex, "[Spine] The graced sensor threw - closing the window.");
      Finish(expired: true)?.Invoke();
      return;
    }

    switch (_window?.Observe(met, Environment.TickCount64) ?? GraceVerdict.Expired)
    {
      case GraceVerdict.Met:
        Finish(expired: false)?.Invoke();
        break;
      case GraceVerdict.Expired:
        Finish(expired: true)?.Invoke();
        break;
      // Waiting: keep asking.
    }
  }

  /// <summary>Tears the hold down and hands back the one continuation that won.</summary>
  private static Action? Finish(bool expired)
  {
    Svc.Framework.Update -= HoldTick;
    IsHolding = false;
    var chosen = expired ? _onExpired : _onReturned;
    _window = null;
    _met = null;
    _onReturned = null;
    _onExpired = null;
    return chosen;
  }

  /// <summary>Plugin dispose - a torn-down world has no continuation to run.</summary>
  internal static void Reset()
  {
    Svc.Framework.Update -= HoldTick;
    IsHolding = false;
    _window = null;
    _met = null;
    _onReturned = null;
    _onExpired = null;
  }
}
