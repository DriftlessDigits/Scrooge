using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>WHY a row is a case - the three ways the machinery withholds a row, and
/// therefore the sentences the case can open with.</summary>
internal enum ReferralReason
{
  /// <summary>
  /// The router had numbers and would not pick between them. TWO DIFFERENT DISPATCHERS
  /// land here and they ask the reader different questions, so the sentence forks on
  /// <see cref="CaseEvidence.RouterDeclined"/> rather than speaking one wording over
  /// both (ruled B5: the referral speaks its real dispatcher).
  /// </summary>
  TooClose,
  /// <summary>An evidence axis DISAGREES with the verdict - the Alexander rule fired.</summary>
  Contradicted,
  /// <summary>Nothing was ever measured. The case is honest about being thin.</summary>
  NoData,
}

/// <summary>
/// WHAT A SCORED NUMBER IS, in the one word the case puts beside it. The doctrine
/// (spec section 2): every line says where its number CAME FROM. A worth with no
/// provenance is a prediction wearing a receipt's clothes.
/// </summary>
internal enum WorthProvenance
{
  /// <summary>Your own settled receipts produced it.</summary>
  Measured,
  /// <summary>An ask on a board. Somebody wants it; nobody paid it.</summary>
  Asked,
  /// <summary>What the NPC counter pays. True, and a floor.</summary>
  VendorFloor,
  /// <summary>An ilvl-band average standing in for melts you have never run.</summary>
  BandPrior,
  /// <summary>A knob nobody has measured yet.</summary>
  Placeholder,
  /// <summary>The player's own ruled constant (a skill-up worth). Certain by
  /// definition - there is no sample size that could firm it up, and no staleness
  /// that erodes it (F3, ruled 08-22: the two-register constitution - estimates
  /// self-regulate with evidence; pegs HOLD).</summary>
  Peg,
}

/// <summary>One exit's scored worth as the router weighed it.</summary>
internal readonly record struct ExitScore(BoardPile Exit, long Worth, WorthProvenance Provenance);

/// <summary>
/// ONE CONTENDING EXIT, ARGUING WITH RECEIPTS (spec section 2). Never a prediction:
/// <see cref="For"/> lines cite where their numbers came from, <see cref="Caveats"/>
/// say how much weight that deserves, and no line asserts what the market will do.
/// </summary>
internal readonly record struct CaseOption(
  BoardPile Exit,
  long Worth,
  string Provenance,
  IReadOnlyList<string> For,
  IReadOnlyList<string> Caveats);

/// <summary>
/// ONE CASE ON THE BAG CALLS PAGE - the tribunal layout, one at a time, which is the
/// room the north star demands ("don't shackle hard decisions with a format that works
/// well for easy decisions").
/// </summary>
/// <param name="Exits">EVERY exit, in the board's order - the buttons. The override
/// grammar never narrows: a click on an unargued exit is still a valid override (ruled
/// 08-15). <see cref="Options"/> is the subset that ARGUES.</param>
/// <param name="LastSold">
/// THE LAST-SOLD SEAT (V31, ruled B1 coda: a DISPLAY, not a gate). One line, on the
/// case itself rather than inside whichever exit happened to argue, because "when did
/// one of these last change hands" is a fact about the ITEM and the reader wants it in
/// the same place every time. Empty when no last-sale date exists at all - the line
/// does not hedge, it does not fire.
/// </param>
internal sealed record TriageCase(
  string Key,
  string Title,
  string Referral,
  IReadOnlyList<CaseOption> Options,
  IReadOnlyList<BoardPile> Exits,
  BoardPile? DecidedExit = null,
  string LastSold = "",
  string Verdict = "",
  bool DisputeLeads = false);

