using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// Regime segmentation - which sales are still voting.
///
/// <para>The lane's blind spot was never its math, it was its electorate: it
/// trusted every sale on the tape and asked "what did this sell for," when the
/// question a regime demands is "does anything still clear there." Blue Zircon
/// parked at 488 because ~20 sales at ~975 outvoted seven fresh clears at
/// 223-439; Ceremonial Culottes asked 44,999 because a June cluster outvoted a
/// two-month slide down to 36k. Both anchors were correctly computed over the
/// wrong voters.</para>
///
/// <para>The fix is DEMOTION, not decay (A2/A6): sales outside the active regime
/// leave the electorate entirely rather than getting a smaller vote. Half-life is
/// exonerated and unchanged - it smooths WITHIN a segment, where the market is
/// one continuous thing. No constant serves both jobs, because decay is
/// continuous and regimes are discontinuous.</para>
///
/// <para>Pure decision core in the LanePricing/RoutingRules mold: no game reads,
/// no storage, no statics. Compiled into Scrooge.Tests as a linked source - the
/// 07-25 case-study boards are the fixture data.</para>
/// </summary>

/// <summary>Why the segment ended where it did - the story receipts and narration carry.</summary>
internal enum SegmentCut
{
  /// <summary>No break found: the whole electorate is one regime (a stable market).</summary>
  None,
  /// <summary>A discrete break - the newest run sits below everything older (the Zircon shape).</summary>
  Cliff,
  /// <summary>A continuous slide - the newest run declines step by step (the Culottes shape).</summary>
  Glide,
}

/// <summary>The active segment: the sales still voting, and why the rest stopped.</summary>
internal sealed record RegimeSegmentModel
{
  /// <summary>The active regime's sales, newest first (the input's order, preserved).</summary>
  public required IReadOnlyList<LaneSale> Sales { get; init; }
  public required SegmentCut Cut { get; init; }
  /// <summary>How many sales were eligible to vote (the electorate window).</summary>
  public required int ElectorateCount { get; init; }
  /// <summary>How many of them still do. Equal to ElectorateCount when nothing cut.</summary>
  public int SegmentCount => Sales.Count;
}

internal static class RegimeSegment
{
  /// <summary>
  /// The electorate: the last K settled sales, COUNT-based, never age-based.
  ///
  /// <para>A fixed D-days window starves exactly the items that need segmenting
  /// most - Culottes moves 0.28/day, so "K sales in the last 7 days" sees one
  /// sale and can never convict a two-month slide. A count window self-scales to
  /// velocity for free: a fast mover's last 20 sales are this week, a slow
  /// mover's last 20 reach back a season, and both describe "the recent market"
  /// in the only unit the market actually produces.</para>
  ///
  /// <para>20 is seeded from the 07-25 case-study boards - deep enough to hold an
  /// old cluster AND the fresh run that convicts it (Zircon: 7 fresh against ~20
  /// old), shallow enough that a genuinely dead regime falls out. It is an
  /// evidence-window knob, not a pricing operand; the receipts carry segment
  /// counts from day one so it becomes measured-at-n later rather than argued.</para>
  /// </summary>
  internal const int SegmentWindowK = 20;

  /// <summary>
  /// A cliff needs at least this many fresh sales. Below three, one cheap Tuesday
  /// rewrites the item's whole price history - the first-law counterweight in A2:
  /// regime evidence is multiple clearing events, never a single one.
  /// </summary>
  internal const int CliffMinRun = 3;

  /// <summary>
  /// And it needs an old CLUSTER to fall off of. A run "below" one or two older
  /// sales is noise wearing a shape; three is the same bar the fresh side pays.
  /// </summary>
  internal const int CliffMinRemainder = 3;

