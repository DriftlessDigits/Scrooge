using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE MISSING RUNG (2026-07-26): the round walks the player to the bell and then reaches
/// for it, instead of waiting for a click it was already authorized to make.
///
/// The gate is the whole feature - the interact itself is three lines of Dalamud. So every
/// way this must NOT fire gets its own case: outside a round, over a live run, past a
/// staged melt, while occupied, when the roster is already up, out of range, and inside
/// the cooldown.
/// </summary>
public class BellReachTests
{
  /// <summary>The all-clear: everything that must be true, is. Each test flips exactly one.</summary>
  private static bool Reach(
    bool roundActive = true,
    bool stageHappensAtBell = true,
    bool anyRunBusy = false,
    bool meltStaged = false,
    bool unoccupied = true,
    bool bellAddonOpen = false,
    bool bellInRange = true,
    long nowMs = 100_000,
    long lastReachMs = 0)
    => BellReach.ShouldReach(roundActive, stageHappensAtBell, anyRunBusy, meltStaged,
      unoccupied, bellAddonOpen, bellInRange, nowMs, lastReachMs);

  [Fact]
  public void StandingAtTheBellMidRound_Reaches()
    => Assert.True(Reach());

  [Fact]
  public void NeverOutsideAPressedRound()
    => Assert.False(Reach(roundActive: false));

  [Fact]
  public void NeverForAStageThatDoesNotHappenAtABell()
    // The melt happens wherever you stand and the turn-in at the GC counter - reaching
    // for a bell you happen to be near would be the advisor doing something nobody asked.
    => Assert.False(Reach(stageHappensAtBell: false));

  [Fact]
  public void NeverOverALiveRun()
    => Assert.False(Reach(anyRunBusy: true));

  [Fact]
  public void NeverPastAStagedMelt()
    // Contract B leaves the melt owing the player a press; opening a bell over it would
    // occupy him out of the very window he has been told to press.
    => Assert.False(Reach(meltStaged: true));

  [Fact]
  public void NeverWhileOccupied()
    => Assert.False(Reach(unoccupied: false));

  [Fact]
  public void NothingToReachForWhenTheRosterIsAlreadyUp()
    // The location gate passes on its own from here - a second interact would close it.
    => Assert.False(Reach(bellAddonOpen: true));

  [Fact]
  public void NeverFromOutOfRange()
    // Still walking. The deck's walk line owns this moment, not the reach.
    => Assert.False(Reach(bellInRange: false));

  [Fact]
  public void FirstReachOfTheSessionIsAllowed()
    => Assert.True(Reach(nowMs: 5, lastReachMs: 0));

  [Fact]
  public void OnePoliteAttempt_ThenTheCooldownHolds()
  {
    // The flow tick runs at frame rate: without the cooldown "one polite attempt" would
    // be sixty a second at a bell the game is refusing.
    const long fired = 50_000;
    Assert.False(Reach(nowMs: fired + 1, lastReachMs: fired));
    Assert.False(Reach(nowMs: fired + BellReach.CooldownMs - 1, lastReachMs: fired));
  }

  [Fact]
  public void AfterTheCooldown_ItTriesAgain()
  {
    const long fired = 50_000;
    Assert.True(Reach(nowMs: fired + BellReach.CooldownMs, lastReachMs: fired));
  }

  [Fact]
  public void TheCooldownDoesNotRescueAnyOtherRefusal()
  {
    // A cooled-down reach still has to clear every other gate - the cooldown is the last
    // condition, never a substitute for the first ones.
    const long fired = 50_000;
    var elapsed = fired + BellReach.CooldownMs + 1;
    Assert.False(Reach(roundActive: false, nowMs: elapsed, lastReachMs: fired));
    Assert.False(Reach(anyRunBusy: true, nowMs: elapsed, lastReachMs: fired));
    Assert.False(Reach(bellInRange: false, nowMs: elapsed, lastReachMs: fired));
  }

  // ========================================================================
  // THE RANGE, CORRECTED (07-26): AutoRetainer's field-proven semantics
  // ========================================================================
  //
  // Our first pass guessed 4.5y measured to the HITBOX. Reading AR's
  // GetValidInteractionDistance / GetReachableRetainerBell (reference prior art, never
  // an integration) corrected both halves: the check the game honours is RAW distance
  // to the object origin, at 4.6y - and 6.5y for a housing bell, a whole class of bell
  // this plugin was not even scanning for.

  [Fact]
  public void TheOrdinaryReachIsAutoRetainersFourPointSix()
    => Assert.Equal(4.6f, BellReach.InteractRangeYalms);

  [Fact]
  public void AHousingBellGetsTheGenerousReach()
  {
    Assert.Equal(6.5f, BellReach.HousingInteractRangeYalms);
    Assert.True(BellReach.HousingInteractRangeYalms > BellReach.InteractRangeYalms);
    Assert.Equal(6.5f, BellReach.ValidRangeFor(housingEventObject: true));
    Assert.Equal(4.6f, BellReach.ValidRangeFor(housingEventObject: false));
  }

  [Theory]
  // raw yalms, housing -> reachable
  [InlineData(0.0f, false, true)]
  [InlineData(4.5f, false, true)]   // the old constant now sits INSIDE the range
  [InlineData(4.59f, false, true)]
  [InlineData(4.6f, false, false)]  // strictly less than - the boundary is a reject
  [InlineData(5.0f, false, false)]
  [InlineData(5.0f, true, true)]    // the same spot reaches a housing bell
  [InlineData(6.49f, true, true)]
  [InlineData(6.5f, true, false)]
  public void IsReachable_IsTheRawDistanceAgainstTheBellsOwnRange(
    float rawYalms, bool housing, bool expected)
    => Assert.Equal(expected, BellReach.IsReachable(rawYalms, housing));

  [Fact]
  public void ABellStandingAtFiveYalmsIndoors_UsedToBeInvisibleTwiceOver()
  {
    // Before the correction: housing bells were never scanned (wrong ObjectKind), and
    // even if they had been, 5.0y raw failed a 4.5y hitbox-adjusted gate. Both halves.
    Assert.True(BellReach.IsReachable(5.0f, housingEventObject: true));
    Assert.False(BellReach.IsReachable(5.0f, housingEventObject: false));
  }
}
