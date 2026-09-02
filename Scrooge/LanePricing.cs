using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// Lane pricing - the pricing spine. "Listings are what people want; sales are
/// what people paid." The lane (recency-weighted clearing price from settled
/// sales) is the pricing model; the board is positioning only.
///
/// Pure decision core in the RoutingRules mold: no game reads, no storage, no
/// statics. Inputs arrive as snapshots, verdicts come back as records. Compiled
/// into Scrooge.Tests as a linked source - the calibration tables in
/// [[Scrooge - Lane Pricing - Design]] are the fixture data.
/// </summary>

/// <summary>One settled sale feeding a lane (MB history packet or community history).</summary>
internal readonly record struct LaneSale(long UnitPrice, long Timestamp, bool IsHq);

/// <summary>
/// One live board listing at decision time. IsHq is the row's RAW quality -
/// the walk decides per-row whether that makes it cross-quality (a better item
/// standing in the same line, A12) or just another seller.
/// </summary>
internal readonly record struct LaneListing(long UnitPrice, bool IsOwn, bool IsHq = false);

/// <summary>Where the lane's evidence came from. Community lanes are always labeled.</summary>
internal enum LaneSource { Local, Community }

/// <summary>
/// The four honest answers (A10). Every older verdict - in lane, bait ignored,
/// dreamer ignored, lane owned, race declined, race joined - was this same
/// selection dressed differently, and the dressing was the part that lied.
/// </summary>
internal enum LaneOutcome
{
  /// <summary>Cut in front of the cheapest cluster on the board.</summary>
  Undercut,
  /// <summary>Stepped over crashers - a lone crazy, or a below-band pack outnumbered by the sellers behind it (A11) - then cut in front of the competitors.</summary>
  CrazySkipped,
  /// <summary>Nobody real in the queue: listed at the top of demonstrated clearing.</summary>
  EmptyBoard,
  /// <summary>No queue AND no tape - genuine silence. Never act on a guess wearing numbers.</summary>
  HeldThinHistory,
  /// <summary>The HQ side had nothing of its own: priced off the item's own NQ tape plus the HQ premium.</summary>
  PremiumFromNq,
}

/// <summary>A built lane: the recency-weighted clearing price and its confidence.</summary>
internal sealed record LaneModel
{
  public required double Median { get; init; }
  public required int SampleCount { get; init; }
  /// <summary>Weighted mean age of the evidence, in days - receipts carry this later.</summary>
  public required double WeightedAgeDays { get; init; }
  public required LaneSource Source { get; init; }
  /// <summary>
  /// The band's lower edge: weighted p25 of the evidence. NOT a confidence
  /// interval - sales are not iid and regimes shift - just an honest report of
  /// how much the evidence disagrees with itself (A4c). Equal to the price when
  /// one sale is all there is: a single sale disagrees with nothing.
  /// </summary>
  public double BandLow { get; init; }
  /// <summary>The band's upper edge: weighted p75, same walk (A4c).</summary>
  public double BandHigh { get; init; }
}

/// <summary>
/// Lane knobs. Ceiling reuses UpwardRepriceMultiplier, promoted: "3x what it
/// actually sells for = suspicious, in every direction."
///
/// <para>The band-less stand-ins - FloorPct and OwnedMult - are GONE (cleanup
/// pass). A lane with a going rate always carries a band: BuildLane draws it
/// from the same sorted walk the median comes off, and at n=1 the band IS the
/// price. There was no reachable path for either line to price on, so they were
/// two knobs the player could turn to move nothing. See
/// Scrooge.Tests/LaneAlwaysBandedTests.cs, which pins the invariant.</para>
/// </summary>
internal sealed record LaneConfig
{
  /// <summary>The hard ceiling, a rail on what we may WRITE: no listing above median x this.</summary>
  public double CeilingMult { get; init; } = 3.0;
  public int MinHistorySamples { get; init; } = 3;
  /// <summary>Recency half-life SEED (resolver v0). Seeded 30d from the 2026-07-13 sale-age query.</summary>
  public double HalfLifeDays { get; init; } = 30.0;
  /// <summary>
  /// How much extra the HQ side asks over its own NQ price when the HQ tape has
  /// nothing to say (Phase 3b, rung 2). Drift's slider and nothing else: the
  /// number IS the setting, not a seed a resolver later re-derives. There is no
  /// measured HQ/NQ ratio anywhere in the plugin and there is deliberately not
  /// going to be one - a measured ratio needs HQ sales, and the whole point of
  /// this rung is the case where there are none.
  /// </summary>
  public double HqPremiumPct { get; init; } = 0.25;
  /// <summary>
  /// THE SEAT RAIL (A12): the deepest seat the walk may take on its own
  /// authority, counted in foreign rows left standing in front of us. "Use good
  /// judgement, but don't be wrong" (Drift, 2026-08-05): spots 1-4 are the
  /// judgment zone, 6+ is wrong by definition - certainty must scale with queue
  /// depth, and the board never supplies enough of it for a deep seat. A walk
  /// that wants one is wrong; it takes the front of the line instead. A knob,
  /// not a law - the 4 is Drift's preference, not everyone's.
  /// </summary>
  public int SeatBudget { get; init; } = 4;
}

/// <summary>
/// THE BOARD THE WALK READ, counted (Movement 1, 2026-08-13). Everything the run
/// log's voice needs to say where the player stands and what stood around him -
/// and nothing it does not.
///
/// <para>Derived, never collected: every field falls out of the price-sorted
/// foreign queue and the lane the walk already had in hand, so a census costs one
/// pass over rows that were about to be walked anyway. It exists because the old
/// grammar buried these numbers in prose - a sentence nothing can count, and
/// nothing can re-layer into a tooltip either.</para>
///
/// <para>Seats are 1-BASED and count foreign rows only: seat 1 is the front of the
/// line. A seat is PRICE RANK, not queue position - rows asking the same price share
/// the seat, so a matched ask is still seat 1 (ruled 2026-09-01: "I'm fine being tied
/// as the cheapest; the point of the seat is to track when we aren't the cheapest").
/// <see cref="Seat"/> 0 means the walk wrote no price (a hold);
/// <see cref="PriorSeat"/> 0 means the item was not standing on the board at all.
/// A prior seat greater than <see cref="Sellers"/> is the back of the line.</para>
/// </summary>
internal readonly record struct LaneCensus(
  int Sellers,
  int Seat,
  int PriorSeat,
  /// <summary>Reachable rows from our seat up to the 3x rail - the line we joined.</summary>
  int Competitors,
  long? CompetitorFloor,
  long? CompetitorCeiling,
  /// <summary>Rows above the ceiling rail: real listings, unreachable ones.</summary>
  int AboveCeiling,
  double? BandLow,
  double? BandHigh,
  int SaleCount,
  double? PerDay,
  /// <summary>
  /// The band's middle - <see cref="LaneModel.Median"/>, carried because it is the
  /// third operand of the ONE spread measure (<see cref="LanePricing.BandSpread"/>)
  /// and the census already carries the other two. Null when the tape could not
  /// speak, which is the same silence <see cref="BandLow"/> keeps.
  /// </summary>
  double? Median = null,
  /// <summary>
  /// The multiple that drew the ceiling rail (<c>CeilingMult</c>, the player's
  /// UpwardRepriceMultiplier) - carried so the voice quotes the number that actually
  /// classified <see cref="AboveCeiling"/>'s rows (ruled 08-22: the knob is a
  /// slider, and a literal in the sentence lies the day it moves). Defaults to the
  /// knob's own default, so an unset fixture reads as an untouched config.
  /// </summary>
  double CeilingMult = 3.0);

