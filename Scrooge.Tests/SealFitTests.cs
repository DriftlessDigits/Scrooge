using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE PLAN STOPS PRETENDING (07-26). The live receipt: 52 items to Expert Delivery at
/// ~2,000 seals apiece against a 90,000-seal wallet holding ~1,000. The run halted at
/// 89,105/90,000 with 7 left - correctly - but "Churn: 52" and "-&gt; 52 turn in" had
/// promised the whole pile, and the arithmetic that says otherwise was available before
/// the first item moved.
///
/// <para>These pin the fits-N table, the silence when a pile genuinely fits, and the
/// refusal to invent a cap when the wallet cannot be read.</para>
/// </summary>
public class SealFitTests
{
  private static List<int> Rewards(int count, int each)
    => Enumerable.Repeat(each, count).ToList();

  // ========================================================================
  // Fits-N
  // ========================================================================

  [Fact]
  public void TheLiveReceipt_FiftyTwoItemsAgainstANinetyThousandWallet()
  {
    // 90,000 cap, 1,000 held -> 89,000 of room; 52 rows at 2,000 = 104,000 wanted.
    var fit = SealFit.Assess(1_000, 90_000, Rewards(52, 2_000));

    Assert.NotNull(fit);
    Assert.Equal(52, fit!.Value.Ready);
    Assert.Equal(104_000, fit.Value.ExpectedSeals);
    Assert.Equal(89_000, fit.Value.Headroom);
    Assert.Equal(44, fit.Value.Fits); // 44 x 2,000 = 88,000; the 45th would overflow
    Assert.True(fit.Value.Overflows);
  }

  [Theory]
  // held, cap, rows, each  -> fits
  [InlineData(0u, 10_000u, 5, 2_000, 5)]     // exact fit - the last row lands on the cap
  [InlineData(1u, 10_000u, 5, 2_000, 4)]     // one seal of drift costs the last row
  [InlineData(0u, 10_000u, 20, 2_000, 5)]    // room, not the pile, is the binding number
  [InlineData(9_999u, 10_000u, 3, 2_000, 0)] // not even the cheapest fits
  [InlineData(0u, 0u, 3, 2_000, 0)]          // a zero cap holds nothing
  [InlineData(0u, 100_000u, 3, 2_000, 3)]    // the whole pile fits
  public void FitsN_IsTheLongestPrefixThatClearsTheHeadroom(
    uint held, uint cap, int rows, int each, int expected)
  {
    var fit = SealFit.Assess(held, cap, Rewards(rows, each));
    Assert.NotNull(fit);
    Assert.Equal(expected, fit!.Value.Fits);
  }

  [Fact]
  public void MixedRewards_AreCountedRichestFirst_TheOrderTheyAreHandedOff()
  {
    // 6,000 of room. Richest-first: 5,000 fits, 3,000 does not -> the run halts at 1.
    // (Cheapest-first would have claimed 3 and been wrong the moment the counter
    //  reached the 5,000 row, which is exactly the flattery this exists to stop.)
    var fit = SealFit.Assess(0, 6_000, new List<int> { 1_000, 5_000, 3_000, 2_000 });

    Assert.NotNull(fit);
    Assert.Equal(1, fit!.Value.Fits);
    Assert.Equal(11_000, fit.Value.ExpectedSeals);
  }

  [Fact]
  public void HeldPastTheCap_ReadsAsNoRoomRatherThanNegativeRoom()
  {
    var fit = SealFit.Assess(95_000, 90_000, Rewards(3, 2_000));

    Assert.NotNull(fit);
    Assert.Equal(0, fit!.Value.Headroom);
    Assert.Equal(0, fit.Value.Fits);
    Assert.True(fit.Value.Overflows);
  }

  [Fact]
  public void RowsWithNoResolvableSealValue_AreNotCounted()
  {
    // ExecuteChurn drops them before the handoff, so the plan must not count them.
    var fit = SealFit.Assess(0, 100_000, new List<int> { 2_000, 0, 0, 2_000 });

    Assert.NotNull(fit);
    Assert.Equal(2, fit!.Value.Ready);
    Assert.Equal(4_000, fit.Value.ExpectedSeals);
  }

  // ========================================================================
  // When it says nothing
  // ========================================================================