/// <summary>
/// EVERYTHING ONE CASE IS BUILT FROM - banked facts, handed in whole. Task 3 fills
/// this off the WorkItem and the stores it already reads; nothing here is derived by
/// the case layer, because a case that re-derived an operand would be a second opinion
/// about a number the board already published.
///
/// <para>"SPEND all of it" (spec section 2): every field below is data the plugin
/// already banks. A missing one is honest zero/null - the sentence it feeds simply does
/// not fire, and no line hedges in its place.</para>
/// </summary>
/// <param name="Reason">Which of the three referral sentences opens the case.</param>
/// <param name="Scores">Every exit's scored worth. Contenders are picked from here.</param>
/// <param name="OwnSalePrice">Your last settled sale of one, 0 when the tape never saw one.</param>
/// <param name="OwnSaleAgeDays">How long ago that sale settled.</param>
/// <param name="OwnSaleDaysToSell">How long it sat before it cleared, null when unknown.</param>
/// <param name="MeltAttempts">Your own desynths of this one.</param>
/// <param name="MeltAverage">What those attempts returned on average.</param>
/// <param name="MeltSpreadLow">Worst attempt.</param>
/// <param name="MeltSpreadHigh">Best attempt.</param>
/// <param name="MeltBandIlvl">The ilvl band the prior comes from, 0 when there is none.</param>
/// <param name="MeltBandAverage">What that band averages - used only when you have never melted one.</param>
/// <param name="SealCount">Expert Delivery seals for one of these.</param>
/// <param name="SealGilRate">The gil-per-seal rate the GC score was multiplied by
/// (effective - after any stock discount).</param>
/// <param name="VendorPrice">What the NPC counter pays.</param>
/// <param name="LaneMedian">Recon's cached lane read.</param>
/// <param name="LaneSampleCount">How many listings back it.</param>
/// <param name="MinSamples">Your bar for "enough".</param>
/// <param name="LaneSpread">Relative spread of that lane.</param>
/// <param name="LaneAgeDays">How old the read is.</param>
/// <param name="StaleDays">Your stale window.</param>
/// <param name="VelocityPerDay">Sales a day, null when unmeasured.</param>
/// <param name="Undercutters">Sellers under you at the last board read.</param>
/// <param name="BoardReadAgeDays">How old that board read is.</param>
/// <param name="ContradictionAxis">The <see cref="BoardConfidence.SalesVerdictAccord"/>
/// result. Disagree is the Alexander rule firing.</param>
/// <param name="ContradictedExit">The verdict that axis disagrees with.</param>
/// <param name="Lean">Which way that verdict leans - the contradiction reads opposite
/// directions for an on-market and an off-market call.</param>
/// <param name="RecentSalesCount">Settled sales in the lookback window.</param>
/// <param name="ReconAsk">The round's own banked Look for this variant - what recon
/// decided it would ask, 0 when no fresh look exists. THE FRESHEST NUMBER IN THE
/// BUILDING by the time the hinge draws (ruled 08-16: "triage must use the latest
/// information we have" - the stage order puts the Look before the rule precisely so
/// this exists, and the hawk already trusts the same row with real gil).</param>
/// <param name="ReconAskAgeSeconds">How old that look is.</param>
/// <param name="ReconAskAnchor">WHAT THE LOOK PRICED AGAINST - the lane outcome recon
/// banked with the number (pen 8a). A price with no anchor is a number the reader has
/// to take on faith; "cut in front of the cheapest cluster" is the derivation.</param>
/// <param name="ReconAskEvidence">The judgment underneath that anchor - the lane's own
/// evidence sentence, banked with the decision.</param>
/// <param name="IsStanding">
/// WHETHER THIS ROW IS ON THE BOARD RIGHT NOW (pen 7). A standing listing and a bag row
/// are contradicted in opposite grammars - "taking it off the board" is meaningless for
/// something that was never on it - and the sales evidence behind them comes from two
/// different witnesses: the standing row's is the local tape, the bag row's is the
/// DC-wide community read. The voice forks on this and nothing else.
/// </param>
/// <param name="RouterDeclined">
/// WHICH DISPATCHER SENT THIS ROW (ruled B5). A scored row arrives at the tribunal by
/// two different doors and the old voice narrated both with one sentence: the ROUTER
/// declining between two scores inside its review band, and the CONFIDENCE TIER refusing
/// to seat a verdict it did not trust. "Too close for the router to call" over a Mixed
/// row 13,556 gil apart was that collapse in the open (the Silvergrace receipt).
///
/// <para>True = the verdict's own <c>IsReview</c>: two exits scored within the review
/// band and the router would not pick, so a dead heat is a fact about this row. False =
/// the tier demoted it; there IS a leader, and what is shaky is the evidence under it.</para>
///
/// <para><b>Defaults FALSE, deliberately.</b> "A dead heat" is the claim that needs the
/// proof - the same fail-closed way round <see cref="RunLogVoice"/> writes its undercut
/// test - so an unsupplied dispatcher speaks the thin-evidence arm rather than asserting
/// a tie nobody demonstrated.</para>
/// </param>
/// <param name="LastSoldAgeDays">
/// HOW LONG AGO ONE LAST CHANGED HANDS, from whichever witness has a date - your own
/// receipt or the tape. Null when nothing ever sold, and the line does not fire. Falls
/// back to <paramref name="OwnSaleAgeDays"/> when a settled own sale is on record, so
/// the seat is never empty on a row that has one.
/// </param>
internal readonly record struct CaseEvidence(
  string Key,
  string Title,
  ReferralReason Reason,
  IReadOnlyList<ExitScore> Scores,
  long OwnSalePrice = 0,
  int OwnSaleAgeDays = 0,
  int? OwnSaleDaysToSell = null,
  int MeltAttempts = 0,
  long MeltAverage = 0,
  long MeltSpreadLow = 0,
  long MeltSpreadHigh = 0,
  int MeltBandIlvl = 0,
  long MeltBandAverage = 0,
  int SealCount = 0,
  double SealGilRate = 0,
  long VendorPrice = 0,
  long LaneMedian = 0,
  int LaneSampleCount = 0,
  int MinSamples = 0,
  double LaneSpread = 0,
  int LaneAgeDays = 0,
  int StaleDays = 0,
  double? VelocityPerDay = null,
  int Undercutters = 0,
  int BoardReadAgeDays = 0,
  Accord ContradictionAxis = Accord.Unknown,
  BoardPile? ContradictedExit = null,
  VerdictLean Lean = VerdictLean.Neutral,
  int RecentSalesCount = 0,
  long ReconAsk = 0,
  long ReconAskAgeSeconds = 0,
  string ReconAskAnchor = "",
  string ReconAskEvidence = "",
  bool IsStanding = false,
  bool RouterDeclined = false,
  int? LastSoldAgeDays = null);

