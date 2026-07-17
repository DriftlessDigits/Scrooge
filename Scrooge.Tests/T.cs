namespace Scrooge.Tests;

/// <summary>
/// Input factories. Defaults describe the least interesting item alive:
/// marketable equipment, no evidence of anything, unvendorable — each test
/// states only the facts that matter to it.
/// </summary>
internal static class T
{
  /// <summary>Default config: floor 15k, velocity 10d, worth 5k, band 15%, bands 1250/750/500, mult 3x.</summary>
  public static RoutingConfig Cfg(int? reviewBandPct = null) => new()
  {
    RoutingReviewBandPct = reviewBandPct ?? 15,
  };

  public static RoutingBatch Batch(int? stock = null, int sealRate = 25,
    bool empirical = false, RoutingConfig? cfg = null) => new()
  {
    VentureStock = stock,
    SealToGilRate = sealRate,
    SealRateEmpirical = empirical,
    Rules = cfg ?? new RoutingConfig(),
  };

  public static RoutingItemInputs Gear(
    int vendor = 0,
    (int Price, long Timestamp, int? SoldAfterDays)? sale = null,
    long? melt = null,
    int? seals = null,
    double? velocity = null,
    long? communityMedian = null,
    int communityCount = 0,
    bool equipment = true,
    bool marketable = true,
    bool skillup = false,
    bool banned = false,
    bool alwaysVendor = false,
    bool isProtected = false,
    string protection = "") => new()
  {
    ItemId = 1,
    IsHq = false,
    Name = "Test Gear",
    Ilvl = 100,
    IsEquipment = equipment,
    IsMarketable = marketable,
    VendorPrice = vendor,
    LastSale = sale,
    MeltValuePerAttempt = melt,
    SealValue = seals,
    MarketVelocity = velocity,
    CommunityMedian = communityMedian,
    CommunitySampleCount = communityCount,
    DesynthColor = skillup ? DesynthSkillupColor.Yellow : null,
    DesynthSkillupEligible = skillup,
    IsBanned = banned,
    IsAlwaysVendor = alwaysVendor,
    IsProtected = isProtected,
    ProtectionReason = protection,
  };
}
