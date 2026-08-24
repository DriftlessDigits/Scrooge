using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The run-pace learning model. Every ETA the round's rail quotes comes out of this
/// arithmetic, and until it moved out of the Ledger window's draw file there was no
/// way to say what it did except read it.
/// </summary>
public class PaceBankTests
{
  private static readonly Dictionary<string, float> NoStages = new();

  [Fact]
  public void FirstMeasurementIsAdoptedWhole()
  {
    // A zero prior is "never run", not "instant" - blending against it would report
    // a first run as three tenths of its real pace forever after.
    Assert.Equal(1000f, PaceBank.Blend(0f, 1000f));
    Assert.Equal(1000f, PaceBank.Blend(-1f, 1000f));
  }

  [Fact]
  public void BlendIsSeventyThirtyPriorFirst()
  {
    // 1000 banked, 2000 measured -> 700 + 600.
    Assert.Equal(1300f, PaceBank.Blend(1000f, 2000f), 3);
    // It moves TOWARD the sample, never past it.
    Assert.Equal(700f, PaceBank.Blend(1000f, 0f), 3);
  }

  [Fact]
  public void GcAndCofferRunsAreExcludedFromTheOverallSeed()
  {
    Assert.True(PaceBank.CountsTowardOverall(isGcRun: false, isCofferRun: false));
    Assert.False(PaceBank.CountsTowardOverall(isGcRun: true, isCofferRun: false));
    Assert.False(PaceBank.CountsTowardOverall(isGcRun: false, isCofferRun: true));
  }

  [Fact]
  public void ARunThatProcessedNothingTeachesNothing()
  {
    var update = PaceBank.Record(60_000, 0, false, false, "Pinch", 1000f, NoStages);
    Assert.False(update.Any);
    Assert.Null(update.Overall);
    Assert.Null(update.Stage);
  }

  [Fact]
  public void PaceIsElapsedOverItemsAndLandsInBothBanks()
  {
    // 10 items in 20s = 2000ms/item, blended against a 1000ms prior in each bank.
    var update = PaceBank.Record(20_000, 10, false, false, "Pinch", 1000f,
      new Dictionary<string, float> { ["Pinch"] = 1000f });

    Assert.True(update.Any);
    Assert.Equal(1300f, update.Overall!.Value, 3);
    Assert.Equal("Pinch", update.Stage!.Value.Key);
    Assert.Equal(1300f, update.Stage!.Value.Value, 3);
  }

  [Fact]
  public void AGcRunStillLearnsItsOwnStageKey()
  {
    // The exclusion is from the SHARED seed only - the per-stage bank is exactly
    // what let the GC run record a pace at all.
    var update = PaceBank.Record(20_000, 10, isGcRun: true, isCofferRun: false,
      "TurnIn", 1000f, NoStages);

    Assert.Null(update.Overall);
    Assert.Equal("TurnIn", update.Stage!.Value.Key);
    Assert.Equal(2000f, update.Stage!.Value.Value, 3); // no prior for that key yet
  }

  [Fact]
  public void ARunThatIsNotAStageMovesOnlyTheOverallSeed()
  {
    var update = PaceBank.Record(20_000, 10, false, false, stageKey: null, 1000f, NoStages);

    Assert.Equal(1300f, update.Overall!.Value, 3);
    Assert.Null(update.Stage);
  }
}
