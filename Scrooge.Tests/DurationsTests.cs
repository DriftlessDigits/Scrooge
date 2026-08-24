using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE PINS ARE THE CONTRACT. Four relative-duration formatters grew up in four
/// files and describe the same quantity with four roundings; these pins were
/// written against the four ORIGINALS before any of them moved, so the move can
/// be proved to have changed nothing a player reads.
///
/// <para>They also record where the four genuinely DISAGREE - see
/// <see cref="TheThreeGrammarsDisagree_AndThatIsWhyTheyAreThree"/>. A rendering
/// two callers cannot share is not a rung they can share.</para>
/// </summary>
public class DurationsTests
{
  // ---- Span: how long something will take -------------------------------

  [Theory]
  [InlineData(-5, "<1m")]   // clock skew never reads negative
  [InlineData(0, "<1m")]
  [InlineData(59, "<1m")]
  [InlineData(60, "1m")]
  [InlineData(89, "1m")]    // nearest minute, not floor
  [InlineData(90, "2m")]
  [InlineData(20 * 60, "20m")]
  [InlineData(5399, "90m")]
  [InlineData(5400, "1.5h")]
  [InlineData(7200, "2h")]
  [InlineData(200000, "55.6h")]  // no day rung: a span this long is a bug, not a week
  public void Span_SaysHowLongSomethingWillTake(long seconds, string expected)
    => Assert.Equal(expected, Durations.Span(seconds));

  [Fact]
  public void FitCheckShortDur_StillSpeaksThroughItsOwnName()
    => Assert.Equal(Durations.Span(5400), FitCheck.ShortDur(5400));

  // ---- Elapsed: how long ago, bare (the caller says " ago") -------------

  [Theory]
  [InlineData(-5, "0m")]    // clock skew reads as no time at all
  [InlineData(0, "0m")]
  [InlineData(59, "0m")]
  [InlineData(60, "1m")]
  [InlineData(89, "1m")]    // floor, unlike Span
  [InlineData(90, "1m")]
  [InlineData(5399, "89m")]
  [InlineData(5400, "1.5h")]
  [InlineData(7050, "1.9h")]  // Drift's rounding disagreement (08-23): a board the 2h gate
  [InlineData(7200, "2h")]    // passed as fresh must never introduce itself as "2h" old
  [InlineData(129599, "35.9h")] // floor to the tenth - "36h" was the formatter rounding up (08-23)
  [InlineData(129600, "1.5d")]
  [InlineData(200000, "2.3d")]
  public void Elapsed_SaysHowLongAgoInTheCoarsestHonestUnit(long seconds, string expected)
    => Assert.Equal(expected, Durations.Elapsed(seconds));

  [Fact]
  public void RipenessAgeText_StillSpeaksThroughItsOwnName()
    => Assert.Equal(Durations.Elapsed(129600), RipenessSensors.AgeText(129600));

  // ---- Ago: the table grammar, whole units with the suffix baked in ------

  [Theory]
  [InlineData(-5, "just now")]
  [InlineData(0, "just now")]
  [InlineData(59, "just now")]
  [InlineData(60, "1m ago")]
  [InlineData(3599, "59m ago")]
  [InlineData(3600, "1h ago")]
  [InlineData(86399, "23h ago")]
  [InlineData(86400, "1d ago")]
  [InlineData(200000, "2d ago")]
  public void Ago_CoarsensAsItAges(long secondsAgo, string expected)
    => Assert.Equal(expected, Durations.Ago(secondsAgo));

  [Fact]
  public void OnMarketRelative_StillSpeaksThroughItsOwnName()
    => Assert.Equal(Durations.Ago(86400), OnMarket.Relative(86400));

  // ---- The run log's long-form words, off the shared bucketing -----------

  [Theory]
  [InlineData(0, "just now")]
  [InlineData(59, "just now")]
  [InlineData(60, "1 minute old")]
  [InlineData(23 * 60, "23 minutes old")]
  [InlineData(3599, "59 minutes old")]
  [InlineData(3600, "1 hour old")]
  [InlineData(7200, "2 hours old")]
  [InlineData(172799, "47 hours old")]
  [InlineData(172800, "2 days old")]
  [InlineData(259200, "3 days old")]
  public void RunLogAge_KeepsItsAntiAbbreviationWording(long seconds, string expected)
    => Assert.Equal(expected, RunLogVoice.Age(seconds));

  // --- THE FOURTH GRAMMAR: whole-day evidence (folded home 08-22) ---

  [Theory]
  [InlineData(0, "today")]
  [InlineData(-3, "today")]   // a negative day count is not a future; it is today
  [InlineData(1, "1d ago")]
  [InlineData(21, "21d ago")]
  public void DayAge_SaysTodayRatherThanZeroDaysAgo(int days, string expected)
    => Assert.Equal(expected, Durations.DayAge(days));

  [Fact]
  public void DayAge_MovedWithoutChangingAByte()
    // The fold is a MOVE: OnMarket keeps the name the voice layer calls through,
    // and both spellings must render the identical string at every rung.
    => Assert.All(new[] { -1, 0, 1, 2, 45 },
      d => Assert.Equal(Durations.DayAge(d), OnMarket.DayAge(d)));

  [Fact]
  public void DayAge_IsAFourthGrammar_NotACallerOfAgo()
  {
    // Its input is already whole days, so there is no seconds bucketing to share:
    // one day of evidence is "1d ago" here and 86,400 seconds is "1d ago" there,
    // and the two arrive by different roads on purpose.
    Assert.Equal("1d ago", Durations.DayAge(1));
    Assert.Equal("today", Durations.DayAge(0));
    Assert.Equal("just now", Durations.Ago(0));
  }

  /// <summary>
  /// WHY THERE ARE THREE SECONDS-GRAMMARS AND NOT ONE (08-16 polish pass). The unification
  /// brief asked for exactly two renderings; the pins say three, because at these
  /// inputs the three disagree by design and a shared rung would have rewritten a
  /// string a player reads. Recorded here so the next pass measures before merging
  /// rather than arguing from the shapes.
  /// </summary>
  [Fact]
  public void TheThreeGrammarsDisagree_AndThatIsWhyTheyAreThree()
  {
    // The minute rung ends at 90 minutes for the precise pair, at 60 for the coarse one.
    Assert.Equal("66m", Durations.Elapsed(4000));
    Assert.Equal("67m", Durations.Span(4000));
    Assert.Equal("1h ago", Durations.Ago(4000));

    // Within the minute rung: nearest for a span, floor for an age.
    Assert.Equal("50m", Durations.Elapsed(3040));
    Assert.Equal("51m", Durations.Span(3040));

    // The day rung: decimal at 36h, whole at 24h, and never for a span. And the
    // floor-vs-nearest split now reaches the decimals too (08-23): 100000s is
    // 27.77h, and an age must not claim the staler 27.8 a span rounds to.
    Assert.Equal("27.7h", Durations.Elapsed(100000));
    Assert.Equal("1d ago", Durations.Ago(100000));
    Assert.Equal("27.8h", Durations.Span(100000));

    // And the run log holds "hours" a day longer than the table grammar does.
    Assert.Equal("1d ago", Durations.Ago(100000));
    Assert.Equal("27 hours old", RunLogVoice.Age(100000));
  }
}
