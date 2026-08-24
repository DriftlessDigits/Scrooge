using System.Collections.Generic;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The weekly venture-burn measure. The contract worth pinning: the bracket
/// seed anchors coverage at the full window, so the readout cannot flicker
/// "unmeasured" over weeks of banked history just because the window's left
/// edge landed in a quiet morning (08-06) - while a genuinely young install
/// still fails the gate honestly.
/// </summary>
public class VentureBurnTests
{
  private const long Day = 86_400;
  private const long Now = 1_000_000_000;
  private const long WeekAgo = Now - 7 * Day;

  private static List<(long At, int Tokens)> Rows(params (long At, int Tokens)[] rows) => [.. rows];

  [Fact]
  public void Seeded_AQuietLeftEdge_StillMeasures()
  {
    // The 08-06 shape: 26 days of history, but the oldest IN-WINDOW snapshot
    // is only 6.1d old because nobody played that morning. The seed sits just
    // outside the window and anchors coverage at the full week.
    var rows = Rows((Now - (long)(6.1 * Day), 2_800), (Now - Day, 2_767));
    Assert.Equal(33, VentureBurn.Measure(seedTokens: 2_800, rows, Now, WeekAgo));
  }

  [Fact]
  public void Unseeded_AYoungInstall_HonestlySaysUnmeasured()
  {
    // No look at or before the window opened, and the first in-window look is
    // too recent to claim a week. The gate is the same one as ever.
    var rows = Rows((Now - 6 * Day, 2_800), (Now - Day, 2_767));
    Assert.Null(VentureBurn.Measure(seedTokens: null, rows, Now, WeekAgo));
  }

  [Fact]
  public void Unseeded_EnoughInWindowSpan_StillMeasures()
  {
    // The pre-bracket happy path survives unchanged: 6.5d+ of in-window
    // coverage measures without any seed.
    var rows = Rows((Now - (long)(6.8 * Day), 2_900), (Now - Day, 2_767));
    Assert.Equal(133, VentureBurn.Measure(seedTokens: null, rows, Now, WeekAgo));
  }

  [Fact]
  public void TheSeedToFirstRowDrop_IsCounted()
  {
    // A burn that happened ACROSS the window boundary belongs to this week -
    // the seed is a real prior reading, not just a coverage token.
    var rows = Rows((Now - 5 * Day, 2_700), (Now - Day, 2_650));
    Assert.Equal(150, VentureBurn.Measure(seedTokens: 2_800, rows, Now, WeekAgo));
  }

  [Fact]
  public void Restocks_AreNotNegativeBurn()
  {
    // Buying tokens mid-week must not cancel spend: 100 burned, then a 500
    // restock, then 50 more burned is a 150-token week.
    var rows = Rows(
      (Now - 6 * Day, 2_700),   // -100 from seed
      (Now - 3 * Day, 3_200),   // restock, ignored
      (Now - Day, 3_150));      // -50
    Assert.Equal(150, VentureBurn.Measure(seedTokens: 2_800, rows, Now, WeekAgo));
  }

  [Fact]
  public void NoSnapshotsAtAll_IsUnmeasured_SeededOrNot()
  {
    Assert.Null(VentureBurn.Measure(seedTokens: null, Rows(), Now, WeekAgo));
    // A lone seed with nothing in-window still covers the window (the counter
    // simply never moved that we saw) - burn is honestly zero, not unknown.
    Assert.Equal(0, VentureBurn.Measure(seedTokens: 2_800, Rows(), Now, WeekAgo));
  }
}