/// <summary>The verdict: an anchor and its named story, with the evidence spelled out.</summary>
internal sealed record LaneDecision
{
  public required LaneOutcome Outcome { get; init; }
  /// <summary>Null = held (keep-price-and-flag at the pinch, don't-auto-price in a Hawk run).</summary>
  public long? Anchor { get; init; }
  /// <summary>True = anchor is a board listing to undercut; false = an absolute lane price, list as-is.</summary>
  public bool AnchorIsListing { get; init; }
  /// <summary>
  /// True = the anchor came from the CROSS-QUALITY rail, so it is a row of the
  /// BETTER quality. Matching it is not a listing, it is a corpse - the undercut
  /// step must beat it outright (see <see cref="LanePricing.StrictlyUnder"/>).
  /// </summary>
  public bool CrossQualityCapped { get; init; }
  /// <summary>How many lone crazies the walk stepped over to reach the anchor.</summary>
  public int CraziesSkipped { get; init; }
  /// <summary>How many foreign rows agree with the anchor's neighborhood, itself included. 1 = a row with no company.</summary>
  public int ClusterSize { get; init; }
  /// <summary>
  /// The crowd that WON the outnumbering test (A11): the competitors standing
  /// behind the stepped-over pack, all of them, not just the cluster we landed
  /// in front of. Banked because the cluster alone misreports the test - a
  /// Golden Silk step read "4 stepped, the line: 3" while the twenty sellers
  /// that actually convicted the pack lived only in the prose (Drift, 08-03).
  /// 0 = no pack step: the lone-crazy walk convicts on aloneness, not a headcount.
  /// </summary>
  public int CrowdBehind { get; init; }
  /// <summary>
  /// The stepped-over rows' gil span (they are always the queue's prefix), and
  /// the anchor cluster's. Banked so a receipt can answer "what WERE the
  /// crashers" in numbers (Drift, 08-02: "I find it hard to believe that there
  /// were 15 crashers on a single item" - he was right; the counts were a board
  /// bug, and only the prices could have said so at a glance). Null = no such
  /// rows in this verdict.
  /// </summary>
  public long? CrasherFloor { get; init; }
  public long? CrasherCeiling { get; init; }
  public long? ClusterFloor { get; init; }
  public long? ClusterCeiling { get; init; }
  /// <summary>
  /// THE WALK'S OWN DOUBT (Defer, 08-06). Not a confidence score and never a
  /// number: the named branch this decision came out of, when it came out of a
  /// shaky one. The spine has always KNOWN its thin moments - a dead heat it
  /// broke by taking the front of the line, a price written with no local tape
  /// behind it, a better-quality row it priced under because it could not be
  /// convicted - and every one of them used to end up as prose in
  /// <see cref="Evidence"/>, which nothing can count.
  ///
  /// <para><see cref="DoubtBranch.None"/> is the confident answer, and it is
  /// the normal one. See <see cref="DeferPlan"/> for what the board does with
  /// a branch that fired.</para>
  /// </summary>
  public DoubtBranch Doubt { get; init; }
  /// <summary>
  /// The board this verdict was reached against, counted (Movement 1). The run
  /// log's voice speaks off this and the hover layers off it; nothing branches on
  /// it. Default (all zeros) on any decision composed outside the walk - a census
  /// nobody took reads as "position unknown", which every composer already handles.
  /// </summary>
  public LaneCensus Census { get; init; }
  public required string Evidence { get; init; }
  /// <summary>
  /// What the 3.1 queue-doctrine candidate would have written on this same queue
  /// (ruled 08-23) - banked on the receipt, read by nothing that prices. Null when
  /// the board offered no real line to join (empty, or dreamers only), and on any
  /// decision composed outside the walk.
  /// </summary>
  public QueueDoctrine.DoctrineShadow? Shadow { get; init; }
}

internal static class LanePricing
{
  /// <summary>
  /// Build the lane from settled sales: recency-weighted median, the p25-p75
  /// band around it, sample count, weighted evidence age. Quality-aware - an HQ
  /// lane is built from HQ sales only. Long window with recency discounting,
  /// never a hard cutoff. Returns null when no quality-matching sales exist.
  ///
  /// <para>A dumb function over whatever list it is handed: the caller decides
  /// WHICH sales still vote (see RegimeSegment). Everything here reads the
  /// evidence it was given and nothing else.</para>
  /// </summary>
  public static LaneModel? BuildLane(IReadOnlyList<LaneSale> sales, bool isHq, LaneConfig cfg, long nowUnix, LaneSource source = LaneSource.Local)
  {
    var lane = new List<(double Price, double Weight, double AgeDays)>();
    foreach (var sale in sales)
    {
      if (sale.IsHq != isHq)
        continue;
      var ageDays = Math.Max(0, (nowUnix - sale.Timestamp) / 86400.0);
      var weight = Math.Pow(2, -ageDays / cfg.HalfLifeDays);
      lane.Add((sale.UnitPrice, weight, ageDays));
    }

    if (lane.Count == 0)
      return null;

    lane.Sort((a, b) => a.Price.CompareTo(b.Price));
    var totalWeight = lane.Sum(s => s.Weight);
    // One sorted walk, three crossings: the band is the median's own walk asked
    // two more questions, so it costs a comparison each and no second sort.
    var q = WeightedQuantiles(lane.Select(s => (s.Price, s.Weight)).ToList(), [0.25, 0.5, 0.75]);

    return new LaneModel
    {
      Median = q[1],
      BandLow = q[0],
      BandHigh = q[2],
      SampleCount = lane.Count,
      WeightedAgeDays = lane.Sum(s => s.Weight * s.AgeDays) / totalWeight,
      Source = source,
    };
  }

  /// <summary>
  /// Weighted quantiles off ONE cumulative-weight walk. <paramref name="ascending"/>
  /// must be sorted by price and <paramref name="quantiles"/> must be ascending
  /// too - the walk crosses each threshold in turn and never restarts.
  ///
  /// <para>The lane's median has always been "the price where half the weight
  /// sits below"; a quantile is the same sentence with a different fraction, so
  /// the band and the median are one measurement read at three depths. Shared
  /// with RegimeSegment's cliff test, which asks the same question of the older
  /// evidence.</para>
  ///
  /// <para>An empty list returns zeros - the caller has already decided whether
  /// no evidence is an answer.</para>
  /// </summary>
  internal static double[] WeightedQuantiles(IReadOnlyList<(double Price, double Weight)> ascending, double[] quantiles)
  {
    var results = new double[quantiles.Length];
    if (ascending.Count == 0)
      return results;

    var totalWeight = 0.0;
    foreach (var s in ascending)
      totalWeight += s.Weight;

    var top = ascending[^1].Price;
    for (var k = 0; k < results.Length; k++)
      results[k] = top; // never crossed = the whole distribution sat below it

    var cumulative = 0.0;
    var next = 0;
    foreach (var s in ascending)
    {
      cumulative += s.Weight;
      while (next < quantiles.Length && cumulative >= totalWeight * quantiles[next])
      {
        results[next] = s.Price;
        next++;
      }
      if (next >= quantiles.Length)
        break;
    }

    return results;
  }

  /// <summary>
  /// Two agreeing sales are not silence (A4, case studies 7 and 9). The count
  /// gate can only ever say "insufficient", so it flagged True Griffin's 1,005
  /// and 1,000 - six days old, half a percent apart - into triage while the ask
  /// froze at 290, and it re-flagged Kumbhira's 2,981/2,984 against a standing
  /// 2,985 that was already right. Band width IS the spread test: at n=2 a band
  /// no wider than a tenth of the going rate is two witnesses agreeing, and the
  /// lane speaks. A gate that cannot ratify keeps asking.
  ///
  /// <para>A tenth is the seed. It sits an order of magnitude under the real
  /// disagreements on the tape (True Griffin's own NQ side spans 23-600) and
  /// comfortably above the receipts' agreements (0.5% and 0.1%); receipts carry
  /// band width from day one, so this becomes measured-at-n rather than argued.</para>
  /// </summary>
  internal const double TightSpreadPct = 0.10;

