using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The 07-25 case-study boards and the 07-26 investigations, end to end:
/// banked tape -> segment -> band -> decision. Every fixture's numbers are read
/// from the ring (a COPY of the live DB, queried 2026-07-26) and its board from
/// the same pinch's snapshot, so these are receipts, not scenarios.
///
/// <para>REWRITTEN to A10 (2026-07-26). Every expectation below is the
/// back-to-basics answer - undercut the cheapest CLUSTER, step over LONE
/// CRAZIES, let the tape price only an empty board - and every case whose
/// answer moved says which A8-era behaviour died to move it. Nothing here was
/// quietly adjusted to whatever the code now returns; the comments carry the
/// arithmetic so a wrong answer stays arguable.</para>
///
/// <para>Ages are days before <see cref="L.Now"/> and preserve the tape's own
/// order; the pipeline's shape is reproduced exactly - quality filter, newest
/// first, Resolve, BuildLane, Decide.</para>
///
/// <para>Where a board row was ours it is dropped (own listings are the thing
/// being repriced, never the market). Where the ring no longer holds an item
/// (Blue Zircon, Sarcenet Cloth - both cleared before the tape reader shipped)
/// the fixture is reconstructed from the numbers in
/// [[Session - 2026-07-25 - Scrooge]] and says so.</para>
/// </summary>
internal static class Tape
{
  public static List<LaneSale> Of(bool hq, params (long Price, double DaysAgo)[] rows)
    => rows.Select(r => new LaneSale(r.Price, L.Now - (long)(r.DaysAgo * 86400), hq)).ToList();

  /// <summary>The live pricing path, minus the game: segment, lane, decision.</summary>
  public static (RegimeSegmentModel Seg, LaneModel Lane, LaneDecision D) Price(
    List<LaneSale> tape, bool hq, List<LaneListing> board, long? current = null)
  {
    var cfg = L.Cfg();
    var newestFirst = tape.Where(s => s.IsHq == hq).OrderByDescending(s => s.Timestamp).ToList();
    var seg = RegimeSegment.Resolve(newestFirst, cfg, L.Now);
    var lane = LanePricing.BuildLane(seg.Sales, hq, cfg, L.Now);
    var d = LanePricing.Decide(board, lane, RegimeSegment.VelocityPerDay(seg.Sales), cfg, current);
    return (seg, lane!, d);
  }
}

/// <summary>
/// The two boards that convicted the A8 classifier on its first live pinch
/// (2026-07-26, 16:02) and produced the A10 ruling the same afternoon. Both are
/// read straight out of the ring and the pinch's own decision receipts.
/// </summary>
public class A10ConvictingExhibitTests
{
  [Fact]
  public void CS10_AlmastySerge_NineSellersAreACrowd_NotBait()
  {
    // THE RULING'S FIRST EXHIBIT. A8 shipped at 15:57 and by 16:02 had graded
    // every row of this board [bait] so a 3,800-4,000 memory could be right:
    // "every listing is below the band (10 of them, from 1,500) but the tape
    // still clears at the going rate - parked at the top of the band, 4,000."
    // Drift: "I find it hard to believe the first 10 entries are 'bait'."
    //
    // A10 rule 1: the cheap crowd steps 1,500 -> 1,670 (+11%) and then in
    // single-digit percents up to 1,750 - seven independent sellers inside a
    // quarter of each other. Nobody engineers a crowd, so it is the market, and
    // the tape's memory of 4,000 is a memory. Undercut 1,500.
    // (1,675 and 1,749 are 2-unit stacks; stack size is not logic and the
    // decision never sees it - LaneListing carries no quantity.)
    var tape = Tape.Of(false,
      (2_999, 1.56), (4_000, 1.78), (3_900, 1.78), (3_800, 1.79),
      (3_968, 2.53), (4_000, 2.53),
      (3_500, 3.56), (2_204, 3.57), (2_500, 3.57), (2_500, 3.57),
      (1_699, 4.59), (1_004, 4.61), (1_200, 4.89), (1_198, 4.89), (1_199, 4.89));
    var board = L.Board(1_500, 1_670, 1_675, 1_690, 1_700, 1_749, 1_750, 2_900, 2_994, 3_000);

    var (seg, lane, d) = Tape.Price(tape, false, board, current: 4_000);

    // The tape reads exactly as the live receipt did: "Sells 3,800-4,000, 6
    // sales, they agree." The band is not wrong - it is just not in charge.
    Assert.Equal(6, seg.SegmentCount);
    Assert.Equal(3_800, lane.BandLow);
    Assert.Equal(4_000, lane.BandHigh);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(1_500, d.Anchor);
    Assert.True(d.AnchorIsListing);
    Assert.Equal(7, d.ClusterSize);
    Assert.Equal(0, d.CraziesSkipped);
    Assert.Contains("7 sellers from 1,500 to 1,750", d.Evidence);
    Assert.Contains("Sells 3,800-4,000, 6 sales, they agree.", d.Evidence);
  }

