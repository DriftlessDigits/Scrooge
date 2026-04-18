namespace Scrooge;

/// <summary>
/// Shared coordination flag for gil-tracking components. Multiple trackers
/// (duty, quest, FATE, exchange, …) can flag that an explicit capture is in
/// flight; a future catch-all chat tracker consults <see cref="IsBlocked"/>
/// to stand down and avoid double-counting.
///
/// Ref-counted so overlapping trackers (e.g. an exchange opened during a
/// duty) don't release the block prematurely. Only unblocks when every
/// caller that blocked has unblocked.
/// </summary>
internal static class GilTrackingState
{
  private static int _blockCount;

  internal static bool IsBlocked => _blockCount > 0;

  internal static void Block() => _blockCount++;

  internal static void Unblock()
  {
    if (_blockCount > 0)
      _blockCount--;
  }
}
