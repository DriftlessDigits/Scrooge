using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE ONE HALT THAT CLEARS ITSELF (2026-07-26). Drift spent seals in front of a halted
/// round and it stayed halted. The wallet is sensable, so it behaves like the location
/// gate he already likes: the gap closes, the stage is re-offered.
///
/// The narrow scope IS the contract, so most of these cases are about what must NOT
/// auto-clear.
/// </summary>
public class WalletHaltTests
{
  /// <summary>The real halt line the turn-in composes, marker and all.</summary>
  private const string WalletMessage =
    "Turn-in halted - stopped - seal wallet nearly full (89,105/90,000), 5 items left. "
    + "Clear it and Resume - the turn-in re-reads the delivery list at the counter.";

  [Fact]
  public void TheOrchestratorsPhraseIsRecognized()
  {
    // The writer composes from WalletHalt.Marker; if that literal ever drifts, this is
    // the test that says so instead of the flow silently never resuming again.
    Assert.Contains(WalletHalt.Marker, WalletMessage);
    Assert.True(WalletHalt.IsWalletHalt(RoundStage.TurnIn, WalletMessage));
  }

  [Fact]
  public void RoomForTheCheapestPendingRow_ReOffers()
  {
    // 89,105 + 500 <= 90,000: the wallet can hold work again.
    Assert.True(WalletHalt.ShouldReoffer(
      roundActive: true, RoundStage.TurnIn, WalletMessage,
      current: 89_105, max: 90_000, cheapestPending: 500));
  }

  [Fact]
  public void ExactlyEnoughRoom_ReOffers()
  {
    Assert.True(WalletHalt.ShouldReoffer(
      roundActive: true, RoundStage.TurnIn, WalletMessage,
      current: 89_105, max: 90_000, cheapestPending: 895));
  }

  [Fact]
  public void OneSealShort_StaysHalted()
  {
    Assert.False(WalletHalt.ShouldReoffer(
      roundActive: true, RoundStage.TurnIn, WalletMessage,
      current: 89_105, max: 90_000, cheapestPending: 896));
  }

  [Fact]
  public void AnUnspentWallet_StaysHalted()
  {
    // The halt's own moment: nothing has changed, so nothing re-offers. Without this the
    // flow would resume into the same stop on the very next tick, forever.
    Assert.False(WalletHalt.ShouldReoffer(
      roundActive: true, RoundStage.TurnIn, WalletMessage,
      current: 89_105, max: 90_000, cheapestPending: 2_000));
  }

  [Fact]
  public void NothingLeftToTurnIn_StaysHalted()
  {
    // No pending rows means no seal reward to fit - re-offering an empty stage would be
    // the flow busying itself with nothing.
    Assert.False(WalletHalt.ShouldReoffer(
      roundActive: true, RoundStage.TurnIn, WalletMessage,
      current: 0, max: 90_000, cheapestPending: 0));
  }

  [Fact]
  public void AnUnreadableWallet_StaysHalted()
  {
    // Fail closed: an unreadable wallet is not evidence of room.
    Assert.False(WalletHalt.ShouldReoffer(
      roundActive: true, RoundStage.TurnIn, WalletMessage,
      current: null, max: 90_000, cheapestPending: 500));
    Assert.False(WalletHalt.ShouldReoffer(
      roundActive: true, RoundStage.TurnIn, WalletMessage,
      current: 89_105, max: null, cheapestPending: 500));
  }

  [Fact]
  public void ACancelledRoundHasNoStageToReOffer()
  {
    Assert.False(WalletHalt.ShouldReoffer(
      roundActive: false, RoundStage.TurnIn, WalletMessage,
      current: 0, max: 90_000, cheapestPending: 500));
  }

  [Fact]
  public void AnUnhaltedRoundIsNotReOffered()
  {
    Assert.False(WalletHalt.ShouldReoffer(
      roundActive: true, haltStage: null, haltMessage: null,
      current: 0, max: 90_000, cheapestPending: 500));
  }

  [Fact]
  public void OtherTurnInHalts_NeverAutoClear()
  {
    // A timeout, a stuck dialog, an unreadable list: no sensor says "cleared", so the
    // halt holds until the player says so. Generalizing past the wallet is a design walk.
    const string timeout =
      "Turn-in halted - timed out (the game stopped responding). "
      + "Clear it and Resume - the turn-in re-reads the delivery list at the counter.";

    Assert.False(WalletHalt.IsWalletHalt(RoundStage.TurnIn, timeout));
    Assert.False(WalletHalt.ShouldReoffer(
      roundActive: true, RoundStage.TurnIn, timeout,
      current: 0, max: 90_000, cheapestPending: 500));
  }

  [Fact]
  public void TheSameWalletTwice_DoesNotResumeAgain()
  {
    // THE ANTI-SPIN. A resume can end in the same halt without turning anything in (the
    // cheapest pending row fits, but the row the run reaches first does not). At frame
    // rate that pairing is a loop - so an unchanged wallet gets exactly one try.
    Assert.False(WalletHalt.ShouldReoffer(
      roundActive: true, RoundStage.TurnIn, WalletMessage,
      current: 89_105, max: 90_000, cheapestPending: 500, lastAutoResumeSeals: 89_105));
  }

  [Fact]
  public void AWalletThatMovedAgain_ResumesAgain()
  {
    // He spent more seals, or the run turned something in. Either way something changed,
    // so the round gets another honest try.
    Assert.True(WalletHalt.ShouldReoffer(
      roundActive: true, RoundStage.TurnIn, WalletMessage,
      current: 80_000, max: 90_000, cheapestPending: 500, lastAutoResumeSeals: 89_105));
  }

  [Fact]
  public void OtherStagesHalts_NeverAutoClear()
  {
    // Even a message carrying the marker, held over another stage, is not this halt.
    Assert.False(WalletHalt.IsWalletHalt(RoundStage.BellRun, WalletMessage));
    Assert.False(WalletHalt.ShouldReoffer(
      roundActive: true, RoundStage.BellRun, WalletMessage,
      current: 0, max: 90_000, cheapestPending: 500));
  }
}
