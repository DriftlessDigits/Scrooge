using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// Pins for the SimpleTweaks-mirror skillup cascade (ExtendedDesynthesisWindow):
/// capped skill is Green regardless of item, equality is Red (strictly greater
/// leaves Red), and the +50 band is Yellow. The reference compares the RAW
/// float skill, so the fractional band above an item level is Yellow, not Red.
/// </summary>
public class DesynthSkillupTests
{
  private const int Max = 500;

  [Fact]
  public void Classify_ACappedSkillIsGreenRegardlessOfTheItem()
  {
    // The reference's first branch: at the ladder's top there is no skillup
    // left to grant, even on an item the +50 band would call Yellow.
    Assert.Equal(DesynthSkillupColor.Green, DesynthSkillup.Classify(Max, 499, Max));
    Assert.Equal(DesynthSkillupColor.Green, DesynthSkillup.Classify(Max, 1, Max));
    Assert.Equal(DesynthSkillupColor.Green, DesynthSkillup.Classify(Max + 12.5f, 499, Max));
  }

  [Fact]
  public void Classify_JustUnderTheCapStillGrades()
  {
    Assert.Equal(DesynthSkillupColor.Yellow, DesynthSkillup.Classify(499.9f, 460, Max));
    Assert.Equal(DesynthSkillupColor.Red, DesynthSkillup.Classify(499.9f, 500, Max));
  }

  [Fact]
  public void Classify_EqualityIsRed()
  {
    // The reference requires STRICTLY greater to leave Red; skill exactly at
    // the item level still skills up (and still fails a lot).
    Assert.Equal(DesynthSkillupColor.Red, DesynthSkillup.Classify(118f, 118, Max));
  }

  [Fact]
  public void Classify_TheFractionalBandAboveAnItemIsYellowNotRed()
  {
    // 118.01 raw skill is past a 118 item — the truncated-int compare would
    // have called this Red for the whole [118, 119) band.
    Assert.Equal(DesynthSkillupColor.Yellow, DesynthSkillup.Classify(118.01f, 118, Max));
    Assert.Equal(DesynthSkillupColor.Yellow, DesynthSkillup.Classify(118.9f, 118, Max));
  }

  [Fact]
  public void Classify_BelowTheItemIsRed()
  {
    Assert.Equal(DesynthSkillupColor.Red, DesynthSkillup.Classify(117.9f, 118, Max));
    Assert.Equal(DesynthSkillupColor.Red, DesynthSkillup.Classify(0f, 118, Max));
  }

  [Fact]
  public void Classify_TheYellowBandEndsAtItemPlusFifty()
  {
    Assert.Equal(DesynthSkillupColor.Yellow, DesynthSkillup.Classify(167.99f, 118, Max));
    Assert.Equal(DesynthSkillupColor.Green, DesynthSkillup.Classify(168f, 118, Max));
  }

  [Fact]
  public void Classify_AnUnknownLadderTopNeverPaintsTheCapGreen()
  {
    // maxLevel 0 = the sheet read failed; the cap branch must not treat every
    // skill as capped. The bands still grade.
    Assert.Equal(DesynthSkillupColor.Yellow, DesynthSkillup.Classify(100f, 60, 0));
    Assert.Equal(DesynthSkillupColor.Red, DesynthSkillup.Classify(50f, 60, 0));
  }

  [Fact]
  public void IsSkillupEligible_RedAndYellowOnly()
  {
    Assert.True(DesynthSkillup.IsSkillupEligible(DesynthSkillupColor.Red));
    Assert.True(DesynthSkillup.IsSkillupEligible(DesynthSkillupColor.Yellow));
    Assert.False(DesynthSkillup.IsSkillupEligible(DesynthSkillupColor.Green));
  }
}