  [Fact]
  public void CS11_AmaurotineSpireChandelier_A31DeepRaceIsNotA1298Park()
  {
    // THE RULING'S SECOND EXHIBIT, same pinch: parked at 1,298 behind a 31-deep
    // board opening at 500, on the strength of a tape whose NEWEST sale was
    // sixteen days old. The band had become a theory of value.
    //
    // A10: the queue in front of us is the evidence. 500 -> 600 is +20%, inside
    // the quarter, and from there the crowd tightens to a few percent a step -
    // ten visible rows, one neighborhood. Undercut 500. The June cluster still
    // prints, because the reader deserves to know what this used to fetch.
    var tape = Tape.Of(false,
      (1_276, 16.49), (1_200, 16.72), (1_277, 17.17), (1_298, 17.56),
      (1_550, 21.76), (150, 22.55), (1_744, 27.06),
      (1_200, 28.14), (1_111, 28.14), (1_000, 28.7), (1_190, 29.59), (1_188, 29.59),
      (1_888, 36.18), (1_920, 40.55), (1_914, 40.56), (900, 40.56),
      (1_000, 41.66), (850, 41.66), (845, 41.66));
    var board = L.Board(500, 600, 618, 619, 620, 645, 646, 650, 650, 728);

    var (seg, lane, d) = Tape.Price(tape, false, board, current: 1_298);

    Assert.Equal(19, seg.SegmentCount);
    Assert.Equal(1_000, lane.BandLow);
    Assert.Equal(1_298, lane.BandHigh);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(500, d.Anchor);
    // SPAN, NOT CHAIN (Pillar ruling, 08-22): ten visible rows, but one
    // neighborhood only reaches the quarter of its anchor - 500..625. The five
    // rows at 645+ are the race's next tier, not this cluster's span.
    Assert.Equal(5, d.ClusterSize);
    Assert.Contains("Sells 1,000-1,298, 19 sales, they agree.", d.Evidence);
  }

  [Fact]
  public void CeremonialLongpole_TheLone11111_IsStillSteppedOver()
  {
    // The other half of A10: the crazy skip survives, and this is the row it was
    // written for. 11,111 under a market whose queue starts at 29,999 and whose
    // tape has not cleared under 26,999 - alone (the next row is 2.7x it), under
    // half the queue behind it (0.37x), and under half the bottom of demonstrated
    // clearing (0.41x of 27,000). Step over it; undercut the 29,999 cluster.
    var tape = Tape.Of(false,
      (19_199, 11.72), (86_666, 20.67),
      (35_000, 41.95), (29_999, 41.95), (27_000, 41.95), (26_999, 41.95), (27_000, 42.94),
      (29_998, 45.82), (35_997, 47.58), (36_990, 48.24), (35_000, 48.6), (34_950, 48.6),
      (69_900, 51.78), (69_899, 51.78), (99_999, 55.68));
    var board = L.Board(11_111, 29_999, 30_000, 30_000, 34_999);

    var (_, lane, d) = Tape.Price(tape, false, board, current: 29_999);

    Assert.Equal(27_000, lane.BandLow);
    Assert.Equal(69_899, lane.BandHigh);
    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(1, d.CraziesSkipped);
    Assert.Equal(29_999, d.Anchor);
    Assert.Equal(4, d.ClusterSize);
    Assert.Contains("stepped over 1 lone lowball at 11,111", d.Evidence);
  }
}