/// <summary>
/// THE CASE'S VOICE - one pure function per source type, and that is the whole design
/// (spec, "Case-sentence discipline"). These strings are the red-pen surface: Drift walks
/// a real triage and rewrites them, so each one has to be swappable in a single line
/// with a pinned test underneath it.
///
/// <para><b>Doctrine, enforced here and nowhere else</b>: no sentence predicts a price.
/// Every sentence says where its number CAME FROM and how much weight it deserves.
/// A line with nothing behind it does not hedge - it does not fire.</para>
/// </summary>
internal static class CaseVoice
{
  /// <summary>The exit in the word a sentence uses for it - lowercase, because these
  /// are sentences, not column headings.</summary>
  internal static string ExitWord(BoardPile exit) => exit switch
  {
    BoardPile.List => "list",
    BoardPile.Melt => "melt",
    BoardPile.Churn => "GC",
    BoardPile.PullAndVendor => "vendor",
    BoardPile.Reprice => "reprice",
    _ => BoardLayout.GroupTitle(exit).ToLowerInvariant(),
  };

  /// <summary>
  /// The exit as an ACT, for a sentence that disagrees with doing it (pen 7). "Disagrees
  /// with melting it" is a claim about a bag row; "disagrees with taking it off the
  /// board" is a claim about a listing, and only one of the two is true of any given row.
  /// </summary>
  internal static string ExitGerund(BoardPile exit) => exit switch
  {
    BoardPile.List => "listing it",
    BoardPile.Melt => "melting it",
    BoardPile.Churn => "churning it",
    BoardPile.PullAndVendor => "vendoring it",
    BoardPile.Reprice => "repricing it",
    _ => $"sending it to {BoardLayout.GroupTitle(exit).ToLowerInvariant()}",
  };

  /// <summary>
  /// WHAT THE LOOK PRICED AGAINST, in words (pen 8a). The banked decision names its lane
  /// outcome; this is that name said the way the case says everything else. An outcome
  /// this build does not know produces "" and the clause simply does not fire - the
  /// derivation is missing, and a made-up anchor would be worse than a bare price.
  /// </summary>
  internal static string LookAnchor(string? outcome) => outcome switch
  {
    nameof(LaneOutcome.Undercut) => "cut in front of the cheapest cluster",
    nameof(LaneOutcome.CrazySkipped) => "stepped past the crashers, then cut in",
    nameof(LaneOutcome.EmptyBoard) => "nobody real in the queue, so it took the top of demonstrated clearing",
    nameof(LaneOutcome.PremiumFromNq) => "off this item's own NQ tape plus the HQ premium",
    _ => "",
  };

  /// <summary>What a number IS, in the parenthetical the case parks beside it.</summary>
  internal static string Provenance(WorthProvenance p) => p switch
  {
    WorthProvenance.Measured => "(measured)",
    WorthProvenance.Asked => "(asked, not proven)",
    WorthProvenance.VendorFloor => "(vendor floor)",
    WorthProvenance.BandPrior => "(band prior, never measured here)",
    WorthProvenance.Peg => "(your skill-up price - a ruled constant)",
    _ => "(placeholder, never measured)",
  };

  // ---- Referrals: why this row is a case at all ------------------------------

  // (The Opening() capitalizer died in the voice round, 08-22: referrals open
  // lowercase everywhere - a referral continues the case's sentence, it does not
  // start one - which also ends the V2/V3-vs-Contradicted capitalization drift.)

  // (ReferralTooClose died in the final pass, 08-23: the dead-heat fold made its
  // one live seat unreachable - a router-declined TooClose case with 2+ argued
  // exits ALWAYS folds the qualifier into the verdict lead, so the referral's
  // own "a dead heat" sentence had no board left to speak on. V2's ruling -
  // the scores ARE the operands, no gap arithmetic, no band recital - lives on
  // in the lead's "- next best X at N, a dead heat".)

