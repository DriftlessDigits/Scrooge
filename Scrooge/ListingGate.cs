namespace Scrooge;

/// <summary>
/// Routing brain Increment 0: the listing gate. Advises AGAINST listing
/// equipment whose better exit is desynth or GC turn-in. Since the era
/// review, the gate is a thin mapping over RoutingRules.Evaluate — the gate
/// tag in the Hawk window and the router's pile are the SAME verdict and can
/// never disagree about an item. Pure functions (config arrives snapshotted
/// on the batch); the only gate-specific behavior is the mapping below.
/// </summary>
internal static class ListingGate
{
  internal enum Verdict
  {
    /// <summary>Not routable (non-equipment, banned, always-vendor) — no opinion.</summary>
    None,
    /// <summary>Evidence says it clears the price x velocity floor — list it.</summary>
    Pass,
    /// <summary>No confident call (never sold, or verdicts too close) — not gated.</summary>
    Unknown,
    /// <summary>Fails the floor with no better exit known — informational only.</summary>
    BelowFloor,
    /// <summary>Better exit: desynth (yield value beats the sale price).</summary>
    GateDesynth,
    /// <summary>Better exit: GC Expert Delivery (low/slow on the MB, has seal value).</summary>
    GateGc,
  }

  internal readonly record struct Result(Verdict Verdict, string Reason)
  {
    /// <summary>Gated = advised off the market. Excluded from Select All, defaults unchecked.</summary>
    public bool IsGated => Verdict is Verdict.GateDesynth or Verdict.GateGc;
  }

  /// <summary>
  /// The equipment listing floor: price x velocity (BP4 Q2). Shared with the
  /// full rules engine so the gate and the pile verdicts can never disagree
  /// about what "worth listing" means. Unknown sit time gets the benefit of
  /// the doubt — evidence only.
  /// </summary>
  internal static bool ClearsEquipmentFloor(int salePrice, int? soldAfterDays,
    double? marketVelocity, RoutingConfig cfg)
    => salePrice >= cfg.ListingFloorGil
       && VelocityAxisClears(soldAfterDays, marketVelocity, cfg);

  /// <summary>
  /// Universalis's home-world velocity (units/day) at or above this rate
  /// means a unit moves within the configured velocity window.
  /// </summary>
  internal static double MarketVelocityFloor(RoutingConfig cfg)
    => 1.0 / System.Math.Max(1, cfg.ListingVelocityDays);

  /// <summary>
  /// The velocity axis: own sit time when captured; else the Universalis
  /// almanac fills the gap; still unknown = benefit of the doubt.
  /// </summary>
  private static bool VelocityAxisClears(int? soldAfterDays, double? marketVelocity,
    RoutingConfig cfg)
  {
    if (soldAfterDays is int days)
      return days <= cfg.ListingVelocityDays;
    if (marketVelocity is double velocity)
      return velocity >= MarketVelocityFloor(cfg);
    return true;
  }

  /// <summary>
  /// Evaluates one item by asking the rules engine and mapping its exit to a
  /// gate verdict. Two deliberate carve-outs: banned/always-vendor items get
  /// no gate opinion (the Hawk window has its own handling for both), and a
  /// never-sold item whose verdict is List maps to Unknown, not Pass — the
  /// locked Universalis design says a healthy market never auto-Passes gear
  /// the player has no price evidence for.
  /// </summary>
  internal static Result Evaluate(RoutingItemInputs item, RoutingBatch batch)
  {
    // Equipment only — the desynth/GC exits are gear exits. Non-gear
    // (mats, consumables) flows through ungated as today.
    if (!item.IsEquipment || item.IsBanned || item.IsAlwaysVendor)
      return new Result(Verdict.None, "");

    var verdict = RoutingRules.Evaluate(item, batch);

    if (verdict.IsReview)
      return new Result(Verdict.Unknown, verdict.Reason);

    return verdict.Exit switch
    {
      RoutingExit.List => item.LastSale is null
        ? new Result(Verdict.Unknown, verdict.Reason)
        : new Result(Verdict.Pass, verdict.Reason),
      RoutingExit.Desynth => new Result(Verdict.GateDesynth, verdict.Reason),
      RoutingExit.Gc => new Result(Verdict.GateGc, verdict.Reason),
      RoutingExit.Vendor => new Result(Verdict.BelowFloor, verdict.Reason),
      // Ban/Hold are unreachable here (guarded above; the gate context
      // passes no protections) — defensively: no opinion.
      _ => new Result(Verdict.None, ""),
    };
  }
}