/// <summary>
/// The 07-26 investigations: five live pinch decisions Drift pulled for review.
/// Each one names the model defect it turned out to be, and now also what A10
/// does with it.
/// </summary>
public class RegimeInvestigationTests
{
  [Fact]
  public void TigerLeather_TheLoneThreeGilRow_IsSteppedOver()
  {
    // REWRITTEN to A10 (was: A8 anchored 1,000, grading 3/100/203/497 as bait
    // against a 500-1,300 band). The 3-gil row is the named exhibit for rule 2
    // and it still goes: alone, 0.03x of both the queue behind it and the band's
    // floor. So do the 100 and the 203 - each alone, each under half the row
    // behind it AND under half the 500 floor.
    //
    // The 497 does NOT go, and that is the change: it is one gil under the band's
    // floor, which is nowhere near "far below". A8 called it bait because the
    // band's edge was a verdict; A10 calls it the front of the queue.
    var tape = Tape.Of(false,
      (500, 0.74), (1_500, 1.53), (1_495, 1.53), (1_300, 1.53),
      (995, 2.25), (995, 2.25), (500, 2.25), (499, 2.25),
      (994, 3.13), (999, 3.35), (999, 3.35));
    var board = L.Board(3, 100, 203, 497, 1_000, 1_003, 1_900);

    var (seg, lane, d) = Tape.Price(tape, false, board, current: 1_100);

    Assert.Equal(11, seg.SegmentCount);
    Assert.Equal(500, lane.BandLow);
    Assert.Equal(995, lane.Median);
    Assert.Equal(1_300, lane.BandHigh);
    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(3, d.CraziesSkipped);
    Assert.Equal(497, d.Anchor);
    Assert.True(d.AnchorIsListing);
  }

  [Fact]
  public void MultifacetedCottonCloth_TheRaceIsTheMarket()
  {
    // REWRITTEN to A10 (was: A8 parked at 232, the band's top, calling the whole
    // 73-115 board bait). Seven sellers walk from 73 to 115 in steps of a few
    // percent - the definition of a crowd. The tape remembers 144-232 and the
    // tape is a witness, not a judge: undercut 73 and let the market be what it
    // is today. The 2,800 row sits above the 3x rail and is named as unreachable.
    var tape = Tape.Of(false,
      (234, 18.40), (232, 18.40), (151, 18.40), (204, 18.40), (144, 18.40), (150, 18.40), (150, 18.40),
      (19, 19.34), (20, 19.34), (20, 19.34),
      (150, 24.57), (145, 24.57),
      (700, 57.18), (254, 57.18),
      (249, 60.65), (245, 60.65), (241, 60.65), (230, 60.65), (220, 60.65), (216, 60.65));
    var board = L.Board(73, 74, 77, 78, 94, 105, 115, 263, 2_800);

    var (seg, lane, d) = Tape.Price(tape, false, board, current: 76);

    Assert.Equal(20, seg.SegmentCount);
    Assert.Equal(144, lane.BandLow);
    Assert.Equal(232, lane.BandHigh);
    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(73, d.Anchor);
    // SPAN, NOT CHAIN (Pillar ruling, 08-22): the race runs 73..115, but a
    // "cluster" may not stretch past the quarter of its own anchor - 73..91 is
    // this crowd's story; 94/105/115 are the next tier's.
    Assert.Equal(4, d.ClusterSize);
    Assert.Contains("above the 3x ceiling", d.Evidence);
  }

  [Fact]
  public void SilvergraceIngot_SameAnchorAsEverAndNowASimplerStory()
  {
    // The control across all three models. Old: [undercut] on 901 with "ignored
    // 3 listings at 3,000+". A8: same 901, reached by calling the 1,000-1,010
    // crowd dreamers. A10: same 901, reached by noticing that 901 and the 1,010s
    // are one neighborhood and 901 is the front of it. When the board is honest
    // every model agrees - the disagreements are where the receipts live.
    var tape = Tape.Of(false,
      (995, 0.45), (1_550, 1.13), (1_010, 1.58), (1_010, 1.58), (800, 1.91),
      (950, 3.04), (950, 3.04), (849, 3.04), (848, 3.04), (843, 3.04),
      (801, 3.04), (800, 3.04), (785, 3.04), (784, 3.04), (700, 3.04), (649, 3.04));
    var board = L.Board(901, 1_000, 1_000, 1_010, 1_010, 3_000, 3_200, 3_500);

    var (_, lane, d) = Tape.Price(tape, false, board, current: 1_554);

    Assert.Equal(800, lane.BandLow);
    Assert.Equal(848, lane.Median);
    Assert.Equal(995, lane.BandHigh);
    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(901, d.Anchor);
    Assert.Equal(5, d.ClusterSize);
    Assert.Contains("Sells 800-995, 16 sales", d.Evidence);
    // The 3,000+ rows are over 3x the 848 going rate: a rail, not a verdict.
    Assert.Contains("3 listings at 3,000+ sit above the 3x ceiling", d.Evidence);
  }

