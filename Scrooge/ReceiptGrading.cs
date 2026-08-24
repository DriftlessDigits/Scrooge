using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// A9 receipt grading - the pure core (LanePricing/DecisionReceipts mold: no game
/// reads, no storage, no statics, linked into Scrooge.Tests). Given a receipt's
/// operands, it returns a verdict. It gathers nothing and writes nothing;
/// GilStorage does both.
///
/// <para><b>THE RECEIPT SCORES THE DECISION, NOT THE PRICE</b> (ruled 2026-08-15,
/// the doctrine sweep's centrepiece). The house doctrine is <i>"avoid trying to
/// predict a price - pick the best spot in the line"</i>, and the old grader broke
/// it in the quietest possible way: it judged the NUMBER. MISS meant "something
/// settled under your ask", UNDERSOLD meant "something settled over it" - both are
/// verdicts on a prediction we explicitly refuse to make. A grader that scores the
/// number teaches the machine to chase the number, which is the exact failure the
/// doctrine exists to prevent.</para>
///
/// <para><b>The motivating case (Drift's own scenario).</b> "If we made a decision to
/// sell at spot 4, does that price eventually clear? Or after time, do we end up
/// chasing a growing list of crashers, and so re-price for spot 1? We were confident
/// to move to spot 4, but ended up being wrong - the market was moving and the right
/// spot was 1." Nothing in that story is about whether 13,989 was the right gil. It
/// is about whether the SEAT we took got its turn, or whether the line kept growing
/// in front of it until we gave up and moved. So that is what these verdicts say.</para>
///
/// <para><b>The direction of the next decision is the verdict on this one.</b> That
/// is the whole trick of the final verdict set, and it is why the set needs no tape
/// at all. A receipt does not have to guess whether its seat was good; the NEXT
/// receipt already answered. Repriced DOWN from here means the market walked down
/// through us and the right spot was in front of where we sat (CHASED). Repriced UP
/// means the board healed above us and we sat too far forward (OUTGROWN). No
/// successor and our own sale confirmed means the seat call was right, whatever its
/// depth (CLEARED). No successor and the listing left without selling means the seat
/// never got its turn and nothing was learned (NEVER_TESTED). A successor at the
/// SAME price is not a supersession at all - see <see cref="GradeChain"/>.</para>
///
/// <para><b>A settle below our ask is AMBIGUOUS on its own</b> - the re-derivation
/// that reshaped the interim set. It means a row in front of us sold, which is
/// either the line ADVANCING toward our seat (good news, the thing we were waiting
/// for) or cutters feeding in ahead of us faster than the line drains (the in-flight
/// shape of CHASED). The price cannot tell those apart; only the queue measurement
/// can. So the old rule "MISS outranks drain" was exactly backwards - it let the
/// ambiguous fact overrule the disambiguating one. Now the drain decides and the
/// price test only opens the question.</para>
///
/// <para><b>This is a dark instrument. Grades feed NOTHING, and that is law.</b> No
/// price logic reads them, no seed tunes off them, no aggregate rolls them up, no
/// auto-tuning, ever. They are a readout for a human. <b>They ARE drawn</b> (the
/// detail pane's receipt trail - <see cref="BoardDetail.Stamp"/>, hung under
/// AccountantWindow's DrawReceiptTrail); the old "no window shows them" claim was
/// true for exactly one release and outlived its truth.</para>
///
/// <para><b>THE NAMED ASYMMETRY - the survivorship caveat, stated here ONCE.</b>
/// This is the seam that owns it; <see cref="DecisionReceipts"/> and
/// <see cref="BoardDetail"/> point here rather than restating it, because three
/// copies of a caveat is three chances to have two of them wrong.</para>
///
/// <para>A sold listing always stamps PASS, and always closes as CLEARED. Whether we
/// could have waited out a race is unknowable, so we do not score it. The
/// consequence is structural, not incidental: <b>the verdict board CANNOT REPORT
/// LEAVING MONEY ON THE TABLE.</b> A listing that sold instantly at half its worth
/// stamps the same PASS/CLEARED as one that sold at the top of the band. That is
/// first-law-correct (a sold item beats a museum piece, and erring toward sold is
/// the whole posture), but it means <b>"all cleared" must never be read as
/// "optimal"</b> - it is a survivorship board, not a scoreboard.</para>
///
/// <para>The counterweight is no longer a grade at all. It is
/// <see cref="MarginDonated"/> - a MEASUREMENT in gil, banked beside the verdict,
/// which is the only thing here allowed to say the market paid more right after we
/// left. A CLEARED receipt carrying margin_donated &gt; 0 is precisely the case the
/// old design structurally could not see, and it is now a number rather than an
/// inference.</para>
/// </summary>
internal static class ReceiptGrading
{
  /// <summary>
  /// The interim verdict on a receipt whose listing is still held - the in-flight
  /// story, told from still-held receipts. Four outcomes, none terminal but PASS.
  /// </summary>
  internal enum InterimGrade
  {
    /// <summary>Our listing sold since the receipt was written. Terminal, and
    /// deliberately unscored - see the asymmetry note on the class. Stamped by the
    /// sale confirm in GilStorage, never by <see cref="GradeInterim"/>.</summary>
    Pass,
    /// <summary>Settles landed below our ask AND the queue ahead of us SHRANK: the
    /// line is advancing toward our seat. This is the good news the old grader
    /// stamped MISS.</summary>
    LineMoving,
    /// <summary>Settles landed below our ask and the queue ahead of us held or
    /// grew: insertions keep eating the demand before it reaches us. The in-flight
    /// forecast of CHASED.</summary>
    CutOff,
    /// <summary>The window convicted nobody. A slow market is not a bad spot - and
    /// neither is a busy one we cannot measure the queue against.</summary>
    Silence,
  }