  /// <summary>
  /// WHERE THE BOARD AS IT STANDS NOW READS AS SCATTERED - the boundary behind the
  /// case's lane caveat ("The lane is scattered - prices spread about 60% around the
  /// middle"). A different job from the gate above and therefore a different number: the
  /// gate decides whether two sales may speak at all, this one only picks an adjective
  /// for a board read. Drift's own examples set the scale - a third of the going rate wide
  /// is tight, more than twice it is "scattered - hard call". Half the going rate splits
  /// them with room on both sides.
  ///
  /// <para><b>THE WORD "SCATTERED" IS THIS INSTRUMENT'S</b> (3b-4). It used to be one
  /// constant across two of them: this board-now boundary and the sale tape's own
  /// confidence line, which is now <see cref="BoardConfidence.TapeNoiseCeiling"/>. They
  /// measure different evidence (live asks vs settled sales) and answer different
  /// questions (what to call the lane vs whether to seat a verdict unasked), and one
  /// number behind both meant tuning either moved the other silently. Two dials now,
  /// same value today - an un-weld, not a retune.</para>
  ///
  /// <para><b>A CONSTANT, NOT A KNOB, MOVED ONLY BY EVIDENCE</b> (RULED B1.5a
  /// 2026-08-21). The adjective is the model reporting what it measured; a slider on it
  /// would let the player tune the machine's description of its own evidence by feel.
  /// It retunes on override-grading evidence and on nothing else.</para>
  /// </summary>
  internal const double ScatteredBandPct = 0.50;

  /// <summary>
  /// HOW MUCH THE EVIDENCE DISAGREES WITH ITSELF, as a fraction of the going rate -
  /// ONE measure, read by two boundaries (<see cref="ScatteredBandPct"/> for the board
  /// as it stands now, <see cref="BoardConfidence.TapeNoiseCeiling"/> for the sale
  /// tape). The band's width over its own middle, and nothing else: the measure is
  /// shared on purpose, so the sentence a player reads and the tier the round acts on
  /// are computing the same thing even where they draw their lines apart.
  ///
  /// <para>0.0 IS THE ABSENCE ANSWER, deliberately, and it reads as tight everywhere
  /// it is compared. A lane with no band has not disagreed with itself; inventing a
  /// spread for it would let silence demote a verdict, which is the opposite of what
  /// silence means here.</para>
  /// </summary>
  internal static double BandSpread(double? bandLow, double? bandHigh, double? median)
    => bandLow is double low && bandHigh is double high && median is double mid
       && mid > 0 && high >= low
      ? (high - low) / mid
      : 0.0;

  /// <summary>
  /// SEED - "near", the width of a neighborhood (A10 rule 1). Two foreign rows
  /// are in the same neighborhood when the dearer sits within a quarter of the
  /// cheaper; a row with company is a CLUSTER member and is never skippable,
  /// however cheap it looks against the tape. Nobody engineers a crowd.
  ///
  /// <para>A quarter is dumb on purpose and picked off the two convicting boards
  /// rather than argued: Almasty Serge's crowd steps 1,500 -> 1,670 (+11%) and
  /// then in single-digit percents to 1,750, and the Amaurotine Chandelier's
  /// 31-deep race opens 500 -> 600 (+20%) before tightening to a few percent a
  /// step. A quarter clears both with room, and stops short of the gaps that
  /// separate a crowd from the next tier up (Almasty's 1,750 -> 2,900 is +66%).
  /// Once a narration-only number, now a DECISION constant three times over
  /// (doc refreshed 08-22, ruled): the lone-crazy company test reads it ("a
  /// crowd is never bait"), SF-P8's vouch reads it (our ask vouches for rows it
  /// is near), and the F1 gap test reads it at the pack's top edge (an anchor
  /// within the quarter of the pack ceiling is the pack's company - one queue,
  /// no step). The cluster NARRATION also holds its whole span to it (the
  /// Pillar ruling: span, not chain). A9's "did position 1 clear by the next
  /// pinch" is what re-cuts it, never a slider.</para>
  /// </summary>
  internal const double ClusterNearPct = 0.25;

  /// <summary>
  /// THE GAP TEST, POINTED AT OURSELVES (ruled 2026-08-23; the Grade 2 Gemsap
  /// receipt - Drift: "We appear as a 'crasher' to some"). A held ask that leads
  /// the reachable line by more than the cluster-company margin is exactly the
  /// anomaly the walk steps over on OTHER people's boards: it exerts no queue
  /// pressure (nobody chases a lone lowball down) and it sells first at the
  /// discount. Reachable competitors only - rows above the 3x rail are dreamers,
  /// and a gap to a wish is not a gap; a board with no real line raises nothing.
  /// Company kills it by construction: a seller within ClusterNearPct above our
  /// ask IS the competitor floor, and the inequality fails.
  /// </summary>
  internal static bool HeldAskReadsAsCrasher(long ask, in LaneCensus census)
    => ask > 0
       && census.Competitors > 0
       && census.CompetitorFloor is long floor
       && ask * (1 + ClusterNearPct) < floor;

  /// <summary>
  /// SEED - "far below" for the lone-crazy test (A10 rule 2): under HALF. A row
  /// is skippable nonsense only when it is alone AND sits under half the queue
  /// behind it AND under half the bottom of demonstrated clearing. Both halves
  /// must agree, because either one alone convicts honest sellers - the tape's
  /// half is what the A8 band did to Almasty's nine agreeing sellers, and the
  /// board's half alone would step over the first row of any market that is
  /// simply cheaper today than it was last week.
  ///
  /// <para>Half is where the live exhibits sit, comfortably on both sides:
  /// Ceremonial Longpole's lone 11,111 is 0.37x the 29,999 behind it and 0.41x
  /// the band's 27,000 floor; Tiger Leather's 3-gil row is 0.03x of both. The
  /// honest rows we must NOT skip clear it just as comfortably - Almasty's 1,500
  /// has company (never reaches this test), and Tiger's lone 497 sits at 0.99x
  /// the band floor, nowhere near half. Nothing in the receipts lands between
  /// 0.41 and 0.99, so the seed sits in a canyon rather than on a boundary.</para>
  /// </summary>
  internal const double FarBelowPct = 0.5;

