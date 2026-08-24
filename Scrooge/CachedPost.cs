using System;

namespace Scrooge;

/// <summary>Which door the list stage's per-item chain walks through.</summary>
internal enum CachedPostRoute
{
  /// <summary>The banked decision rides: set the price, confirm, no board asked.</summary>
  PostCached,
  /// <summary>The classic full chain - delay, Compare Prices, the await ladder, the spine.</summary>
  FullChain,
}

/// <summary>
/// ONE item's answer to "does the act leg pay the four seconds again?".
///
/// <para><see cref="Reason"/> is not decoration. Every FullChain verdict here is a
/// REFUSAL to spend a banked answer, and the refusals are exactly the receipts that
/// will eventually tune <c>ReconFreshHours</c> - "how often did the round re-read
/// something recon had already priced, and why". A route with no reason would make
/// that question unanswerable from the run log.</para>
/// </summary>
/// <para><see cref="ItemId"/> and <see cref="IsHq"/> are the plan's OWN identity, not
/// a convenience: the plan is composed at enqueue time against a bag slot and spent
/// later against whatever the sell panel actually opened on. Those are the same item
/// almost always, and the almost is the problem - the slot guard upstream compares
/// item ids only, so an NQ plan meeting an HQ panel would post the NQ price on the HQ
/// item with no board read to catch it. The plan carries what it was built for so the
/// post can refuse to be spent on anything else.</para>
/// <para><see cref="ReceiptId"/> rides along for the TRUE-UP (unit 4). A cached post
/// writes no receipt of its own - the spine did not run - so the only receipt
/// describing this listing is the one recon wrote, and its decided_price still holds
/// recon's anchor rather than the gil that actually got posted. Carrying the id on the
/// plan is what lets the ordinary true-up at the end of the pricing pass correct a
/// receipt written hours earlier, with no second code path to keep in step (see
/// <see cref="CachedPostTrueUp"/>).</para>
/// <para>A lane median rode beside it from V40 until the doctrine sweep
/// (2026-08-15): the true-up used to recompute position_in_lane, and that ratio's
/// writer is retired. The id is all the ending needs now.</para>
internal readonly record struct CachedPostPlan(
  CachedPostRoute Route, uint ItemId, bool IsHq, long Price, long BankedAt, string Reason,
  long? ReceiptId = null)
{
  /// <summary>True when the banked decision may be posted as-is.</summary>
  internal bool RidesCache => Route == CachedPostRoute.PostCached;

  /// <summary>Does this plan describe the item actually in the panel?</summary>
  internal bool Matches(uint itemId, bool isHq) => ItemId == itemId && IsHq == isHq;

  /// <summary>The classic chain, with the sentence that sent it there.</summary>
  internal static CachedPostPlan Chain(string reason)
    => new(CachedPostRoute.FullChain, 0, false, 0, 0, reason);

  /// <summary>
  /// The true-up this plan owes once the listing is real, or null when it owes none.
  /// A chained plan owes nothing (the spine will write its own receipt this pass),
  /// and a banked decision with no receipt behind it - recon's receipt write failed,
  /// or the row predates the column - owes nothing either, because there is no record
  /// to correct. See <see cref="CachedPostTrueUp"/>.
  /// </summary>
  internal long? TrueUp => RidesCache ? ReceiptId : null;
}

/// <summary>
/// THE CHEAP GUARD, resolved against the config as it stands RIGHT NOW. One number
/// since the one floor law (2026-08-21): the minimum and the mode floor are one rule
/// with one answer, and this door asks it exactly the way the pinch does.
///
/// <para>It is arithmetic, not a fetch, which is the whole reason it is
/// re-asked at post time instead of trusted from the recon pass. The config can
/// have changed between the Look and the Act - the player raises his minimum
/// listing price between the recon stage and the list stage of the same round, and
/// every banked price under the new floor is suddenly a price he has said he does
/// not want posted. Recon's own guard verdict was honest when it was made; it was
/// made against a different rule. Re-running a comparison costs nothing and is the
/// difference between "the cache is a saved server round trip" and "the cache is a
/// way to post prices the current settings forbid".</para>
///
/// <para>Floor arrives pre-resolved (the mode switch and the vendor-price lookup
/// are the game's business) so this file stays Dalamud-free. Zero means "no floor",
/// matching the pipeline's own convention.</para>
/// </summary>
internal readonly record struct PostGuards(long EffectiveFloor);