  /// <summary>
  /// The verdict on a receipt whose ask has stopped standing. Named FinalGrade for
  /// the column it lands in (final_grade); it is a verdict on the DECISION, read off
  /// the receipt chain and the outcome state, and it touches no tape.
  /// </summary>
  internal enum FinalGrade
  {
    /// <summary>No successor, and our own sale confirmed: the seat call was right,
    /// whatever its depth. Says nothing about whether it was the BEST seat - that is
    /// what <see cref="MarginDonated"/> measures.</summary>
    Cleared,
    /// <summary>The successor sits at a LOWER price: the market moved down through
    /// us, and the right spot was in front of where we sat. Drift's spot-4 case,
    /// landed.</summary>
    Chased,
    /// <summary>The successor sits at a HIGHER price: the board healed above us. We
    /// were wrong in the timid direction.</summary>
    Outgrown,
    /// <summary>No successor, and the listing left the board without a confirmed
    /// sale (pulled/evicted). The spot never got its turn; the absence is the
    /// finding.</summary>
    NeverTested,
  }

  /// <summary>The storage-side spelling of an interim verdict (the TEXT in the row).
  /// "MISS" and "ON_TRACK" are never written again - legacy rows keep theirs, which
  /// is history and stays readable.</summary>
  internal static string Name(InterimGrade g) => g switch
  {
    InterimGrade.Pass => "PASS",
    InterimGrade.LineMoving => "LINE_MOVING",
    InterimGrade.CutOff => "CUT_OFF",
    _ => "SILENCE",
  };

  /// <summary>The storage-side spelling of a final verdict. "UNDERSOLD",
  /// "WELL_TIMED" and "UNGRADEABLE" are never written again; UNGRADEABLE in
  /// particular died with the tape - the verdict set needs no tape, so there is no
  /// longer a state where the evidence can scroll away before we look.</summary>
  internal static string Name(FinalGrade g) => g switch
  {
    FinalGrade.Cleared => "CLEARED",
    FinalGrade.Chased => "CHASED",
    FinalGrade.Outgrown => "OUTGROWN",
    _ => "NEVER_TESTED",
  };

  /// <summary>One settled sale off the tape, already quality-matched by the caller.</summary>
  internal readonly record struct Settle(long UnitPrice, long SaleTime);

  /// <summary>
  /// Everything the interim verdict is computed from. The caller gathers; this
  /// record is the whole contract. Unchanged operands from the price-grading era -
  /// the reframe rewired what they MEAN, not what has to be fetched.
  /// </summary>
  internal readonly record struct InterimOperands(
    // The absolute gil the receipt wrote. Null on a pre-V26 receipt - ungradeable,
    // because the settle comparison that opens the question is against absolutes.
    long? DecidedPrice,
    // The receipt's own queue_position: foreign rows still ahead when we listed.
    // Null = we never listed (held), and a held row has no queue to measure.
    int? QueuePosition,
    // Quality-matched settles with sale_time AFTER the receipt's created_at.
    IReadOnlyList<Settle> SettlesSince,
    // Foreign rows on the CURRENT board strictly below our ask. Null = no board
    // read this pass, so there is nothing to compare the queue against.
    int? ForeignRowsBelowNow);

