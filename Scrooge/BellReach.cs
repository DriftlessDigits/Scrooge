namespace Scrooge;

/// <summary>
/// THE MISSING RUNG (Drift, 2026-07-26: <i>"I had also expected the round to retarget
/// the retainer bell."</i>).
///
/// <para>Every bell stage's location gate is "a bell addon is open" - which means the
/// round walked the player to the bell and then stopped, waiting for him to click it.
/// That is a gap in the ladder, not a policy: opening the roster is the same class of
/// act as opening the salvage window for the melt, and the round already does that
/// one. The press that started the round authorized the errand; clicking the bell is
/// part of doing the errand, not a new decision.</para>
///
/// <para><b>The first law holds.</b> This can only ever answer true inside a round the
/// human pressed, with no run in flight and no melt awaiting his press. Outside a
/// round it is structurally unreachable - <c>roundActive</c> false answers false, and
/// nothing else in the predicate can rescue it.</para>
///
/// <para>Pure and clock-injected so the whole gate is table-testable; the object-table
/// read and the interact itself stay Dalamud-bound (see GameSafe).</para>
/// </summary>
internal static class BellReach
{
  /// <summary>
  /// How close the player must be to an ORDINARY bell before the game will honour an
  /// interact, in yalms - measured RAW, centre to centre, with no hitbox subtraction.
  ///
  /// <para><b>Provenance: AutoRetainer's <c>GetValidInteractionDistance</c></b> -
  /// constants field-proven across its userbase. Reference prior art, NOT an
  /// integration: Scrooge does not talk to AutoRetainer and never will (standing rule);
  /// it just stops guessing where somebody else has already measured.</para>
  ///
  /// <para>Our own first pass guessed 4.5y measured to the HITBOX, and both halves of
  /// that guess were wrong - the check the game honours is against the object's origin,
  /// and the number is 4.6. AR carries a third case, 4.75y inside inns off a territory
  /// list; 4.6 everywhere non-housing is marginally conservative there, which costs at
  /// most a failed reach the walk fixes a step later and buys one less territory list
  /// to keep alive across patches.</para>
  /// </summary>
  internal const float InteractRangeYalms = 4.6f;

  /// <summary>
  /// The reach for a bell inside a house or apartment - a <c>HousingEventObject</c>,
  /// which the game is markedly more generous with (same source).
  ///
  /// <para>Housing bells were invisible to this plugin until now: the scan only ever
  /// accepted <c>EventObj</c>, so every bell indoors read as no bell at all and the
  /// reach simply never happened there.</para>
  /// </summary>
  internal const float HousingInteractRangeYalms = 6.5f;

  /// <summary>The reach the game will honour for this bell.</summary>
  internal static float ValidRangeFor(bool housingEventObject)
    => housingEventObject ? HousingInteractRangeYalms : InteractRangeYalms;

  /// <summary>
  /// Is a bell measured at <paramref name="rawYalms"/> (centre to centre) close enough
  /// to reach? Strictly LESS than the range, matching the check AutoRetainer's users
  /// have been running for years - a bell sitting exactly on the boundary is the one
  /// the game rejects, and the walk closes that gap on the next frame anyway.
  /// </summary>
  internal static bool IsReachable(float rawYalms, bool housingEventObject)
    => rawYalms < ValidRangeFor(housingEventObject);

  /// <summary>
  /// The quiet time after a reach before another one is allowed, in milliseconds.
  ///
  /// <para>ONE polite attempt, then wait. A reach can fail for reasons the advisor
  /// cannot see - the player is still moving, the game briefly refuses, AutoRetainer
  /// took the bell a frame earlier - and the flow tick runs at frame rate, so without
  /// this the "polite attempt" would be sixty attempts a second. Five seconds is long
  /// enough that a failed reach reads as one attempt to the player and to the server,
  /// and short enough that a genuinely missed click is retried while he is still
  /// standing there. Note that a SUCCESSFUL reach never needs the retry: the roster
  /// opens, the location gate passes, and the stage fires.</para>
  /// </summary>
  internal const int CooldownMs = 5000;

  /// <summary>
  /// The beat between deciding to reach and the hand moving, in milliseconds. This is
  /// only the BASE: the caller runs it through the same ApplyJitter every other click
  /// gets, and that helper floors its result at 1000ms whenever jitter is enabled - so
  /// the observed pause is ~0.6s with jitter off and ~1s with it on. Either reads as a
  /// person noticing the bell rather than a script snapping to it.
  /// </summary>
  internal const int ReachDelayMs = 600;

  /// <summary>
  /// Should the advisor reach for the bell right now?
  /// </summary>
  /// <param name="roundActive">A human-pressed round is live and un-halted.</param>
  /// <param name="stageHappensAtBell">The offered stage's place is a bell (the pinch and the bell run).</param>
  /// <param name="anyRunBusy">Any executor is working - the wheel belongs to it.</param>
  /// <param name="meltStaged">A melt is staged and owes the player a press - never reach past that.</param>
  /// <param name="unoccupied">The game will accept an interact (no bell session, no NPC, no cutscene).</param>
  /// <param name="bellAddonOpen">A bell window is already up - there is nothing to reach for.</param>
  /// <param name="bellInRange">
  /// A targetable Summoning Bell is within its own valid reach (see
  /// <see cref="IsReachable"/>). Targetability is folded into the sensor rather than
  /// carried as a second operand here: an untargetable bell is not a bell you can
  /// reach, and splitting it would let the two answers disagree on the same frame.
  /// </param>
  /// <param name="nowMs">Monotonic clock.</param>
  /// <param name="lastReachMs">When the last reach fired; 0 = never.</param>
  internal static bool ShouldReach(
    bool roundActive,
    bool stageHappensAtBell,
    bool anyRunBusy,
    bool meltStaged,
    bool unoccupied,
    bool bellAddonOpen,
    bool bellInRange,
    long nowMs,
    long lastReachMs)
  {
    if (!roundActive) return false;          // the first law, structurally
    if (!stageHappensAtBell) return false;   // the melt and the turn-in are not bell work
    if (anyRunBusy) return false;
    if (meltStaged) return false;
    if (!unoccupied) return false;           // the game refuses an interact while occupied
    if (bellAddonOpen) return false;         // already there - the gate will pass on its own
    if (!bellInRange) return false;          // still walking; the walk line owns this
    return lastReachMs <= 0 || nowMs - lastReachMs >= CooldownMs;
  }
}