  /// <summary>
  /// How far below the old market's cheap quarter a break has to land. Without a
  /// floor, "all below p25" is nearly free at the edges - the cheapest member of
  /// any cluster sits below the p25 of the rest of it, so a run could absorb the
  /// old cluster one sale at a time and quietly re-elect the voters it just
  /// demoted. A quarter is the same order as the lane's own floor (0.5x is
  /// suspicious in the other direction) and Zircon clears it four times over
  /// (fresh top 439 against an old cheap edge near 910 - a 52% gap).
  /// </summary>
  internal const double CliffMinGap = 0.25;

  /// <summary>
  /// A glide needs five steps. It is a claim about ORDER, and order is the easiest
  /// pattern to find in noise - three sales fall in sequence by chance constantly,
  /// five do not. (Culottes' real slide ran far longer than five.)
  /// </summary>
  internal const int GlideMinRun = 5;

  /// <summary>
  /// Near-monotonic, not monotonic: a sale up to 5% above its predecessor still
  /// belongs to a decline. Real tape jitters - a stack sold at a round number, a
  /// buyer who did not check - and a strict test would break every real slide on
  /// its first hiccup.
  /// </summary>
  internal const double GlideRiseTolerance = 1.05;

  /// <summary>
  /// A glide is a slide, not a step. One drop steeper than 40% is a BREAK, and
  /// breaks belong to the cliff test - without this ceiling a cliff reads as a
  /// (technically monotonic) glide and drags the demoted old cluster back into
  /// the segment, which is the exact failure this file exists to end.
  /// </summary>
  internal const double GlideMaxStepDown = 0.40;

  /// <summary>
  /// And it has to go somewhere: 20% from the run's start to its newest sale.
  /// A five-sale drift of a few percent is a market holding still.
  /// </summary>
  internal const double GlideMinTotalDecline = 0.20;

  /// <summary>
  /// Resolves the active segment from quality-matched sales ordered NEWEST FIRST.
  ///
  /// <para>The order matters: a regime is the newest RUN of sales, so every test
  /// walks backward from now. Callers hand this the ring read (already newest
  /// first) filtered to the lane's quality.</para>
  ///
  /// <para>Glide is tested before cliff. Both describe "the newest sales sit
  /// below the older ones," but a glide additionally claims ORDER, so where both
  /// fit, the ordered reading is the more specific one and its cut lands where
  /// the slide actually began. The step ceiling keeps the two honest: a glide
  /// that hits a break stops at the break and the cliff test answers instead.</para>
  ///
  /// <para>Neither test firing is the common, healthy answer: the market is one
  /// regime and the whole electorate votes.</para>
  /// </summary>
  internal static RegimeSegmentModel Resolve(IReadOnlyList<LaneSale> newestFirst, LaneConfig cfg, long nowUnix)
  {
    var electorate = new List<LaneSale>(Math.Min(newestFirst.Count, SegmentWindowK));
    for (var i = 0; i < newestFirst.Count && i < SegmentWindowK; i++)
      electorate.Add(newestFirst[i]);

    var n = electorate.Count;
    if (n == 0)
      return Segment(electorate, electorate, SegmentCut.None);

    var glide = GlideRun(electorate);
    if (glide > 0)
      return Segment(electorate, electorate.GetRange(0, glide), SegmentCut.Glide);

    var cliff = CliffRun(electorate, cfg, nowUnix);
    if (cliff > 0)
      return Segment(electorate, electorate.GetRange(0, cliff), SegmentCut.Cliff);

    return Segment(electorate, electorate, SegmentCut.None);
  }

  /// <summary>
  /// Sales per day across the ACTIVE SEGMENT's own span (A5): one window, two
  /// readings. Sarcenet's "9.55/day" was real - at 600-1,500 - and then justified
  /// confidence in a 2,500 ask; a velocity averaged across regimes inflates
  /// exactly when the anchor is most wrong, because a dump spikes the rate while
  /// cratering the price. Anchor and velocity now read the same tape, so they can
  /// never disagree, and there is no second window to tune.
  ///
  /// <para>The half-day floor keeps a burst of same-minute round fills from
  /// reporting an infinite rate; null when the segment is empty.</para>
  /// </summary>
  internal static double? VelocityPerDay(IReadOnlyList<LaneSale> segment)
  {
    if (segment.Count == 0)
      return null;

    long newest = long.MinValue, oldest = long.MaxValue;
    foreach (var s in segment)
    {
      if (s.Timestamp > newest) newest = s.Timestamp;
      if (s.Timestamp < oldest) oldest = s.Timestamp;
    }

    var spanDays = Math.Max((newest - oldest) / 86400.0, 0.5);
    return segment.Count / spanDays;
  }

