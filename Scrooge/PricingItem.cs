using System;
using System.Collections.Generic;

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
  /// <summary>
  /// Upward reprice held: the new price exceeds own-sales sanity
  /// (UpwardRepriceMultiplier). Price kept, flagged to triage — a human
  /// priced this listing, a robot must not multiply it on one bad packet.
  /// </summary>
  UpwardHeld,
  /// <summary>
  /// Lane held: history too thin to build a lane (local and community).
  /// Pinch keeps the price and flags; a Hawk run does not auto-list.
  /// Never act on a guess wearing numbers.
  /// </summary>
  LaneHeld,
  /// <summary>Below floor + auto vendor sell enabled — will vendor-sell.</summary>
  VendorSell,
  /// <summary>Item is on the ban list — observed but not repriced.</summary>
  Banned,
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

  /// <summary>When true, cap and undercut price guards are skipped. Set by triage reprice. Also skips the lane decision — the human wins.</summary>
  public bool BypassPriceGuards { get; set; }

  /// <summary>True when the price came from the run cache — the item was lane-decided when first priced this run, so the lane block skips.</summary>
  public bool FromPriceCache { get; set; }

  // --- Market-board await/retry state (transient, per item) ---

  /// <summary>Deadline for the current MB await window. MinValue = window not yet armed.</summary>
  public DateTime MbAwaitDeadline { get; set; } = DateTime.MinValue;

  /// <summary>MB request attempts completed (1 initial + up to 3 retries). Reported in the timeout hold.</summary>
  public int MbAttempts { get; set; }

  /// <summary>True when every MB request attempt elapsed with no response — hold and retry next pinch.</summary>
  public bool MbTimedOut { get; set; }

  /// <summary>True when the local lane was thin and a community history fetch was queued this pass (warm next pinch).</summary>
  public bool CommunityQueued { get; set; }

  /// <summary>The lane decision for this item (null when the lane block didn't run: cache hit, bypass, hotkey).</summary>
  public LaneDecision? Lane { get; set; }

  /// <summary>
  /// Snapshot of the world this pass was judged against (standing listing, sale
  /// count, newest sale, cheapest competitor). Built in the lane block, read at
  /// the flag-raise site so a held flag remembers what it was asked about. Null
  /// when the lane block didn't run.
  /// </summary>
  public TriageMemory.EvidenceSnapshot? LaneEvidence { get; set; }

  /// <summary>
  /// Triage reasons whose rule FIRED this pass (lane_held, slow_evict). Drives
  /// self-heal: on the finally sweep, any open flag on this (item, retainer)
  /// whose reason is NOT here has resolved and closes itself.
  /// </summary>
  public HashSet<string> RaisedFlagReasons { get; } = new();

  /// <summary>Action assigned by the user in the triage window. Used by the orchestrator.</summary>
  public TriageAction QueuedAction { get; set; } = TriageAction.None;
}