  /// <summary>
  /// THE TIER-DEMOTION REFERRAL: there IS a leader, and what is shaky is the evidence
  /// under it (V3, ruled B5). The other half of the seam - this row did not arrive
  /// because two numbers touched, it arrived because the model would not seat a verdict
  /// it did not trust, and saying "a dead heat" over it was the Silvergrace lie.
  ///
  /// <para>The operands are the demoter and the evidence: who leads, at what, and the
  /// two facts that made the evidence thin. Neither evidence clause hedges - an absent
  /// one does not fire, and with both absent the sentence is the lead alone. No
  /// threshold is quoted: how few sales is too few and how old is stale are the model's
  /// business (<see cref="BoardConfidence.EvidenceStaleDays"/>), not the reader's.</para>
  /// </summary>
  internal static string ReferralThinEvidence(
    BoardPile leader, long leaderWorth, int recentSales, int readAgeDays)
  {
    var facts = new List<string>(2);
    if (recentSales > 0)
      facts.Add($"{recentSales} sale{(recentSales == 1 ? "" : "s")}");
    if (readAgeDays > 0)
      facts.Add($"read {readAgeDays}d ago");
    var tail = facts.Count > 0 ? $" - {string.Join(", ", facts)}" : "";
    return $"{ExitWord(leader)} leads at {leaderWorth:N0} on shaky evidence{tail}.";
  }

  /// <summary>
  /// THE THIN-CONTENDER REFERRAL (V4). A scored row with only ONE exit carrying
  /// evidence is not a race and never was - the old voice sent it to
  /// <see cref="ReferralNoData"/>, which told a reader holding one real number that
  /// nothing had ever been measured. This says the true thing: something was, and only
  /// one thing was.
  /// </summary>
  internal static string ReferralOneContender()
    => "only one exit has any evidence - the rest were never measured.";

  /// <summary>
  /// THE CONTRADICTED FALLBACK (V4). A Contradicted tier has exactly two doors - the
  /// sales axis disagreed, or the local-vs-community axis did
  /// (<see cref="BoardConfidence.BaseTier"/>) - and <see cref="CaseVoice.Contradiction"/>
  /// speaks only the first. So a Contradicted row whose sales axis is not the
  /// disagreement fell back from the OTHER one, and this names it rather than claiming
  /// nothing was ever measured.
  /// </summary>
  internal static string ReferralLocalCommunityDisagree()
    => "your own sales and the community read disagree.";

  /// <summary>
  /// The Alexander referral: the verdict, and the evidence standing against it.
  /// "Settled sales", not "your own sales" (red-pen 08-16): the witness is the tape -
  /// the board's history packet, everyone's sales - and the old prefix claimed the
  /// player's own ledger over evidence that is usually somebody else's receipts.
  ///
  /// <para><b>THE PREFIX IS THE STANDING ROW'S ONLY</b> (pen 7 / V10). A standing row's
  /// witness is the local tape, and "settled sales contradict the X verdict" is a claim
  /// that tape can carry. A BAG row's witness is the DC-wide community read, which is a
  /// different market and indicative only - so the bag arm says so itself, in its own
  /// sentence, and this prefix would put a local label on a DC number.</para>
  /// </summary>
  internal static string ReferralContradicted(BoardPile verdict, string contradiction, bool isStanding)
    => isStanding
      ? $"settled sales contradict the {ExitWord(verdict)} verdict: {contradiction}"
      : contradiction;

  /// <summary>The honest-thinness referral. Nothing was measured, and the case says so
  /// rather than dressing a default up as a finding.</summary>
  internal static string ReferralNoData()
    => "no evidence either way - never measured.";

  // ---- Arguments FOR an exit: one function per source type -------------------

  /// <summary>Your own last settled sale - the strongest receipt the plugin holds.</summary>
  internal static string OwnLastSale(long price, int ageDays, int? daysToSell)
  {
    var when = OnMarket.DayAge(ageDays);
    var sat = daysToSell is int d
      ? d == 0 ? ", and it cleared inside a day" : $", and it took {d}d to clear"
      : "";
    return $"You sold one at {price:N0}, {when}{sat}.";
  }

  /// <summary>
  /// Your own melt rollup - count, average, and the spread that says how much the
  /// average is worth.
  ///
  /// <para><b>A RANGE IS ONLY SPOKEN WHEN THE SPREAD IS REAL</b> (pen 10). One receipt
  /// has no interval: "between 1,246 and 1,246" is an invented confidence band, and a
  /// band drawn on top of a single point is the same sin as "Sells 200-200" (08-15
  /// shake). At one attempt - or at any count whose edges agree - the stats speak a
  /// FLAT STATEMENT, and the reader is told the number and how many made it.</para>
  /// </summary>
  internal static string MeltRollup(int attempts, long average, long low, long high)
    => attempts <= 1
      ? $"Your one melt of this returned {average:N0}."
      : high > low
        ? $"{attempts} of your own melts returned {average:N0} an attempt on average,"
          + $" between {low:N0} and {high:N0}."
        : $"{attempts} of your own melts returned {average:N0} an attempt, every time.";

  /// <summary>The ilvl-band prior, which fires only when you have never melted one -
  /// and says so in the same breath as the number.</summary>
  internal static string MeltBandPrior(int ilvl, long average)
    => $"You have never melted one. The ilvl {ilvl} band averages {average:N0} an attempt.";

  /// <summary>The band prior's companion (ruled 08-16, Drift's wording): the gap said
  /// flat, and the action that closes it. Melting is the only exit that converts this
  /// item's borrowed number into its own.</summary>
  internal static string MeltNeedsData()
    => "We need data to calculate desynth value for this item - melting one is how we get it.";

