using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using System;

namespace Scrooge;

/// <summary>
/// Everything the ending needs, read ONCE at the door of
/// <see cref="ItemPricingPipeline.SetNewPrice"/> and carried here.
///
/// <para><b>Explicit operands, and that is the whole point.</b> This block runs in a
/// <c>finally</c>, which is the one place in the pass that also runs after the exception
/// door has torn a recon run down - so a mode re-read here would answer "neither" and
/// both mode guards below would fall open, booking a recon's would-have-asked price as
/// gil on market on the one path where nothing at all happened (review ruling S9). The
/// mode a pass ran under cannot change mid-pass, so a local is not a cache, it is the
/// fact - and a record parameter is that local made unmissable.</para>
/// </summary>
/// <param name="Result">The item's final result, or Pending when there was no item.</param>
/// <param name="Item">The item under pricing, if there was one (the hotkey door has none).</param>
/// <param name="ItemPayload">Identity as the addon gave it, for the gil tracker.</param>
/// <param name="ListingQuantity">Stack size on the panel — the multiplier on unit value.</param>
/// <param name="IsReconRun">Recon books nothing: it listed nothing.</param>
/// <param name="IsHawkRun">The hawk is excluded from GilTracker's ask accounting.</param>
/// <param name="CachedRefused">A refused cached post read no board and posted nothing.</param>
/// <param name="MbHandler">The board captured this pass, for the market-memory diff.</param>
internal readonly record struct PricingPassOperands(
  PricingResult Result,
  PricingItem? Item,
  ItemPayload? ItemPayload,
  int ListingQuantity,
  bool IsReconRun,
  bool IsHawkRun,
  bool CachedRefused,
  MarketBoardHandler MbHandler);

