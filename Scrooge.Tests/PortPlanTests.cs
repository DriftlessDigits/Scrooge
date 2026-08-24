using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// PORT + ARRIVAL (WALK unit 7). The contract:
/// <list type="bullet">
///   <item>the offer is an OFFER - this core can only ever return words;</item>
///   <item>silence before the errand exists, loudness only once we would have
///     offered and something is in the way;</item>
///   <item>a wrong-city port is not sold as a good idea.</item>
/// </list>
/// </summary>
public class PortPlanTests
{
  private static PortDecision Decide(
    bool bellBehind = true,
    int pileCount = 7,
    bool atCounter = false,
    string? destination = "New Gridania",
    bool inDestinationCity = false,
    bool castable = true,
    string blocked = "you're in combat",
    bool blockSettled = true,
    bool bellCloseable = false)
    => PortPlan.Decide(bellBehind, pileCount, atCounter, destination, inDestinationCity, castable,
      blocked, blockSettled, bellCloseable);

  // ========================================================================
  // The destination table - verified against the game sheets, not memory
  // ========================================================================

  [Theory]
  [InlineData(1, 8u)]  // Maelstrom -> Limsa Lominsa Lower Decks
  [InlineData(2, 2u)]  // Order of the Twin Adder -> New Gridania
  [InlineData(3, 9u)]  // Immortal Flames -> Ul'dah - Steps of Nald
  public void AetheryteFor_MapsEachGrandCompanyToItsHqAetheryte(byte gc, uint aetheryte)
    => Assert.Equal(aetheryte, PortPlan.AetheryteFor(gc));

  [Theory]
  [InlineData(0)]   // no Grand Company
  [InlineData(4)]   // anything the sheet grows later
  [InlineData(200)]
  public void AetheryteFor_HasNoDestinationWithoutAGrandCompany(byte gc)
    => Assert.Null(PortPlan.AetheryteFor(gc));

  // ========================================================================
  // Silence - no question asked, no answer given
  // ========================================================================

  [Fact]
  public void Decide_SaysNothingBeforeTheBellIsBehindUs()
    => Assert.True(Decide(bellBehind: false).IsSilent);

  [Fact]
  public void Decide_SaysNothingWithAnEmptyPile()
    => Assert.True(Decide(pileCount: 0).IsSilent);

  [Fact]
  public void Decide_SaysNothingOnceYouAreAtTheCounter()
    => Assert.True(Decide(atCounter: true).IsSilent);

  [Fact]
  public void Decide_StaysSilentAboutABlockedCastNobodyNeeded()
  {
    // The blocker is real, but the errand isn't - a deck that names a gap that
    // costs nothing is noise.
    var d = Decide(pileCount: 0, castable: false);
    Assert.True(d.IsSilent);
    Assert.Equal("", d.Message);
  }

  // ========================================================================
  // The offer
  // ========================================================================

  [Fact]
  public void Decide_OffersThePortWithoutAMessageLine()
  {
    // Ruled 08-23 (the turn-in prompt trim): an offer carries NO sentence - the
    // button label is the offer, and the pile count already rides the stage's
    // fire label. Three sentences at one decision was the finding.
    var d = Decide(pileCount: 7, destination: "Ul'dah - Steps of Nald");
    Assert.Equal(PortVerdict.Offer, d.Verdict);
    Assert.Equal("", d.Message);
  }

  [Fact]
  public void ButtonLabel_NamesWhereItSendsYou()
    => Assert.Equal("Port to New Gridania", PortPlan.ButtonLabel("New Gridania"));

  // ========================================================================
  // The bell-close offer (2026-08-05) - the standing block becomes a button
  // ========================================================================

  [Fact]
  public void Decide_ACloseableBellSession_IsAnOfferNotARefusal()
  {
    // The every-night shape: bell behind, pile waiting, still in the retainer
    // session the round walked us into. Teleport is uncastable and the block is
    // SETTLED - it holds forever on its own - and before this verdict the deck
    // answered "it answered 579" here. The session is closeable, so the honest
    // answer is a button whose click closes it and ports.
    var d = Decide(castable: false, blocked: "the game won't let you cast Teleport right now (it answered 579)",
      blockSettled: true, bellCloseable: true);

    Assert.Equal(PortVerdict.OfferBellClose, d.Verdict);
    // Same trim as the plain offer: the two-act button label says it all.
    Assert.Equal("", d.Message);
  }

  [Fact]
  public void Decide_TheBellCloseOffer_StillWaitsItsTurn()
  {
    // The silent rungs outrank it exactly as they outrank the plain offer: no
    // errand, no question - a closeable window nobody needs closed is not news.
    Assert.True(Decide(pileCount: 0, castable: false, bellCloseable: true).IsSilent);
    Assert.True(Decide(bellBehind: false, castable: false, bellCloseable: true).IsSilent);
    Assert.True(Decide(atCounter: true, castable: false, bellCloseable: true).IsSilent);
  }