  [Fact]
  public void AnUnreadableWallet_SaysNothing_RatherThanAssumingNinetyThousand()
  {
    Assert.Null(SealFit.Assess(null, 90_000, Rewards(52, 2_000)));
    Assert.Null(SealFit.Assess(1_000, null, Rewards(52, 2_000)));
    Assert.Null(SealFit.Assess(null, null, Rewards(52, 2_000)));
  }

  [Fact]
  public void NothingWaiting_SaysNothing()
  {
    Assert.Null(SealFit.Assess(1_000, 90_000, new List<int>()));
    Assert.Null(SealFit.Assess(1_000, 90_000, new List<int> { 0, 0 }));
  }

  [Fact]
  public void APileThatFits_CarriesNoNoteAndNoSuffix()
  {
    var fit = SealFit.Assess(0, 100_000, Rewards(5, 2_000));

    Assert.NotNull(fit);
    Assert.False(fit!.Value.Overflows);
    Assert.Equal(string.Empty, fit.Value.Note);
    Assert.Equal(string.Empty, fit.Value.Tag);
    Assert.Equal(string.Empty, fit.Value.PlanSuffix);
  }

  // ========================================================================
  // The wording
  // ========================================================================

  [Fact]
  public void TheNote_NamesTheCountTheSealsTheRoomAndWhereItStops()
  {
    var fit = SealFit.Assess(1_000, 90_000, Rewards(52, 2_000))!.Value;

    Assert.Equal(
      "52 items ready (~104k seals), but your seal wallet only has room for ~89k - about 44 will fit. The run stops there unless you spend some seals first.",
      fit.Note);
    Assert.Equal("  [~44 fit the seal wallet]", fit.Tag);
    Assert.Equal(" (~44 fit)", fit.PlanSuffix);
  }

  [Theory]
  [InlineData(0, "0")]
  [InlineData(900, "900")]
  [InlineData(9_999, "9,999")]
  [InlineData(10_000, "10k")]
  [InlineData(46_500, "46.5k")]
  [InlineData(104_000, "104k")]
  public void SealsRead_TheWayAPlayerSaysThem(long seals, string expected)
    => Assert.Equal(expected, SealFit.Seals(seals));

  // ========================================================================
  // The seam with the halt
  // ========================================================================

  [Fact]
  public void WhatFitsAtPlanTime_IsWhatTheHaltWouldAgreeTo()
  {
    // The plan says N fit; the halt (WalletHalt) re-offers only while the cheapest
    // waiting row still fits. Walk the pile forward and the two must land together.
    const uint cap = 10_000;
    var rewards = Rewards(10, 2_000);
    var fit = SealFit.Assess(0, cap, rewards)!.Value;
    Assert.Equal(5, fit.Fits);

    var held = (uint)(fit.Fits * 2_000);
    Assert.False(WalletHalt.ShouldReoffer(true, RoundStage.TurnIn,
      $"stopped - {WalletHalt.Marker} ({held}/{cap})", held, cap, 2_000));
  }

  // ========================================================================
  // The riders header's line (3b-6, pen 5)
  // ========================================================================

  [Fact]
  public void Line_SpeaksTheTwoNumbersWhetherOrNotThePileOverflows()
  {
    // Note is a WARNING and stays silent on a clean plan. Line is the riders page's one
    // look at the wallet before Continue, so "no warning" is not the same information
    // as the pair - it speaks either way.
    var fits = SealFit.Assess(0, 100_000, Rewards(3, 2_000))!.Value;
    Assert.False(fits.Overflows);
    Assert.Equal("3 turn-ins pay ~6,000 seals; the wallet has room for ~100k.", fits.Line);
    Assert.Equal("", fits.Note);

    var over = SealFit.Assess(0, 10_000, Rewards(10, 2_000))!.Value;
    Assert.True(over.Overflows);
    Assert.Equal(
      "10 turn-ins pay ~20k seals; the wallet has room for ~10k - about 5 fit.",
      over.Line);
  }

  [Fact]
  public void Line_IsSilentWithNothingToTurnIn()
  {
    // A wallet reading on a page with no GC rows is a number about nothing.
    Assert.Equal("", default(SealFit).Line);
  }

  [Fact]
  public void Line_RecitesNoMechanics()
  {
    // The dark-mode rule at this seat: the cap, the curve and the richest-first estimate
    // order are all this type's business and none of them may appear in the sentence.
    var over = SealFit.Assess(0, 10_000, Rewards(10, 2_000))!.Value;
    foreach (var word in new[] { "cap", "curve", "richest", "venture", "config" })
      Assert.DoesNotContain(word, over.Line);
  }
}
