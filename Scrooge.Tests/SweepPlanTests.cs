using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The one-button sweep's cursor (v2.18). The contract: stages fire in
/// workflow order, empty stages are skipped but not buried (work appearing
/// late is picked up), done is done, and the sweep completes when nothing
/// with work remains.
/// </summary>
public class SweepPlanTests
{
  private static bool All(SweepStage _) => true;

  [Fact]
  public void Order_IsTheEndgameSentence()
  {
    // "kick off a full sweep - pinch, desynth, and then list items for sale"
    // resolved to workflow order: the board read first, everything at the
    // bell next, then the melt, then the churn.
    Assert.Equal(new[]
    {
      SweepStage.Pinch, SweepStage.BellRun, SweepStage.Reprice,
      SweepStage.Desynth, SweepStage.TurnIn,
    }, SweepPlan.Order);
  }

  [Fact]
  public void PlaceOf_ThreeStops()
  {
    Assert.Equal(SweepPlace.Bell, SweepPlan.PlaceOf(SweepStage.Pinch));
    Assert.Equal(SweepPlace.Bell, SweepPlan.PlaceOf(SweepStage.BellRun));
    Assert.Equal(SweepPlace.Bell, SweepPlan.PlaceOf(SweepStage.Reprice));
    Assert.Equal(SweepPlace.Anywhere, SweepPlan.PlaceOf(SweepStage.Desynth));
    Assert.Equal(SweepPlace.ExpertDelivery, SweepPlan.PlaceOf(SweepStage.TurnIn));
  }

  [Fact]
  public void Next_WalksTheOrderAsStagesComplete()
  {
    var plan = new SweepPlan();
    plan.Start();
    Assert.Equal(SweepStage.Pinch, plan.Next(All));
    plan.MarkDone(SweepStage.Pinch);
    Assert.Equal(SweepStage.BellRun, plan.Next(All));
    plan.MarkDone(SweepStage.BellRun);
    Assert.Equal(SweepStage.Reprice, plan.Next(All));
  }

  [Fact]
  public void Next_SkipsEmptyStages()
  {
    var plan = new SweepPlan();
    plan.Start();
    plan.MarkDone(SweepStage.Pinch);
    // No bell work, no reprices - cursor lands on the melt.
    Assert.Equal(SweepStage.Desynth,
      plan.Next(s => s is SweepStage.Desynth or SweepStage.TurnIn));
  }

  [Fact]
  public void Next_PicksUpWorkThatAppearsLate()
  {
    // A pinch flags reprices AFTER the cursor skipped past an empty Reprice:
    // skipped-empty is not marked done, so the stage is picked up in order.
    var plan = new SweepPlan();
    plan.Start();
    plan.MarkDone(SweepStage.Pinch);
    Assert.Equal(SweepStage.Desynth, plan.Next(s => s == SweepStage.Desynth));
    Assert.Equal(SweepStage.Reprice,
      plan.Next(s => s is SweepStage.Reprice or SweepStage.Desynth));
  }

  [Fact]
  public void Unmark_HandsAnAbortedStageBackToTheCursor()
  {
    // The 07-22 sweep lap: melt fired (marked done at fire time), the run died
    // over an open bell, and the deck kept claiming the melt was done. Unmark
    // reverts the fire-time promise so the cursor offers the stage again.
    var plan = new SweepPlan();
    plan.Start();
    plan.MarkDone(SweepStage.Pinch);
    plan.MarkDone(SweepStage.Desynth);
    Assert.Equal(SweepStage.TurnIn,
      plan.Next(s => s is SweepStage.Desynth or SweepStage.TurnIn));
    plan.Unmark(SweepStage.Desynth);
    Assert.Equal(SweepStage.Desynth,
      plan.Next(s => s is SweepStage.Desynth or SweepStage.TurnIn));
  }

  [Fact]
  public void Unmark_OfAnUnmarkedStage_IsANoOp()
  {
    var plan = new SweepPlan();
    plan.Start();
    plan.Unmark(SweepStage.Desynth);
    Assert.False(plan.IsDone(SweepStage.Desynth));
    Assert.Equal(SweepStage.Pinch, plan.Next(All));
  }

  [Fact]
  public void Next_NullWhenEverythingLeftIsDoneOrEmpty()
  {
    var plan = new SweepPlan();
    plan.Start();
    plan.MarkDone(SweepStage.Pinch);
    plan.MarkDone(SweepStage.Desynth);
    Assert.Null(plan.Next(s => s is SweepStage.Pinch or SweepStage.Desynth));
  }

  [Fact]
  public void StartAndCancel_ResetTheCursor()
  {
    var plan = new SweepPlan();
    Assert.False(plan.Active);
    plan.Start();
    plan.MarkDone(SweepStage.Pinch);
    plan.Cancel();
    Assert.False(plan.Active);
    plan.Start();
    // A fresh sweep forgets the last one's progress.
    Assert.Equal(SweepStage.Pinch, plan.Next(All));
  }
}
