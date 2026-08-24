using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE SEAM BETWEEN THE BOARD AND THE WALK (Task 3, 08-15). Four rules that used to
/// be candidates for "a switch in an ImGui method": which exit a verdict button
/// presses, what a scored number IS, why a row is a case at all, and how the two
/// spellings of one override fold into one trap.
/// </summary>
public class TriageBridgeTests
{
  // ---- The verdict button's exit ------------------------------------------

  // Facts rather than Theories: BoardPile and RoutingExit are internal, and an
  // InlineData over them would make a public test method take a less accessible type.
  [Fact]
  public void ExitFor_MapsEveryCaseButtonToTheDoorItPresses()
  {
    Assert.Equal(RoutingExit.List, TriageBridge.ExitFor(BoardPile.List));
    Assert.Equal(RoutingExit.Desynth, TriageBridge.ExitFor(BoardPile.Melt));
    Assert.Equal(RoutingExit.Gc, TriageBridge.ExitFor(BoardPile.Churn));
    Assert.Equal(RoutingExit.Vendor, TriageBridge.ExitFor(BoardPile.PullAndVendor));
  }

  [Fact]
  public void ExitFor_TheEyesAxisIsNotAVerdict()
  {
    // Answering a case with "Review" would be the human answering the question with
    // the question. Reprice is a STANDING verb, and a standing row is a Board Call -
    // it never reaches a case's buttons.
    Assert.Null(TriageBridge.ExitFor(BoardPile.Review));
    Assert.Null(TriageBridge.ExitFor(BoardPile.Defer));
    Assert.Null(TriageBridge.ExitFor(BoardPile.Silent));
    Assert.Null(TriageBridge.ExitFor(BoardPile.Reprice));
  }

  // ---- Provenance: what a number IS ---------------------------------------

  [Fact]
  public void MeltProvenance_ABandAverageNeverWearsAMeasurementsClothes()
  {
    Assert.Equal(WorthProvenance.Measured, TriageBridge.MeltProvenance(MeltGrade.Measured));
    Assert.Equal(WorthProvenance.BandPrior, TriageBridge.MeltProvenance(MeltGrade.Prior));
  }

  [Fact]
  public void MeltProvenance_TheSkillupKnobIsAPeg_NotAPlaceholder()
  {
    // F3 (ruled 08-22, the Archeo Kingdom Scepter): the skill-up worth is the
    // player's own ruled constant - certain by definition. Calling it a
    // placeholder apologized for a peg; only a genuinely empty grade is one.
    Assert.Equal(WorthProvenance.Peg, TriageBridge.MeltProvenance(MeltGrade.Skillup));
    Assert.Equal(WorthProvenance.Placeholder, TriageBridge.MeltProvenance(MeltGrade.None));
  }

  [Fact]
  public void PegConfidence_APegLedVerdictGradesOnThePeg_NotTheSampleCount()
  {
    // The Scepter's chain: thin mat evidence graded the whole score "shaky" and
    // summoned a case whose deciding number was the player's 100k ruling. Peg-led
    // grades Unanimous - it rides with the melt pile and never becomes a case.
    var thin = new BoardConfidence.Evidence(
      Lean: VerdictLean.OffMarket, LaneSampleCount: 1, LaneSpread: 0.0,
      VelocityPerDay: null, RecentSalesCount: 1, EvidenceAgeDays: 0,
      LocalCommunityAccord: Accord.Unknown, MinSamples: 3,
      StaleDays: BoardConfidence.EvidenceStaleDays, VerdictWorth: 100_000, MarketBid: null);
    Assert.Equal(ConfidenceTier.Unanimous, BoardConfidence.PegOrGraded(true, thin, 0));
    // Without the peg the same evidence grades exactly as before - the door only
    // opens where the knob actually led the score.
    Assert.Equal(BoardConfidence.Tier(thin, 0), BoardConfidence.PegOrGraded(false, thin, 0));
  }

  [Fact]
  public void ListProvenance_YourOwnSaleOutranksAnAskThatOutranksNothing()
  {
    Assert.Equal(WorthProvenance.Measured, TriageBridge.ListProvenance(41_200, 39_000));
    Assert.Equal(WorthProvenance.Asked, TriageBridge.ListProvenance(0, 39_000));
    Assert.Equal(WorthProvenance.Placeholder, TriageBridge.ListProvenance(0, 0));
  }

  [Fact]
  public void SealProvenance_AnUnmeasuredRateSaysSo()
  {
    Assert.Equal(WorthProvenance.Measured, TriageBridge.SealProvenance(true));
    Assert.Equal(WorthProvenance.Placeholder, TriageBridge.SealProvenance(false));
  }

  // ---- The referral reason ------------------------------------------------

  [Fact]
  public void ReasonFor_ContradictedWinsOutright()
  {
    // Even with nothing scored: the Alexander rule names a specific piece of evidence
    // against a specific verdict, and "never measured" would bury it.
    Assert.Equal(ReferralReason.Contradicted,
      TriageBridge.ReasonFor(ConfidenceTier.Contradicted, anyScore: false));
    Assert.Equal(ReferralReason.Contradicted,
      TriageBridge.ReasonFor(ConfidenceTier.Contradicted, anyScore: true));
  }

  [Fact]
  public void ReasonFor_NoScoresAtAllIsHonestThinness()
    => Assert.Equal(ReferralReason.NoData,
      TriageBridge.ReasonFor(ConfidenceTier.Mixed, anyScore: false));

  [Fact]
  public void ReasonFor_ScoredButUnresolvedIsTheRouterDecliningToPick()
    => Assert.Equal(ReferralReason.TooClose,
      TriageBridge.ReasonFor(ConfidenceTier.Mixed, anyScore: true));
}
