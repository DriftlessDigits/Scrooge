namespace Scrooge;

/// <summary>
/// Run-summary accounting: what one processed listing contributes to the
/// "gil on market" total. Pure — linked into Scrooge.Tests.
///
/// The rule: a HELD reprice (cap block, upward hold, below floor...) keeps
/// the OLD price on the market board, so the item counts at its current
/// listing price. Counting the rejected FinalPrice books a troll wall's
/// asking price as if it were ours (the 58M summary bug). Only an applied
/// price counts as FinalPrice.
/// </summary>
internal static class ListingAccounting
{
  /// <summary>
  /// Per-unit value this item leaves listed on the market after the run.
  /// 0 = nothing listed (skipped, vendored, or no known price).
  /// </summary>
  internal static int ListedUnitValue(PricingResult result, int? finalPrice, int? currentListingPrice)
  {
    if (result is PricingResult.Skipped or PricingResult.VendorSell)
      return 0;

    var current = currentListingPrice is int c && c > 0 ? c : 0;
    if (IsHeld(result))
      return current;

    return finalPrice is int applied && applied > 0 ? applied : current;
  }

  /// <summary>
  /// Results where the run did NOT apply a new price — the listing stays on
  /// the market at its old price. Mirrors RunData.IsTriageResult plus Banned
  /// (observed, never repriced).
  /// </summary>
  internal static bool IsHeld(PricingResult result) => result switch
  {
    PricingResult.BelowFloor => true,
    PricingResult.BelowMinimum => true,
    PricingResult.CapBlocked => true,
    PricingResult.UndercutTooDeep => true,
    PricingResult.UpwardHeld => true,
    PricingResult.NoData => true,
    PricingResult.Banned => true,
    _ => false,
  };
}