  [Fact]
  public void CheeseCollection_TheCliffCutsAndTheBoardIsJustACrowd()
  {
    // Old: 15,947 -> 7,999 [bait] - the June/July cluster at ~28,000 was still
    // voting, so the 6,994 board read as a race and we parked at the 0.5x floor.
    // The cliff demotes it; A8 then called the board competition and A10 calls it
    // a five-row cluster. Same anchor, three models, one honest reason.
    var tape = Tape.Of(false,
      (5_000, 0.81), (4_999, 0.81), (4_000, 0.81), (3_999, 0.81), (4_998, 1.62),
      (15_000, 7.79), (14_999, 7.88), (14_900, 7.88), (14_899, 7.88),
      (24_994, 10.86), (24_993, 11.61), (22_500, 12.51), (28_990, 14.29),
      (29_450, 18.85), (29_020, 18.85), (28_950, 18.85), (28_945, 18.87),
      (28_944, 18.87), (27_120, 18.87), (27_119, 18.87));
    var board = L.Board(6_994, 6_994, 6_995, 6_995, 8_000, 15_950, 15_990, 15_998, 20_000, 24_995);

    var (seg, lane, d) = Tape.Price(tape, false, board, current: 15_947);

    Assert.Equal(SegmentCut.Cliff, seg.Cut);
    Assert.Equal(9, seg.SegmentCount);
    Assert.Equal(20, seg.ElectorateCount);
    Assert.Equal(5_000, lane.Median);
    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(6_994, d.Anchor);
    Assert.True(d.AnchorIsListing);
    Assert.Equal(5, d.ClusterSize);
  }

  [Fact]
  public void CobaltTungstenIngotHq_TwoSellersOutrankTheTape()
  {
    // REWRITTEN to A10 (was: A8 parked at 2,197, the band's top, because both
    // board rows sat below a 1,990-2,197 band and the newest tape still cleared
    // at 2,222). Two sellers at 800 and 1,000 are a crowd of two - 1,000 is
    // exactly 1.25x of 800, right on the "near" seed - and A10 rule 1 says a
    // crowd is the market. We cut in front at 800 and find out by the next pinch
    // whether position 1 clears; A9 is the mechanism that re-cuts the seed if it
    // does not. (The anchor is 800 either way: alone, it would still not be far
    // below the 1,000 behind it.)
    var tape = Tape.Of(true,
      (2_222, 1.76), (1_990, 1.76), (1_175, 1.76), (2_000, 2.76), (2_197, 3.67), (2_000, 3.67));
    var board = L.Board(800, 1_000);

    var (_, lane, d) = Tape.Price(tape, true, board, current: 4_000);

    Assert.Equal(1_990, lane.BandLow);
    Assert.Equal(2_000, lane.Median);
    Assert.Equal(2_197, lane.BandHigh);
    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(800, d.Anchor);
    Assert.Contains("Sells 1,990-2,197, 6 sales", d.Evidence);
  }
}

/// <summary>
/// The nine 07-25 case-study boards, re-derived to A10. The quality-ladder cases
/// (CS4's cross-cap, CS6's rung 2) are Phase 3 work and CS5/CS8's round parks are
/// the Phase 4 A4b exception - only today's behaviour is asserted here, with the
/// owed override named in the comment so the change stays visible when it lands.
/// </summary>
public class RegimeCaseStudyTests
{
  [Fact]
  public void CS12_ClassicalCaligae_TheGapTestRefusesTheWallStep()
  {
    // THE LAP RECEIPT (08-22, decision_receipts id 8058): the live pinch wrote
    // 7,997 "over" a pack topping 7,999 under an 8,000 anchor - crasher ceiling
    // and cluster floor ONE GIL apart in the engine's own banked counts, the
    // write landing inside the rows it claimed to step, and the sentence
    // quoting the pre-step seat. The board: one continuous descending wall
    // (gaps of 1 / 500 / 499 / 1 gil on a ~16k lane). The gap test refuses the
    // step - no daylight over 7,999 - and the walk joins the line at its front,
    // which is the price Drift asked for live: "why wouldn't I just jump that
    // 6999 price."
    var tape = Tape.Of(true,
      (16_000, 2.0), (16_050, 3.1), (16_100, 4.2), (16_200, 5.5), (16_390, 8.0),
      (16_990, 12.5), (17_000, 15.0), (17_099, 18.1), (17_197, 20.0), (17_198, 22.2),
      (16_999, 25.0), (17_100, 28.0), (16_800, 31.0), (17_050, 34.0), (16_600, 37.0));
    var board = L.Board(6_999, 7_000, 7_500, 7_999, 8_300, 15_948, 16_429, 16_999, 17_200, 52_631);

    var (_, _, d) = Tape.Price(tape, true, board, current: 15_948);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(6_999, d.Anchor);
    Assert.Equal(0, d.CraziesSkipped);
    Assert.Contains("no gap to step over", d.Evidence);
    // THE SEAT IS THE WRITE'S: one gil under 6,999 is the front of the line,
    // and the sentence must say so - never a seat quoted off a different price.
    Assert.Equal(1, d.Census.Seat);
    // The doctrine's shadow agrees here (ruled 08-23) - and rides the decision
    // so the receipt banks both worlds even when they concur.
    Assert.Equal(6_998, d.Shadow!.Value.Price);
    Assert.Equal("front", d.Shadow.Value.Defense);
  }