  /// <summary>
  /// The one decision function, both pricing paths - A10, back to basics.
  /// <b>Undercut the cheapest cluster on the board.</b> A cluster is sellers
  /// with company: two or more foreign rows agreeing on a neighborhood. Ten
  /// cheap sellers are never bait, because nobody engineers a crowd. A row is
  /// skippable nonsense only when it is ALONE and far below both the queue
  /// behind it and the bottom of what the tape says clears - then we step over
  /// it and cut in front of the next real cluster. An empty board is the one
  /// case where the tape sets the number: list at the top of demonstrated
  /// clearing, because there is no queue to read.
  ///
  /// <para>The queue in front of us IS the evidence (Drift, 07-26). The A8
  /// classifier graded the board against the band and the band became a theory
  /// of value: nine independent Almasty sellers at 1,500-1,750 were all graded
  /// [bait] so a 4,000 memory could be right, and an Amaurotine Chandelier
  /// parked at 1,298 behind a 31-deep board on the strength of a tape whose
  /// newest sale was seventeen days old. The tape is a WITNESS now - it prices
  /// empty boards, arms the "far below memory" half of the crazy test, and
  /// prints in every narration - never the judge of a live queue.</para>
  ///
  /// <para>A11 sharpens the question into a classification (Drift, 08-02): every
  /// row is a crasher, a competitor, or a dreamer, and we take the best spot
  /// among the COMPETITORS. Dreamers sit above the 3x rail as always. A pack of
  /// two or more rows under the far-below margin is judged crowd vs crowd -
  /// outnumbered by the sellers behind it = crashers (step over the pack),
  /// outnumbering them = the wall IS the market (join its line), a dead heat =
  /// undercut everything and let a sale settle it. Aloneness stops immunizing
  /// company: two crashers huddling are not a cluster. The lone-crazy test
  /// survives for the single below-band row, where aloneness is the evidence.</para>
  ///
  /// <para>The thin gate still decides whether the TAPE may speak. It no longer
  /// decides whether WE may: a queue is evidence, so a thin tape holds nothing
  /// back when foreign rows are on the board. Genuine silence is an empty board
  /// AND a tape that cannot speak - that, and only that, is a hold.</para>
  ///
  /// <para>A12 adds the SEAT RAIL and moves the cross-quality question INTO the
  /// walk (Drift, 2026-08-05). The board is one physical queue - buyers take the
  /// cheapest row and go - so HQ rows beside an NQ lane are rows in the line,
  /// classified like everyone else but convicted only by their own tape. And
  /// certainty must scale with seat depth: a walk whose seat lands deeper than
  /// the budget is wrong by definition and takes the front of the line instead.
  /// "Use good judgement, but don't be wrong."</para>
  ///
  /// <para>Safety references stay lane-relative (absolute) - currentPrice is
  /// optional reprice context, never the baseline - so both doors are protected
  /// identically. The hard ceiling and the seat rail are the rails that must
  /// live here, because they decide what can be cut in front of; vendor floor,
  /// minimum listing price and the increase/undercut caps stay downstream in
  /// the pipeline where they always have been, unchanged.</para>
  /// </summary>
  /// <param name="hqLane">
  /// The better quality's own tape (A12), when pricing the NQ side of an item
  /// that can be HQ. The old cross-quality cap retconned the walk after the
  /// fact - "walking the board, making a decision, and then retconning that
  /// because of an HQ item seems wrong" (Drift, 2026-08-05) - so the HQ rows walk
  /// the board with everyone else now, and this lane is what convicts one:
  /// stepping over an HQ row takes "swagger and receipts", and the receipts are
  /// its own quality's clears. No HQ tape = no conviction = the row is a
  /// competitor we price under, which is the old cap's behavior surviving as
  /// the fail-closed default instead of an absolute law. Null on the HQ side
  /// and on items that cannot be HQ.
  /// </param>
  /// <param name="itemIsHq">
  /// The quality of the ITEM being priced - the discriminator for which rows
  /// are cross-quality. Distinct from the lane's quality on purpose: an HQ item
  /// priced off its NQ lane (HQ toggle off) still faces no better quality.
  /// </param>
  /// <param name="nqBandTop">
  /// The premium ladder's rung 2 (Phase 3b): the top of demonstrated NQ clearing
  /// for the SAME item, passed only when pricing the HQ side of an item whose HQ
  /// tape cannot speak. Null - the default - on the NQ side, whenever the HQ tape
  /// can speak for itself, and whenever the NQ tape cannot speak either; the rung
  /// then does not exist and today's answer (a hold) stands. See the ladder block
  /// at the bottom of this function.
  /// </param>
  public static LaneDecision Decide(
    IReadOnlyList<LaneListing> board, LaneModel? lane, double? velocityPerDay, LaneConfig cfg,
    long? currentPrice = null, LaneModel? hqLane = null, long? nqBandTop = null, bool itemIsHq = false)
  {
    // Our own listings are the thing being repriced, not the market. They never
    // count as company or as an anchor against ourselves - a stale own lowball
    // must be free to walk UP. UndercutSelf governs the undercut target in the
    // offerings path, not the queue read.
    // Cross = a strictly BETTER quality standing in the same line (A12). Only
    // ever true on the NQ walk - the physical board is one queue and no buyer
    // skips a cheap HQ, so HQ rows are genuinely in line; HQ-seeking buyers DO
    // skip NQ, which is why the HQ walk's board stays HQ-only at the caller.
    var queue = new List<(long Price, bool Cross)>();
    foreach (var listing in board)
      if (!listing.IsOwn)
        queue.Add((listing.UnitPrice, listing.IsHq && !itemIsHq));
    // Ties break toward the BETTER quality: at equal money every buyer takes
    // the better item first, so the cross row is genuinely ahead of a same-
    // price row of ours - and if it ends up the anchor, StrictlyUnder makes
    // matching it impossible, which is the whole point.
    queue.Sort((a, b) => a.Price != b.Price
      ? a.Price.CompareTo(b.Price)
      : b.Cross.CompareTo(a.Cross));

    // The better quality's memory arm: what HQ itself demonstrably clears. This
    // is the "receipts" half of stepping over an HQ row - without it, no HQ row
    // is ever convictable, whatever it asks.
    var hqSpeaks = !itemIsHq && CanSpeak(hqLane, cfg);
    double? hqMemoryLow = hqSpeaks ? hqLane!.BandLow : null;

    var n = lane?.SampleCount ?? 0;
    var tapeSpeaks = CanSpeak(lane, cfg);
    // THE no_tape BRANCH'S OPERAND (Defer, 08-06). Deliberately LOCAL-only: the
    // doubt this branch names is "no sales census of our own to judge the board
    // against", and a community lane is precisely the case where there is none.
    // The lane is still allowed to price off it - that is what the fallback is
    // for - it just does not get to claim the census exists.
    var localTapeSpoke = tapeSpeaks && lane!.Source == LaneSource.Local;

    // The band's edges are the tape's report of where things actually cleared. A
    // lane that speaks always has them: BuildLane reads all three quantiles off
    // one sorted walk, so a going rate and a band arrive together (at n=1 the band
    // is the price). See Scrooge.Tests/LaneAlwaysBandedTests.cs.
    //
    // The bottom of demonstrated clearing - the crazy test's memory arm. Null
    // when the tape cannot speak: then the queue is the only memory there is.
    double? memoryLow = tapeSpeaks ? lane!.BandLow : null;

    // Plain-language reason clauses only (the sentence after the [tag]). The
    // pipeline owns the item name, the old->new transition, and the tag; the
    // pure core only speaks the "why". "median" -> "the going rate / what it
    // sells for"; ratios -> printed prices; never make the reader multiply.
    var band = BandLine(lane, tapeSpeaks, velocityPerDay);

    // The hard ceiling - a RAIL, not a verdict (A10 rule 4). It says nothing
    // about whether a seller is honest; it says no listing we write may sit
    // above 3x what the thing demonstrably sells for. Cutting in front of a
    // 55,000,000 row means writing 54,999,899, and the Highland Fence is the
    // receipt for what that costs. Rows above it are simply unreachable: we
    // list at the line instead, which is still ahead of them in the queue.
    // Without a tape there is no going rate and therefore no rail.
    double? ceiling = tapeSpeaks ? lane!.Median * cfg.CeilingMult : null;

    // Sorted ascending, so the rail is one index: everything from there up is
    // unreachable. Counted whether or not the walk gets that far - a run-log
    // line that undercuts row 1 while two 55M dreams sit behind it should still
    // say the dreams are there.
    var railIndex = queue.Count;
    if (ceiling is double cap)
      for (var i = 0; i < queue.Count; i++)
        if (queue[i].Price > cap) { railIndex = i; break; }
    var overRail = queue.Count - railIndex;

    // WHERE THE PLAYER STANDS, BEFORE AND AFTER (Movement 1). Both seats come off
    // the snapshot already in hand - nothing new is collected and no second board
    // read is implied. The prior seat is the one number the old grammar never had
    // and the voice cannot do without: "moving you from the back of the line to
    // 3rd" is a claim about two positions, and only one of them is the anchor's.
    var priorSeat = SeatOf(queue, currentPrice);

    // THE DOCTRINE'S SHADOW (ruled 08-23): what the 3.1 queue-doctrine candidate
    // would write on this same queue, banked on the receipt beside what actually
    // lists. Computed here because this is the one seat that holds the sorted
    // queue and the rail count together; nothing below reads it to price.
    var shadow = QueueDoctrine.Evaluate(queue, overRail, cfg.CeilingMult, ClusterNearPct);
    LaneCensus Census(int seat)
    {
      // A hold took no seat, so its census reports the WHOLE reachable line rather
      // than a slice of it - there is no "from here up" without a here.
      var from = seat > 0 ? Math.Min(seat - 1, railIndex) : 0;
      var competitors = railIndex - from;
      return new LaneCensus(
        Sellers: queue.Count,
        Seat: seat,
        PriorSeat: priorSeat,
        Competitors: competitors,
        CompetitorFloor: competitors > 0 ? queue[from].Price : null,
        CompetitorCeiling: competitors > 0 ? queue[railIndex - 1].Price : null,
        AboveCeiling: overRail,
        CeilingMult: cfg.CeilingMult,
        BandLow: tapeSpeaks ? lane!.BandLow : null,
        BandHigh: tapeSpeaks ? lane!.BandHigh : null,
        SaleCount: n,
        PerDay: velocityPerDay,
        Median: tapeSpeaks ? lane!.Median : null);
    }

    // --- Classify the queue: crasher / competitor / dreamer (A11) -----------
    // Dreamers are already behind the rail. Rows under the same far-below
    // margin the lone-crazy test has always used form the PACK, and a pack of
    // two or more stops reading as a cluster - two crashers huddling used to
    // immunize each other (the Stuffed Alphas' 50/60 under a real 854+ crowd,
    // 08-02). Crowd vs crowd decides what the pack IS: outnumbered by the
    // sellers behind it, it is crashers and we step over all of it; outnumbering
    // them, the wall IS the market and we take our spot in that line; a dead
    // heat is undercut everything - front of the whole queue, let a sale settle
    // the argument. Zero new constants: "two or more = company" is the cluster
    // definition A10 already had, and the margin is the memory arm unchanged.
    // A single below-band row is not a crowd and keeps the full three-part
    // lone-crazy test below - aloneness is exactly what that test reads.
    // A12 amends the pack's membership test only: each row is convicted against
    // ITS OWN quality's tape. An NQ row far below NQ clearing is the same
    // crasher it always was; an HQ row is convicted by HQ clears or not at all
    // ("swagger and receipts") - an unconvictable HQ row TERMINATES the pack,
    // because a better item we cannot call nonsense is a competitor standing at
    // the front, and pricing under it is the old cap's truth in its honest place.
    var packSize = 0;
    var hqInPack = 0;
    if (memoryLow is double bandFloor)
      while (packSize < railIndex
             && !AskVouchesFor(queue[packSize].Price, currentPrice, memoryLow)
             && (queue[packSize].Cross
               ? hqMemoryLow is double hqFloor && queue[packSize].Price < hqFloor * FarBelowPct
               : queue[packSize].Price < bandFloor * FarBelowPct))
      {
        if (queue[packSize].Cross) hqInPack++;
        packSize++;
      }

    var skipped = 0;
    var idx = -1;
    var crowdBehind = 0;
    var deadHeat = false;
    string? packStory = null;
    if (packSize >= 2)
    {
      var crowd = railIndex - packSize;
      // THE GAP TEST (F1, ruled 08-22 - the Caligae wall). A real dump is
      // SEPARATED from the line: 47/47 then daylight then 150. The tape test
      // above convicts cheapness but never looks at geometry, and the headcount
      // that follows happily stepped a "pack" whose ceiling stood ONE GIL under
      // the anchor (7,999 -> 8,000 on a 16k lane) - landing the write inside the
      // very rows it claimed to step. So the step needs daylight at the pack's
      // TOP edge: the anchor must not be the pack's company, and "company" is
      // the same quarter the lone-crazy test already reads (ClusterNearPct -
      // the constant's third decision seat after the company test and SF-P8's
      // vouch). No gap = one continuous queue, and for one continuous queue the
      // law is already ruled (Tumbleclaw, F10): the queue IS the market - join
      // its line at the front.
      var packHasGap = packSize < railIndex
        && queue[packSize].Price > queue[packSize - 1].Price * (1 + ClusterNearPct);
      if (crowd > packSize && !packHasGap)
      {
        idx = 0;
        packStory = $"the {packSize} cheap sellers from {MoneyText.Gil(queue[0].Price)} run straight into the line behind them — no gap to step over, one continuous queue; ";
      }
      else if (crowd > packSize)
      {
        // Outnumbered AND separated: crashers. Step over the whole pack; the
        // cheapest competitor is the anchor - a spot behind two crashers at
        // 50/60 IS front of line, the raw queue just doesn't know it yet.
        skipped = packSize;
        idx = packSize;
        // The number that won the test, kept for the cell surfaces (08-03): the
        // story banked only the cluster we undercut, so the inline note could
        // not say what the pack was actually outnumbered BY.
        crowdBehind = crowd;
        var hqReceipt = hqInPack > 0
          ? $" ({hqInPack} of them HQ — HQ itself clears {MoneyText.Gil((long)Math.Round(hqMemoryLow!.Value))}+)"
          : "";
        packStory = $"stepped over {packSize} crashers from {MoneyText.Gil(queue[0].Price)}{hqReceipt} — outnumbered by the {crowd} sellers behind them; ";
      }
      else
      {
        idx = 0;
        if (crowd == packSize)
        {
          // THE dead_heat BRANCH. The outnumbering test came back tied, so
          // nothing about the board convicted the pack OR crowned it - we broke
          // the tie by taking the front of the whole queue and letting a sale
          // settle it. That is exactly the shape Defer exists to flag.
          deadHeat = true;
          packStory = $"a dead heat — {packSize} sellers far below what clears, {crowd} behind them; a sale settles the argument: ";
        }
        else if (crowd > 0)
          packStory = $"the {packSize} cheap sellers from {MoneyText.Gil(queue[0].Price)} outnumber the {crowd} behind them — the wall IS the market; ";
        // crowd == 0: the pack is the whole queue. Nothing to compare it
        // against, and nobody engineers a crowd - a plain undercut.
      }
    }
    else
    {
      // --- Walk the queue from the front, stepping over lone crazies --------
      // The crazy test reads the WHOLE queue (rows above the rail are still
      // part of "the rest of the board"); only the anchor has to sit under the
      // rail.
      for (var i = 0; i < railIndex; i++)
      {
        if (IsLoneCrazy(queue, i, memoryLow, hqMemoryLow, currentPrice))
        {
          skipped++;
          continue;
        }
        idx = i;
        break;
      }
    }

    // --- THE SEAT RAIL (A12) ------------------------------------------------
    // "Use good judgement, but don't be wrong" (Drift, 2026-08-05). Certainty
    // must scale with the depth of the seat: being wrong at spot 1 costs an
    // undercut's worth of gil, being wrong at spot 15 costs the wait behind the
    // whole pile - and no one rounds the discount bin; buyers eat the queue in
    // strict price order. A walk that wants a seat deeper than the budget is
    // wrong by definition, so it takes the front of the line instead. The pile
    // grew followers; the front of the line just IS there now. On the pack
    // path "more behind than ahead" already held (the step only fires when the
    // crowd outnumbers the pack), so depth is the one test left to fail.
    string? railStory = null;
    var railPile = 0;
    long railRealFloor = 0;
    if (idx > cfg.SeatBudget)
    {
      railPile = idx;
      railRealFloor = queue[idx].Price;
      railStory = $"the crashers have friends — {railPile} cheap row{(railPile == 1 ? "" : "s")} {SteppedFrom(railPile, queue[0].Price)}, and a seat behind all of them is no seat (budget {cfg.SeatBudget}); the pile is the line now: ";
      idx = 0;
      skipped = 0;
      crowdBehind = 0; // the step never happened, so no crowd won anything
      packStory = null;
      deadHeat = false; // and the tie it broke was never the reason for this price
    }

    if (idx >= 0)
    {
      var anchor = queue[idx].Price;
      // A cross-quality anchor is a BETTER item standing in front - matching it
      // is a corpse, not a position (the Gemsap at 175 beside fifty HQ walls at
      // 123). The flag drives StrictlyUnder downstream, exactly as the old
      // cap's re-anchoring did - the rail moved INTO the walk; the invariant on
      // what we write never changed.
      var anchorCross = queue[idx].Cross;
      // How far the neighborhood reaches above the anchor. Rows below it were
      // all skipped, and a skipped row cannot be near this one - a row with
      // company is never skippable - so the cluster only ever grows upward.
      // SPAN, NOT CHAIN (Pillar, ruled 08-22): neighbour-to-neighbour links let
      // "5 sellers from 2,222 to 3,000" claim one crowd across a 35% stretch
      // while the tape agreed at the top of it. A cluster's whole span is held
      // to the same quarter one link is - rows past it are the next tier's
      // story, not this one's.
      var cluster = 1;
      for (var j = idx; j + 1 < queue.Count - overRail && queue[j + 1].Price <= queue[idx].Price * (1 + ClusterNearPct); j++)
        cluster++;

      // NAME THE ANCHOR the price actually came from, always. A run-log line
      // reading "ignored 10 listings at 6,001+" while quietly listing at 200
      // hid the ~201 target that produced the number (07-25, finding #6) - the
      // reader could not tell a good decision from a broken one.
      var story = cluster > 1
        ? $"undercut the cheapest {(packStory == null ? "cluster on the board" : "competitors")} — {cluster} sellers {SpanFromTo(anchor, queue[idx + cluster - 1].Price)}"
        : packStory != null
          ? $"undercut the cheapest competitor ({MoneyText.Gil(anchor)})"
          : $"undercut the cheapest listing ({MoneyText.Gil(anchor)}) — no company on the board, but not far enough below the rest of it to be nonsense";
      if (anchorCross)
        story += " — the better quality, standing in our line; nobody pays more for the worse item";
      if (packStory != null)
        story = packStory + story;
      else if (railStory != null)
        story = railStory + story + $"; the real line ({MoneyText.Gil(railRealFloor)}+) sits {railPile} row{(railPile == 1 ? "" : "s")} back";
      else if (skipped > 0)
        story = $"stepped over {skipped} lone lowball{(skipped == 1 ? "" : "s")} {SteppedFrom(skipped, queue[0].Price)} with nobody near {(skipped == 1 ? "it" : "them")}; " + story;
      // ONE LISTING SITS (08-15 shake: "1 listing ... sit"). The noun was already
      // counted; the verb has to agree with the same number.
      if (overRail > 0)
        story += $"; {overRail} listing{(overRail == 1 ? "" : "s")} at {MoneyText.Gil(queue[queue.Count - overRail].Price)}+ sit{(overRail == 1 ? "s" : "")} above the 3x ceiling and cannot be cut in front of";

      return new LaneDecision
      {
        Outcome = skipped > 0 ? LaneOutcome.CrazySkipped : LaneOutcome.Undercut,
        Shadow = shadow,
        Anchor = anchor,
        AnchorIsListing = true,
        CrossQualityCapped = anchorCross,
        CraziesSkipped = skipped,
        ClusterSize = cluster,
        CrowdBehind = crowdBehind,
        // Skipped rows are the queue's prefix by construction (both walks stop
        // at the first row they keep), so their span is queue[0]..queue[skipped-1].
        CrasherFloor = skipped > 0 ? queue[0].Price : null,
        CrasherCeiling = skipped > 0 ? queue[skipped - 1].Price : null,
        ClusterFloor = anchor,
        ClusterCeiling = queue[idx + cluster - 1].Price,
        // The branch, in the walk's own order of specificity: the tie we broke
        // outranks the corpse we priced under, which outranks the census we
        // never had. Each names a narrower thing than the one after it, and the
        // narrowest true statement is the one worth counting.
        Doubt = deadHeat ? DoubtBranch.DeadHeat
          : anchorCross ? DoubtBranch.UnconvictableHq
          : !localTapeSpoke ? DoubtBranch.NoTape
          : DoubtBranch.None,
        // THE SEAT IS THE WRITE'S, not the anchor's queue index (F1, ruled
        // 08-22): the sentence "moving you to 5th" once quoted idx + 1 while
        // the strictly-under write landed at a different seat, contradicting
        // itself in one line. SeatOf against the price we are about to write
        // (one gil under a listing anchor - the strictest honest reading, and
        // every rows-below-anchor case the walk leaves standing is far enough
        // down that the humanized cut cannot cross it).
        Census = Census(SeatOf(queue, anchor - 1)),
        Evidence = $"{story}. {band}",
      };
    }

    // --- Nobody real in the queue -------------------------------------------
    // Either the board is empty or every row on it was a lone crazy. The tape
    // is the only witness left, and this is the one job A10 leaves it: list at
    // the top of demonstrated clearing. The ceiling stays the hard cap.
    if (tapeSpeaks)
    {
      var line = (long)Math.Round(Math.Min(lane!.BandHigh, lane.Median * cfg.CeilingMult));
      // No cap clause here anymore (A12): the board is one queue, so an HQ row
      // that could have capped this line is IN the queue - either it terminated
      // the walk as a competitor (never reaching this branch) or it was stepped
      // with receipts, and a band-top listing behind a convicted corpse is the
      // seat we meant to take.
      return new LaneDecision
      {
        Outcome = LaneOutcome.EmptyBoard,
        Shadow = shadow,
        Anchor = line,
        AnchorIsListing = false,
        // A community lane priced this - our own census never spoke - so the
        // number is honest and thin at the same time, which is the branch.
        Doubt = localTapeSpoke ? DoubtBranch.None : DoubtBranch.NoTape,
        CraziesSkipped = skipped,
        CrasherFloor = skipped > 0 ? queue[0].Price : null,
        CrasherCeiling = skipped > 0 ? queue[skipped - 1].Price : null,
        // An absolute price takes whatever seat it buys - the rows it stepped over
        // are still standing, so an "empty board" listing is not automatically 1st.
        Census = Census(SeatOf(queue, line)),
        // THE TENSE HAS TO AGREE WITH THE ACT (Drift, 08-03, the Ink row). This
        // branch used to open "nothing real in the queue" and then list the
        // rows it had just stepped over - one clause saying nobody was there,
        // the next naming who was. If we stepped over something, the board was
        // NOT empty: lead with the step, and let "nobody real behind them" be
        // the claim that survives. Only a genuinely empty queue may say so.
        Evidence = (queue.Count == 0
          ? $"an empty board — nobody in the queue. Listed at the top of what it sells for, {MoneyText.Gil(line)}."
          : $"{(skipped > 0 ? $"stepped over {skipped} lone lowball{(skipped == 1 ? "" : "s")} {SteppedFrom(skipped, queue[0].Price)} - nobody real behind {(skipped == 1 ? "it" : "them")}" : "nothing reachable in the queue")}"
            + $"{(overRail > 0 ? $", and {overRail} listing{(overRail == 1 ? "" : "s")} at {MoneyText.Gil(queue[queue.Count - overRail].Price)}+ above the 3x ceiling" : "")}"
            + $". Listed at the top of what it sells for, {MoneyText.Gil(line)}.")
          + $" {band}",
      };
    }

    // --- Rung 2 of the premium ladder (Phase 3b) ----------------------------
    // We are here because the HQ side has nothing of its own: no real rows to
    // cut in front of and an HQ tape that cannot speak. But the SAME ITEM's NQ
    // tape can, and an HQ item is worth more than its NQ twin - that is the one
    // thing about HQ that is true without measuring anything. So take the top of
    // demonstrated NQ clearing and add Drift's premium. ONE multiply.
    //
    // Deliberately not a measured ratio: measuring HQ/NQ needs HQ sales, and the
    // whole reason this rung exists is that there are none. The slider IS the
    // number (Drift, 07-26) - no resolver, no readout, nothing that pretends to
    // have learned something it cannot have learned.
    //
    // It is the LAST rung, and every earlier one outranks it: a live cluster is
    // real money in a real queue, and an HQ tape that speaks is the item's own
    // evidence. Both return above this line, so neither can ever reach it.
    // One-directional too - the NQ side never asks a premium over HQ; that
    // direction is the 3a cap's job, and it points the other way.
    if (nqBandTop is long nqTop && nqTop > 0)
    {
      var premiumPrice = (long)Math.Round(nqTop * (1 + cfg.HqPremiumPct));
      var pct = Math.Round(cfg.HqPremiumPct * 100);
      // Same tense rule as the empty-board branch above: a step-over is a thing
      // that happened to somebody, so the sentence names them. (The old shape
      // could also print "0 lone lowballs" on a board that was nothing but
      // dreamers - a count of a thing it had just denied existed.)
      var boardClause = queue.Count == 0
        ? "nothing on the board"
        : skipped > 0
          ? $"stepped over {skipped} lone lowball{(skipped == 1 ? "" : "s")} {SteppedFrom(skipped, queue[0].Price)} - nothing real behind {(skipped == 1 ? "it" : "them")}"
          : "nothing reachable on the board";
      var premiumClause = pct <= 0
        ? $"with no premium set — the same money: {MoneyText.Gil(premiumPrice)}"
        : $"plus your {pct:0}% premium: {MoneyText.Gil(premiumPrice)}";
      return new LaneDecision
      {
        Outcome = LaneOutcome.PremiumFromNq,
        Shadow = shadow,
        Anchor = premiumPrice,
        AnchorIsListing = false,
        // Rung 2 exists BECAUSE this quality has no tape of its own - the
        // branch is the rung's own premise, said in the vocabulary the board
        // can count.
        Doubt = DoubtBranch.NoTape,
        CraziesSkipped = skipped,
        CrasherFloor = skipped > 0 ? queue[0].Price : null,
        CrasherCeiling = skipped > 0 ? queue[skipped - 1].Price : null,
        Census = Census(SeatOf(queue, premiumPrice)),
        Evidence = $"{boardClause}, and no HQ sales on record — priced off its own NQ sales history, "
          + $"which clears up to {MoneyText.Gil(nqTop)}, {premiumClause}.",
      };
    }

    // --- Genuine silence: no queue to read AND no tape to read it with ------
    return new LaneDecision
    {
      Outcome = LaneOutcome.HeldThinHistory,
      Shadow = shadow,
      Anchor = null,
      // Genuine silence IS the no-tape branch at its purest. The hold itself
      // does not change (the pinch keeps the standing ask, the Hawk still
      // refuses to invent a number) - the branch only lets the row say why.
      Doubt = DoubtBranch.NoTape,
      CraziesSkipped = skipped,
      // Seat 0: the walk wrote nothing, so it took nowhere. The voice reads that
      // as "position unknown" and says nothing about the line at all.
      Census = Census(0),
      Evidence = $"{(queue.Count == 0 ? "nothing on the board" : $"nothing but {skipped} lone lowball{(skipped == 1 ? "" : "s")} on the board")}"
        // "only 0 sales on record" was a count pretending to be evidence. A silent
        // tape has no sales to be stingy with - say the window came back empty.
        + $" and {(n == 0 ? "no sales in window" : $"only {n} sale{(n == 1 ? "" : "s")} on record")}"
        + $", need {cfg.MinHistorySamples} to judge the board. Flagged for a ruling.",
    };
  }

