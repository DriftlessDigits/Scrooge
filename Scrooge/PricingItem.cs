using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>User-assigned action for a triage item.</summary>
public enum StandingAction
{
  /// <summary>No action selected — item stays in the list.</summary>
  None,
  /// <summary>Pull from MB and vendor-sell to NPC.</summary>
  Vendor,
  /// <summary>Pull from MB back to inventory (no vendor).</summary>
  Pull,
  /// <summary>Reprice on the MB with price guards bypassed.</summary>
  Reprice,
  /// <summary>
  /// Pull from MB, then melt - the pull happens at the retainer, the melt at
  /// the NEXT desynth stop, and the ruling persists the retainer->bag crossing
  /// (walk ruling 2, 08-02: the router never re-asks an answered question).
  /// </summary>
  Melt,
  /// <summary>Pull from MB, then GC turn-in - same contract as Melt.</summary>
  Gc,
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
  /// <summary>
  /// THE ONE FLOOR VERDICT (ruled 2026-08-21): the honest ask lands under the
  /// effective floor - <c>max(MinimumListingPrice, PriceFloor.For(mode, vendor))</c> -
  /// so no legal listing exists for this item at this price.
  ///
  /// <para>BelowMinimum was folded in here. It was the same verdict reached by the
  /// other of the two floor rules, and carrying two members meant every consumer had
  /// to remember to handle both - which four of them did not. Persisted rows carrying
  /// the legacy string fold to this on read, the way "upward_held" folds to LaneHeld.</para>
  /// </summary>
  BelowFloor,
  /// <summary>No MB listings found (was sentinel -1).</summary>
  NoData,
  /// <summary>Price increase exceeds MaxPriceIncreasePercentage cap. No longer
  /// produced since clamp-and-climb (07-26) - the cap steps instead of skipping.
  /// Kept for persisted rows that carry it.</summary>
  CapBlocked,
  // UndercutTooDeep is GONE (3.1 sweep). The deep-cut guard was delisted 08-23
  // (inert at its 100 default; the lane owns crasher defense) and its warn row,
  // confirm flow, and this state died with it.
  // UpwardHeld is GONE (cleanup pass). Its own-sales guard was deleted with the
  // lane rewrite on 2026-07-13 and nothing has set the state since; the only way
  // to reach it was rehydrating a legacy persisted flag, and those fold to
  // LaneHeld on read now. The lane ceiling carries the 3x discipline.
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
  /// <summary>
  /// The PLAYER contested his own standing listing from the On Market tab
  /// (walk unit 4, 08-02) - no pinch produced this row, and its reason card
  /// must say so instead of wearing a pricing verdict nobody reached.
  /// </summary>
  PlayerContest,
  /// <summary>
  /// The ROUTER contested a standing ask because melting it measurably pays
  /// more (ruled 2026-08-06): own-ledger desynths of this exact item return more per
  /// attempt than the board is being asked for. Informational - the row lands
  /// in the inbox proposing Melt, and the player answers it like any contest.
  /// </summary>
  MeltBeatsAsk,
  /// <summary>
  /// RECON banked the decision and cancelled the panel (Rounds unit 2). The spine
  /// ran in full and its answer is in <c>decision_cache</c>; no price was written
  /// and nothing was listed.
  ///
  /// <para>Its own verdict rather than a reused one, because every other value here
  /// is a claim about a LISTING - applied, listed, held, skipped - and recon's
  /// listing does not exist. Reporting a reconned item as Listed would put gil on
  /// the run's board total that is not on the market; reporting it as Skipped would
  /// say the pass learned nothing, when learning was the whole errand.</para>
  ///
  /// <para><b>NOTHING BRANCHES ON THIS VALUE, and that is worth saying out loud</b>
  /// (the minors batch, 2026-08-12). It is a LABEL - the item's honest final state,
  /// read by the run log and by anyone debugging a pass. The behaviour it looks like
  /// it guards is guarded elsewhere: recon books no gil because the accounting is
  /// told <c>postedNothing: isReconRun</c>, and recon lists nothing because the recon
  /// tail returns before <c>ApplyPriceDecision</c> can be reached at all. Do not add a
  /// rule that reads this enum and assume it is load-bearing today; wire the fact you
  /// actually mean.</para>
  /// </summary>
  Reconned,
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

  /// <summary>NPC vendor sell price from Lumina (Item.PriceLow).</summary>
  public int VendorPrice { get; set; }

  // --- Result ---

  /// <summary>Outcome of the pricing evaluation. Set by the pipeline, read by orchestrators.</summary>
  public PricingResult Result { get; set; } = PricingResult.Pending;

  /// <summary>Price change percentage (old → new). Populated for CapBlocked results.</summary>
  public float? PriceChangePercent { get; set; }

  /// <summary>The final price that was applied (if Result is Applied or Listed).</summary>
  public int? FinalPrice { get; set; }