  [Fact]
  public void CS14_TheMidBandGap_LiveStepsWhereTheShadowJoins()
  {
    // THE DIVERGENCE CLASS (the 08-23 audit: 31 receipts, 40.8k gil in the
    // 1.25x-3x band). Two crashers at 60/70 under a 150 anchor on a ~200 lane:
    // the live walk convicts them on tape (far below band) and the F1 gap test
    // sees daylight (150 > 70 x 1.25), so it steps; the doctrine reads
    // 150/70 = 2.1x - no ceiling-multiple hole - and joins the front. This
    // board is exactly what the shadow corpus exists to count, and the pin
    // proves live pricing DID NOT MOVE while the shadow disagrees beside it.
    var tape = Tape.Of(false,
      (200, 2.1), (210, 3.4), (205, 5.0), (198, 7.7), (202, 9.1), (200, 11.4));
    var board = L.Board(60, 70, 150, 165, 180, 185);

    var (_, _, d) = Tape.Price(tape, false, board, current: 160);

    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(150, d.Anchor);
    Assert.Equal(2, d.CraziesSkipped);
    Assert.Equal(59, d.Shadow!.Value.Price);
    Assert.Equal(1, d.Shadow.Value.Seat);
    Assert.Equal(0, d.Shadow.Value.SteppedRows);
  }

  [Fact]
  public void CS13_ExcitingLeather_ARealCliffStillSteps()
  {
    // The lap's own control case (08-22 run log): 47 with the market at 150+ -
    // real separation (3.2x), the step is exactly what A10 exists for, and the
    // gap test must wave it through untouched. Seat 2: one row (the stepped 47)
    // stays ahead of the 149 write, and the sentence says so honestly.
    var tape = Tape.Of(false,
      (150, 2.1), (155, 3.4), (150, 5.0), (148, 7.7), (152, 9.1), (150, 11.4));
    var board = L.Board(47, 150, 165, 180);

    var (_, _, d) = Tape.Price(tape, false, board, current: 160);

    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(150, d.Anchor);
    Assert.Equal(1, d.CraziesSkipped);
    Assert.Equal(2, d.Census.Seat);
    // 150/47 = 3.2x - the queue itself convicts, so live and shadow agree on
    // stepping a real cliff. The doctrine keeps the dump-step class (the audit's
    // 27 receipts, ~171k protected).
    Assert.Equal(149, d.Shadow!.Value.Price);
    Assert.Equal(1, d.Shadow.Value.SteppedRows);
  }

  [Fact]
  public void CS3_Culottes_TheGlideJoinsTheQueueAtItsFront()
  {
    // Two months of monotonic decline, 137k -> 36k, board racing at 34.3k. The
    // original parked at 44,999 - above the last clearing AND the whole board.
    // A8 needed the chaos policy's newest-quarter discriminator to climb down;
    // A10 needs nothing: ten sellers inside a quarter of each other are the
    // market, and the front of them is 34,300. The discriminator is deleted.
    var tape = Tape.Of(false,
      (36_000, 5.59), (48_000, 11.71), (78_400, 24.60), (89_998, 37.42), (89_900, 40.50),
      (90_000, 42.86), (92_998, 43.50), (95_999, 48.79), (95_997, 49.46), (138_999, 64.92),
      (136_455, 70.54), (136_445, 70.63), (125_000, 71.43), (119_999, 71.55), (119_990, 71.84));
    var board = L.Board(34_300, 34_300, 34_300, 34_349, 34_350, 34_555, 35_995, 35_999, 36_000, 36_550);

    var (seg, _, d) = Tape.Price(tape, false, board, current: 44_950);

    Assert.Equal(SegmentCut.Glide, seg.Cut);
    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(34_300, d.Anchor);
    Assert.True(d.AnchorIsListing);
    Assert.Equal(10, d.ClusterSize);
  }