  /// <summary>
  /// The other half of the cross-quality rail: the invariant on the price we
  /// actually WRITE. The walk's half can only hand the pipeline an anchor and a
  /// flag saying that anchor is a better item standing in front (A12); what
  /// turns an anchor into a listing is the undercut step, and the
  /// undercut step is quality-blind - it will happily copy a price exactly.
  ///
  /// <para>Against a same-quality row that is a legitimate move: matching the
  /// cheapest seller is a real market position, which is the whole point of
  /// Gentleman's Match, and nothing here touches it. Against the BETTER quality
  /// it is not a position at all. Sitting level with a strictly superior HQ is
  /// the exact listing this rail was built to kill - the Gemsap at 175 was dead
  /// because no buyer takes the worse item for equal money, and a rail that
  /// re-anchors to 123 only to write 123 has done nothing but move the corpse.
  /// </para>
  ///
  /// <para>Applied to the OUTPUT rather than to each undercut mode, because
  /// every mode can land on the anchor - Gentleman's Match by design, Clean Numbers
  /// rounding back up under a small price, an own listing by early return. One
  /// guard over the answer closes all of them, and
  /// closes the next one nobody has written yet. Floor-guarded at 1 gil; the
  /// vendor floor and minimum-listing guards downstream still get their say.</para>
  /// </summary>
  internal static long StrictlyUnder(long price, long anchor)
    => price >= anchor ? Math.Max(1, anchor - 1) : price;