  /// <summary>
  /// The in-flight verdict on a still-held receipt. Pure and total.
  ///
  /// <para><b>The drain decides; the price only opens the question.</b> A settle
  /// below our ask means a row in front of us sold - that is all it means. Two
  /// opposite stories fit it: the line advancing toward our seat, or cutters feeding
  /// in ahead of us. The queue measurement is what separates them, so it must never
  /// be overruled by the price test (the old "MISS outranks drain" had that
  /// backwards, and stamped a failure on the very case we were waiting for).</para>
  ///
  /// <para><b>Only a WITNESSED non-shrinking queue convicts.</b> If we cannot
  /// compare - a null queue_position (held, or pre-A10) or no board in hand this
  /// pass - the two stories are still both alive, so the answer is SILENCE, never
  /// CUT_OFF. A conviction we cannot see the evidence for is not a conviction.</para>
  ///
  /// <para><b>The below-boundary is STRICT.</b> A settle AT our exact ask is not the
  /// line moving in front of us - it is the market clearing at our number, which is
  /// the thing we asked for. Only strictly below opens the question at all.</para>
  ///
  /// <para><b>SILENCE is stamped, not left NULL,</b> and it is the catch-all. That
  /// buys one thing worth more than a finer taxonomy: NULL becomes unambiguous - it
  /// means the grader never looked at this receipt, never "looked and shrugged". So
  /// SILENCE covers "nothing settled at all", "everything settled at or above us",
  /// and "things settled below us and we have no way to read the queue". All three
  /// are the same finding: this window convicts nobody.</para>
  ///
  /// <para>Returns null only when the receipt cannot be graded at all (no absolute
  /// ask to compare against) - the row keeps its NULL and stays honest. PASS is
  /// never returned here: it is the sale confirm's stamp, written where the sale is
  /// known (GilStorage.FillReceiptOutcomeOnSale).</para>
  /// </summary>
  internal static InterimGrade? GradeInterim(InterimOperands o)
  {
    if (o.DecidedPrice is not long ask)
      return null; // pre-V26 receipt: nothing to compare settles to

    var settles = o.SettlesSince;
    if (settles == null || settles.Count == 0)
      return InterimGrade.Silence;

    var anyBelow = false;
    foreach (var s in settles)
      if (s.UnitPrice < ask) { anyBelow = true; break; }

    if (!anyBelow)
      return InterimGrade.Silence; // the market cleared at or above our number

    // Something in front of us sold. Which story? Only the queue knows.
    if (o.QueuePosition is int recorded && o.ForeignRowsBelowNow is int nowBelow)
      return nowBelow < recorded ? InterimGrade.LineMoving : InterimGrade.CutOff;

    return InterimGrade.Silence; // no way to compare - both stories still alive
  }

  /// <summary>
  /// Everything the chain verdict is computed from. <b>No tape</b> - that absence is
  /// the design, not an oversight: the verdict on a decision is the direction of the
  /// next decision, plus how the listing actually ended.
  /// </summary>
  internal readonly record struct ChainOperands(
    // The absolute gil this receipt wrote (null on a pre-V26 receipt).
    long? DecidedPrice,
    // Is there a later receipt in the same lane? True means this ask stopped
    // standing when that one was written, whatever the outcome column says.
    bool HasSuccessor,
    // The successor's decided_price. Null with HasSuccessor true = the successor
    // named no price (held, or pre-V26), so the direction cannot be read.
    long? SuccessorDecidedPrice,
    // How the listing itself ended. Only consulted when there is no successor.
    DecisionReceipts.OutcomeState Outcome);

  /// <summary>
  /// The verdict on a decision, read off the receipt chain. Pure, total, tape-free.
  ///
  /// <para><b>Supersession beats sale,</b> the same rule the tab's standing-ask
  /// picker uses: a repriced ask stopped standing the moment its successor was
  /// written, so the successor's direction is the verdict even if a sale landed
  /// later on the lane.</para>
  ///
  /// <para><b>A successor at the SAME price stamps NOTHING, on purpose.</b> A
  /// re-affirmed spot is not a supersession - it is the same decision, made again,
  /// usually because a pinch re-checked a listing and confirmed the seat still fit.
  /// Stamping a verdict there would score a decision that was never reversed, and it
  /// would score it once per re-check, drowning the real reversals in noise. The
  /// verdict instead lands on the LAST receipt of a same-price run: that is the one
  /// whose successor finally moved, and it is the one whose seat the market actually
  /// answered. Every earlier receipt of the run keeps its NULL, which reads honestly
  /// as "this ask was re-affirmed, not overturned".</para>
  ///
  /// <para><b>Ambiguous ends stay unstamped.</b> An open listing has no verdict yet
  /// (the backfill looks again next recurrence). A listing the pinch reconciler
  /// found merely ABSENT (gone_unobserved) is provisional by construction - a late
  /// sale confirm upgrades it to cleared - and a final verdict is written once and
  /// never moves, so stamping NEVER_TESTED on an ambiguity would freeze a possible
  /// lie. Only a listing we KNOW was pulled convicts as NEVER_TESTED.</para>
  /// </summary>
  internal static FinalGrade? GradeChain(ChainOperands o)
  {
    if (o.DecidedPrice is not long price)
      return null; // no ask on this receipt - there is no seat to have a verdict about

    if (o.HasSuccessor)
    {
      if (o.SuccessorDecidedPrice is not long next)
        return null; // a successor with no price cannot name a direction
      if (next < price) return FinalGrade.Chased;
      if (next > price) return FinalGrade.Outgrown;
      return null;   // re-affirmed, not superseded - the run's last receipt carries it
    }

    return o.Outcome switch
    {
      DecisionReceipts.OutcomeState.Cleared => FinalGrade.Cleared,
      DecisionReceipts.OutcomeState.NeverCleared => FinalGrade.NeverTested,
      _ => null, // still open, or gone_unobserved (provisional - look again later)
    };
  }