  [Fact]
  public void CS4_GemsapOfStrength_CutsInFrontOfBothQueues_CrossCapIsPhase3()
  {
    // NQ: A8 parked at 420 (the band's top) because the 46-100 board sat below
    // the band and the newest clears still reached 425. A10: 46 and 50 agree,
    // that is a crowd, cut in front of it.
    // THAT IS STILL NOT THE FINAL ANSWER, and for the same reason as before: the
    // same pinch shows HQ clearing at 123-124 with 50 units of HQ at 123 on the
    // board, and no buyer takes NQ above the strictly-superior HQ. The
    // one-directional cross-cap (A4, Phase 3) is what makes this listing sane -
    // it is a CEILING on the NQ ask, so it survives A10 untouched.
    var nq = Tape.Of(false,
      (425, 37.4), (499, 48.65), (420, 48.65), (420, 48.65), (418, 48.65), (400, 48.65),
      (342, 48.65), (350, 48.65), (341, 48.65), (340, 48.65), (32, 48.65),
      (200, 60.05), (200, 60.05));

    var (_, nqLane, nqD) = Tape.Price(nq, false, L.Board(46, 50, 100, 100), current: 175);

    Assert.Equal(350, nqLane.Median);
    Assert.Equal(LaneOutcome.Undercut, nqD.Outcome);
    Assert.Equal(46, nqD.Anchor);
    Assert.Equal(2, nqD.ClusterSize);

    // HQ side, same pinch. A8 anchored the 123 wall-race and called the lone 100
    // bait; A10 notices 100 is within a quarter of 123 - one neighborhood, six
    // sellers - and cuts in front of the whole thing.
    var hq = Tape.Of(true,
      (124, 1.5), (123, 1.5), (699, 38.0), (695, 38.0), (694, 38.0), (430, 38.0), (65, 38.0));
    var (_, _, hqD) = Tape.Price(hq, true, L.Board(100, 123, 123, 123, 123, 123));

    Assert.Equal(LaneOutcome.Undercut, hqD.Outcome);
    Assert.Equal(100, hqD.Anchor);
    Assert.Equal(6, hqD.ClusterSize);
  }

  [Fact]
  public void CS5_SpiritExtract_JoinsTheRoundRace_TheParkIsOwedToPhase4()
  {
    // THE SHARPEST EDGE OF A10, pinned deliberately. A4b's receipt says demand
    // here arrives as rounds - Aurelius took 99 units at 732 - and A8's chaos
    // policy reached the same conclusion from the tape, parking at the band's
    // top (~420). A10 deletes that branch: ten sellers walk 30 -> 190, that is a
    // crowd, and rule 1 cuts in front at 30.
    //
    // A10 rule 5 is explicit that the park-high move SURVIVES as the one
    // exception - tape-proof only, off the buyer column, when Phase 4 builds it.
    // Until then this board sells cheap to a buyer who demonstrably pays more,
    // and this fixture is the alarm that says so out loud.
    var tape = Tape.Of(false,
      (304, 12.6), (308, 12.6), (430, 12.6), (732, 12.6),
      (430, 24.0), (428, 24.0), (426, 24.0), (421, 24.0), (421, 24.0), (421, 24.0),
      (419, 24.0), (418, 24.0), (415, 24.0), (427, 24.0),
      (419, 24.6), (300, 24.6), (232, 24.6),
      (298, 25.6), (199, 25.6), (199, 25.6));
    var board = L.Board(30, 31, 32, 32, 49, 91, 92, 100, 131, 190);

    var (_, _, d) = Tape.Price(tape, false, board, current: 210);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(30, d.Anchor);
    Assert.Equal(4, d.ClusterSize); // 30/31/32/32 - the 49 is more than a quarter up
  }