  /// <summary>
  /// The Sarcenet clamp (Phase 3b): a borrowed answer may never price above
  /// everything that has actually cleared HERE. Community history is the DC's
  /// tape, not our server's, and it is only ever consulted because our own tape
  /// was too thin to price - so it arrives at exactly the moment we have the
  /// least ability to sanity-check it. Sarcenet is the receipt: the DC's number
  /// justified a 2,500 ask on an item whose local clears sat at 600-1,500.
  ///
  /// <para>The local tape is thin, not silent - one honest local clear still
  /// knows something the DC does not, namely what a buyer on THIS server paid.
  /// Nearest evidence wins on the ceiling even when it lost on the price.</para>
  ///
  /// <para>Only ABSOLUTE community answers are clamped. A board anchor is a live
  /// local row - real money a local buyer can spend right now - and it did not
  /// come from the community lane at all, so clamping it would be clamping the
  /// board with the tape, which is the model A10 threw out. And with no local
  /// settled sales there is no local clearing to clamp against: the community
  /// number stands, because nothing better exists.</para>
  ///
  /// <para>Local-sourced decisions are untouched by construction - there is
  /// nothing borrowed about them.</para>
  /// </summary>
  internal static LaneDecision ClampToLocalClearing(LaneDecision decision, LaneSource? source, long maxLocalSettled)
  {
    if (source != LaneSource.Community || maxLocalSettled <= 0)
      return decision;
    if (decision.AnchorIsListing || decision.Anchor is not long priced || priced <= maxLocalSettled)
      return decision;

    // The clamp edits a sentence already composed: "Listed at the top of what it
    // sells for, 1,995" upstream + "clamped to 1,550" here read as a self-
    // contradiction (Stonegold, 08-02 shake). The clamp changes the number, so it
    // changes the number IN the sentence - targeted at the exact pre-clamp text,
    // a no-op on any evidence shape that never spoke it.
    return decision with
    {
      Anchor = maxLocalSettled,
      // THE SEAT WAS COMPUTED FOR A PRICE THAT NO LONGER EXISTS. The clamp only
      // ever moves the ask DOWN, so the real seat is this one or better - but
      // "or better" is not a position, and the run log says where you stand. The
      // board is not in scope here to re-walk, so the claim is dropped rather
      // than restated: a line that cannot honestly name a seat says nothing
      // about the line at all.
      Census = decision.Census with { Seat = 0 },
      Evidence = decision.Evidence
        .Replace($"Listed at the top of what it sells for, {MoneyText.Gil(priced)}",
                 $"Listed at the top of what it sells for, {MoneyText.Gil(maxLocalSettled)}")
        + $" Community history says {MoneyText.Gil(priced)}, but it has never locally cleared above "
        + $"{MoneyText.Gil(maxLocalSettled)} — clamped to that.",
    };
  }

