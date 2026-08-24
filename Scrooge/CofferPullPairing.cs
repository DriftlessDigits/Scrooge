namespace Scrooge;

/// <summary>
/// What a watched container's bag count did between two polls, and therefore
/// what the watcher should do about it.
/// </summary>
internal enum CofferCountMove
{
  /// <summary>The count is unchanged. No baseline to move, nothing to arm.</summary>
  Nothing,

  /// <summary>Take the reading as the new baseline and say nothing. Buying boxes lands here.</summary>
  Rebaseline,

  /// <summary>
  /// A box left the bags. Re-baseline AND open the capture window: the next
  /// obtain line inside it is the pull.
  /// </summary>
  Arm,

  /// <summary>
  /// The bags could not be read - zoning, login, a null InventoryManager. Forget
  /// the baseline; do NOT arm. The next readable poll starts over.
  /// </summary>
  Forget,
}

/// <summary>
/// The coffer watcher's pure half: given the last known bag count of a watched
/// container and this tick's reading, what happens.
///
/// <para>Split out for the same reason <see cref="VentureStamp"/> was - the decision
/// is arithmetic over two nullable ints, and the file it would otherwise live in is
/// welded to ClientStructs and the game's chat. Linked into Scrooge.Tests.</para>
///
/// <para>The rule that earns the split is the <b>unreadable</b> one. A count that
/// goes 3 -> unreadable -> 3 across a zone change is not a box being opened, but the
/// naive "is it lower than last time" test never sees the middle reading and cannot
/// tell. Modeling "no baseline" as its own state is what makes a zoning transition
/// structurally incapable of writing a phantom pull, instead of merely unlikely to.</para>
/// </summary>
internal static class CofferPullPairing
{
  /// <summary>
  /// The move, from the banked baseline (null = none held) and this tick's reading
  /// (null = the bags did not answer).
  ///
  /// <para>An increase re-baselines silently: buying a stack of boxes is not evidence
  /// about any pull. Only a DECREASE arms - which is the whole scope guard. The
  /// watcher polls nothing but the two Materiel Container ids, so no other coffer,
  /// chest or box in the game can reach this function, let alone a row.</para>
  /// </summary>
  internal static CofferCountMove Decide(int? baseline, int? reading)
  {
    if (reading is not { } now) return CofferCountMove.Forget;
    if (baseline is not { } before) return CofferCountMove.Rebaseline;
    if (now < before) return CofferCountMove.Arm;
    if (now > before) return CofferCountMove.Rebaseline;
    return CofferCountMove.Nothing;
  }
}
