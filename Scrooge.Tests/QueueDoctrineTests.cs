using System.Collections.Generic;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The queue doctrine's shadow arm (ruled 08-23, live pricing deferred to 3.1).
/// These pins ARE the 3.1 candidate's spec: every board below is a ruling receipt
/// from the 08-22 lap or the 08-23 audit, and the shadow must answer each one the
/// way Drift ruled it. When 3.1 promotes the doctrine to live pricing, these
/// boards move with it.
/// </summary>
public class QueueDoctrineTests
{
  private const double CeilingMult = 3.0;
  private const double NearPct = 0.25; // LanePricing.ClusterNearPct

  private static QueueDoctrine.DoctrineShadow? Eval(long[] prices, int overRail = 0)
  {
    var queue = new List<(long Price, bool Cross)>();
    foreach (var p in prices) queue.Add((p, false));
    return QueueDoctrine.Evaluate(queue, overRail, CeilingMult, NearPct);
  }

  [Fact]
  public void AZeroGilRow_IsUnconvictable_JoinsInsteadOfNaNStepping()
  {
    // 0/0 is NaN and NaN < CeilingMult is false, so an unguarded loop would step
    // a corrupt zero-gil row and bank "successor NaN x the pack" into the
    // corpus. A non-positive top ends the conviction walk - join the front.
    var s = Eval(new long[] { 0, 500, 550 });
    Assert.Equal(1, s!.Value.Price); // Max(1, 0 - 1)
    Assert.Equal(0, s.Value.SteppedRows);
    Assert.Equal("front", s.Value.Defense);
    Assert.DoesNotContain("NaN", s.Value.Defense);
  }

  [Fact]
  public void TheCaligaeWall_IsOneQueue_JoinsTheFront()
  {
    // Receipt 8058: 6,999 / 7,000 / 7,500 / 7,999 / 8,000 - rows one gil apart
    // got opposite convictions and the write landed 4th while narrating 5th.
    // The doctrine reads one continuous line and takes the front.
    var s = Eval(new long[] { 6_999, 7_000, 7_500, 7_999, 8_000 });
    Assert.Equal(6_998, s!.Value.Price);
    Assert.Equal(1, s.Value.Seat);
    Assert.Equal(0, s.Value.SteppedRows);
    Assert.Equal("front", s.Value.Defense);
  }

  [Fact]
  public void TheDyeSixPack_BelowBandButContinuous_JoinsAt292()
  {
    // The #3 trigger board: six sellers 293-301, next real ask 438 (1.46x - no
    // ceiling-multiple hole). The walk-as-built stepped them off tape conviction
    // and priced 438; Drift read 292. "Below what buyers actually pay - that is
    // prediction poison."
    var s = Eval(new long[] { 293, 295, 296, 298, 300, 301, 438, 450, 500 });
    Assert.Equal(292, s!.Value.Price);
    Assert.Equal(1, s.Value.Seat);
    Assert.Equal(0, s.Value.SteppedRows);
  }

  [Fact]
  public void ExcitingLeather_ARealCliff_StillSteps()
  {
    // The F1 control case: 47 then 150 - a 3.2x hole. A real queue does not
    // have a ceiling-multiple gap in the middle of it; the 47 is a dump.
    var s = Eval(new long[] { 47, 150, 160, 175 });
    Assert.Equal(149, s!.Value.Price);
    Assert.Equal(2, s.Value.Seat); // the stepped row still stands ahead - the true seat says so
    Assert.Equal(1, s.Value.SteppedRows);
    Assert.StartsWith("stepped 1", s.Value.Defense);
  }

  [Fact]
  public void TheBigDump_Receipt5924_KeepsItsMargin()
  {
    // The worry's receipt: a 44,444 crash under a ~180k lane (4.0x). Front-join
    // would donate ~135k; the queue itself convicts the dump and the step keeps
    // the margin. 13 of 27 such steps cleared - the class is real.
    var s = Eval(new long[] { 44_444, 180_000, 185_000 });
    Assert.Equal(179_999, s!.Value.Price);
    Assert.Equal(1, s.Value.SteppedRows);
  }

  [Fact]
  public void TheTwoGilSeat_ConvictedByItsOwnSuccessor()
  {
    // Drift's clause (b) example: a 2-gil seat with the line at 292 - 146x. No
    // tape needed; the queue said so itself.
    var s = Eval(new long[] { 2, 292, 300, 310 });
    Assert.Equal(291, s!.Value.Price);
    Assert.Equal(2, s.Value.Seat);
    Assert.Equal(1, s.Value.SteppedRows);
  }

  [Fact]
  public void ADreamerWall_CannotConvictTheClusterUnderIt()
  {
    // The dye's afternoon board: 440-500 cluster, then 3,000+ dreamers behind
    // the rail. A cliff into fantasy is not a cliff into a market - the cluster
    // IS the queue and the front is free.
    var s = Eval(new long[] { 440, 460, 475, 480, 490, 500, 3_000, 3_200 }, overRail: 2);
    Assert.Equal(439, s!.Value.Price);
    Assert.Equal(1, s.Value.Seat);
    Assert.Equal(0, s.Value.SteppedRows);
  }

  [Fact]
  public void ChainedConvictions_EachCompanyAnsweredByItsOwnSuccessor()
  {
    // 2 -> 639 (319x: convicted) -> 3,000 (4.7x: convicted again, it is real
    // and unrailed) -> join at 2,999. Every step carries the queue's own receipt.
    var s = Eval(new long[] { 2, 639, 3_000, 3_100 });
    Assert.Equal(2_999, s!.Value.Price);
    Assert.Equal(2, s.Value.SteppedRows);
    Assert.Equal(3, s.Value.Seat);
  }

  [Fact]
  public void TheTumbleclawWall_FrontIsTheMarket()
  {
    // "if the market is at 44 then it's at 44" - a crashed wall with company is
    // one queue. The doctrine writes 44; whether 44 is worth the slot is the
    // floor law's question downstream, never this function's.
    var s = Eval(new long[] { 45, 45, 46, 48, 50, 52 });
    Assert.Equal(44, s!.Value.Price);
    Assert.Equal(0, s.Value.SteppedRows);
  }

  [Fact]
  public void NoRealLine_NoAnswer()
  {
    // Empty board, or nothing but dreamers: the doctrine has no listing answer -
    // null, never an invented price (the tape-only branch is the walk's).
    Assert.Null(Eval(new long[] { }));
    Assert.Null(Eval(new long[] { 50_000, 52_000 }, overRail: 2));
  }

  [Fact]
  public void AOneGilFront_NeverWritesZero()
  {
    // The same min-1 clamp every undercut mode carries.
    var s = Eval(new long[] { 1, 1, 2 });
    Assert.Equal(1, s!.Value.Price);
  }
}
