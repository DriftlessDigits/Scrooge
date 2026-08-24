using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The card's memory (08-02): the router's reason states its claim; the memoir
/// states what the book knows. The load-bearing receipt is the Hose: "List:
/// sold at 20,803" alone reads confident - beside "1 sale observed, the
/// latest 36d ago" and "your last ask stood 21d at 51,714 and never sold" the
/// same claim reads as the argument it actually is.
/// </summary>
public class RowMemoirTests
{
  [Fact]
  public void TheHoseCard_SaysTheTapeIsThin_AndTheTrialFailed()
  {
    var lines = RowMemoir.Lines(new RowMemoir.MemoirFacts(
      TapeSales: 1, LatestSaleDaysAgo: 36,
      TrialPrice: 51_714, TrialDaysStood: 21, TrialState: "never_cleared",
      TrialTimeToClearDays: null, LastRuling: "melt"));

    Assert.Equal(3, lines.Count);
    Assert.Equal("1 sale observed, the latest 36d ago.", lines[0]);
    // never_cleared is written for a pull AND for an evict (CloseReceiptsNeverCleared),
    // so the line states the finding both share rather than naming the player's hand.
    Assert.Equal("Your last ask stood for 21d at 51,714 and came off the board unsold.", lines[1]);
    Assert.Equal("You last ruled this: melt.", lines[2]);
  }

  [Fact]
  public void ASilentBook_ProducesNoLines_NeverAHedge()
  {
    var lines = RowMemoir.Lines(new RowMemoir.MemoirFacts(
      TapeSales: 0, LatestSaleDaysAgo: null,
      TrialPrice: null, TrialDaysStood: null, TrialState: "",
      TrialTimeToClearDays: null, LastRuling: ""));
    Assert.Empty(lines);
  }

  [Fact]
  public void AClearedTrial_SpeaksItsSpeed()
  {
    var lines = RowMemoir.Lines(new RowMemoir.MemoirFacts(
      TapeSales: 14, LatestSaleDaysAgo: 2,
      TrialPrice: 4_800, TrialDaysStood: 3, TrialState: "cleared",
      TrialTimeToClearDays: 3, LastRuling: ""));

    Assert.Equal("14 sales observed, the latest 2d ago.", lines[0]);
    Assert.Equal("Your last ask at 4,800 sold in 3d.", lines[1]);
  }

  [Fact]
  public void SameDayFacts_ReadAsToday_NeverZeroDays()
  {
    var lines = RowMemoir.Lines(new RowMemoir.MemoirFacts(
      TapeSales: 2, LatestSaleDaysAgo: 0,
      TrialPrice: 1_000, TrialDaysStood: 0, TrialState: "open",
      TrialTimeToClearDays: null, LastRuling: "", IsStanding: true));

    Assert.Equal("2 sales observed, the latest today.", lines[0]);
    Assert.Equal("You have a standing ask at 1,000 (listed today).", lines[1]);
  }

  [Fact]
  public void AnOpenTrialOnABagRow_DoesNotClaimTheRowIsListed()
  {
    // Pen 7, the memoir half of the same seam. The trial receipt is keyed to the
    // VARIANT, so an open ask can exist while the copy in front of the reader sits
    // in his bags - and "you have a standing ask" told him it was on the board.
    var lines = RowMemoir.Lines(new RowMemoir.MemoirFacts(
      TapeSales: 0, LatestSaleDaysAgo: null,
      TrialPrice: 1_000, TrialDaysStood: 3, TrialState: "open",
      TrialTimeToClearDays: null, LastRuling: ""));

    Assert.Equal(
      "An ask at 1,000 is still open for this item (3d on the board) - this one is in your bags.",
      Assert.Single(lines));
  }

  [Fact]
  public void AGoneUnobservedTrial_SaysNobodyWatched()
  {
    var lines = RowMemoir.Lines(new RowMemoir.MemoirFacts(
      TapeSales: 0, LatestSaleDaysAgo: null,
      TrialPrice: 2_500, TrialDaysStood: 9, TrialState: "gone_unobserved",
      TrialTimeToClearDays: null, LastRuling: ""));

    Assert.Single(lines);
    Assert.Equal("Your last ask at 2,500 left the board while nobody watched (stood for 9d).", lines[0]);
  }

  [Fact]
  public void NeverCleared_DoesNotBlameThePlayerForAnEvict()
  {
    // One state, two acts: the player pulled it, or the retainer's slot gave it up.
    // The line may not claim the first over the second.
    var lines = RowMemoir.Lines(new RowMemoir.MemoirFacts(
      TapeSales: 0, LatestSaleDaysAgo: null,
      TrialPrice: 9_000, TrialDaysStood: 4, TrialState: "never_cleared",
      TrialTimeToClearDays: null, LastRuling: ""));

    Assert.Single(lines);
    Assert.DoesNotContain("you pulled", lines[0]);
    Assert.Contains("came off the board unsold", lines[0]);
  }

  [Fact]
  public void NoShopJargonInTheMemoir()
  {
    var lines = RowMemoir.Lines(new RowMemoir.MemoirFacts(
      TapeSales: 5, LatestSaleDaysAgo: 12,
      TrialPrice: 51_714, TrialDaysStood: 21, TrialState: "never_cleared",
      TrialTimeToClearDays: null, LastRuling: "gc"));

    foreach (var line in lines)
    {
      Assert.DoesNotContain("receipt", line);        // the taxonomy stays in code
      Assert.DoesNotContain("outcome_state", line);
      Assert.DoesNotContain("lane", line);
      Assert.DoesNotContain("—", line);              // the app speaks ' - '
    }
  }
}