  [Fact]
  public void Decide_InTownWithTheBellOpen_TheWalkStillWins()
  {
    // Closing the bell would make the port castable - toward a fare the walk
    // already beats. The wrong-city rung keeps outranking everything below it.
    var d = Decide(inDestinationCity: true, castable: false, bellCloseable: true);
    Assert.Equal(PortVerdict.WalkIsNearer, d.Verdict);
  }

  [Fact]
  public void Decide_AnUnknownOccupier_StillRefusesWithTheGamesAnswer()
  {
    // Not closeable (an NPC, a cutscene, combat): the old settled refusal stands
    // untouched, codes and all - we close what we recognize, nothing else.
    var d = Decide(castable: false, blocked: "the game won't let you cast Teleport right now (it answered 579)",
      blockSettled: true, bellCloseable: false);
    Assert.Equal(PortVerdict.Refuse, d.Verdict);
    Assert.Contains("579", d.Message);
  }

  [Fact]
  public void BellCloseButtonLabel_SaysBothThingsTheClickDoes()
    => Assert.Equal("Close bell & port to New Gridania", PortPlan.BellCloseButtonLabel("New Gridania"));

  // ========================================================================
  // Refusals - loud, and they name the gap
  // ========================================================================

  [Fact]
  public void Decide_RefusesWithoutAGrandCompany()
  {
    var d = Decide(destination: null);
    Assert.Equal(PortVerdict.Refuse, d.Verdict);
    Assert.Contains("haven't joined one", d.Message);
  }

  [Fact]
  public void Decide_RefusesWhenTheGameWontCastIt_AndRepeatsTheGamesReason()
  {
    var d = Decide(castable: false, blocked: "you're in combat");
    Assert.Equal(PortVerdict.Refuse, d.Verdict);
    Assert.Contains("expected Teleport to be castable", d.Message);
    Assert.Contains("you're in combat", d.Message);
  }

  // ========================================================================
  // The wrong-city case - the walk wins, and says why
  // ========================================================================

  [Fact]
  public void Decide_PrefersTheWalkWhenYouAreAlreadyInThatCity()
  {
    // "In town", never "in {destination}": the flag covers the whole city
    // cluster (08-02, the Upper Decks offer), so the line must not claim a
    // zone the player may not be standing in.
    var d = Decide(inDestinationCity: true, destination: "Limsa Lominsa Lower Decks");
    Assert.Equal(PortVerdict.WalkIsNearer, d.Verdict);
    Assert.Equal("you're in town already - the walk to Limsa Lominsa Lower Decks beats the fare", d.Message);
  }

  [Fact]
  public void Decide_TheWalkStillWinsEvenWhenTheCastWouldHaveWorked()
  {
    // Being in the city is decided BEFORE the cast gate: a castable port across a
    // plaza is still a fare for nothing.
    Assert.Equal(PortVerdict.WalkIsNearer, Decide(inDestinationCity: true, castable: true).Verdict);
  }

  // ========================================================================
  // The castability grace (2026-07-26) - a blink is not a blocker
  // ========================================================================

  [Fact]
  public void Decide_AFreshCastBlock_SettlesQuietlyInsteadOfRefusing()
  {
    // The live incident: the turn-in's port was asked the instant the bell run finished,
    // while the retainer UI was still closing, and answered a red refusal quoting a
    // status code for a state that cleared itself in under a second.
    var d = Decide(castable: false, blocked: "the game won't let you cast Teleport right now (it answered 579)",
      blockSettled: false);

    Assert.Equal(PortVerdict.Settling, d.Verdict);
    Assert.DoesNotContain("Can't port", d.Message);
    Assert.DoesNotContain("579", d.Message); // no code the player can act on, for a blink
  }

  [Fact]
  public void Decide_ASettledCastBlock_RefusesWithTheGamesAnswerIntact()
  {
    // Once it outlives the window it is a real blocker again - and the diagnosable
    // detail comes back verbatim, codes and all, so receipts stay readable.
    var d = Decide(castable: false, blocked: "the game won't let you cast Teleport right now (it answered 580)",
      blockSettled: true);

    Assert.Equal(PortVerdict.Refuse, d.Verdict);
    Assert.Contains("expected Teleport to be castable", d.Message);
    Assert.Contains("580", d.Message);
  }

  [Fact]
  public void Decide_TheGraceNeverManufacturesAnOffer()
  {
    // Settling is a THIRD answer, not a softened offer: an un-settled block must never
    // put a teleport button under the player's cursor.
    Assert.False(Decide(castable: false, blockSettled: false).IsOffer);
  }

  [Fact]
  public void Decide_TheGraceDoesNotReachTheSilentOrWalkCases()
  {
    // Order is unchanged: no errand and wrong-city are still decided before the cast
    // gate, so an un-settled block cannot make either of them speak.
    Assert.Equal(PortVerdict.Silent, Decide(pileCount: 0, castable: false, blockSettled: false).Verdict);
    Assert.Equal(PortVerdict.WalkIsNearer,
      Decide(inDestinationCity: true, castable: false, blockSettled: false).Verdict);
  }
}
