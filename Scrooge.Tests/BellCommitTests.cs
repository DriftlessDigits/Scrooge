using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// INVARIANT B - the act half executes exactly what was ruled (Drift, 2026-08-10, the
/// unit-5 addendum).
///
/// <para>The bug these guard is quiet and expensive: the human rules a board, presses
/// Continue, walks to a bell, and the run posts three things he never saw because
/// venture loot arrived in between. Nothing errors, nothing looks wrong, and the
/// market gets a price nobody decided.</para>
/// </summary>
public class BellCommitTests
{
  private static readonly IReadOnlySet<BellVariant> NoYields = new HashSet<BellVariant>();

  private static BellVariant V(uint id, bool hq = false) => new(id, hq);

  private static HashSet<BellVariant> Set(params BellVariant[] v) => v.ToHashSet();

  // ---- The rule itself ------------------------------------------------------

  [Fact]
  public void InTheSnapshot_Rides()
  {
    var hold = BellCommit.Judge(V(100), hingeCommitted: true, Set(V(100), V(200)), NoYields);

    Assert.Equal(BellHold.InTheSnapshot, hold);
    Assert.True(BellCommit.Rides(hold));
  }

  [Fact]
  public void ArrivedAfterTheHinge_IsHeldForTheNextRound()
  {
    // The whole ruling in one assertion: it was in the bags at bell time, it was NOT
    // in the bags at the press, so the human never ruled on it and it does not ride.
    var hold = BellCommit.Judge(V(999), hingeCommitted: true, Set(V(100)), NoYields);

    Assert.Equal(BellHold.ArrivedAfterTheHinge, hold);
    Assert.False(BellCommit.Rides(hold));
  }

  [Fact]
  public void ThisRoundsYield_Rides_EvenThoughTheHingeNeverSawIt()
  {
    // The ONE declared exception. The ruled order puts the melt between the hinge and
    // the bell deliberately - the melt FEEDS the bell - so a yield is authorised by
    // the same press that authorised the melt.
    var hold = BellCommit.Judge(V(5057), hingeCommitted: true, Set(V(100)), Set(V(5057)));

    Assert.Equal(BellHold.ThisRoundsYield, hold);
    Assert.True(BellCommit.Rides(hold));
  }

  [Fact]
  public void YieldsAreVariantExact_AnHqYieldDoesNotAdmitItsNq()
  {
    // HQ is a slot flag and a different listing at a different price. Admitting the
    // NQ off an HQ yield would be the exception quietly widening itself.
    Assert.False(BellCommit.Rides(
      BellCommit.Judge(V(5057, hq: false), true, Set(), Set(V(5057, hq: true)))));
    Assert.True(BellCommit.Rides(
      BellCommit.Judge(V(5057, hq: true), true, Set(), Set(V(5057, hq: true)))));
  }

  // ---- Where it does NOT apply ---------------------------------------------

  [Fact]
  public void AnUncommittedHinge_HoldsNothing_TheLiveScanStands()
  {
    // Outside a Round, and inside one whose hinge was deferred or self-skipped, there
    // is no commit point - so there is no ruling to hold the bell to, and holding rows
    // back would break a manual bell run for a reason that does not exist.
    Assert.Equal(BellHold.Admitted,
      BellCommit.Judge(V(999), hingeCommitted: false, committed: null, NoYields));
    Assert.Equal(BellHold.Admitted,
      BellCommit.Judge(V(999), hingeCommitted: false, Set(V(100)), NoYields));
  }

  // ---- Fail closed ----------------------------------------------------------

  [Fact]
  public void ACommittedHingeWithNoSnapshot_FailsClosed()
  {
    // A Round that started before this build, or one whose config write never landed.
    // It cannot say what the human ruled, so it must not guess: nothing from the gate
    // rides, and the rows wait for the next Round rather than being listed blind.
    var hold = BellCommit.Judge(V(100), hingeCommitted: true, committed: null, NoYields);

    Assert.Equal(BellHold.SnapshotLost, hold);
    Assert.False(BellCommit.Rides(hold));
  }

