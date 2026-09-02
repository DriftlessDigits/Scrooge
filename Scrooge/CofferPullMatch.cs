using System;

namespace Scrooge;

/// <summary>
/// One captured pull: which box, what came out.
/// </summary>
internal readonly record struct CofferPull(uint Container, uint ItemId, int Quantity, bool IsHq);

/// <summary>
/// The coffer watcher's OTHER pure half: pairing the two edges of a pull - the
/// bag count falling (the arm) and the "You obtain" chat line (the reward) -
/// regardless of which one the game delivers first.
///
/// <para>Live receipt 2026-09-01, two boxes two minutes apart: on the first the
/// Framework poll armed 6ms BEFORE the chat line and the pull banked; on the second
/// the chat line landed 13ms BEFORE the poll and the pull was dropped on the
/// handler's disarmed-state guard. Frame-vs-chat ordering is a coin flip, so a
/// watcher that only listens after arming captures half the book.</para>
///
/// <para>So both edges are held, each for one window, and whichever arrives second
/// closes the pair. An obtain line with no arm behind it and none ahead of it
/// inside the window is somebody else's loot and expires unbanked - the scope guard
/// (only a watched container's count can arm) is untouched; what changed is that
/// the arm may now look BACK as well as forward.</para>
/// </summary>
internal sealed class CofferPullMatch
{
  private readonly TimeSpan _window;

  private DateTime _armedUntil = DateTime.MinValue;
  private uint _armedContainer;

  private DateTime _obtainUntil = DateTime.MinValue;
  private (uint ItemId, int Quantity, bool IsHq) _obtain;

  internal CofferPullMatch(TimeSpan window) => _window = window;

  /// <summary>
  /// A watched container's count fell. Pairs with an obtain line still inside its
  /// window, or holds the arm for one window otherwise.
  /// </summary>
  internal CofferPull? Arm(uint container, DateTime now)
  {
    if (now <= _obtainUntil)
    {
      var pull = new CofferPull(container, _obtain.ItemId, _obtain.Quantity, _obtain.IsHq);
      Reset();
      return pull;
    }

    _armedContainer = container;
    _armedUntil = now + _window;
    return null;
  }

  /// <summary>
  /// A "You obtain" line with an item on it. Pairs with an arm still inside its
  /// window, or holds the line for one window otherwise.
  /// </summary>
  internal CofferPull? Obtain(uint itemId, int quantity, bool isHq, DateTime now)
  {
    if (now <= _armedUntil)
    {
      var pull = new CofferPull(_armedContainer, itemId, quantity, isHq);
      Reset();
      return pull;
    }

    _obtain = (itemId, quantity, isHq);
    _obtainUntil = now + _window;
    return null;
  }

  private void Reset()
  {
    _armedUntil = DateTime.MinValue;
    _armedContainer = 0;
    _obtainUntil = DateTime.MinValue;
  }
}