  /// <summary>
  /// The 1-based seat a listing at <paramref name="price"/> takes in the foreign
  /// queue: every row asking strictly less stands ahead of it. Ties go to US -
  /// matching the cheapest seller IS a position (Gentleman's Match), and the one
  /// place matching is not a position, the cross-quality anchor, is closed by
  /// <see cref="StrictlyUnder"/> before a price ever reaches this queue.
  ///
  /// <para>Zero for no price at all: an item that is not on the board has no seat
  /// to report, which is a different statement from standing at the front of the
  /// line and must never render as one.</para>
  /// </summary>
  private static int SeatOf(IReadOnlyList<(long Price, bool Cross)> ascendingQueue, long? price)
  {
    if (price is not long asked || asked <= 0)
      return 0;
    var ahead = 0;
    foreach (var row in ascendingQueue)
      if (row.Price < asked)
        ahead++;
    return ahead + 1;
  }

  /// <summary>
  /// OUR OWN ASK VOUCHES FOR ITS NEIGHBOURS (SF-P8; Drift, live shake 2026-08-15:
  /// "hard to call 2 prices a low ball and then be 1 gil behind them"). The
  /// conviction tests are ABSOLUTE - a row against the tape - and the Gargantua
  /// Leather board caught the line slicing through the front of the queue: 493 and
  /// 494 convicted as crashers off a 989 band floor while our own ask stood at 495,
  /// one gil back, narrating the step. If two gil below us is nonsense, so are we;
  /// if ours is a legitimate ask, they are competitors. One board cannot be read
  /// both ways.
  ///
  /// <para>Both words are ones the house already owns: "near" is the cluster's own
  /// <see cref="ClusterNearPct"/>, and the ask only vouches while it is itself
  /// credible by the very line it vouches against (at or above
  /// <see cref="FarBelowPct"/> of the band floor) - so a crushed ask (the
  /// Multifaceted-Cotton-at-75 shape) vouches for nobody and clamp-and-climb is
  /// untouched. No standing ask - a hawk, a fresh list - vouches for nothing. The
  /// exemption only ever loosens toward "competitor", A12's fail-closed direction,
  /// and it fires exactly when the conviction line splits hairs with our own price
  /// - the rare case, by construction. A vouched row is priced against, not stepped
  /// over: the manual play on that board is front of the line, and now so is ours.</para>
  /// </summary>
  private static bool AskVouchesFor(long rowPrice, long? currentPrice, double? memoryLow)
    => currentPrice is long ask && ask > 0
       && memoryLow is double floor && ask >= floor * FarBelowPct
       && ask <= rowPrice * (1 + ClusterNearPct);