  [Fact]
  public void ALostSnapshotStillAdmitsThisRoundsYields()
  {
    // The exception is asked FIRST for exactly this case: the melt happened inside
    // this Round under this Round's own press, which is a fact the bell can vouch for
    // without the snapshot's help. Holding yields back over a lost snapshot would
    // punish the player for a config hiccup by stranding materials he watched appear.
    Assert.True(BellCommit.Rides(
      BellCommit.Judge(V(5057), hingeCommitted: true, committed: null, Set(V(5057)))));
  }

  [Fact]
  public void AnEmptySnapshotIsNotTheSameAsNoSnapshot()
  {
    // An empty SET is a real answer - the hinge committed over a board with no gate
    // rows on it - and it admits nothing. A NULL is the absence of an answer and is
    // the fail-closed case. They agree on the outcome here and must not be collapsed:
    // the reasons differ, and one of them is a bug worth seeing in the log.
    Assert.Equal(BellHold.ArrivedAfterTheHinge,
      BellCommit.Judge(V(100), true, Set(), NoYields));
    Assert.Equal(BellHold.SnapshotLost,
      BellCommit.Judge(V(100), true, null, NoYields));
  }

  // ---- The filter -----------------------------------------------------------

  [Fact]
  public void Admit_KeepsPerSlotRows_AndReportsEachHeldVariantOnce()
  {
    // Two stacks of one mat are two listings, so both slots ride on one variant's
    // admission - and two held slots of one variant are ONE line in the log, not two.
    var rows = new[]
    {
      ("slotA", V(100)), ("slotB", V(100)),
      ("lateA", V(999)), ("lateB", V(999)),
      ("yield", V(5057)),
    };

    var admitted = BellCommit.Admit(rows, hingeCommitted: true,
      Set(V(100)), Set(V(5057)), out var held);

    Assert.Equal(new[] { "slotA", "slotB", "yield" }, admitted);
    Assert.Equal(new[] { V(999) }, held);
  }

  [Fact]
  public void Admit_OutsideACommit_PassesEverythingThrough()
  {
    var rows = new[] { ("a", V(1)), ("b", V(2)) };

    var admitted = BellCommit.Admit(rows, hingeCommitted: false, null, NoYields, out var held);

    Assert.Equal(new[] { "a", "b" }, admitted);
    Assert.Empty(held);
  }

  // ---- The routed half (addendum 2) ----------------------------------------

  /// <summary>
  /// The routed snapshot runs the SAME predicate - that is the claim, and it is worth
  /// asserting rather than trusting, because the two halves are filtered in two methods
  /// and the whole reason the routed hole existed is that one of them had a rule the
  /// other did not.
  /// </summary>
  [Theory]
  [InlineData(100u, true)]   // in the routed snapshot: rides
  [InlineData(999u, false)]  // Unanimous gear that arrived after the press: held
  public void RoutedRows_RunTheSameRuleTheGateRowsDo(uint itemId, bool rides)
  {
    var snapshot = Set(V(100), V(200));

    Assert.Equal(rides, BellCommit.Rides(
      BellCommit.Judge(V(itemId), hingeCommitted: true, snapshot, NoYields)));
  }

  [Fact]
  public void ARoutedSnapshotAndAGateSnapshotAreIndependent()
  {
    // Two scans, two precedence rules, two lists. A row admitted by the gate's
    // snapshot must not ride the routed side on that strength - the router owns every
    // variant it evaluated, and borrowing the other half's answer would smuggle a gear
    // row past the confidence gate on a mat's credentials.
    var gate = Set(V(100));
    var routed = Set(V(500));

    Assert.False(BellCommit.Rides(BellCommit.Judge(V(500), true, gate, NoYields)));
    Assert.False(BellCommit.Rides(BellCommit.Judge(V(100), true, routed, NoYields)));
  }

  [Fact]
  public void ARoutedHalfWithNoSnapshot_FailsClosedLikeTheGateHalf()
  {
    // Same three states, same fail-closed answer: a committed Round that cannot say
    // what gear the human ruled must not list gear.
    Assert.Equal(BellHold.SnapshotLost,
      BellCommit.Judge(V(500), hingeCommitted: true, committed: null, NoYields));
  }

  // ---- Re-Look discards the commit (addendum 2) -----------------------------

