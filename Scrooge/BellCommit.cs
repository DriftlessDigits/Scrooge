using System.Collections.Generic;

namespace Scrooge;

/// <summary>Why a gate row did or did not make it into a committed Round's bell.</summary>
internal enum BellHold
{
  /// <summary>It rides.</summary>
  Admitted,
  /// <summary>It was in the bags when the human pressed Continue.</summary>
  InTheSnapshot,
  /// <summary>Not in the snapshot, but this Round's own melt produced it - the one declared exception.</summary>
  ThisRoundsYield,
  /// <summary>It arrived after the hinge. Not an error: it waits for the next Round, where recon reads it properly.</summary>
  ArrivedAfterTheHinge,
  /// <summary>The Round committed but its snapshot is gone. Fail closed - admit nothing the hinge cannot vouch for.</summary>
  SnapshotLost,
}

/// <summary>
/// INVARIANT B - <b>the act half executes exactly what was ruled</b> (Drift, ruled
/// 2026-08-10, the addendum to unit 5).
///
/// <para><b>The drift this closes.</b> The hinge's Continue is the Round's commit
/// point: the human looks at a board, rules it, and presses. The bell then fires
/// minutes later and, until this, recomposed its gate half from a LIVE bag scan -
/// so anything that landed in the bags in between (venture loot returning, a retainer
/// coming home, loot from a duty) joined the run. Those items were never on the board
/// the human ruled, never in the count he pressed against, and never read by recon.
/// They went on the market on the strength of a press that was about other things.</para>
///
/// <para><b>The shape.</b> At Continue, the Round snapshots the gate-joiner variants.
/// At bell time a gate row is admitted iff it was in that snapshot - with exactly ONE
/// declared exception, <b>this Round's own desynth yields</b>, because the ruled order
/// puts the melt between the hinge and the bell on purpose (the melt FEEDS the bell)
/// and the human pressing Continue is authorising that melt. Yields ride the ordinary
/// pricing pipeline like any other row: market beats vendor means list, else vendor.
/// No special pricing, just admission.</para>
///
/// <para><b>Exclusion is not failure.</b> A held row is not an error and does not
/// refuse anything - it simply waits for the next Round, where recon will read its
/// board and the hinge will ask about it properly. That is strictly better than what
/// it got before, which was to be listed off a decision nobody made.</para>
///
/// <para><b>Where it does NOT apply.</b> Outside a Round, and inside a Round whose
/// hinge never committed (the human deferred the step, or it self-skipped as empty),
/// there is no commit point and therefore no ruling to hold the bell to - the live
/// scan stands, exactly as it always did. A manual Hawk run composes its own rows and
/// never comes through here at all.</para>
///
/// Pure and Dalamud-free (linked into the test project).
/// </summary>
internal static class BellCommit
{
  /// <summary>
  /// One variant as a single persisted number, in the HOUSE CONVENTION: HQ is the
  /// item id plus one million, which is how banned ids, always-vendor ids and the
  /// game's own context-menu ids have been written since the beginning. A second
  /// encoding for the same fact would be a second thing to get wrong, and every real
  /// item id is five digits.
  /// </summary>
  internal static long Encode(BellVariant v) => v.IsHq ? v.ItemId + 1_000_000L : v.ItemId;

  /// <summary>The inverse. A code that is not a live item id decodes to a variant nothing matches, which is harmless: it simply never admits anything.</summary>
  internal static BellVariant Decode(long code)
    => code >= 1_000_000L
      ? new BellVariant((uint)(code - 1_000_000L), true)
      : new BellVariant((uint)code, false);

  /// <summary>
  /// THE ADMISSION RULE, and the whole of invariant B in one method.
  ///
  /// <para><paramref name="committed"/> is null when the Round has no snapshot. With
  /// <paramref name="hingeCommitted"/> false that is the ordinary state - nothing was
  /// ruled, so nothing is being held to. With it TRUE it means the snapshot was lost
  /// (a Round that started before this build, or a config write that never landed),
  /// and the answer is FAIL CLOSED: admit only what the melt made, because a Round
  /// that cannot say what the human ruled must not guess.</para>
  /// </summary>
  internal static BellHold Judge(
    BellVariant candidate,
    bool hingeCommitted,
    IReadOnlySet<BellVariant>? committed,
    IReadOnlySet<BellVariant> roundYields)
  {
    // No commit point: the bell is composing off a live world nobody ruled against,
    // which is what it has always done and what it should keep doing.
    if (!hingeCommitted) return BellHold.Admitted;

    // The declared exception, asked FIRST so a yield rides even when the snapshot is
    // gone - the melt happened inside this Round under this Round's own press, and
    // that is a fact the bell can vouch for without the snapshot's help.
    if (roundYields.Contains(candidate)) return BellHold.ThisRoundsYield;

    if (committed is null) return BellHold.SnapshotLost;

    return committed.Contains(candidate)
      ? BellHold.InTheSnapshot
      : BellHold.ArrivedAfterTheHinge;
  }

  /// <summary>Did this fate put the row on the bell?</summary>
  internal static bool Rides(BellHold hold)
    => hold is BellHold.Admitted or BellHold.InTheSnapshot or BellHold.ThisRoundsYield;

  /// <summary>
  /// Filters gate candidates down to what a committed Round may actually spend, and
  /// hands back what it held so the caller can say so once, quietly. Per-SLOT rows are
  /// kept intact exactly as <see cref="BellPlan.Join"/> keeps them - two stacks of one
  /// mat are two listings - and the admission is by VARIANT, so a variant the hinge
  /// saw brings all of its slots with it.
  /// </summary>
  internal static List<T> Admit<T>(
    IEnumerable<(T Row, BellVariant Key)> candidates,
    bool hingeCommitted,
    IReadOnlySet<BellVariant>? committed,
    IReadOnlySet<BellVariant> roundYields,
    out List<BellVariant> held)
  {
    var admitted = new List<T>();
    var holdList = new List<BellVariant>();
    foreach (var c in candidates)
    {
      if (Rides(Judge(c.Key, hingeCommitted, committed, roundYields)))
        admitted.Add(c.Row);
      else if (!holdList.Contains(c.Key))
        holdList.Add(c.Key);
    }
    held = holdList;
    return admitted;
  }
}