  /// <summary>The seal arithmetic in full: count, the gil it comes to, and the rate
  /// between them (~ owns the rounding). The rate's provenance lives in the option's
  /// own header tag and ONLY there - the old trailing clause re-hedged an
  /// already-hedged number (red-penned 08-15 night, both variants cut).</summary>
  internal static string SealRate(int seals, long worth, double gilPerSeal)
    => $"{seals:N0} seals - about {worth:N0} gil at ~{gilPerSeal:0.##} a seal.";

  /// <summary>The vendor counter. True, fixed, and a floor - which the sentence says,
  /// because a floor quoted like a market read is how a 1,200 beats a 41,200.</summary>
  internal static string VendorFloor(long price)
    => $"The NPC counter pays {price:N0} for one. That is a floor, not a market.";

  /// <summary>
  /// The round's own banked Look - the price recon decided it would post, said with
  /// its age. The same row the hawk spends ("Priced from your Look"), so the case
  /// and the act quote one number.
  ///
  /// <para><b>THE LINE CARRIES ITS DERIVATION</b> (pen 8a). The banked decision knows
  /// which anchor produced the number and which judgment stood behind that anchor; the
  /// sentence used to drop both and present the price bare, which is a number the reader
  /// has to take on faith from the one surface built to stop doing that. Either operand
  /// missing simply does not fire its clause.</para>
  ///
  /// <para><b>ELAPSED, not Span</b> (the mechanical pile, 08-21). This is an AGE, and
  /// <see cref="Durations.Span"/> is the "will take" grammar - it has no day rung, so a
  /// three-day-old Look read as "72.0h ago". The plugin's only grammar mismatch.</para>
  /// </summary>
  internal static string ReconLook(long ask, long ageSeconds, string anchor = "", string evidence = "")
  {
    var derivation = anchor.Length > 0 ? $" - {anchor}" : "";
    var judgment = evidence.Length > 0 ? $" {evidence.Trim()}" : "";
    return $"Your Look priced this lane at {ask:N0}, {Durations.Elapsed(ageSeconds)} ago{derivation}.{judgment}";
  }

  /// <summary>Recon's cached lane read - what the board was asking, and when.</summary>
  internal static string LaneRead(long median, int samples, int ageDays)
    => $"The lane read {median:N0} across {samples} listing{(samples == 1 ? "" : "s")},"
     + $" {OnMarket.DayAge(ageDays)}.";

  // ---- Caveats ON that evidence ---------------------------------------------

  /// <summary>The lane is under your own bar for enough.</summary>
  internal static string ThinLane(int samples, int minSamples)
    => $"Thin lane: {samples} listing{(samples == 1 ? "" : "s")} against your {minSamples}-sample bar.";

  /// <summary>
  /// HOW FEW MELTS IS TOO FEW for their average to mean much - the threshold behind
  /// <see cref="ThinMelts"/>. Once the sample-asymmetry trap's thin side; the trap
  /// died with the register (3b-1) and this consumer outlived it. (CaseTraps, the
  /// class both survivors lived in, dissolved 08-22 - nothing in it was a trap.)
  /// </summary>
  internal const int ThinAt = 2;

  /// <summary>
  /// THE CONTRADICTION AXIS IN PLAIN WORDS - the Alexander sentence, said the way a
  /// person would say it. Fires only on <see cref="Accord.Disagree"/>, and reads in
  /// opposite directions by verdict lean: a live market contradicts taking something
  /// OFF the board, and a dead one contradicts keeping it ON.
  ///
  /// <para><b>AND IT FORKS ON WHETHER THE ROW IS ON THE BOARD</b> (pen 7). A bag row
  /// was never listed, so "taking it off the board" and "keeping it listed" are claims
  /// about a state it is not in - the old voice told a bagged row its sales disagreed
  /// with churning it while it sat in the bags. The bag arm also names its WITNESS: a
  /// bag row's settled sales are the DC-wide community read, not the local tape, and the
  /// "~" prefix is the house provenance grammar for that rung (ruled 08-21, the trust
  /// gradient: your own Lamia sale is a real witness, the DC is a different market and
  /// indicative only).</para>
  /// </summary>
  /// <param name="verdict">The exit the evidence stands against - named as an ACT on a
  /// bag row ("melting it"), because that is the thing being disagreed with.</param>
  internal static string Contradiction(
    Accord axis, VerdictLean lean, int recentSales, double? velocityPerDay,
    bool isStanding = true, BoardPile? verdict = null)
  {
    if (axis != Accord.Disagree) return "";
    var act = verdict is BoardPile p ? ExitGerund(p) : "it";
    switch (lean)
    {
      case VerdictLean.OffMarket:
        return isStanding
          ? $"the market disagrees with taking it off the board: {recentSales} settled"
            + $" sale{(recentSales == 1 ? " says" : "s say")} somebody is buying these."
          : $"~DC sales disagree with {act}: {recentSales} sold there lately.";
      case VerdictLean.OnMarket:
        var rate = velocityPerDay is double v ? $" and it moves {v:0.##} a day" : "";
        return isStanding
          ? $"the market disagrees with keeping it listed: nothing has sold{rate}"
            + " - a listing here just sits."
          : $"~DC sales disagree with {act}: nothing has sold there{rate}.";
      default:
        return "";
    }
  }


