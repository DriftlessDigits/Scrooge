using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// THE QUEUE DOCTRINE'S SHADOW ARM (ruled 08-23, deferred to 3.1 as live pricing;
/// Drift: "can we calculate both prices as an A/B test and gather some data for a
/// bit?"). Computes what the 3.1 candidate doctrine WOULD have written on the same
/// board the live walk just priced, so every receipt banks both worlds and the 3.1
/// ruling session opens on paired data instead of a retroactive reconstruction.
///
/// <para><b>The doctrine, in one read</b> (Drift's spine, 08-22: "we are here to
/// optimize the spot in line. usually we take the 1st spot for 'free'. the whole
/// game is knowing when not to be first"): position comes from the queue alone -
/// the tape gets no vote on it. The front is the free default. A seat is stepped
/// only when the queue ITSELF convicts it: the first real row above the front
/// company asks at least the ceiling multiple over the company's top - the same
/// ratio that already defines a dreamer, pointed at the bottom edge. A real queue
/// does not have a 3x hole in the middle of it; anything less is one line of
/// sellers, and stepping it is betting a buyer skips cheaper live stock.</para>
///
/// <para><b>Zero new constants</b>: the company is <see cref="LanePricing.ClusterNearPct"/>
/// (the quarter every other company test reads) and the conviction bar is the
/// player's own ceiling multiple. The 08-23 receipt audit sized the disagreement:
/// the live walk's tape conviction stepped 22 continuous queues for ~154 gil each,
/// while the 27 steps over genuinely separated dumps protected ~171k - this shape
/// keeps the dumps and joins the walls (Caligae joins at 6,998; the dye six-pack
/// joins at 292; a 44k crash under a 180k lane still steps).</para>
///
/// <para><b>Shadow only</b>: nothing reads this to price. It is written onto the
/// decision receipt (shadow_price / shadow_seat / shadow_defense) and compared
/// against what actually listed. Posture and floor are deliberately not applied -
/// the shadow's ask is the strictest honest read (one gil under the front it
/// joins), so the delta measures GEOMETRY, not undercut style.</para>
///
/// <para>Pure - no game reads, no storage, no statics (the LanePricing mold,
/// linked into Scrooge.Tests).</para>
/// </summary>
internal static class QueueDoctrine
{
  /// <summary>What the doctrine would have done on this board.</summary>
  /// <param name="Price">The ask it would write: one gil under the front it joins (min 1).</param>
  /// <param name="Seat">1-based seat that ask buys, counted over the WHOLE foreign queue - crashers count (the true seat, not the competitors-only fiction).</param>
  /// <param name="SteppedRows">Rows convicted and stepped by the queue itself. 0 = took the front.</param>
  /// <param name="Defense">The present-tense reason, greppable: "front", or "stepped N - successor Rx the pack". Tape and headcount are not on the list.</param>
  internal readonly record struct DoctrineShadow(
    long Price, int Seat, int SteppedRows, string Defense);

  /// <summary>
  /// Evaluates the doctrine over the walk's own sorted queue. <paramref name="overRail"/>
  /// is the dreamer suffix the walk already counted - dreamers cannot testify, so a
  /// cliff into them convicts nobody, and a queue that is nothing but dreamers has no
  /// real line to join (null: the doctrine has no listing answer on this board, same
  /// as the live walk's tape-only branch).
  /// </summary>
  internal static DoctrineShadow? Evaluate(
    IReadOnlyList<(long Price, bool Cross)> queue, int overRail,
    double ceilingMult, double clusterNearPct)
  {
    var realCount = queue.Count - Math.Max(0, overRail);
    if (realCount <= 0) return null;

    var front = 0;
    var stepped = 0;
    var lastRatio = 0.0;
    while (true)
    {
      // The front company: rows within the quarter of the row that fronts it -
      // the same anchored shape the walk's own cluster loop reads.
      var companyEnd = front;
      while (companyEnd + 1 < realCount
             && queue[companyEnd + 1].Price <= queue[front].Price * (1 + clusterNearPct))
        companyEnd++;

      // No real successor: the company IS the queue - nothing left to convict it.
      if (companyEnd + 1 >= realCount) break;

      // The conviction: a real queue has no ceiling-multiple hole in it. A
      // successor at least that far above the company's top says the company is
      // not in line - it is someone dumping - and the queue said so itself.
      // A non-positive top is unconvictable (final pass, 08-23): 0/0 is NaN and
      // NaN < ceilingMult is FALSE, so a corrupt zero-gil row would step and
      // bank "successor NaN x the pack" into the corpus. Join instead.
      if (queue[companyEnd].Price <= 0) break;
      var ratio = (double)queue[companyEnd + 1].Price / queue[companyEnd].Price;
      if (ratio < ceilingMult) break;

      stepped += companyEnd + 1 - front;
      lastRatio = ratio;
      front = companyEnd + 1;
    }

    var price = Math.Max(1, queue[front].Price - 1);
    var seat = 1;
    foreach (var row in queue)
      if (row.Price < price)
        seat++;

    var defense = stepped == 0
      ? "front"
      : $"stepped {stepped} - successor {lastRatio:0.#}x the pack";
    return new DoctrineShadow(price, seat, stepped, defense);
  }
}