  /// <summary>
  /// Everything the margin measurement is computed from. This is the ONE place the
  /// tape still enters A9, and it enters as a measurement rather than a grade.
  /// </summary>
  internal readonly record struct MarginOperands(
    // The absolute gil the receipt wrote (null on a pre-V26 receipt).
    long? DecidedPrice,
    // When the receipt's ask stopped standing: the successor's created_at for a
    // superseded ask, or closed_at for a listing that left the board.
    long? ClosedAt,
    // The oldest sale_time we still hold for this item+quality. Null = no tape.
    long? OldestBankedSaleTime,
    // Quality-matched settles with sale_time AFTER ClosedAt.
    IReadOnlyList<Settle> SettlesAfterClose);

  /// <summary>
  /// THE MEASUREMENT: how much the market paid over our ask once we were out of it,
  /// in gil. Not a grade - a tape fact banked beside the verdict, and the only thing
  /// in A9 that can say we left money on the table.
  ///
  /// <para><b>It is the ruled revival-or-burial trigger for the parked Phase 4
  /// demand-shape work.</b> Phase 4 GUESSED at the margin donated to a sweeper; this
  /// MEASURES it. The trigger contract is exactly two reads, and nothing else in the
  /// codebase reads this column: <i>frequency</i> (how often the value is non-null
  /// and positive) and <i>magnitude</i> (how much gil, when it is). Rare and small
  /// buries Phase 4 for good; common and large brings it back with a number instead
  /// of a theory. Writing it is that decision's whole justification.</para>
  ///
  /// <para><b>0 means measured-and-nothing-donated; NULL means never measured.</b>
  /// The distinction is the entire value of the column - an analysis that cannot
  /// tell "we looked and the market paid no premium" from "we never got to look"
  /// cannot compute a frequency at all. NULL happens three ways: the receipt is
  /// still open (no close to measure from), the tape scrolled past the close before
  /// we came back (the ring's oldest sale is NEWER than our close, so whatever
  /// settled in the gap is simply gone), or the gap is covered but the market has
  /// not spoken yet - the last one is re-examined on every recurrence.</para>
  ///
  /// <para><b>The above-boundary is STRICT,</b> mirroring the interim one: a settle
  /// AT our price means the next buyer paid exactly what we took, which is not a
  /// donation. And the result is floored at 0, so a market that only settled BELOW
  /// us reads as a measured zero rather than a negative "we overcharged" number -
  /// this column answers one question, and the other direction is not it.</para>
  ///
  /// <para><b>A CLEARED receipt can carry a positive margin.</b> That pairing is not
  /// a contradiction; it is the survivorship hole, finally visible: the ask sold, so
  /// the seat call was right, AND the market paid more right afterward, so the seat
  /// was not the best one available. The old design could not represent that at all
  /// - it graded a sale PASS and stopped asking.</para>
  /// </summary>
  internal static long? MarginDonated(MarginOperands o)
  {
    if (o.DecidedPrice is not long price || o.ClosedAt is not long closedAt)
      return null; // no ask, or the ask is still standing - nothing to measure yet

    // No tape at all, or a ring that starts after our exit: the gap is gone.
    if (o.OldestBankedSaleTime is not long oldest || oldest > closedAt)
      return null;

    var settles = o.SettlesAfterClose;
    if (settles == null || settles.Count == 0)
      return null; // covered, but the market has not spoken yet - look again later

    long best = 0;
    foreach (var s in settles)
    {
      var over = s.UnitPrice - price;
      if (over > best) best = over;
    }
    return best;
  }
}