  /// <summary>Too few of your own melts for the average to mean much.</summary>
  internal static string ThinMelts(int attempts)
    => $"Only {attempts} melt{(attempts == 1 ? "" : "s")} behind that average - one good roll moves it.";

  /// <summary>
  /// The read is past the stale window (V11). It says the READ is stale and stops
  /// there: where the line sits is <see cref="BoardConfidence.EvidenceStaleDays"/>'s
  /// business, and a caveat that recites the threshold behind it is teaching the config
  /// screen's lesson at the verdict.
  /// </summary>
  internal static string StaleEvidence(int ageDays)
    => $"That read is {ageDays}d old - stale.";

  // ---- The case's own facts, outside any one exit's argument ------------------

  /// <summary>
  /// WHEN ONE LAST CHANGED HANDS (V31, ruled B1 coda: a DISPLAY, not a gate). One seat,
  /// one grammar, fired by the existence of a date and nothing else - whose receipt it
  /// was does not change what the reader is asking. The price is deliberately absent:
  /// the option arguments already carry every number worth weighing, and a second price
  /// on the page would be a fourth contender nobody scored.
  /// </summary>
  internal static string LastSold(int ageDays)
    => $"Last sold {OnMarket.DayAge(ageDays)}.";

  /// <summary>The lane is too scattered to average honestly.</summary>
  internal static string WideSpread(double spread)
    => $"The lane is scattered - prices spread about {spread * 100:0}% around the middle.";

  /// <summary>How fast it moves, which is how long the gil takes to arrive.</summary>
  internal static string Velocity(double perDay)
    => perDay <= 0
      ? "Nothing has sold in the window - there is no measured rate at all."
      // "on this world" (F4, ruled 08-22): match the claim to the instrument -
      // this number is the home world's, and it sat two lines from a DC-scope
      // rate wearing the same words on the Dinosaur Leather Shoes case.
      : $"It clears about {perDay:0.##} a day on this world.";

  /// <summary>Who was under you when we last looked, and how long ago that was.</summary>
  internal static string Undercutters(int count, int readAgeDays)
    => $"{count} seller{(count == 1 ? "" : "s")} sat under you at the last board read,"
     + $" {OnMarket.DayAge(readAgeDays)}.";
}


/// <summary>
/// CASE ASSEMBLY - the pure function from banked evidence to the tribunal page's
/// content. No game, no storage, no ImGui (the BoardDetail mold, linked into
/// Scrooge.Tests).
///
/// <para><b>Every exit is a button; only the top three contenders argue</b> (ruled
/// 08-15, the middle path). The override grammar never narrows - <see cref="TriageCase.Exits"/>
/// carries every door - but arguments cost the reader attention, and spending it on a
/// fourth-place exit nobody is weighing is how the room the north star bought gets
/// filled with noise.</para>
/// </summary>
internal static class TriageCases
{
  /// <summary>Every exit a case can be ruled to, in the board's own order. The buttons.</summary>
  internal static readonly BoardPile[] ExitOrder =
  {
    BoardPile.List, BoardPile.Melt, BoardPile.Churn, BoardPile.PullAndVendor,
  };

  /// <summary>How many contenders get to argue.</summary>
  internal const int Contenders = 3;

  /// <summary>
  /// Builds the case. <paramref name="decided"/> is the human's verdict if he has
  /// already ruled it this walk - carried on the case so the page can draw its own
  /// answered state without a second lookup.
  /// </summary>
  internal static TriageCase Assemble(in CaseEvidence e, BoardPile? decided = null)
  {
    var argued = Argued(e);
    var evidence = e;
    return new TriageCase(
      e.Key,
      e.Title,
      Referral(e, argued),
      argued.Select((s, i) => Option(s, evidence, isLead: i == 0)).ToList(),
      ExitOrder,
      decided,
      LastSoldLine(e),
      VerdictLine(e, argued),
      // THE DISPUTE LEADS (ruled 08-23, the cotton-cloth case): when the
      // contradiction door is WHY the case exists, opening with a verdict the next
      // line impeaches reads as the case arguing with itself. The one exception to
      // F4's verdict-leads law - the dispute is the headline, the call comes second.
      DisputeLeads: e.Reason == ReferralReason.Contradicted);
  }