  [Fact]
  public void AfterAReLookDiscardsTheCommit_ACrashBeforeTheNewContinueFailsClosed()
  {
    // The Re-Look nulls both snapshots and un-marks the hinge. Un-marked, nothing is
    // held at all (the hinge has not committed). But if a crash and a restore somehow
    // land with the mark standing and no snapshot behind it, the bell must admit
    // NOTHING it cannot vouch for rather than fall back on a snapshot describing the
    // board the player has just asked to re-read.
    var wasCommitted = Set(V(100), V(500));

    // Before the Re-Look: the old ruling governs.
    Assert.True(BellCommit.Rides(BellCommit.Judge(V(100), true, wasCommitted, NoYields)));

    // After it, with the hinge un-marked: the live world stands, nothing is held.
    Assert.True(BellCommit.Rides(BellCommit.Judge(V(999), false, null, NoYields)));

    // And in the crash window - marked but snapshot-less - it fails closed.
    Assert.False(BellCommit.Rides(BellCommit.Judge(V(100), true, null, NoYields)));
  }

  [Fact]
  public void TheYieldsExceptionSurvivesAReLook()
  {
    // A Re-Look discards what the human ruled; it does not un-melt anything. The
    // yields are still this Round's, so they still ride once the new Continue lands -
    // and they ride even in the crash window, for the same reason they always do.
    Assert.True(BellCommit.Rides(
      BellCommit.Judge(V(5057), hingeCommitted: true, committed: null, Set(V(5057)))));
  }

  // ---- The four-set commit (review ruling S4) -------------------------------

  /// <summary>
  /// THE FOUR SETS RUN ONE RULE. Drift's ruling on S4 was consistency - "one sentence of
  /// doctrine, all four verbs" - so the melt and the turn-in are told their own snapshot
  /// and get the same three answers the bell's two halves get. These pin the rule the
  /// window's <c>AdmitAgainst</c> hands each set; the window's job is only to hand each
  /// one the right snapshot, which the independence test below is about.
  /// </summary>
  [Theory]
  [InlineData(100u, true)]   // the human ruled this one at the press: it melts
  [InlineData(999u, false)]  // it landed in the bags after Continue: it does NOT melt
  public void MeltRows_RunTheSameRuleTheBellHalvesDo(uint itemId, bool rides)
  {
    // The stakes are higher here than on either bell half: a wrongly-listed item can be
    // pulled back off the market, and a wrongly-melted one is gone.
    var meltSnapshot = Set(V(100), V(200));

    Assert.Equal(rides, BellCommit.Rides(
      BellCommit.Judge(V(itemId), hingeCommitted: true, meltSnapshot, NoYields)));
  }

  [Theory]
  [InlineData(300u, true)]
  [InlineData(999u, false)]
  public void ChurnRows_RunTheSameRuleTheBellHalvesDo(uint itemId, bool rides)
  {
    // Expert Delivery consumes the item exactly as the salvage window does.
    var churnSnapshot = Set(V(300));

    Assert.Equal(rides, BellCommit.Rides(
      BellCommit.Judge(V(itemId), hingeCommitted: true, churnSnapshot, NoYields)));
  }

  [Fact]
  public void TheFourSnapshotsAreIndependent_NoVerbBorrowsAnothersCredentials()
  {
    // Four scans, four sets, four verbs. A row the hinge saw in the LIST pile must not
    // authorise melting that same row - the human ruled a destination, not merely an
    // item, and collapsing the four sets into one would turn "you said list it" into
    // "you said do anything to it".
    var gate = Set(V(100));
    var routed = Set(V(500));
    var melt = Set(V(700));
    var churn = Set(V(900));

    Assert.False(BellCommit.Rides(BellCommit.Judge(V(700), true, gate, NoYields)));
    Assert.False(BellCommit.Rides(BellCommit.Judge(V(900), true, routed, NoYields)));
    Assert.False(BellCommit.Rides(BellCommit.Judge(V(100), true, melt, NoYields)));
    Assert.False(BellCommit.Rides(BellCommit.Judge(V(500), true, churn, NoYields)));
  }

  [Fact]
  public void EveryHalfFailsClosedOnALostSnapshot_IncludingTheOnesThatDestroy()
  {
    // A Round that cannot say what the human ruled must not melt and must not turn in.
    // Nothing is lost by holding: the items are still in the bags next Round.
    foreach (var probe in new[] { V(100), V(500), V(700), V(900) })
      Assert.Equal(BellHold.SnapshotLost,
        BellCommit.Judge(probe, hingeCommitted: true, committed: null, NoYields));
  }