  /// <summary>
  /// The GLIDE test (Culottes): the length of the near-monotonic decline running
  /// back from the newest sale, or 0 if there isn't one worth the name. Extends
  /// greedily while each older sale stands above its newer neighbour (within the
  /// rise tolerance) and no single step is steep enough to be a break.
  /// </summary>
  private static int GlideRun(IReadOnlyList<LaneSale> electorate)
  {
    var run = 1;
    for (var i = 0; i + 1 < electorate.Count; i++)
    {
      double newer = electorate[i].UnitPrice, older = electorate[i + 1].UnitPrice;
      if (older <= 0)
        break;
      // The newer sale must sit at or below the older one (5% of jitter allowed)...
      if (newer > older * GlideRiseTolerance)
        break;
      // ...and the step down must be a slide, not a cliff face.
      if (newer < older * (1 - GlideMaxStepDown))
        break;
      run++;
    }

    if (run < GlideMinRun)
      return 0;

    double start = electorate[run - 1].UnitPrice, end = electorate[0].UnitPrice;
    if (start <= 0)
      return 0;
    return (start - end) / start >= GlideMinTotalDecline ? run : 0;
  }

  /// <summary>
  /// The CLIFF test (Zircon): the length of the newest run that falls ENTIRELY
  /// below the weighted p25 of everything older in the electorate, or 0. The
  /// remainder's p25 - not its median - is the "does anything still clear up
  /// there" line: a run under even the cheap quarter of the old market is a run
  /// the old market can no longer explain.
  ///
  /// <para>The remainder is re-measured at every candidate length and the run
  /// takes the LONGEST length the evidence still supports - so the cut lands
  /// where the old cluster actually starts, not at the arbitrary minimum. Short
  /// lengths can pass on a contaminated remainder (at j=3 the "older" sales are
  /// still mostly the fresh dump), so the scan never stops at the first miss;
  /// the gap floor is what stops it from walking into the old cluster.</para>
  /// </summary>
  private static int CliffRun(IReadOnlyList<LaneSale> electorate, LaneConfig cfg, long nowUnix)
  {
    var best = 0;
    for (var j = CliffMinRun; j <= electorate.Count - CliffMinRemainder; j++)
    {
      var older = new List<(double Price, double Weight)>(electorate.Count - j);
      for (var i = j; i < electorate.Count; i++)
      {
        var ageDays = Math.Max(0, (nowUnix - electorate[i].Timestamp) / 86400.0);
        older.Add((electorate[i].UnitPrice, Math.Pow(2, -ageDays / cfg.HalfLifeDays)));
      }

      // WeightedQuantiles walks ascending-by-price; the remainder arrives in
      // time order. Unsorted, p25 lands on whatever sold most recently.
      older.Sort((a, b) => a.Price.CompareTo(b.Price));
      var p25 = LanePricing.WeightedQuantiles(older, [0.25])[0];
      if (p25 <= 0)
        continue;

      double runTop = 0;
      for (var i = 0; i < j; i++)
        if (electorate[i].UnitPrice > runTop) runTop = electorate[i].UnitPrice;

      if (runTop < p25 && (p25 - runTop) / p25 >= CliffMinGap)
        best = j;
    }

    return best;
  }

  private static RegimeSegmentModel Segment(List<LaneSale> electorate, List<LaneSale> sales, SegmentCut cut)
    => new() { Sales = sales, Cut = cut, ElectorateCount = electorate.Count };
}
