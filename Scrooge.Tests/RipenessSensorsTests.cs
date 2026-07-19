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

  [Fact]
  public void HeaderLine_NoScanEver_SaysSoPlainly()
    => Assert.Equal("no board scan yet", RipenessSensors.HeaderLine(0, 1000, 5));

  [Fact]
  public void HeaderLine_WithEvents_NamesTheCount()
    => Assert.Equal("board read 38m old, 37 market events since",
      RipenessSensors.HeaderLine(1000, 1000 + 60 * 38, 37));

  [Fact]
  public void HeaderLine_SingleEvent_Singular()
    => Assert.Contains("1 market event since", RipenessSensors.HeaderLine(1000, 4000, 1));

  [Fact]
  public void HeaderLine_ZeroEvents_OmitsTheNoise()
    => Assert.Equal("board read 38m old", RipenessSensors.HeaderLine(1000, 1000 + 60 * 38, 0));
}
