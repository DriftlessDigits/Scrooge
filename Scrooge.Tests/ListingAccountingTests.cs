using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The 58M bug: held reprices counted the REJECTED market price toward
/// "gil on market" instead of the price actually still listed.
/// </summary>
public class ListingAccountingTests
{
  [Theory]
  [InlineData(PricingResult.Applied)]
  [InlineData(PricingResult.Listed)]
  [InlineData(PricingResult.Pending)]
  public void AppliedPriceCountsFinalPrice(PricingResult result)
  {
    Assert.Equal(25_000, ListingAccounting.ListedUnitValue(result, finalPrice: 25_000, currentListingPrice: 30_000));
  }

  [Theory]
  [InlineData(PricingResult.LaneHeld)]
  [InlineData(PricingResult.CapBlocked)]
  [InlineData(PricingResult.BelowFloor)]
  [InlineData(PricingResult.NoData)]
  [InlineData(PricingResult.Banned)]
  public void HeldResultCountsWhatIsActuallyListed(PricingResult result)
  {
    // The 58M case: troll wall says 900k, our listing sits at 150k.
    Assert.Equal(150_000, ListingAccounting.ListedUnitValue(result, finalPrice: 900_000, currentListingPrice: 150_000));
  }

  [Theory]
  [InlineData(PricingResult.Skipped)]
  [InlineData(PricingResult.VendorSell)]
  public void SkippedAndVendoredCountNothing(PricingResult result)
  {
    Assert.Equal(0, ListingAccounting.ListedUnitValue(result, finalPrice: 25_000, currentListingPrice: 30_000));
  }

  [Fact]
  public void NoFinalPriceFallsBackToCurrentListing()
  {
    Assert.Equal(30_000, ListingAccounting.ListedUnitValue(PricingResult.Pending, finalPrice: null, currentListingPrice: 30_000));
  }

  [Fact]
  public void NoPricesAtAllCountsNothing()
  {
    Assert.Equal(0, ListingAccounting.ListedUnitValue(PricingResult.Pending, finalPrice: null, currentListingPrice: null));
    Assert.Equal(0, ListingAccounting.ListedUnitValue(PricingResult.LaneHeld, finalPrice: 900_000, currentListingPrice: null));
  }

  [Fact]
  public void ZeroAndNegativePricesCountNothing()
  {
    Assert.Equal(0, ListingAccounting.ListedUnitValue(PricingResult.Applied, finalPrice: 0, currentListingPrice: 0));
    Assert.Equal(0, ListingAccounting.ListedUnitValue(PricingResult.LaneHeld, finalPrice: 900_000, currentListingPrice: -1));
  }

  // ---- "Don't log fake data": the pass that listed nothing books nothing (S7) ----

  /// <summary>
  /// THE OPERANDS AS THEY REALLY ARRIVE. A refused cached post lands on NoData with no
  /// FinalPrice - and with a CurrentListingPrice of 1,890, because the sell panel opens
  /// PRE-FILLED with the game's suggested ask for an item that has never been listed.
  /// NoData is a held result, so the ordinary rule books that suggestion as gil on
  /// market: money the run reports that nobody can buy.
  ///
  /// <para>The old guard for this passed a null listing price, which is a state the
  /// live panel cannot produce - it proved the rule against a case that never happens.
  /// This is the reachable one.</para>
  /// </summary>
  [Fact]
  public void ARefusedCachedPost_BooksNothing_EvenOffAPrefilledPanel()
  {
    Assert.Equal(1_890, ListingAccounting.ListedUnitValue(
      PricingResult.NoData, finalPrice: null, currentListingPrice: 1_890));

    Assert.Equal(0, ListingAccounting.ListedUnitValue(
      PricingResult.NoData, finalPrice: null, currentListingPrice: 1_890, postedNothing: true));
  }

  /// <summary>
  /// Recon's own guard, now the same guard: it runs the whole spine and would have
  /// asked 1,890, then cancels the panel. Everything about the operands says "listing";
  /// the only thing that knows better is which pass ran.
  /// </summary>
  [Fact]
  public void AReconPass_BooksNothing_ThoughItHasAPriceInHand()
  {
    Assert.Equal(0, ListingAccounting.ListedUnitValue(
      PricingResult.Pending, finalPrice: 1_890, currentListingPrice: 1_500, postedNothing: true));
  }

  // ---- SF-P6: the pre-filled panel is not a prior ask (live shake, 2026-08-15) ----

  /// <summary>
  /// THE SAME INTEGER, TWO DIFFERENT FACTS. RetainerSell shows 7 either way: on a pinch
  /// that is the ask standing on the board, on a hawk it is the game's suggestion for
  /// an item still in the bags. Only the pass knows which, so only the pass may say.
  /// </summary>
  [Fact]
  public void ThePreFillIsAnAskOnAPinch_AndASuggestionOnAHawk()
  {
    Assert.Equal(7, ListingAccounting.StandingAsk(7, listingFromBags: false));
    Assert.Null(ListingAccounting.StandingAsk(7, listingFromBags: true));
  }

  /// <summary>
  /// A panel with no number in it is no ask either - every reader downstream tests
  /// <c>&gt; 0</c> before speaking, and null is the shape they all already handle.
  /// </summary>
  [Fact]
  public void AnEmptyPriceBoxIsNoAskAtAll()
  {
    Assert.Null(ListingAccounting.StandingAsk(0, listingFromBags: false));
    Assert.Null(ListingAccounting.StandingAsk(-1, listingFromBags: false));
  }

  /// <summary>
  /// The accounting half of the same fix. A hawk item that ends HELD cancels the panel
  /// and lists nothing, yet NoData/LaneHeld are held results and held results book the
  /// current listing price - so the pre-fill was being reported as gil on market, the
  /// exact shape S7 closed for recon and the refused cached post. Gating the operand at
  /// the source closes it here without a third <c>postedNothing</c> caller.
  /// </summary>
  [Fact]
  public void AHeldHawkItem_BooksNothing_BecauseItsAskWasNeverAnAsk()
  {
    Assert.Equal(0, ListingAccounting.ListedUnitValue(
      PricingResult.LaneHeld, finalPrice: null,
      currentListingPrice: ListingAccounting.StandingAsk(7, listingFromBags: true)));

    Assert.Equal(7, ListingAccounting.ListedUnitValue(
      PricingResult.LaneHeld, finalPrice: null,
      currentListingPrice: ListingAccounting.StandingAsk(7, listingFromBags: false)));
  }

  /// <summary>
  /// A verdict heals flags; a non-evaluation does not. Skipped and Banned were already
  /// excluded on this reasoning (observed, never judged) and the refusal joins them -
  /// it asked no board at all. A lane hold is the contrast case that proves the rule is
  /// about evidence, not about whether a price got written: the pass read a board and
  /// decided against acting, which is exactly what closes a stale flag.
  /// </summary>
  [Theory]
  [InlineData(PricingResult.Skipped, false, false)]
  [InlineData(PricingResult.Banned, false, false)]
  [InlineData(PricingResult.NoData, true, false)]
  [InlineData(PricingResult.NoData, false, true)]
  [InlineData(PricingResult.LaneHeld, false, true)]
  [InlineData(PricingResult.Applied, false, true)]
  public void EvaluatedNess_IsAboutEvidence_NotAboutWhetherAPriceGotWritten(
    PricingResult result, bool refused, bool expected)
  {
    Assert.Equal(expected, ListingAccounting.Evaluated(result, refused));
  }
}
