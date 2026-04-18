using System;
using System.Collections.Generic;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Scrooge;

/// <summary>
/// Safety-net gil tracker. Listens for chat messages on the set of types
/// the game uses to narrate in-world gil-affecting events and, when no
/// specific tracker has blocked, waits 100 ms for the event to settle and
/// diffs the player's gil against a running baseline.
///
/// Non-zero diffs are recorded as <c>source="catchall"</c>. This keeps the
/// derived "Other" bucket near zero without needing a dedicated parser for
/// every minor gil source.
///
/// Mirrors CurrencyTracker's ChatHandler pattern (chat types
/// {0, 57, 62, 2110, 2105, 2238, 2622, 3001, 3006} + 100 ms debounce).
///
/// The baseline resets to current gil whenever a specific tracker signals
/// <see cref="GilTrackingState.OnGilChangeHandled"/>, so gil movement
/// already attributed to <c>duty_reward</c>, <c>npc_purchase</c>, etc. is
/// not re-captured here.
/// </summary>
internal sealed class ChatCatchallTracker : IDisposable
{
  private static readonly HashSet<int> ValidChatTypes =
    [0, 57, 62, 2110, 2105, 2238, 2622, 3001, 3006];

  private const int DebounceMs = 100;

  private long _baselineGil;
  private bool _baselineInitialized;
  // Environment.TickCount64 at which the debounce should fire. 0 = idle.
  private long _scheduledDiffTick;
  private bool _frameworkSubscribed;

  internal ChatCatchallTracker()
  {
    Svc.Chat.ChatMessage += OnChatMessage;
    GilTrackingState.OnGilChangeHandled += OnGilChangeHandled;
  }

  public void Dispose()
  {
    if (_frameworkSubscribed)
      Svc.Framework.Update -= OnFrameworkUpdate;
    GilTrackingState.OnGilChangeHandled -= OnGilChangeHandled;
    Svc.Chat.ChatMessage -= OnChatMessage;
  }

  private unsafe void OnChatMessage(XivChatType type, int timestamp,
    ref SeString sender, ref SeString message, ref bool isHandled)
  {
    if (!Plugin.Configuration.EnableGilTracking) return;
    if (!ValidChatTypes.Contains((int)type)) return;
    if (GilTrackingState.IsBlocked) return;

    if (!_baselineInitialized)
    {
      _baselineGil = (long)InventoryManager.Instance()->GetGil();
      _baselineInitialized = true;
    }

    _scheduledDiffTick = Environment.TickCount64 + DebounceMs;
    if (!_frameworkSubscribed)
    {
      Svc.Framework.Update += OnFrameworkUpdate;
      _frameworkSubscribed = true;
    }
  }

  private unsafe void OnFrameworkUpdate(IFramework framework)
  {
    if (_scheduledDiffTick == 0)
    {
      Svc.Framework.Update -= OnFrameworkUpdate;
      _frameworkSubscribed = false;
      return;
    }

    if (Environment.TickCount64 < _scheduledDiffTick) return;

    _scheduledDiffTick = 0;
    Svc.Framework.Update -= OnFrameworkUpdate;
    _frameworkSubscribed = false;

    var currentGil = (long)InventoryManager.Instance()->GetGil();

    if (GilTrackingState.IsBlocked)
    {
      // A specific tracker came online during the debounce window — it will
      // own this capture. Refresh baseline silently and bail.
      _baselineGil = currentGil;
      return;
    }

    var diff = currentGil - _baselineGil;
    _baselineGil = currentGil;

    if (diff == 0) return;

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var direction = diff > 0 ? "earned" : "spent";
    var amount = Math.Abs(diff);
    GilStorage.InsertTransaction(now, direction, "catchall", amount,
      0, "", "", 1, (int)amount, false, "", "");

    Svc.Log.Debug($"[GilTrack] catchall: {direction} {amount:N0}g");
  }

  private unsafe void OnGilChangeHandled()
  {
    // A specific tracker just recorded the current gil state — adopt it as
    // our new baseline so the upcoming debounced diff sees 0 and skips.
    _baselineGil = (long)InventoryManager.Instance()->GetGil();
    _baselineInitialized = true;

    // Any debounce queued by the same chat message is now stale.
    _scheduledDiffTick = 0;
  }
}