/// <summary>
/// THE FORK (Rounds unit 3): the list stage's per-item choice between spending a
/// banked decision and paying the board again.
///
/// <para><b>The freshness question is asked with unit 2's predicate, verbatim.</b>
/// <see cref="ReconFreshness"/> owns it for recon's work set and it owns it here,
/// because the ruling was that these are one rule at two doors - "fresh enough to
/// skip re-reading" and "fresh enough to act on" are the same sentence. A second
/// implementation of that sentence would eventually disagree with the first, and
/// the two ways it could disagree are both bad: re-reading an item we were about to
/// post from cache anyway, or posting from a row recon had just declared too old to
/// walk past.</para>
///
/// <para><b>Every ambiguity resolves toward the chain.</b> No row, a stale row, a
/// row recon banked with no price, a price the current guards refuse - all of them
/// pay the full ComparePrice chain, which is exactly what the plugin did before this
/// unit existed. The failure mode of this gate is therefore "the round is as slow as
/// it used to be", never "the round posted something blind". That matters most on
/// the path nobody plans for: a Round resumed days after its Look half, where every
/// row falls out of the window at once and the act leg quietly goes back to reading
/// real boards.</para>
///
/// <para><b>A null-price row is a decision, and it still pays the chain.</b> Recon
/// banks null for two different verdicts - the spine held on thin history, and the
/// guards rejected the price it built - and neither leaves a number anyone could
/// post. Reading such a row and holding the item would be defensible, but it would
/// bank a hold made minutes ago against a board nobody has looked at since, on the
/// one leg of the round that is standing at the panel with the item in hand and can
/// simply ask. So the row's existence buys recon a skip, not the act leg a
/// shortcut.</para>
/// </summary>
internal static class CachedPostGate
{
  internal static CachedPostPlan Decide(
    DecisionCacheRow? row, long nowUnix, int freshHours, PostGuards guards)
  {
    if (row is not { } banked)
      return CachedPostPlan.Chain("nothing banked for this item");

    if (!ReconFreshness.IsFresh(banked.BankedAt, nowUnix, freshHours))
      return CachedPostPlan.Chain("the banked decision is stale");

    if (banked.DecidedPrice is not long price || price <= 0)
      return CachedPostPlan.Chain("recon banked a hold, not a price");

    // THE ONE FLOOR LAW at the cache's door (ruled 2026-08-21). The guards arrive
    // pre-resolved so this file stays Dalamud-free; the max() is the whole rule, and
    // a banked price that fails it is a price today's settings forbid.
    if (guards.EffectiveFloor > 0 && price < guards.EffectiveFloor)
      return CachedPostPlan.Chain("the banked price is under today's floor");

    return new CachedPostPlan(CachedPostRoute.PostCached,
      banked.ItemId, banked.IsHq, price, banked.BankedAt, banked.Outcome,
      banked.ReceiptId);
  }
}