  [Fact]
  public void CS5b_RoofTileHq_StepsOverTheLone71_AndTakesTheRealQueue()
  {
    // Second receipt of the same pinch. The 2,900 round cluster is a month old
    // and the cliff demotes it; the newest three clears (195-300, this morning)
    // are the market now. A8 parked at 300, the top of that band. A10: the 71 is
    // a lone crazy (0.16x the row behind it, 0.36x the 195 floor) and goes; the
    // 450 is alone but nowhere near far below anything, so it is the queue and
    // we cut in front of it. The 4,000 is over the 3x rail.
    var tape = Tape.Of(true,
      (300, 0.25), (292, 0.25), (195, 0.25), (749, 0.25), (899, 30.5),
      (4_989, 38.44), (2_400, 38.44), (2_499, 38.44), (2_500, 38.44), (2_899, 38.44),
      (2_000, 38.44), (1_500, 38.44), (1_500, 38.44), (999, 38.44), (200, 38.44), (200, 38.44));
    var board = L.Board(71, 450, 4_000);

    var (seg, lane, d) = Tape.Price(tape, true, board, current: 750);

    Assert.Equal(SegmentCut.Cliff, seg.Cut);
    Assert.Equal(3, seg.SegmentCount);
    Assert.Equal(292, lane.Median);
    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(1, d.CraziesSkipped);
    Assert.Equal(450, d.Anchor);
  }

  [Fact]
  public void CS6_GroundSlothLeatherHq_TheQueuePricesIt_RungTwoIsStillOwed()
  {
    // REWRITTEN to A10 (was: HeldThinHistory - one HQ sale is not a going rate).
    // The tape still cannot speak and still says so in the narration. What
    // changed is that a thin tape no longer silences a live queue: three sellers
    // sit at 196-305 and we cut in front of them instead of mailing Drift homework.
    // The ladder's rung 2 (local NQ tape x HQ premium, ~1,180 against a lone HQ
    // clear at 700) is still owed - under A10 it is scoped to boards like this
    // one where the tape is the operand, i.e. thin and empty boards.
    var tape = Tape.Of(true, (700, 14.3));
    var (_, _, d) = Tape.Price(tape, true, L.Board(196, 200, 305), current: 200);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(196, d.Anchor);
    Assert.Equal(2, d.ClusterSize);
    Assert.Contains("too thin to check a price against", d.Evidence);
  }

  [Fact]
  public void CS7_TrueGriffinLeatherHq_TwoAgreeingSalesFixTheWrongPrice()
  {
    // Held at 290 [thin, n=2] while its only two sales - 1,005 and 1,000, six
    // days old, half a percent apart - sat there refused. The spread gate lets
    // them speak; the lone 295 row is then a crazy by both arms (0.29x the band
    // floor, nobody behind it), so the queue empties and rule 3 prices the item
    // at the top of what actually cleared. Same 1,005 as A8, reached honestly.
    var tape = Tape.Of(true, (1_005, 6.56), (1_000, 6.56));
    var (_, lane, d) = Tape.Price(tape, true, L.Board(295), current: 290);

    Assert.Equal(2, lane.SampleCount);
    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(1, d.CraziesSkipped);
    Assert.Equal(1_005, d.Anchor);
  }

  [Fact]
  public void CS8_SmilodonLeatherHq_TakesTheFrontOfTheQueue_A4bIsPhase4()
  {
    // Patient buyers cleared 6,996 and 6,805x2; a sweeper took ~17 units at
    // 1,200-1,500 the next day. A8 cut in front of the 1,505 (the cheapest row
    // it graded competition); A10 cuts in front of the 1,100 - alone, but only
    // 27% under the row behind it and well above half the 1,200 band floor, so
    // it is a seller, not nonsense. Drift's two-clientele ruling (the patient band
    // IS the park) needs the buyer column - A4b, Phase 4, deliberately not built.
    var tape = Tape.Of(true, (1_200, 1.9), (6_805, 2.9), (6_996, 6.2));
    var (_, lane, d) = Tape.Price(tape, true, L.Board(1_100, 1_505, 1_600), current: 3_402);

    Assert.Equal(1_200, lane.BandLow);
    Assert.Equal(6_996, lane.BandHigh);
    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(1_100, d.Anchor);
    Assert.Contains("they disagree", d.Evidence); // the width says "hard call" out loud
  }