  /// <summary>
  /// This decision's receipt row, banked at insert so the applied price can be
  /// TRUED UP once it exists (A12, walk #2): the receipt is written when only
  /// the anchor is known, and the anchor is not the write.
  /// </summary>
  public long? ReceiptId { get; set; }

  // ReceiptLaneMedian lived here until the doctrine sweep (2026-08-15). It was the
  // true-up's denominator for position_in_lane, and that ratio's writer is retired -
  // the true-up now corrects the absolute price and the retainer, neither of which
  // needs a lane.

  /// <summary>
  /// The candidate price a guard REJECTED - kept so the narration can render the
  /// number that actually lost the comparison. Three producers, one meaning:
  /// <list type="bullet">
  ///   <item>floor / minimum on the LANE path - FinalPrice is nulled on rejection,
  ///     and MbPrice is the board read, which can sit far ABOVE the rejected lane
  ///     candidate; rendering it produced the Mossy Stone Daggers lie ("MB/ea at
  ///     121 gil &lt; 21 gil vendor"). Null on the board path, where MbPrice IS
  ///     the compared operand.</item>
  ///   <item>the price-increase CAP - the blocked PROPOSAL, never applied. MbPrice
  ///     is the board read the proposal was built from and can sit far BELOW it;
  ///     rendering it produced "Cap (306 -&gt; 200, 880%)", a raise narrated as a
  ///     cut (07-24).</item>
  /// </list>
  /// (The undercut-too-deep guard was a third producer until the 3.1 sweep.)
  /// One field because it is one question: what number did the guard compare?
  /// </summary>
  public int? RejectedPrice { get; set; }

  /// <summary>
  /// The verdict that sent this item to the vendor, captured BEFORE
  /// <see cref="Result"/> is overwritten with <see cref="PricingResult.VendorSell"/>.
  /// The executor acts on VendorSell; the narrator needs the reason that produced
  /// it, and a narrator reading Result after the switch only ever sees VendorSell -
  /// which is why "Below floor" / "Below minimum" were unreachable and every
  /// auto-vendored item read "Price check failed" (07-24). Null when the item was
  /// not vendored off a failed price check (Always Vendor, or never vendored).
  /// </summary>
  public PricingResult? VendorFallbackFrom { get; set; }

  /// <summary>When true, cap and undercut price guards are skipped. Set by triage reprice. Also skips the lane decision — the human wins.</summary>
  public bool BypassPriceGuards { get; set; }

  // ConfirmedPrice is GONE (3.1 sweep) - the deep-cut confirm flow was its only
  // producer, and the floor-outranks-the-confirm door (08-23) went with it: no
  // confirm can exist to outrank.

  /// <summary>True when the price came from the run cache — the item was lane-decided when first priced this run, so the lane block skips.</summary>
  public bool FromPriceCache { get; set; }

  /// <summary>
  /// THE BANKED DECISION this item is posting from (Rounds unit 3), or null when it
  /// is walking the classic full chain.
  ///
  /// <para>Stamped at ENQUEUE time by the list stage, not read here at price time,
  /// and the ordering is the whole design: the plan decides which TASKS the item
  /// gets, so an item riding the cache never has a Compare Prices click or an await
  /// ladder in its queue at all. "The cached post skips the board" is therefore a
  /// fact about what was scheduled rather than a branch that has to remember to skip
  /// something.</para>
  ///
  /// <para>Distinct from <see cref="FromPriceCache"/>, which is the per-RUN price
  /// memo (the same item name priced twice in one pass). This one crosses runs and
  /// crosses days: it is what a recon stage banked, spent by a list stage that may
  /// be hours behind it.</para>
  /// </summary>
  public CachedPostPlan? CachedPost { get; set; }

  // --- Market-board await/retry state (transient, per item) ---

  /// <summary>Deadline for the current MB await window. MinValue = window not yet armed.</summary>
  public DateTime MbAwaitDeadline { get; set; } = DateTime.MinValue;

  /// <summary>MB request attempts completed (1 initial + up to 3 retries). Reported in the timeout hold.</summary>
  public int MbAttempts { get; set; }

  /// <summary>True when every MB request attempt elapsed with no response — hold and retry next pinch.</summary>
  public bool MbTimedOut { get; set; }

  /// <summary>Board depth when the current MB await window was armed — the pager's progress baseline. -1 = not armed.</summary>
  public int MbDepthAtArm { get; set; } = -1;

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
  public StandingMemory.EvidenceSnapshot? LaneEvidence { get; set; }

  /// <summary>
  /// Standing-listing reasons whose rule FIRED this pass (lane_held, slow_evict). Drives
  /// self-heal: on the finally round, any open flag on this (item, retainer)
  /// whose reason is NOT here has resolved and closes itself.
  /// </summary>
  public HashSet<string> RaisedFlagReasons { get; } = new();

  /// <summary>Action assigned by the user in the triage window. Used by the orchestrator.</summary>
  public StandingAction QueuedAction { get; set; } = StandingAction.None;
}