/// <summary>
/// THE RECEIPT TRUE-UP FOR CACHED POSTS (ruled 2026-08-10), and an honest account of
/// what it is worth.
///
/// <para><b>The bug it closes.</b> decided_price's spec is "the ABSOLUTE gil the
/// decision wrote" (V26). A receipt is inserted at DECISION time, when only the
/// anchor is known, so every pricing pass ends by correcting it with the gil that
/// actually got posted - the Golden Silk receipt banked 195 while the market got 190.
/// A cached post skipped that ending entirely: the receipt it lists against was
/// written during recon, recon posted nothing, and so the row kept recon's anchor
/// while a real listing stood at a different number. The lie is small and permanent,
/// and it is exactly the fossilised-measurement shape - a number that reads like a
/// measurement six months later because nothing on the row says it was never trued
/// up.</para>
///
/// <para><b>What it touches, said plainly - CORRECTED 2026-08-15.</b> This paragraph
/// used to claim "the queue-coordinate grading spine does not consume decided_price",
/// and that was false when it was written. Two kinds of surface read the column. The
/// READER surfaces - the On Market tab, the receipt trail in the detail pane, any
/// later analysis that takes decided_price at its word - are the obvious ones, and a
/// reader that quietly disagrees with the market by 5 gil is a reader nobody can check
/// anything against. But <see cref="ReceiptGrading"/> reads it too, today: every pass
/// gates on a receipt HAVING a decided_price - the interim one compares settle prices
/// against it, the chain verdict compares the SUCCESSOR's decided_price against it, and
/// the margin measurement subtracts it from the tape. So the true-up also changes what
/// the grader sees - and that is correct,
/// not a side effect to apologise for. decided_price's spec is the ABSOLUTE gil the
/// decision wrote (V26); until this ran, a cached post's receipt was not carrying that,
/// and a grade against a price nobody ever posted is a grade of nothing.</para>
///
/// <para><b>One call site, not two.</b> The correction is the SAME call the classic
/// chain already makes at the end of the pass - it needs a receipt id on the item in
/// hand, and it does not care where that id came from. So a cached post ADOPTS recon's
/// receipt id onto the item and the existing ending fires unchanged. A second true-up
/// path would be a second thing to keep in step with a rule that has already changed
/// twice (S13 added the retainer half; the doctrine sweep took the lane median back
/// out).</para>
///
/// <para><b>A refused cached post adopts NOTHING.</b> The refusal - the plan meeting a
/// panel it was not built for - posts no listing, so there is no applied price for a
/// correction to be made from. It is refused here rather than only downstream because
/// "did we post this" is a fact this file already knows, and the ending's own guard (a
/// held result contributes no listing value) should be the second door, not the
/// first.</para>
/// </summary>
internal static class CachedPostTrueUp
{
  /// <summary>
  /// The receipt this pass should correct at its end, adopted from the banked row.
  /// Null = nothing to adopt, which is the answer for a chained item, a refusal, and
  /// a banked row with no receipt behind it (recon's receipt write failed, or the row
  /// predates the column).
  /// </summary>
  internal static long? Adopt(CachedPostPlan? plan, bool refused)
    => refused ? null : plan?.TrueUp;
}

/// <summary>
/// What a post-time board read amounts to, as far as the veto cares.
///
/// <para>Two facts, because the Q3 arithmetic only ever needed two: whether the
/// read is the WHOLE board (the x-of-y question - a page-1 packet is not a board),
/// and what the cheapest foreign row asks (the front of the line, the only number a
/// partial read can honestly be compared on).</para>
/// </summary>
internal readonly record struct FreshBoardRead(bool Complete, long? FrontOfLine);

/// <summary>What the veto decided about one cached post.</summary>
internal enum VetoVerdict
{
  /// <summary>Nothing fresh disagreed loudly enough: the banked decision rides.</summary>
  PostCached,
  /// <summary>
  /// Fresh data is in hand and worth re-deriving from: run the spine. VERDICT ONLY -
  /// <see cref="FreshBoardRead"/> carries no listings, so the re-derivation this
  /// verdict calls for has no data seam behind it yet. See the class remarks on
  /// <see cref="CachedPostVeto"/> before acting on this branch.
  /// </summary>
  RePriceOnFresh,
}

