using System;
using ECommons.DalamudServices;

namespace Scrooge;

/// <summary>
/// Shared coordination flag for gil-tracking components. Multiple trackers
/// (duty, quest, FATE, exchange, …) can flag that an explicit capture is in
/// flight; the chat catch-all consults <see cref="IsBlocked"/> to stand down
/// and avoid double-counting.
///
/// Blocks are owner-scoped: <see cref="Block"/> returns a token whose
/// disposal releases exactly once no matter how many paths reach it
/// (completion, abort, plugin unload). This replaces a bare ref-count that
/// stranded the block forever when a task chain died between Block and
/// Unblock — silently killing catch-all tracking for the session.
/// </summary>
internal static class GilTrackingState
{
  private static int _blockCount;

  /// <summary>
  /// Fires when a specific tracker has finished recording a gil change —
  /// either by releasing the last live block token or by declaring a
  /// one-shot chat-driven insert via <see cref="NotifyHandled"/>. The chat
  /// catch-all subscribes here and resets its baseline to current gil so the
  /// just-recorded delta isn't re-captured by its debounced diff.
  /// </summary>
  internal static event Action? OnGilChangeHandled;

  internal static bool IsBlocked => _blockCount > 0;

  /// <summary>
  /// Acquires a block. Dispose the returned token to release it; disposal is
  /// idempotent, so every exit path (success, abort, unload) can safely
  /// dispose the same token.
  /// </summary>
  internal static IDisposable Block(string owner) => new BlockToken(owner);

  /// <summary>
  /// Signals that a synchronous tracker (e.g. a chat-message parser) just
  /// wrote a transaction that accounts for a gil change. The catch-all
  /// should refresh its baseline so the change isn't double-counted on its
  /// next debounced diff.
  /// </summary>
  internal static void NotifyHandled() => OnGilChangeHandled?.Invoke();

  private sealed class BlockToken : IDisposable
  {
    private readonly string _owner;
    private bool _released;

    internal BlockToken(string owner)
    {
      _owner = owner;
      _blockCount++;
      Svc.Log.Verbose($"[GilTrack] catch-all block +{_owner} ({_blockCount} live)");
    }

    public void Dispose()
    {
      if (_released) return;
      _released = true;
      Svc.Log.Verbose($"[GilTrack] catch-all block -{_owner} ({_blockCount - 1} live)");
      if (_blockCount > 0 && --_blockCount == 0)
        OnGilChangeHandled?.Invoke();
    }
  }
}
