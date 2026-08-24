namespace Scrooge;

/// <summary>
/// Determines the minimum price floor when listing items.
/// Items priced below the floor are skipped during auto-pinch.
/// </summary>
public enum PriceFloorMode
{
  /// <summary>No price floor — list at any price.</summary>
  None,
  /// <summary>Skip if undercut price falls below vendor sell price (Item.PriceLow).</summary>
  Vendor,
  /// <summary>Skip if undercut price falls below 2x vendor sell price (Doman Enclave rate).</summary>
  DomanEnclave
}

/// <summary>
/// WHICH FLOOR ACTUALLY BOUND (the one floor law, ruled 2026-08-21). The effective
/// floor is a max() of two rules, and a verdict that cannot name which one it lost to
/// sends the player to the wrong knob.
/// </summary>
internal enum FloorBinding
{
  /// <summary>No floor at all - nothing to clear.</summary>
  None,
  /// <summary>The player's own <c>MinimumListingPrice</c> bound.</summary>
  PlayerMinimum,
  /// <summary>The vendor's counter price bound.</summary>
  Vendor,
  /// <summary>Twice the vendor's counter - the Doman Enclave rate - bound.</summary>
  DomanEnclave,
}

/// <summary>
/// The one number a legal ask has to clear, and the rule that set it.
/// <c>0</c> means "no floor", the convention every caller has always read.
/// </summary>
internal readonly record struct EffectiveFloor(long Floor, FloorBinding Binding)
{
  /// <summary>
  /// THE ONE VERDICT. An honest ask - priced off the full lane, never clamped - is
  /// legal iff it clears this. Fail-loud by omission: nothing here rounds, nudges or
  /// raises the ask, because a clamped ask is a price nobody decided.
  /// </summary>
  internal bool Refuses(long honestAsk) => Floor > 0 && honestAsk < Floor;
}

/// <summary>
/// THE VENDOR FLOOR, once (review pricing item 1, 2026-08-16).
///
/// <para>"Don't list below what the vendor would pay" is one rule, and it was spelled
/// four times: twice inside <c>ItemPricingPipeline</c> (the cached-post re-check and
/// the lane's own guard), once in <c>MarketBoardHandler</c>'s first-pass sentinel
/// conversion, and once in the Ledger's relist preview. Four spellings of one rule is
/// four places for the Doman doubling to drift, and the preview drifting from the pinch
/// is exactly the two-compositions disease <c>LaneEvaluation</c> exists to prevent -
/// the player would be shown a relist target the pinch would then refuse.</para>
///
/// <para>Arithmetic only: no config read, no sheet read, no game. The caller brings the
/// mode and the item's Lumina <c>PriceLow</c>; this answers what the floor is. Zero
/// means "no floor" and every caller already reads it that way (<c>floor > 0 &amp;&amp;
/// price &lt; floor</c>), so a vendor price of zero and <see cref="PriceFloorMode.None"/>
/// stay the same answer they have always been.</para>
/// </summary>
internal static class PriceFloor
{
  /// <summary>
  /// The floor in gil for one item under one mode. <c>0</c> = no floor.
  /// Doman Enclave pays twice vendor, so its floor is twice vendor.
  /// </summary>
  internal static long For(PriceFloorMode mode, long vendorPrice) => mode switch
  {
    PriceFloorMode.None => 0L,
    PriceFloorMode.DomanEnclave => vendorPrice * 2,
    _ => vendorPrice,
  };

  /// <summary>
  /// THE ONE FLOOR LAW (ruled 2026-08-21). A listing is legal only if the HONEST ask
  /// clears <c>max(MinimumListingPrice, For(mode, vendorPrice))</c> - one calculation
  /// point, one verdict species, and the binding rule carried out with the number so
  /// the narration can name it.
  ///
  /// <para>It replaces two checks that lived at different points of the pipeline with
  /// different failure behaviours: the mode floor rejected, the minimum rejected
  /// somewhere else, and three narration variants argued about which one a player had
  /// actually hit. Two rules that both answer "may this ask exist" are one rule.</para>
  ///
  /// <para>A tie goes to the MODE floor: the vendor and the Enclave are facts about the
  /// world, and the minimum is a preference sitting at the same number. Nothing
  /// downstream branches on the binding, so the choice only decides which words the
  /// player reads - and "the vendor pays this much" is the sentence that teaches.</para>
  /// </summary>
  /// <param name="vendorPrice">The item's Lumina <c>PriceLow</c>, or null when unknown.</param>
  /// <param name="minimumListingPrice">The configured minimum; <c>0</c> = disabled.</param>
  internal static EffectiveFloor Effective(PriceFloorMode mode, long? vendorPrice, int minimumListingPrice)
  {
    var modeFloor = vendorPrice is long v && v > 0 ? For(mode, v) : 0L;
    var minimum = minimumListingPrice > 0 ? minimumListingPrice : 0L;

    if (modeFloor <= 0 && minimum <= 0)
      return new EffectiveFloor(0L, FloorBinding.None);

    if (modeFloor >= minimum)
      return new EffectiveFloor(modeFloor, mode == PriceFloorMode.DomanEnclave
        ? FloorBinding.DomanEnclave
        : FloorBinding.Vendor);

    return new EffectiveFloor(minimum, FloorBinding.PlayerMinimum);
  }
}