  /// <summary>
  /// The lone-crazy test (A10 rule 2), on the price-sorted foreign queue. A row
  /// is skippable ONLY when all three hold: it is alone (no second seller within
  /// <see cref="ClusterNearPct"/> either side), it sits far below the queue
  /// behind it, and it sits far below the bottom of demonstrated clearing.
  ///
  /// <para>Nothing behind it = the board arm has nothing to say, so the tape
  /// decides alone (the lone 3-gil row on an otherwise empty board). No tape at
  /// all = the queue is the only memory we have, so the board arm decides alone
  /// - and then there must BE something behind it, or we would be skipping a
  /// row on no evidence whatsoever.</para>
  /// </summary>
  private static bool IsLoneCrazy(IReadOnlyList<(long Price, bool Cross)> ascendingQueue, int i,
    double? memoryLow, double? hqMemoryLow, long? currentPrice)
  {
    var price = ascendingQueue[i].Price;
    var companyBelow = i > 0 && price <= ascendingQueue[i - 1].Price * (1 + ClusterNearPct);
    var companyAbove = i + 1 < ascendingQueue.Count && ascendingQueue[i + 1].Price <= price * (1 + ClusterNearPct);
    if (companyBelow || companyAbove)
      return false; // a crowd is never bait

    // SF-P8: a lone row our own credible ask stands beside is not bait either.
    // Our rows never enter the queue, so the company test above cannot see us -
    // the vouch is the same claim the pack test honors, made by the same seller.
    if (AskVouchesFor(price, currentPrice, memoryLow))
      return false;

    var hasQueueBehind = i + 1 < ascendingQueue.Count;
    var farBelowBoard = !hasQueueBehind || price < ascendingQueue[i + 1].Price * FarBelowPct;

    // A cross-quality row is convicted by ITS OWN tape or not at all (A12,
    // "swagger and receipts"): the board arm alone may never step a better
    // item, because "far below the NQ queue" is exactly where an honestly
    // cheap HQ would sit. No HQ tape, no conviction - it stays a competitor.
    if (ascendingQueue[i].Cross)
      return hqMemoryLow is double hqFloor && farBelowBoard && price < hqFloor * FarBelowPct;

    if (memoryLow is double floor)
      return farBelowBoard && price < floor * FarBelowPct;
    return hasQueueBehind && farBelowBoard;
  }

  /// <summary>
  /// A SPAN WHOSE EDGES AGREE IS NOT A SPAN (Drift, 08-15 shake: "sells 200-200",
  /// "2 sellers from 444 to 444"). Two numbers printed to say one number reads as
  /// a range the reader then has to collapse himself - and the house already owns
  /// the collapsed form, "at X", from the census facts and the Pos@list story.
  /// One preposition, decided by the operands, so no caller has to remember.
  /// </summary>
  private static string SpanFromTo(long low, long high)
    => low == high ? $"at {MoneyText.Gil(low)}" : $"from {MoneyText.Gil(low)} to {MoneyText.Gil(high)}";

  /// <summary>
  /// WHERE A STEPPED-OVER PREFIX STARTS, in the preposition the count earns (Drift,
  /// 08-15 shake: "lone lowball from 200"). "From" opens a range, and one row is
  /// not a range - it is a price, and a price is "at". The stepped rows are always
  /// the queue's prefix, so <paramref name="floor"/> is queue[0] at every site.
  /// </summary>
  private static string SteppedFrom(int count, long floor)
    => count == 1 ? $"at {MoneyText.Gil(floor)}" : $"from {MoneyText.Gil(floor)}";

  /// <summary>
  /// The band context line, on EVERY verdict (A10 keeps it): where the tape says
  /// this thing sells, how many sales say so, and how much they disagree with
  /// each other - so the reader can second-guess the price without opening a
  /// receipt. Community lanes stay labeled (the confidence net). A tape that
  /// cannot speak says so out loud rather than going quiet.
  /// </summary>
  private static string BandLine(LaneModel? lane, bool tapeSpeaks, double? velocityPerDay)
  {
    // "here" = this world (F4, ruled 08-22): every velocity the walk is handed
    // resolves local-first (segment pace, then the packet's, then the home-world
    // almanac - LaneEvaluation), so the word can be claimed. The DC's rate only
    // ever prints in the community sentence that names the DC.
    var pace = velocityPerDay is double v ? $" Selling about {v:0.##}/day here." : "";
    if (!tapeSpeaks)
    {
      var thin = lane?.SampleCount ?? 0;
      return thin == 0
        ? "No settled sales on record — the queue is the only evidence." + pace
        : $"Only {thin} sale{(thin == 1 ? "" : "s")} on record, too thin to check a price against — the queue is the evidence." + pace;
    }

    var n = lane!.SampleCount;
    // A one-sale lane draws its band on top of itself, and "Sells 200-200" is a
    // range with nothing in it (08-15 shake). Compared AFTER rounding, because the
    // rendered pair is the one the reader sees disagree. The agreement clause
    // drops with the collapse (ruled 08-15): it reports how much the witnesses
    // disagree, and a zero-width band has no disagreement to characterize -
    // claiming agreement there would claim something the witnesses barely exist to give.
    //
    // "THEY AGREE" / "THEY DISAGREE", NOT "TIGHT" / "SCATTERED" (3b-4). This clause is
    // about the SALE TAPE, and the word "scattered" belongs to the other instrument -
    // the board as it stands now, whose caveat the case tribunal speaks. Same measure,
    // its own boundary (BoardConfidence.TapeNoiseCeiling), and now its own words: this
    // sentence and the confidence tier are one reading of the tape, so it says what the
    // tier concluded rather than describing a board it never looked at.
    var bandLow = (long)Math.Round(lane.BandLow);
    var bandHigh = (long)Math.Round(lane.BandHigh);
    var agree = BandSpread(lane.BandLow, lane.BandHigh, lane.Median)
      <= BoardConfidence.TapeNoiseCeiling;
    var line = bandHigh == bandLow
      ? $"Sells at {MoneyText.Gil(bandLow)}, {n} sale{(n == 1 ? "" : "s")}."
      : $"Sells {MoneyText.Gil(bandLow)}-{MoneyText.Gil(bandHigh)}, {n} sale{(n == 1 ? "" : "s")}, "
        + $"{(agree ? "they agree" : "they disagree")}.";
    if (lane.Source == LaneSource.Community)
      line += " Based on community sales history.";
    return line + pace;
  }

  /// <summary>
  /// The thin gate: n at or above MinHistorySamples, OR two sales that agree
  /// (see <see cref="TightSpreadPct"/>). Everything else is genuine silence.
  /// </summary>
  /// <para>Internal rather than private since Phase 3b: the pipeline has to ask
  /// the same question from outside (may the HQ tape speak for itself, or does
  /// the NQ rung arm?) and there must be exactly one answer to it.</para>
  internal static bool CanSpeak(LaneModel? lane, LaneConfig cfg)
  {
    if (lane == null)
      return false;
    if (lane.SampleCount >= cfg.MinHistorySamples)
      return true;
    // A band is what ratifies the pair, so there has to BE one: a lane carrying
    // no quantile evidence has a zero-width band by absence, not by agreement,
    // and a width of nothing must never read as two witnesses agreeing.
    if (lane.SampleCount != 2 || lane.Median <= 0 || lane.BandLow <= 0 || lane.BandHigh < lane.BandLow)
      return false;
    return (lane.BandHigh - lane.BandLow) / lane.Median <= TightSpreadPct;
  }
}

// LaneNote - the old "<Item>: <transition> [tag] - <reason>" grammar - was retired
// by Movement 1 (2026-08-13). Its three jobs moved rather than vanished: the
// transition became RunLogVoice's tense-honest head ("From 680 to 441" / "Kept at
// 999"), the [tag] became the verb prefix that names the act, and the reason became
// the plain second sentence. The bracketed tags themselves were internal jargon
// inline - exactly what rule 4 forbids - and nothing ever grepped them.