  [Fact]
  public void CS9_KumbhiraLeatherHq_RatifiesInsteadOfReflagging()
  {
    // Held at 2,985 [thin, n=2] against 2,981x2 and 2,984 one day old - the price
    // was already right and the count gate could only say "insufficient", so it
    // flagged into triage and queued a community retry. A gate that cannot
    // ratify keeps asking. Our own 2,982 is skipped as ours; the foreign 2,844
    // and 2,985 are one neighborhood, so we cut in front of the cheaper.
    // (A8 anchored 2,984, the band's top, having graded 2,844 bait and 2,985 a
    // dreamer-by-one-gil - two verdicts about two sellers three gil apart.)
    var tape = Tape.Of(true, (2_975, 0.7), (2_984, 1.9), (2_981, 1.9));
    var board = new List<LaneListing> { new(2_844, IsOwn: false), L.Own(2_982), new(2_985, IsOwn: false) };

    var (_, lane, d) = Tape.Price(tape, true, board, current: 2_982);

    Assert.Equal(3, lane.SampleCount);
    Assert.NotEqual(LaneOutcome.HeldThinHistory, d.Outcome);
    Assert.Equal(2_844, d.Anchor);
    Assert.Equal(2, d.ClusterSize);
  }

  [Fact]
  public void Zircon_TheFiveRowCrowdConvictsTheTwoRowPack()
  {
    // RECONSTRUCTED from [[Session - 2026-07-25 - Scrooge]] - Blue Zircon cleared
    // out of the ring before the tape reader shipped, so these numbers are the
    // session's, not the DB's. Segmentation still does its job: the old ~975
    // cluster is demoted and the fresh run (223-439) IS the lane.
    //
    // THE SEED CANARY - and it tripped (A11, 08-02). A8 anchored 296 by calling
    // the 99/100 pair bait; A10 rule 1 immunized them as a cluster and cut in at
    // 99, and this test's A10 form warned that "two agreeing rows outrank five
    // agreeing rows further up" was the first thing to convict if wrong. The
    // Stuffed Alphas were the conviction: the identical shape - a two-row pack
    // far below clearing, outnumbered by the crowd behind it - skipped forever
    // at the floor. Under A11 crowd vs crowd decides: the five sellers at
    // 200-299 outnumber the pack of two, so the pack is crashers and we step
    // over it into the real line at 200.
    var tape = Tape.Of(false,
      (223, 1), (250, 2), (300, 3), (320, 4), (380, 5), (400, 6), (439, 7),
      (975, 10), (970, 10.2), (980, 10.4), (960, 10.6), (990, 10.8), (975, 11),
      (965, 11.2), (985, 11.4), (975, 11.6), (1_000, 11.8), (950, 12), (975, 12.2),
      (940, 12.4), (1_010, 12.6), (975, 12.8), (930, 13), (975, 13.2), (920, 13.4));
    var board = L.Board(99, 100, 200, 200, 296, 297, 299);

    var (seg, lane, d) = Tape.Price(tape, false, board, current: 488);

    Assert.Equal(7, seg.SegmentCount); // the old cluster is demoted, not decayed
    Assert.Equal(320, lane.Median);
    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(200, d.Anchor);
    Assert.Equal(2, d.CraziesSkipped);
    Assert.Contains("stepped over 2 crashers from 99", d.Evidence);
  }

  [Fact]
  public void Sarcenet_LocalClearingOutvotesTheCommunityLane()
  {
    // RECONSTRUCTED (same reason as Zircon). The community lane deployed at
    // ~5,000 and became the operand, so we listed at 2,500 - above every local
    // sale ever - the morning after a bulk buyer swept ~30 units at 600-1,500.
    // With the tape banked there is a local lane at all, so the community rung is
    // never reached. A8 then graded the 505 bait and anchored 999; A10 asks only
    // whether the 505 is nonsense, and it is not - 505 is a hair OVER half the
    // 999 behind it, so the board arm of the crazy test fails and the 505 is the
    // queue. A knife-edge fixture on purpose: it is the closest any receipt sits
    // to the "far below" seed, and it moves the moment that seed does.
    var tape = Tape.Of(true,
      (600, 1), (700, 1), (750, 1), (800, 1), (900, 1), (1_000, 1),
      (1_100, 1), (1_200, 1), (1_300, 1), (1_500, 1));
    var board = L.Board(505, 999, 1_198, 1_199);

    var (_, lane, d) = Tape.Price(tape, true, board, current: 2_500);

    Assert.Equal(LaneSource.Local, lane.Source);
    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(505, d.Anchor);
    Assert.True(d.AnchorIsListing);
  }
}
