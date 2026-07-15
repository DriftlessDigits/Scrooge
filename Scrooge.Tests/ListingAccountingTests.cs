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
  [InlineData(PricingResult.UpwardHeld)]
  [InlineData(PricingResult.LaneHeld)]
  [InlineData(PricingResult.CapBlocked)]
  [InlineData(PricingResult.UndercutTooDeep)]
  [InlineData(PricingResult.BelowFloor)]
  [InlineData(PricingResult.BelowMinimum)]
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
    Assert.Equal(0, ListingAccounting.ListedUnitValue(PricingResult.UpwardHeld, finalPrice: 900_000, currentListingPrice: null));
  }

  [Fact]
  public void ZeroAndNegativePricesCountNothing()
  {
    Assert.Equal(0, ListingAccounting.ListedUnitValue(PricingResult.Applied, finalPrice: 0, currentListingPrice: 0));
    Assert.Equal(0, ListingAccounting.ListedUnitValue(PricingResult.UpwardHeld, finalPrice: 900_000, currentListingPrice: -1));
  }
}
