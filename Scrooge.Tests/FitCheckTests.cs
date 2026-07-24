using System;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The fit check at the sweep's press (WALK unit 8). The contract: two MEASURED
/// clocks, honest advice, never a gate. A board fresher than the re-pinch floor
/// skips the pinch; a pinch that won't comfortably clear the venture return
/// advises waiting but still fires on a deliberate click; a clock it can't read
/// degrades to advise-only and never blocks.
/// </summary>
public class FitCheckTests
{
  private static readonly TimeSpan Floor4h = TimeSpan.FromHours(4);

  // Board-stale ages so the board-fresh branch never pre-empts the clock cases.
  private const long StaleBoard = 6 * 3600;

  private const long Min = 60;        // seconds in a minute
  private const long MinMs = 60_000;  // ms in a minute

  [Fact]
  public void Fits_WhenPaddedEstimateClearsReturn()
  {
    // 10m pinch, retainers return in 30m: 10 * 1.25 = 12.5m << 30m.
    var fit = FitCheck.AtPress(10 * MinMs, 30 * Min, StaleBoard, Floor4h);
    Assert.Equal(FitVerdict.Fits, fit.Verdict);
    Assert.False(fit.SkipPinch);
    Assert.False(fit.RequiresConfirm);
    Assert.Contains("fits", fit.Message);
  }

  [Fact]
  public void DoesntFit_WhenEstimateOverrunsReturn()
  {
    // Sam's example: a 20m pinch into a return well under it.
    var fit = FitCheck.AtPress(20 * MinMs, 10 * Min, StaleBoard, Floor4h);
    Assert.Equal(FitVerdict.DoesntFit, fit.Verdict);
    Assert.True(fit.RequiresConfirm);
    Assert.False(fit.SkipPinch);
    Assert.Contains("wait for the haul", fit.Message);
  }

  [Fact]
  public void DoesntFit_WhenReturnIsImminent()
  {
    // Haul lands in a minute - even a short pinch shouldn't start.
    var fit = FitCheck.AtPress(5 * MinMs, 1 * Min, StaleBoard, Floor4h);
    Assert.Equal(FitVerdict.DoesntFit, fit.Verdict);
  }

  [Fact]
  public void DoesntFit_WhenTheHaulIsAlreadyWaiting()
  {
    // A completed venture reads as 0s remaining: nothing clears 0.
    var fit = FitCheck.AtPress(2 * MinMs, 0, StaleBoard, Floor4h);
    Assert.Equal(FitVerdict.DoesntFit, fit.Verdict);
  }

  [Fact]
  public void BoardFresh_SkipsThePinch_ClocksMoot()
  {
    // Board read 1h ago, floor 4h: the pinch is skipped regardless of the clocks
    // (here they'd otherwise "fit", proving fresh wins first).
    var fit = FitCheck.AtPress(10 * MinMs, 30 * Min, 1 * 3600, Floor4h);
    Assert.Equal(FitVerdict.BoardFresh, fit.Verdict);
    Assert.True(fit.SkipPinch);
    Assert.False(fit.RequiresConfirm);
    Assert.Contains("skips the pinch", fit.Message);
  }

  [Fact]
  public void BoardFresh_WinsEvenWhenClocksAreBlind()
  {
    // No estimate, no return clock, but the board is fresh: skip, don't degrade.
    var fit = FitCheck.AtPress(null, null, 1 * 3600, Floor4h);
    Assert.Equal(FitVerdict.BoardFresh, fit.Verdict);
    Assert.True(fit.SkipPinch);
  }

  [Fact]
  public void BoardStale_AtExactFloor_IsNotFresh()
  {
    // Age == floor is NOT under the floor: the pinch is step 1, clocks decide.
    var fit = FitCheck.AtPress(10 * MinMs, 30 * Min, 4 * 3600, Floor4h);
    Assert.Equal(FitVerdict.Fits, fit.Verdict);
  }

  [Fact]
  public void NeverScannedBoard_IsNotFresh()
  {
    // Null board age = never scanned = maximally stale, never fresh.
    var fit = FitCheck.AtPress(20 * MinMs, 10 * Min, null, Floor4h);
    Assert.Equal(FitVerdict.DoesntFit, fit.Verdict);
  }

  [Fact]
  public void NoData_WhenEstimateIsBlind_DoesNotBlock()
  {
    // No pinch timing on record: advise-only, never a gate.
    var fit = FitCheck.AtPress(null, 30 * Min, StaleBoard, Floor4h);
    Assert.Equal(FitVerdict.NoData, fit.Verdict);
    Assert.False(fit.RequiresConfirm);
    Assert.False(fit.SkipPinch);
    Assert.Contains("can't estimate", fit.Message);
  }

  [Fact]
  public void NoData_WhenVentureClockIsBlind_DoesNotBlock()
  {
    // Not at a bell / no ventures out: the return clock can't be read.
    var fit = FitCheck.AtPress(10 * MinMs, null, StaleBoard, Floor4h);
    Assert.Equal(FitVerdict.NoData, fit.Verdict);
    Assert.False(fit.RequiresConfirm);
    Assert.Contains("retainer clock", fit.Message);
  }

  [Fact]
  public void NoData_WhenBothClocksBlind_NamesBoth()
  {
    var fit = FitCheck.AtPress(null, null, StaleBoard, Floor4h);
    Assert.Equal(FitVerdict.NoData, fit.Verdict);
    Assert.Contains("either clock", fit.Message);
  }

  [Fact]
  public void MarginBoundary_ExactlyOnTheMarginStillFits()
  {
    // est * 1.25 == return exactly: 8m * 1.25 = 10m return. "Comfortably" is
    // inclusive at the boundary (<=), so this fits.
    var fit = FitCheck.AtPress(8 * MinMs, 10 * Min, StaleBoard, Floor4h);
    Assert.Equal(FitVerdict.Fits, fit.Verdict);
  }

  [Fact]
  public void MarginBoundary_OneMsOverTheMarginDoesNotFit()
  {
    // A single ms past the padded estimate tips it to doesn't-fit.
    var fit = FitCheck.AtPress(8 * MinMs + 1, 10 * Min, StaleBoard, Floor4h);
    Assert.Equal(FitVerdict.DoesntFit, fit.Verdict);
  }

  [Fact]
  public void ShortDur_SaysDurationsTheWaySamDoes()
  {
    Assert.Equal("<1m", FitCheck.ShortDur(30));
    Assert.Equal("<1m", FitCheck.ShortDur(0));
    Assert.Equal("20m", FitCheck.ShortDur(20 * Min));
    Assert.Equal("1.5h", FitCheck.ShortDur(90 * Min));
    Assert.Equal("<1m", FitCheck.ShortDur(-5)); // clock skew never reads negative
  }

  [Fact]
  public void RepinchFloor_IsHonored_ShorterFloorMakesStaleFresh()
  {
    // The floor is a config seed, not a constant: a 1h floor means a 90m board is
    // already stale (pinch is step 1), where a 4h floor would call it fresh.
    var shortFloor = TimeSpan.FromHours(1);
    var fresh = FitCheck.AtPress(10 * MinMs, 30 * Min, 90 * Min, Floor4h);
    var stale = FitCheck.AtPress(10 * MinMs, 30 * Min, 90 * Min, shortFloor);
    Assert.Equal(FitVerdict.BoardFresh, fresh.Verdict);
    Assert.Equal(FitVerdict.Fits, stale.Verdict);
  }
}