/// <summary>
/// THE ENDING OF ONE PRICING PASS (review pricing split, 2026-08-16): five independent
/// jobs that run whatever happened above them - applied, held, banned, refused, or
/// thrown - each behind its own guard so no one of them can cost the other four.
///
/// <para>In order: book the listing value (and true the receipt up to it), diff the
/// captured board into market memory, collect a standing-listing item for later review,
/// run the self-heal round on the item's triage flags, and count the item processed.
/// They were five try/catch blocks inside a 117-line <c>finally</c>; they are the same
/// five here, moved verbatim, with the pass's facts arriving as
/// <see cref="PricingPassOperands"/> instead of as field reads.</para>
/// </summary>
internal static class PricingPassEnding
{
  internal static void Run(in PricingPassOperands ops)
  {
    var result = ops.Result;
    var currentItem = ops.Item;
    var isReconRun = ops.IsReconRun;
    var isHawkRun = ops.IsHawkRun;
    var cachedRefused = ops.CachedRefused;
    var itemPayload = ops.ItemPayload;
    var listingQuantity = ops.ListingQuantity;
    var mbHandler = ops.MbHandler;

    // Track listing value for run summary. Held results (upward hold, cap
    // block...) keep the OLD price on the market — count that, never the
    // rejected reprice target (the 58M troll-wall inflation).
    //
    // RECON BOOKS NONE OF IT. The operands would all be there and all be wrong:
    // the sell panel opens pre-filled with the game's suggested ask, so
    // CurrentListingPrice is a real number for an item that has never been on the
    // market, and FinalPrice is what recon WOULD have asked. Running the accounting
    // over those would report gil "on market" that nobody can buy, true-up the
    // receipt's decided_price to a listing that does not exist, and (through
    // GilTracker, which excludes only the hawk) file the whole thing as a real ask.
    // The pass listed nothing; its total is zero.
    //
    // A REFUSED CACHED POST IS THE SAME SENTENCE (review ruling S7, 2026-08-12), and
    // it used to be missing it. The refusal lands on NoData, NoData is a HELD result,
    // and a held result books the CURRENT listing price - which on the bell's own
    // door is the game's pre-filled suggestion for an item that was never listed.
    // So a refusal booked the panel's guess as gil on market, filed it through
    // GilTracker as a real ask, and stood ready to true a receipt up to it. Recon's
    // guard was one door short; this is the same guard on both.
    var listingValue = ListingAccounting.ListedUnitValue(
      result, currentItem?.FinalPrice, currentItem?.CurrentListingPrice,
      postedNothing: isReconRun || cachedRefused);
    if (listingValue > 0)
    {
      Plugin.Ledger.AddListingValue(listingValue * listingQuantity);
      if (!isHawkRun && Plugin.Configuration.EnableGilTracking && itemPayload != null)
        GilTracker.RecordFinalPrice(itemPayload.ItemId, listingValue, listingQuantity);

      // The receipt's decided_price true-up (A12, walk #2): listingValue is
      // already the honest answer to "what ask stands after this decision" -
      // the applied write, or the old price a hold left on the market - which
      // is exactly what decided_price claims to be. The insert could only
      // bank the anchor; this is the write.
      //
      // THE RETAINER IS TRUED UP AT THE SAME MOMENT (review ruling S13, 2026-08-12):
      // "if Karen is selling the item, that is what the data needs to say". A cached
      // post lists against a receipt RECON wrote, and recon stood at whichever
      // retainer had a free slot to borrow a panel from - while the bell picks its
      // own retainer and swaps at 20/20. Every reader of that row keys on the pair:
      // the On Market tab shows the lane by retainer, receipt retention prunes per
      // (item, quality, retainer), and the sale confirm's PASS grade joins on it. A
      // receipt left pointing at Dave while Karen holds the listing shows the row on
      // the wrong shelf and never closes when it sells. Passed on every pass, not
      // just cached ones - on a fresh spine the receipt was inserted with this same
      // name a moment ago, so the correction is a no-op rather than a second path.
      if (currentItem?.ReceiptId is long receiptId)
      {
        try
        {
          GilStorage.UpdateReceiptDecidedPrice(receiptId, listingValue,
            currentItem.RetainerName);
        }
        catch (Exception rex) { Svc.Log.Debug($"[Receipt] decided_price true-up skipped: {rex.Message}"); }
      }
    }

    // Market memory (M4): diff the captured board against the stored snapshot and
    // append events + refresh the snapshot - ONE write path. Runs at the pricing
    // door for EVERY board packet that arrived this pass, even for items the pricer
    // skipped/held (the observation was made, record it - ruling 3). Guarded on a
    // real captured board so cache hits (no MB query) and empty passes no-op.
    // Must run BEFORE ClearHistory wipes the captured board.
    if (Plugin.Configuration.EnableGilTracking && mbHandler.BoardItemId != 0)
    {
      try
      {
        var boardId = mbHandler.BoardItemId;
        var snapshot = mbHandler.GetBoardSnapshot(boardId);
        if (snapshot.Count > 0)
          GilStorage.ApplyBoardScan(boardId, snapshot, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
      }
      catch (Exception ex)
      {
        Svc.Log.Warning($"[MarketMemory] Board diff failed: {ex.Message}");
      }
    }

    mbHandler.ClearHistory(); // history + captured board are per-item

    // Standing-listing collection — save skipped items for post-run review
    if (currentItem != null && RunData.IsStandingResult(result))
      Plugin.CurrentRun?.StandingItems.Add(currentItem);

    // Self-heal (M2): this pass processed the item, so any open flag on its
    // (item, retainer) whose rule did NOT re-fire has resolved — close it.
    // Skipped/Banned items were never really evaluated (mannequin, bound,
    // ban list), so they neither raise nor heal. Dead-producer legacy flags
    // (upward_held/outlier_warn) fall out here: nothing raises them, so the
    // moment their item is pinched they round.
    //
    // A REFUSED CACHED POST JOINS THAT LIST (review ruling S7): it asked no board and
    // ran no spine, so it evaluated nothing and has nothing to declare resolved.
    // Closing a flag off it would tell the player we looked and the problem was gone.
    // The whole predicate lives in ListingAccounting.Evaluated, where it is testable.
    if (currentItem != null && currentItem.ItemId != 0
        && ListingAccounting.Evaluated(result, cachedRefused))
    {
      try
      {
        GilStorage.SelfHealStandingFlags(currentItem.ItemId, currentItem.IsHq,
          currentItem.RetainerName, currentItem.RaisedFlagReasons);
      }
      catch (Exception ex)
      {
        Svc.Log.Warning($"[Standing] Self-heal round failed: {ex.Message}");
      }
    }

    if (result != PricingResult.VendorSell)
      Plugin.Ledger.IncrementProcessed();
  }
}
