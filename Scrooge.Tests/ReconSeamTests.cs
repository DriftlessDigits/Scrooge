using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE SEAMS THAT KEEP RECON FROM LOOKING LIKE A LISTING RUN (Rounds unit 2).
///
/// <para>The act recon refuses - writing a price, confirming a panel - lives in
/// unsafe game code and cannot be asserted from here. What CAN be asserted is
/// everything that would have to be true for a reconned item to be mistaken for a
/// listed one downstream, and each of these is a real trap the pipeline had to be
/// taught to step over: the sell panel opens pre-filled with the game's suggested
/// ask, so the accounting operands are all present and all wrong.</para>
/// </summary>
public class ReconSeamTests
{
  [Fact]
  public void ListedUnitValue_WouldHappilyBookAReconnedItem_WhichIsWhyThePipelineZeroesIt()
  {
    // THE RECEIPT for the guard in ItemPricingPipeline's finally. Reconned is not a
    // hold (nothing is standing on the market to keep its old price) and not a skip,
    // so the accounting falls straight through to "the price we decided" - a number
    // that would land in the run's gil-on-market total, true up the receipt's
    // decided_price to a listing that does not exist, and reach GilTracker, which
    // excludes only the hawk.
    //
    // Fixing it HERE was the wrong answer: this function is honestly reporting what
    // a listing worth that much would contribute, and teaching it about a run mode
    // would put run-shape knowledge in the accounting. The pipeline refuses to ask
    // the question instead. This test exists so that decision is visible rather
    // than looking like an oversight.
    Assert.Equal(900, ListingAccounting.ListedUnitValue(
      PricingResult.Reconned, finalPrice: 900, currentListingPrice: 800));
    Assert.False(ListingAccounting.IsHeld(PricingResult.Reconned));
  }

  [Fact]
  public void Reconned_IsItsOwnVerdict_NotAnExistingOneWearingANewName()
  {
    // Every other value in this enum is a claim about a LISTING. Recon's listing
    // does not exist, so reusing Listed would put gil on the board that nobody can
    // buy, and reusing Skipped would say the pass learned nothing when learning was
    // the entire errand.
    Assert.NotEqual(PricingResult.Listed, PricingResult.Reconned);
    Assert.NotEqual(PricingResult.Skipped, PricingResult.Reconned);
    Assert.NotEqual(PricingResult.LaneHeld, PricingResult.Reconned);
  }

  [Fact]
  public void TheTwoTriages_StillMapWhereTheyMean()
  {
    // The trap FlowPlan warns about in prose, asserted: RunKind.Standing is the
    // standing-listing EXECUTOR and answers to the bell; RoundStage.Triage is the
    // human hinge and has no run kind at all. Recon sits between them and would be
    // the easiest of the three to wire into the wrong one.
    Assert.Equal(RoundStage.Recon, FlowPlan.StageOf(RunKind.Recon));
    Assert.Equal(RoundStage.BellRun, FlowPlan.StageOf(RunKind.Standing));
    Assert.Equal(RoundStage.BellRun, FlowPlan.StageOf(RunKind.Bell));
  }

  [Fact]
  public void ADeadRecon_HaltsWithARescanPromiseItCanActuallyKeep()
  {
    // Every halt line promises what Resume will do, and recon's is the one that
    // could most easily lie: its work set DERIVES from the freshness filter, so a
    // resumed recon really does skip what it already banked rather than replaying
    // the pass from the top. The sentence has to say that, because a player who
    // aborted 60 items into an 80-item pass needs to know he is not paying for the
    // 60 again.
    var halt = FlowPlan.HaltFor(RoundStage.Recon, "the market board stopped answering, aborted at 12/40");
    Assert.Equal(RoundStage.Recon, halt.Stage);
    Assert.Contains("Recon halted", halt.Message);
    Assert.Contains("aborted at 12/40", halt.Message);
    Assert.Contains("re-derives its work set", halt.Message);
  }

  [Fact]
  public void TheReconStage_SitsBeforeEverythingIrreversible()
  {
    // The one ordering constraint in the round that is not about efficiency. The
    // melt destroys the item; if it ran before the List door had seen a real board,
    // the round would answer "worth more melted than listed?" off banked guesses,
    // and the wrong answer is unrecoverable in a way a bad listing never is.
    // Asserted again here, from unit 2's side, because unit 2 is the first build in
    // which moving recon later would compile AND run.
    var recon = System.Array.IndexOf(RoundPlan.Order, RoundStage.Recon);
    Assert.True(recon < System.Array.IndexOf(RoundPlan.Order, RoundStage.Desynth));
    Assert.True(recon < System.Array.IndexOf(RoundPlan.Order, RoundStage.BellRun));
    Assert.True(recon < System.Array.IndexOf(RoundPlan.Order, RoundStage.TurnIn));
  }
}
