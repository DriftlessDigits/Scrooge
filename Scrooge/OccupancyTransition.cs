using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace Scrooge;

/// <summary>
/// CLOSE THE KNOWN OCCUPIER (WALK unit 9, from the 07-24 lap's findings #3+#4).
///
/// <para>Two symptoms, one mechanism. The coffer rider refused at its own stage's
/// location - <i>"expected to be un-occupied, but the retainer bell is open"</i> -
/// standing exactly where the round had just sent the player. And the melt made Drift
/// close the retainer roster by hand before it would fire. Both are the spine's
/// ladder answering RUNG 4 (refuse) to a gap that is plainly rung 1: the advisor
/// recognizes the window that is in the way, and closing a window it recognizes is
/// the definition of self-navigation.</para>
///
/// <para><b>What counts as KNOWN.</b> Only a retainer bell session - the game's own
/// <c>OccupiedSummoningBell</c> - and only the addons that session puts on screen
/// (the roster, the sell view, the retainer menu, the sale-history window). These
/// are the same addons the sensors already recognize as "you are at a bell"
/// (SpineSensors.AnyAddonReady) and the same ones the pinch already closes on its
/// way out (GameNavigation.CloseRetainerSellList / CloseRetainer / CloseSaleHistory).
/// ANY other occupancy - an NPC event, a quest cutscene, a duty - is UNKNOWN and
/// still refuses loudly, exactly as it did before. The advisor closes what it put
/// there, never what it merely found.</para>
///
/// <para><b>The first law.</b> This only ever runs INSIDE a stage's fire - after
/// the human's press, or after the flow's auto-fire within a round that human
/// pressed (see FlowPlan.Advance). Nothing here is reachable from a draw, a timer
/// or an idle deck: closing a window is part of doing the thing you asked for, not
/// a thing the advisor does while you are looking at something else.</para>
/// </summary>
internal static class OccupancyTransition
{
  /// <summary>
  /// The retainer session's windows, INNERMOST FIRST. Closing them in this order
  /// walks back out of the session the way the player would: the sale history sits
  /// over the sell view, the sell view over the retainer menu, the menu over the
  /// roster - and it is closing the ROSTER that finally ends the session and clears
  /// the occupancy flag.
  /// </summary>
  private static readonly string[] SessionWindows =
  {
    "RetainerHistory",
    "RetainerSellList",
    "SelectString",
    "RetainerList",
  };

  /// <summary>How long to keep closing before admitting we cannot clear it.</summary>
  private const int ClearTimeoutMs = 4000;

  /// <summary>Minimum gap between close attempts - the game needs a beat per window.</summary>
  private const int CloseIntervalMs = 250;

  private static Action? _proceed;
  private static Action<string>? _onRefused;
  private static long _deadline;
  private static long _nextAttempt;

  /// <summary>A clear is in flight - the tick is closing windows and waiting.</summary>
  internal static bool IsClearing { get; private set; }

  /// <summary>
  /// Is the player occupied by something this file can CLOSE? True only for a
  /// retainer bell session with one of its windows actually on screen - the
  /// "unknown occupier" case (an NPC, a cutscene) reads false and its caller
  /// refuses. <paramref name="what"/> names the window, for the narration.
  /// </summary>
  internal static unsafe bool KnownOccupier(out string what)
  {
    what = "";
    if (!DesynthOrchestrator.PlayerOccupied(out _)) return false;
    if (!Svc.Condition[ConditionFlag.OccupiedSummoningBell]) return false;

    foreach (var name in SessionWindows)
    {
      if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out _)) continue;
      what = name == "RetainerList" ? "the retainer roster" : "the retainer window";
      return true;
    }
    return false;
  }

  /// <summary>
  /// Whether a stage that declares "un-occupied" can be reached from where the
  /// player stands: either he is already free, or the thing holding him is a window
  /// we know how to close. THE DECK'S LOCATION QUESTION READS THIS - a stage whose
  /// only blocker is a closeable window is reachable, because the fire clears it.
  /// </summary>
  internal static bool ReachableUnoccupied()
    => !DesynthOrchestrator.PlayerOccupied(out _) || KnownOccupier(out _);

  /// <summary>
  /// Clears a KNOWN occupier and then runs <paramref name="proceed"/> - or runs it
  /// straight away when nothing is in the way. An UNKNOWN occupier (or a window
  /// that will not close inside the timeout) calls <paramref name="onRefused"/>
  /// with the gap named in the spine's own "expected X, but Y" shape, and
  /// <paramref name="proceed"/> is never invoked: a refusal that then proceeded
  /// anyway would be worse than the refusal it replaced.
  ///
  /// <para>Asynchronous by necessity - the occupancy flag clears a few frames after
  /// the addon does - so the continuation runs on a later framework tick, off any
  /// ImGui frame. A second call while one is in flight refuses rather than stacking
  /// two continuations onto one close.</para>
  /// </summary>
  internal static void ClearThenRun(string action, Action proceed, Action<string> onRefused)
  {
    if (!DesynthOrchestrator.PlayerOccupied(out var why))
    {
      proceed();
      return;
    }

    if (!KnownOccupier(out var what))
    {
      // Rung 4, still - and correctly. We close what we recognize; an NPC has the
      // player and nothing we can click will change that.
      onRefused($"Can't {action} - expected to be un-occupied, but {why}.");
      return;
    }

    if (IsClearing)
    {
      onRefused($"Can't {action} - already closing a window for another stage. Try again in a moment.");
      return;
    }

    Svc.Chat.Print($"[Scrooge] Closing {what} so the {action} can run.");

    _proceed = proceed;
    _onRefused = onRefused;
    _deadline = Environment.TickCount64 + ClearTimeoutMs;
    _nextAttempt = 0;
    IsClearing = true;
    Svc.Framework.Update -= ClearTick; // defensive: never double-subscribe
    Svc.Framework.Update += ClearTick;
  }

  /// <summary>
  /// One tick of the clear: free -> hand off; past the deadline -> refuse; otherwise
  /// close the innermost session window and wait a beat. Closing one per tick rather
  /// than all at once is deliberate - the game reopens the parent window as each
  /// child closes, and a burst of Close calls races that.
  /// </summary>
  private static unsafe void ClearTick(Dalamud.Plugin.Services.IFramework framework)
  {
    if (!DesynthOrchestrator.PlayerOccupied(out _))
    {
      var proceed = Finish();
      proceed?.Invoke();
      return;
    }

    if (Environment.TickCount64 > _deadline)
    {
      var refused = _onRefused;
      Finish();
      refused?.Invoke(
        "Can't clear the retainer window - it didn't close. Close it yourself and press the stage again.");
      return;
    }

    if (Environment.TickCount64 < _nextAttempt) return;
    _nextAttempt = Environment.TickCount64 + CloseIntervalMs;

    foreach (var name in SessionWindows)
    {
      if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon)) continue;
      if (!GenericHelpers.IsAddonReady(addon)) return; // still loading - wait, don't poke it
      addon->Close(true);
      return;
    }
  }

  /// <summary>Tears the clear down and hands back the continuation, exactly once.</summary>
  private static Action? Finish()
  {
    Svc.Framework.Update -= ClearTick;
    IsClearing = false;
    var proceed = _proceed;
    _proceed = null;
    _onRefused = null;
    return proceed;
  }

  /// <summary>Plugin dispose - a torn-down world has no continuation to run.</summary>
  internal static void Reset() => Finish();
}