  /// <summary>
  /// THE VERDICT LEADS (F4, ruled 08-22 - the Dinosaur Leather Shoes: "It says a
  /// lot, but what does it all mean?"). Every case opens with one sentence - the
  /// call and the numbers that decided it - and the evidence below becomes the
  /// audit trail instead of the whole answer. Composing this sentence is the
  /// walk's whole job; the reader must never have to assemble it himself.
  /// Empty when nothing scored - the referral already says so, and a verdict
  /// over no numbers would be an invention.
  /// </summary>
  internal static string VerdictLine(in CaseEvidence e, IReadOnlyList<ExitScore> argued)
  {
    if (argued.Count == 0) return "";
    var lead = argued[0];
    var call = $"The call: {CaseVoice.ExitWord(lead.Exit)} at {lead.Worth:N0}";
    // The dead-heat qualifier rides the call itself (ruled 08-23, docket #1): the
    // old referral line repeated the same two numbers one line down, back to back.
    // One sentence carries both the numbers and the word for what they are; the
    // echo line stands down (see Referral's TooClose arm).
    var deadHeat = DeadHeatFoldsIntoTheCall(e, argued) ? ", a dead heat" : "";
    var second = argued.Count > 1
      ? $" - next best {CaseVoice.ExitWord(argued[1].Exit)} at {argued[1].Worth:N0}{deadHeat}"
      : " - nothing else scored";
    // The pace rides only a List call, in its own scope (this world - F4's
    // instrument-honesty law): how long the gil takes to arrive is part of the
    // call, not a caveat.
    var pace = lead.Exit == BoardPile.List && e.VelocityPerDay is double v && v > 0
      ? $". It clears ~{v:0.##}/day on this world."
      : ".";
    return call + second + pace;
  }

  /// <summary>
  /// ONE fact, two consumers (ruled 08-23, docket #1): the verdict lead appends
  /// ", a dead heat" exactly when the referral's echo line stands down. A single
  /// predicate keeps the fold and the drop from ever disagreeing - the failure
  /// mode would be a case with the qualifier in neither seat, or both.
  /// </summary>
  private static bool DeadHeatFoldsIntoTheCall(in CaseEvidence e, IReadOnlyList<ExitScore> argued)
    => e.Reason == ReferralReason.TooClose && e.RouterDeclined && argued.Count > 1;

  /// <summary>
  /// The case's last-sold seat (V31). Whichever witness has a date answers - the tape's
  /// when it is banked, your own settled receipt otherwise - and no date at all is an
  /// empty line the page simply does not draw.
  /// </summary>
  private static string LastSoldLine(in CaseEvidence e)
  {
    if (e.LastSoldAgeDays is int tape && tape >= 0)
      return CaseVoice.LastSold(tape);
    return e.OwnSalePrice > 0 ? CaseVoice.LastSold(e.OwnSaleAgeDays) : "";
  }

  /// <summary>
  /// The contenders, in the order the page argues them: the top three by scored worth,
  /// descending. On a CONTRADICTED row the contradicted verdict leads whatever it
  /// scored - the case is about that verdict, and burying it under the exits that beat
  /// it would answer a question nobody asked (spec: "the verdict, the evidence against
  /// it, next-best exit").
  /// </summary>
  internal static List<ExitScore> Argued(in CaseEvidence e)
  {
    var scores = (e.Scores ?? Array.Empty<ExitScore>())
      .OrderByDescending(s => s.Worth)
      .ThenBy(s => Array.IndexOf(ExitOrder, s.Exit))
      .ToList();

    var top = scores.Take(Contenders).ToList();
    if (e.Reason != ReferralReason.Contradicted || e.ContradictedExit is not BoardPile verdict)
      return top;

    var seat = top.FindIndex(s => s.Exit == verdict);
    if (seat < 0)
    {
      var pulled = scores.FirstOrDefault(s => s.Exit == verdict);
      if (pulled.Exit != verdict) return top;
      if (top.Count >= Contenders) top.RemoveAt(top.Count - 1);
      top.Insert(0, pulled);
      return top;
    }

    var lead = top[seat];
    top.RemoveAt(seat);
    top.Insert(0, lead);
    return top;
  }