  [Fact]
  public void ALateArrival_IsHeldByAllFourVerbsAtOnce()
  {
    // The end-to-end shape of the ruling: venture loot lands mid-Round, and every one of
    // the four sets refuses it on its own snapshot. It is not an error and nothing warns
    // the player - it simply waits for the next Round's recon.
    var late = V(4242);

    foreach (var snapshot in new[] { Set(V(100)), Set(V(500)), Set(V(700)), Set(V(900)) })
      Assert.Equal(BellHold.ArrivedAfterTheHinge,
        BellCommit.Judge(late, hingeCommitted: true, snapshot, NoYields));
  }

  [Fact]
  public void AnUncommittedHinge_LeavesAllFourVerbsOnTheLiveScan()
  {
    // The 90/10 half of the ruling, and the reason a night where nothing arrives is
    // unchanged: before the press - and outside a Round entirely - no set holds anything.
    foreach (var probe in new[] { V(100), V(500), V(700), V(900), V(4242) })
      Assert.Equal(BellHold.Admitted,
        BellCommit.Judge(probe, hingeCommitted: false, committed: null, NoYields));
  }

  [Fact]
  public void TheYieldsExceptionIsAFactAboutTheRound_NotAboutWhichSetAsks()
  {
    // Asked first, whichever snapshot is passed. The window hands the same yields set to
    // all four sites deliberately: hard-coding "a yield can only be a gate row" would be
    // an assumption about the router's scope that the admission rule has no business
    // holding - and it is the assumption that produced S4 in the first place.
    foreach (var snapshot in new[] { Set(V(100)), Set(V(500)), Set(V(700)), Set(V(900)) })
      Assert.Equal(BellHold.ThisRoundsYield,
        BellCommit.Judge(V(5057), hingeCommitted: true, snapshot, Set(V(5057))));
  }

  [Fact]
  public void AReLookReOpensAllFourSets_AndTheCrashWindowFailsClosedOnAllFour()
  {
    // The Re-Look nulls four snapshots now, not two. Un-marked, every verb is back on
    // the live scan; in the crash window - marked, snapshot-less - every verb holds.
    foreach (var probe in new[] { V(100), V(500), V(700), V(900) })
    {
      Assert.True(BellCommit.Rides(BellCommit.Judge(probe, false, null, NoYields)));
      Assert.False(BellCommit.Rides(BellCommit.Judge(probe, true, null, NoYields)));
    }

    // And what the melt already made still rides, because a Re-Look does not un-melt.
    Assert.True(BellCommit.Rides(
      BellCommit.Judge(V(5057), hingeCommitted: true, committed: null, Set(V(5057)))));
  }

  // ---- The wire format ------------------------------------------------------

  [Fact]
  public void Encode_RoundTripsThroughTheHouseHqConvention()
  {
    // HQ is the item id plus one million - the same encoding banned ids, always-vendor
    // ids and the game's own context-menu ids have used since the beginning. A second
    // encoding for one fact is a second thing to get wrong.
    foreach (var v in new[] { V(1), V(44100), V(1, true), V(44100, true) })
      Assert.Equal(v, BellCommit.Decode(BellCommit.Encode(v)));

    Assert.Equal(44100L, BellCommit.Encode(V(44100)));
    Assert.Equal(1_044_100L, BellCommit.Encode(V(44100, true)));
  }

  [Fact]
  public void ARestoredSnapshot_AdmitsExactlyWhatItAdmittedBeforeTheReload()
  {
    // The reload is what the persisted cursor exists to survive, so the commit has to
    // survive it too. Round-tripping through the wire format must not change one
    // admission - a reload that quietly re-opened the bell would restore the exact
    // drift this invariant closes, at the one moment nobody would notice.
    var before = Set(V(100), V(44100, true));
    var wire = before.Select(BellCommit.Encode).ToList();
    var after = wire.Select(BellCommit.Decode).ToHashSet();

    foreach (var probe in new[] { V(100), V(44100, true), V(44100), V(999) })
      Assert.Equal(
        BellCommit.Judge(probe, true, before, NoYields),
        BellCommit.Judge(probe, true, after, NoYields));
  }
}
