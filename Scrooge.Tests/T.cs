namespace Scrooge.Tests;

/// <summary>
/// Input factories. Defaults describe the least interesting item alive:
/// marketable equipment, no evidence of anything, unvendorable — each test
/// states only the facts that matter to it.
/// </summary>
internal static class T
{
  /// <summary>Default config: review band 15%, venture bands 1250/750/500.</summary>
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
    int? saleAgeDays = null,
    long? melt = null,
    MeltPrior? meltPrior = null,
    int? seals = null,
    double? velocity = null,
    long? communityMedian = null,
    int communityCount = 0,
    long? tapeMedian = null,
    int tapeCount = 0,
    double? tapeAgeDays = null,
    long? lookAsk = null,
    double? lookAgeHours = null,
    bool equipment = true,
    bool marketable = true,
    bool? desynthable = null,
    bool skillup = false,
    bool redSkillup = false,
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
    // Desynth eligibility mirrors production: melt evidence or a skillup color
    // can only exist for a desynthable item, so default it true whenever those
    // are present unless a test states otherwise.
    IsDesynthable = desynthable ?? (melt is not null || meltPrior is not null || skillup || redSkillup),
    VendorPrice = vendor,
    LastSale = sale,
    LastSaleAgeDays = saleAgeDays,
    MeltValuePerAttempt = melt,
    MeltBandPrior = meltPrior,
    SealValue = seals,
    MarketVelocity = velocity,
    LocalTapeMedian = tapeMedian,
    LocalTapeSampleCount = tapeCount,
    LocalTapeAgeDays = tapeAgeDays,
    LookAsk = lookAsk,
    LookAgeHours = lookAgeHours,
    CommunityMedian = communityMedian,
    CommunitySampleCount = communityCount,
    DesynthColor = redSkillup ? DesynthSkillupColor.Red : skillup ? DesynthSkillupColor.Yellow : null,
    DesynthSkillupEligible = skillup || redSkillup,
    IsBanned = banned,
    IsAlwaysVendor = alwaysVendor,
    IsProtected = isProtected,
    ProtectionReason = protection,
  };
}