  /// <summary>
  /// The opening sentence - WHY IT'S HERE, one per DISPATCHER (ruled B5). The scored
  /// reason forks twice more than the enum does, and deliberately: a scored row reaches
  /// the tribunal through the router's review band or through a tier demotion, and it
  /// reaches it with two contenders or with one. Four sentences, each fired by the fact
  /// that is actually true of the row in front of the reader.
  /// </summary>
  private static string Referral(in CaseEvidence e, List<ExitScore> argued)
  {
    switch (e.Reason)
    {
      case ReferralReason.Contradicted:
        var verdict = e.ContradictedExit ?? (argued.Count > 0 ? argued[0].Exit : BoardPile.Review);
        var plain = CaseVoice.Contradiction(e.ContradictionAxis, e.Lean, e.RecentSalesCount,
          e.VelocityPerDay, e.IsStanding, verdict);
        // A Contradicted tier that the SALES axis did not produce came from the other
        // disagree door, and the fallback names it (V4) rather than telling a reader
        // holding a live contradiction that nothing was ever measured.
        return plain.Length > 0
          ? CaseVoice.ReferralContradicted(verdict, plain, e.IsStanding)
          : CaseVoice.ReferralLocalCommunityDisagree();

      case ReferralReason.TooClose:
        // ONE CONTENDER IS NOT A RACE, and it is not a silence either (V4). The old
        // arm sent it to the no-data sentence, which denied the one real number on
        // the page.
        if (argued.Count < 2) return CaseVoice.ReferralOneContender();
        var ranked = argued.OrderByDescending(s => s.Worth).ToList();
        var leader = ranked[0];
        var runnerUp = ranked[1];
        // THE SEAM (ruled B5). The router declining inside its review band is a dead
        // heat; the tier refusing to seat a verdict is a lead on shaky evidence. Two
        // dispatchers, two sentences - the old voice spoke the first over both.
        // A router-declined case here ALWAYS folds (final pass, 08-23): this arm
        // guarantees TooClose and 2+ argued exits, which is the fold predicate
        // whole - the qualifier rides the verdict lead and the referral line
        // stands down. (The old ternary's ReferralTooClose arm was unreachable.)
        if (e.RouterDeclined)
          return "";
        return CaseVoice.ReferralThinEvidence(leader.Exit, leader.Worth, e.RecentSalesCount, e.LaneAgeDays);

      default:
        return CaseVoice.ReferralNoData();
    }
  }

  /// <summary>
  /// One exit's argument. The FOR lines come from that exit's own source type and no
  /// other - a melt case argued with a lane read would be citing a receipt about a
  /// different transaction.
  /// </summary>
  private static CaseOption Option(ExitScore score, in CaseEvidence e, bool isLead = false)
  {
    var forLines = new List<string>(2);
    var caveats = new List<string>(4);

    switch (score.Exit)
    {
      case BoardPile.List:
        // The freshest number leads (ruled 08-16): the round's own Look, when one
        // is banked - the same row the hawk posts real gil from minutes later.
        if (e.ReconAsk > 0)
          forLines.Add(CaseVoice.ReconLook(e.ReconAsk, e.ReconAskAgeSeconds,
            CaseVoice.LookAnchor(e.ReconAskAnchor), e.ReconAskEvidence));
        if (e.LaneMedian > 0 && e.LaneSampleCount > 0)
          forLines.Add(CaseVoice.LaneRead(e.LaneMedian, e.LaneSampleCount, e.LaneAgeDays));
        if (e.OwnSalePrice > 0)
          forLines.Add(CaseVoice.OwnLastSale(e.OwnSalePrice, e.OwnSaleAgeDays, e.OwnSaleDaysToSell));
        if (e.LaneSampleCount > 0 && e.MinSamples > 0 && e.LaneSampleCount < e.MinSamples)
          caveats.Add(CaseVoice.ThinLane(e.LaneSampleCount, e.MinSamples));
        if (e.LaneSampleCount > 0 && e.StaleDays > 0 && e.LaneAgeDays > e.StaleDays)
          caveats.Add(CaseVoice.StaleEvidence(e.LaneAgeDays));
        if (e.LaneSampleCount > 0 && e.LaneSpread > LanePricing.ScatteredBandPct)
          caveats.Add(CaseVoice.WideSpread(e.LaneSpread));
        // The pace stands down when this option IS the verdict lead: VerdictLine
        // already speaks it there ("It clears ~N/day on this world"), and two
        // pace sentences on one page is the docket-#1 echo wearing new numbers
        // (final pass, 08-23).
        // (v > 0 mirrors VerdictLine's own gate: a zero pace never folds, so the
        // caveat keeps that seat.)
        if (e.VelocityPerDay is double v && !(isLead && v > 0))
          caveats.Add(CaseVoice.Velocity(v));
        if (e.Undercutters > 0)
          caveats.Add(CaseVoice.Undercutters(e.Undercutters, e.BoardReadAgeDays));
        break;

      case BoardPile.Melt:
        if (e.MeltAttempts > 0)
        {
          forLines.Add(CaseVoice.MeltRollup(e.MeltAttempts, e.MeltAverage, e.MeltSpreadLow, e.MeltSpreadHigh));
          if (e.MeltAttempts <= CaseVoice.ThinAt)
            caveats.Add(CaseVoice.ThinMelts(e.MeltAttempts));
        }
        else if (e.MeltBandAverage > 0)
        {
          forLines.Add(CaseVoice.MeltBandPrior(e.MeltBandIlvl, e.MeltBandAverage));
          forLines.Add(CaseVoice.MeltNeedsData());
        }
        break;

      case BoardPile.Churn:
        if (e.SealCount > 0)
          forLines.Add(CaseVoice.SealRate(e.SealCount, score.Worth, e.SealGilRate));
        break;

      case BoardPile.PullAndVendor:
        if (e.VendorPrice > 0)
          forLines.Add(CaseVoice.VendorFloor(e.VendorPrice));
        break;
    }

    return new CaseOption(score.Exit, score.Worth, CaseVoice.Provenance(score.Provenance),
      forLines, caveats);
  }
}
