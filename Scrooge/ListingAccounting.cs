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
///
/// <para><see cref="StandingAsk"/> lives here too, because it is the same fact one
/// step earlier: this file already knows the sell panel opens PRE-FILLED, and the
/// operand that trap poisons is the very one <see cref="ListedUnitValue"/> counts.
/// Splitting the rule from its accounting is how one of them drifts.</para>
/// </summary>
internal static class ListingAccounting
{
  /// <summary>
  /// THE PANEL'S PRE-FILL IS NOT AN ASK (SF-P6, live shake 2026-08-15). RetainerSell
  /// opens with a number already in the price box, and what that number MEANS depends
  /// entirely on which pass opened it. On a pinch it is the item's real standing ask -
  /// the thing being repriced. On a hawk the panel was opened over a BAG slot: the
  /// number is the game's suggestion for an item that has never been listed, so there
  /// is no prior ask and no prior seat behind it.
  ///
  /// <para>Adopting it anyway is how the run log came to say "Reprice: From 7 to 395,
  /// dropping you to 2nd" about a listing nobody had ever posted - a verb, a
  /// transition and a move, all off an operand that never existed. Null is the whole
  /// fix, because every surface downstream already knows what an item with no standing
  /// ask looks like: <see cref="RunLogVoice.Priced"/> says "List: Posted at...",
  /// <see cref="RunLogVoice.Skip"/> says "Nothing posted", the lane counts no prior
  /// seat, and <see cref="ListedUnitValue"/> books no gil on a market the item is not
  /// on.</para>
  ///
  /// <para>The math loses nothing real. The cut and cap guards that price AGAINST a
  /// standing ask are the pinch's alone, and they read the panel directly; the hawk
  /// path writes its lane price and never consults an old one.</para>
  /// </summary>
  /// <param name="listingFromBags">
  /// This pass is PUTTING UP a listing (a hawk), not repricing one that is already
  /// standing. The caller knows which pass it ran; this refuses to guess from the
  /// numbers - a suggestion of 7 and a real ask of 7 are the same integer.
  /// </param>
  internal static int? StandingAsk(int panelAsk, bool listingFromBags)
    => listingFromBags || panelAsk <= 0 ? null : panelAsk;

  /// <summary>
  /// Per-unit value this item leaves listed on the market after the run.
  /// 0 = nothing listed (skipped, vendored, or no known price).
  /// </summary>
  /// <param name="postedNothing">
  /// THE PASS PUT NOTHING ON THE BOARD, whatever its operands say (review ruling S7,
  /// 2026-08-12 - "don't log fake data"). Two passes reach this method having listed
  /// nothing at all: a recon item (read, banked, cancelled) and a REFUSED cached post
  /// (the banked plan met a panel it was not built for). Both arrive with operands
  /// that look perfectly ordinary, and that is the trap - the sell panel opens
  /// PRE-FILLED with the game's suggested ask, so <paramref name="currentListingPrice"/>
  /// is a real number for an item that has never been on the market. A refusal lands on
  /// <see cref="PricingResult.NoData"/>, which is a HELD result, and held results book
  /// the current listing price: the run summary would report gil on market that nobody
  /// can buy and the receipt true-up would correct a record to a listing that does not
  /// exist. The caller knows which pass it ran; this refuses to guess from the numbers.
  /// </param>
  internal static int ListedUnitValue(
    PricingResult result, int? finalPrice, int? currentListingPrice, bool postedNothing = false)
  {
    if (postedNothing)
      return 0;

    if (result is PricingResult.Skipped or PricingResult.VendorSell)
      return 0;

    var current = currentListingPrice is int c && c > 0 ? c : 0;
    if (IsHeld(result))
      return current;

    return finalPrice is int applied && applied > 0 ? applied : current;
  }

  /// <summary>
  /// Results where the run did NOT apply a new price — the listing stays on
  /// the market at its old price. Mirrors RunData.IsStandingResult plus Banned
  /// (observed, never repriced).
  /// </summary>
  internal static bool IsHeld(PricingResult result) => result switch
  {
    PricingResult.BelowFloor => true,
    PricingResult.CapBlocked => true,
    PricingResult.UndercutTooDeep => true,
    PricingResult.LaneHeld => true,
    PricingResult.NoData => true,
    PricingResult.Banned => true,
    _ => false,
  };

  /// <summary>
  /// DID THIS PASS ACTUALLY EVALUATE THE ITEM? (review ruling S7, 2026-08-12.)
  ///
  /// <para>The pricing pass ends by self-healing the item's open standing flags - a
  /// flag whose rule did not re-fire this time round has resolved, so close it. That
  /// is only true of an item the pass looked at. Two results were already excluded on
  /// exactly this reasoning (a Skipped mannequin/bound row and a Banned one are
  /// observed, never judged), and a REFUSED cached post belongs with them: it asked no
  /// board, ran no spine, and reached no verdict about anything. Closing a flag off it
  /// would say "we checked, the problem is gone" about an item nobody checked - the
  /// same fabricated-evidence shape as booking its pre-filled ask as gil on market.</para>
  ///
  /// <para>Note what is NOT here: a lane hold, a below-floor rejection, a cap block.
  /// Those are verdicts - the pass read a board and decided against acting - and a
  /// verdict is precisely what heals a flag.</para>
  /// </summary>
  internal static bool Evaluated(PricingResult result, bool cachedPostRefused)
    => !cachedPostRefused
       && result != PricingResult.Skipped
       && result != PricingResult.Banned;
}
