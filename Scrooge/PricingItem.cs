namespace Scrooge;

/// <summary>User-assigned action for a triage item.</summary>
public enum TriageAction
{
  /// <summary>No action selected — item stays in the list.</summary>
  None,
  /// <summary>Pull from MB and vendor-sell to NPC.</summary>
  Vendor,
  /// <summary>Pull from MB back to inventory (no vendor).</summary>
  Pull,
  /// <summary>Reprice on the MB with price guards bypassed.</summary>
  Reprice,
}

/// <summary>
/// The result of evaluating a single item's price during a run.
/// Replaces sentinel values (-1, -2, -3) and boolean flag side-channels
/// (VendorSellPending, _needsHistoryFetch, ItemWasListed).
/// </summary>
public enum PricingResult
{
  /// <summary>Not yet processed.</summary>
  Pending,
  /// <summary>Price was set successfully (pinch run — existing listing repriced).</summary>
  Applied,
  /// <summary>New listing placed successfully (hawk run).</summary>
  Listed,
  /// <summary>Item skipped (mannequin, or other non-priceable state).</summary>
  Skipped,
  /// <summary>MB price below vendor/doman enclave floor (was sentinel -2).</summary>
  BelowFloor,
  /// <summary>MB price below configured minimum listing price (was sentinel -3).</summary>
  BelowMinimum,
  /// <summary>No MB listings found (was sentinel -1).</summary>
  NoData,
  /// <summary>Price increase exceeds MaxPriceIncreasePercentage cap.</summary>
  CapBlocked,
  /// <summary>Undercut exceeds MaxUndercutPercentage threshold.</summary>
  UndercutTooDeep,
  /// <summary>Below floor + auto vendor sell enabled — will vendor-sell.</summary>
  VendorSell,
}

/// <summary>
/// Represents a single item being evaluated during a pinch or hawk run.
/// Holds all identity, pricing, and result state for the item.
/// Replaces scattered fields across ItemPricingPipeline, MarketBoardHandler,
/// and per-item state that was previously reconstructed from UI scraping.
/// </summary>
internal class PricingItem
{
  // --- Identity (populated when item enters the pipeline) ---

  /// <summary>Position in the retainer sell list (0-based). Used for context menu targeting.</summary>
  public int SlotIndex { get; init; }

  /// <summary>Game item ID (base ID, no HQ offset).</summary>
  public uint ItemId { get; set; }

  /// <summary>Display name from the game addon (cleaned, no SeString control chars).</summary>
  public string ItemName { get; set; } = "";

  /// <summary>Whether this is a high-quality item.</summary>
  public bool IsHq { get; set; }

  /// <summary>Stack size of the listing.</summary>
  public int Quantity { get; set; }

  /// <summary>Name of the retainer this item is listed on.</summary>
  public string RetainerName { get; set; } = "";

  // --- Prices (populated as discovered during the pipeline) ---

  /// <summary>Current listing price on the retainer (what we have it listed at).</summary>
  public int? CurrentListingPrice { get; set; }

  /// <summary>Calculated MB undercut price (from MarketBoardHandler). Null if not yet received.</summary>
  public int? MbPrice { get; set; }

  /// <summary>Median price from sale history (populated only when outlier triggers history fetch).</summary>
  public int? HistoryPrice { get; set; }

  /// <summary>Number of sales in the last 14 days from sale history.</summary>
  public int HistorySaleCount { get; set; }

  /// <summary>Median sale price from the last 14 days.</summary>
  public int? HistoryMedianPrice { get; set; }

  /// <summary>Average sale price from the last 14 days.</summary>
  public int? HistoryAvgPrice { get; set; }

  /// <summary>NPC vendor sell price from Lumina (Item.PriceLow).</summary>
  public int VendorPrice { get; set; }

  // --- Result ---

  /// <summary>Outcome of the pricing evaluation. Set by the pipeline, read by orchestrators.</summary>
  public PricingResult Result { get; set; } = PricingResult.Pending;

  /// <summary>Price change percentage (old → new). Populated for CapBlocked and UndercutTooDeep results.</summary>
  public float? PriceChangePercent { get; set; }

  /// <summary>The final price that was applied (if Result is Applied or Listed).</summary>
  public int? FinalPrice { get; set; }

  /// <summary>When true, cap and undercut price guards are skipped. Set by triage reprice.</summary>
  public bool BypassPriceGuards { get; set; }

  /// <summary>Action assigned by the user in the triage window. Used by the orchestrator.</summary>
  public TriageAction QueuedAction { get; set; } = TriageAction.None;
}
