using System;

namespace Scrooge;

/// <summary>
/// Shared coordination flag for gil-tracking components. Multiple trackers
/// (duty, quest, FATE, exchange, …) can flag that an explicit capture is in
/// flight; the chat catch-all consults <see cref="IsBlocked"/> to stand down
/// and avoid double-counting.
///
/// Ref-counted so overlapping trackers (e.g. an exchange opened during a
/// duty) don't release the block prematurely. Only unblocks when every
/// caller that blocked has unblocked.
/// </summary>
internal static class GilTrackingState
{
  private static int _blockCount;

  /// <summary>
  /// Fires when a specific tracker has finished recording a gil change —
  /// either by closing a blocked window (last <see cref="Unblock"/> in a
  /// ref-count chain) or by declaring a one-shot chat-driven insert via
  /// <see cref="NotifyHandled"/>. The chat catch-all subscribes here and
  /// resets its baseline to current gil so the just-recorded delta isn't
  /// re-captured by its debounced diff.
  /// </summary>
  internal static event Action? OnGilChangeHandled;

  internal static bool IsBlocked => _blockCount > 0;

  internal static void Block() => _blockCount++;

  internal static void Unblock()
  {
    if (_blockCount > 0 && --_blockCount == 0)
      OnGilChangeHandled?.Invoke();
  }

  /// <summary>
  /// Signals that a synchronous tracker (e.g. a chat-message parser) just
  /// wrote a transaction that accounts for a gil change. The catch-all
  /// should refresh its baseline so the change isn't double-counted on its
  /// next debounced diff.
  /// </summary>
  internal static void NotifyHandled() => OnGilChangeHandled?.Invoke();
}
