using Xunit;

namespace Scrooge.Tests;

public class RipenessSensorsTests
{
  [Theory]
  [InlineData(0, "0m")]
  [InlineData(-50, "0m")]          // clock skew never reads negative
  [InlineData(60 * 38, "38m")]
  [InlineData(60 * 89, "89m")]     // minutes right up to the 90m seam
  [InlineData(60 * 90, "1.5h")]
  [InlineData(3600 * 2 + 360, "2.1h")]
  [InlineData(3600 * 35, "35h")]
  [InlineData(3600 * 36, "1.5d")]
  [InlineData(86400 * 3, "3d")]
  public void AgeText_PicksTheHonestUnit(long seconds, string expected)
    => Assert.Equal(expected, RipenessSensors.AgeText(seconds));

  // --- THE TWO CLOCKS (ruled 08-21, pen 6) ---

  [Fact]
  public void HeaderClocks_SaysBothReads()
    // The pinch's clock and recon's, and nothing else. The market_events tally
    // that used to ride the header is gone: it only counted what our own runs
    // saw, so it reported self-observation as news about the world.
    => Assert.Equal("board read 38m old; recon 12m old on 214 items",
      RipenessSensors.HeaderClocks(1000, 1000 + 60 * 38, 1000 + 60 * 26, 214));

  [Fact]
  public void HeaderClocks_NeverScanned_SaysNeverRatherThanDatingToTheEpoch()
    => Assert.Equal("no board scan yet; no recon read yet",
      RipenessSensors.HeaderClocks(0, 1000, 0, 0));

  [Fact]
  public void HeaderClocks_ReconBankedNothing_ReadsAsNever()
    // A banked time with a zero count is an empty table, not a fresh read.
    => Assert.Contains("no recon read yet",
      RipenessSensors.HeaderClocks(1000, 4000, 3000, 0));

  [Fact]
  public void HeaderClocks_SingleItem_Singular()
    => Assert.Contains("on 1 item", RipenessSensors.HeaderClocks(1000, 4000, 2000, 1));

  // --- THE COLLAPSE (ruled 08-22): one clause when both clocks RENDER the same ---

  [Fact]
  public void HeaderClocks_SameRenderedAge_CollapsesToOneClause()
    // A pinch and the recon that follows it inside one Round both read 3.5d old.
    => Assert.Equal("board and recon both read 3.5d old, 152 items",
      RipenessSensors.HeaderClocks(1000, 1000 + 86400 * 7 / 2, 1000, 152));

  [Fact]
  public void HeaderClocks_CollapseIsOnTheRenderedString_NotAToleranceWindow()
  {
    // 40 seconds apart, both floor to "12m": the reader could not have seen the
    // difference, so the header does not spend a clause on it.
    var now = 100_000L;
    Assert.Equal("board and recon both read 12m old, 3 items",
      RipenessSensors.HeaderClocks(now - 60 * 12, now, now - (60 * 12 + 40), 3));
  }

  [Fact]
  public void HeaderClocks_DifferentRenderedAges_KeepBothClauses()
    => Assert.Equal("board read 38m old; recon 12m old on 214 items",
      RipenessSensors.HeaderClocks(1000, 1000 + 60 * 38, 1000 + 60 * 26, 214));

  [Fact]
  public void HeaderClocks_OneClockNeverRead_NeverCollapses()
    // "no recon read yet" is not an age, so there is nothing to match against.
    => Assert.Equal("board read 0m old; no recon read yet",
      RipenessSensors.HeaderClocks(1000, 1000, 1000, 0));

  [Fact]
  public void HeaderClocks_CollapsedSingleItem_Singular()
    => Assert.Equal("board and recon both read 0m old, 1 item",
      RipenessSensors.HeaderClocks(1000, 1000, 1000, 1));
}