/// <summary>
/// THE PANEL VETO (Q3 arithmetic, ruled 2026-08-10) - and, today, a seam with
/// nothing behind it.
///
/// <para><b>Why it is built anyway.</b> Verified in game 2026-08-10: opening the
/// RetainerSell panel WITHOUT pressing Compare Prices produces zero market-board
/// traffic - no orphan offerings drop, no history packet. Only Compare Prices
/// fetches the board, and the cached post's entire value is not pressing it. So the
/// provider returns null on every item and case 3 is the only branch that runs. The
/// arbitration exists because the OTHER two cases are the ones that are dangerous to
/// invent later under pressure, and because the day a fresh source does appear
/// (a passive packet, a community read, a second retainer's incidental board) the
/// question "what do we do with it" must already have an answer that was reasoned
/// about rather than guessed at the keyboard.</para>
///
/// <para><b>THE SEAM IS HALF-BUILT: verdicts exist, the data hand-off does not
/// (ruled 2026-08-12: stays this way until a real provider matters).</b>
/// <see cref="FreshBoardRead"/> deliberately carries two facts - completeness and
/// front-of-line - and no listings. So cases 1 and 2 can RULE "re-run the spine on
/// the fresh data" but cannot HAND the spine that data: a provider wired up today
/// would send the spine to price against an empty board, confidently and silently
/// wrong. Wiring a provider therefore requires building the listings hand-off in
/// the same change - do not connect one without the other.</para>
///
/// <para><b>Case 1 - a fresh COMPLETE board: re-run the spine, always, cache
/// ignored.</b> No threshold guards this and none should. The recalculation is
/// microseconds against evidence that is strictly better than the banked evidence;
/// a threshold in front of a free recalculation is ceremony that can only ever
/// produce a worse answer.</para>
///
/// <para><b>Case 2 - a fresh PARTIAL read: the threshold arbitrates.</b> This is
/// the only place a knob belongs, and it exists because the comparison is genuinely
/// between two flawed readings: a complete-but-banked board and a fresh-but-censored
/// one. A page-1 packet knows the front of the line and nothing about depth, walls,
/// or foreign spread - the very context the spine prices on. So the front of the
/// line is all it may vote with: if it disagrees with the banked anchor by more than
/// the caller's tolerance, the market has moved under us and being fresh beats being
/// complete; inside the band, the complete board wins and the banked price
/// posts.</para>
///
/// <para><b>Case 3 - nothing fresh: post cached.</b> The gate already established
/// the row is inside the freshness window; there is nothing here to arbitrate.</para>
///
/// <para>Pure and Dalamud-free; the provider is a func so the arbitration can be
/// exercised against a fake source in the tests, which is the only way any of the
/// unreachable branches get proven at all.</para>
/// </summary>
internal static class CachedPostVeto
{
  internal static VetoVerdict Arbitrate(long cachedPrice, FreshBoardRead? fresh, int disagreePct)
  {
    // Case 3: no fresh data. The banked answer is the best answer in the room.
    if (fresh is not { } read)
      return VetoVerdict.PostCached;

    // Case 1: a whole board in hand. Free recalculation beats any threshold.
    if (read.Complete)
      return VetoVerdict.RePriceOnFresh;

    // Case 2: a partial read votes with the one number it actually knows. With no
    // front of line - or no banked anchor to compare it against - it knows nothing,
    // and a partial read that knows nothing must not outvote a complete board.
    if (read.FrontOfLine is not long front || front <= 0 || cachedPrice <= 0)
      return VetoVerdict.PostCached;

    var disagreement = Math.Abs(front - cachedPrice) * 100.0 / cachedPrice;
    // BEYOND the threshold, not at it: the knob names the tolerance, so a
    // disagreement equal to it is still tolerated.
    return disagreement > Math.Max(0, disagreePct)
      ? VetoVerdict.RePriceOnFresh
      : VetoVerdict.PostCached;
  }

  /// <summary>
  /// Asks the seam, then arbitrates. A provider that throws is a provider that
  /// answered nothing - the cached post is not the place to take a run down over an
  /// optional read, and "no fresh data" is the honest reading of a source that
  /// failed to produce any.
  /// </summary>
  internal static VetoVerdict Decide(
    uint itemId, bool isHq, long cachedPrice,
    Func<uint, bool, FreshBoardRead?>? provider, int disagreePct)
  {
    FreshBoardRead? fresh;
    try { fresh = provider?.Invoke(itemId, isHq); }
    catch { fresh = null; }
    return Arbitrate(cachedPrice, fresh, disagreePct);
  }
}

/// <summary>
/// PROVENANCE, said out loud. A cached post must never dress as a fresh board read.
///
/// <para>The run log is the round's transcript and it is read later, cold, to answer
/// "why is this listed at that". A line reading like every other listing line would
/// make a decision taken hours ago at a board nobody has seen since indistinguishable
/// from one taken against a board read seconds earlier - which is the same
/// fossilised-measurement failure the receipts already paid for once. So the line
/// carries the age, in the same house grammar every other outcome uses.</para>
/// </summary>
internal static class CachedPostNote
{
  /// <summary>
  /// How old the banked decision is. The coarsening rule lives in
  /// <see cref="RunLogVoice.Age"/> - one house voice, and the age of a cached post
  /// is not a different kind of age from anything else the log says out loud.
  /// </summary>
  internal static string Age(long bankedAt, long nowUnix) => RunLogVoice.Age(nowUnix - bankedAt);

  /// <summary>
  /// The transcript line for one cached post. NO SEAT: no board was read this pass,
  /// so there is no position this listing can honestly claim - and claiming one
  /// would be exactly the fresh-read costume this line exists to refuse.
  /// </summary>
  internal static VoiceLine Line(long price, long bankedAt, long nowUnix)
    => RunLogVoice.Posted(price, seat: 0, RunLogVoice.Reasons.FromTheLook(Age(bankedAt, nowUnix)));
}
