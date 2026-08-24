namespace Scrooge;

/// <summary>What the deck should say about porting to the GC counter.</summary>
internal enum PortVerdict
{
  /// <summary>Offer the button. The human's click is the only thing that fires it.</summary>
  Offer,
  /// <summary>
  /// Offer the button, with the bell in the way (2026-08-05): the player is still
  /// in a retainer session the advisor knows how to close (OccupancyTransition,
  /// unit 9), which is a STANDING Teleport block - the grace can never wait it
  /// out, and every round ends in exactly this state. So the click closes the
  /// session and then ports: one press, first law intact, and nothing closes a
  /// window the human didn't ask cleared. Before this verdict the deck answered
  /// "it answered 579" here, every night - accurate, and useless.
  /// </summary>
  OfferBellClose,
  /// <summary>Nothing to say - the port isn't relevant yet. Draw nothing.</summary>
  Silent,
  /// <summary>Portable in principle, but the walk is shorter - say so, offer nothing.</summary>
  WalkIsNearer,
  /// <summary>
  /// The game says "not right now" and it has not been saying it for long. The stage
  /// before this one is still closing its windows; say so quietly and wait. Becomes
  /// <see cref="Refuse"/> if it is still true when the grace window closes.
  /// </summary>
  Settling,
  /// <summary>We would have offered, and something is in the way. Name it out loud.</summary>
  Refuse,
}

/// <summary>The verdict plus the line that says it, in the spine's vocabulary.</summary>
internal readonly record struct PortDecision(PortVerdict Verdict, string Message)
{
  /// <summary>The one thing the button's draw checks.</summary>
  internal bool IsOffer => Verdict == PortVerdict.Offer;

  /// <summary>Nothing to draw at all.</summary>
  internal bool IsSilent => Verdict == PortVerdict.Silent;

  internal static readonly PortDecision Nothing = new(PortVerdict.Silent, "");
}

/// <summary>
/// PORT + ARRIVAL (WALK unit 7, spec ruled 2026-07-23) - the pure half.
///
/// <para>THE ONE PROMPT THE FLOW KEEPS. Everything else in the round is a stage
/// button the human presses at a place he already walked to. A port is different:
/// it YANKS the screen, it has a cast, and it costs gil. So it is offered, never
/// taken - "We EARN what we make" means the teleport fires on the player's click
/// of the offered button and on nothing else. No timer, no arrival trigger, no
/// "the bell finished so off we go". This file decides what the OFFER says; it
/// cannot fire anything, because it cannot reach the game.</para>
///
/// <para>ARRIVAL IS NOT A CLICK. The port drops the player at the aetheryte, not
/// at the counter - he still walks the last yards, opens Expert Delivery, and
/// presses the turn-in like any other stage. The turn-in's place precondition
/// (GcTurnInOrchestrator.AtExpertDelivery) is what clears, and it clears by
/// ARRIVING. That sensor already exists and already gates the stage, so unit 7
/// adds no arrival state machine and no new run kind: a teleport is not a run, so
/// nothing about it reports to RunFlow.</para>
///
/// Pure and Dalamud-free (linked into the test project): the shell reads the GC,
/// the aetheryte sheet, the action status and the territory; this answers "offer,
/// stay quiet, or refuse - and in what words."
/// </summary>
internal static class PortPlan
{
  /// <summary>
  /// The GC HQ aetheryte for a <c>PlayerState.GrandCompany</c> id, or null when
  /// the player has no Grand Company (id 0, or anything the sheet grows later).
  ///
  /// <para>Both halves verified against the game sheets rather than memory
  /// (07-25): GrandCompany rows are 1 Maelstrom / 2 Order of the Twin Adder /
  /// 3 Immortal Flames, and Aetheryte rows 8 / 2 / 9 are Limsa Lominsa Lower
  /// Decks / New Gridania / Ul'dah - Steps of Nald. The ids are the only thing
  /// hardcoded here - the NAME and the territory the shell shows come back out of
  /// the Aetheryte sheet at read time, so a wrong id shows up as a wrong place
  /// name instead of a silent mis-port.</para>
  /// </summary>
  internal static uint? AetheryteFor(byte grandCompany) => grandCompany switch
  {
    1 => 8u, // Maelstrom             -> Limsa Lominsa Lower Decks
    2 => 2u, // Order of the Twin Adder -> New Gridania
    3 => 9u, // Immortal Flames       -> Ul'dah - Steps of Nald
    _ => null,
  };

  /// <summary>
  /// The offer, or the reason there isn't one.
  ///
  /// <para>Order matters, and it is the difference between a quiet deck and a
  /// nagging one. The first two rungs are SILENT: a round that has not reached
  /// the turn-in, or has nothing to turn in, has no port question, and a deck that
  /// answered a question nobody asked is noise. Only once we WOULD have offered
  /// does a blocker become loud - that is the spine's rule (name the gap when the
  /// gap actually costs you something), applied to travel.</para>
  /// </summary>
  /// <param name="bellBehind">
  /// The bell stage is behind us - marked done, OR empty this round. Empty stages
  /// are skipped silently and never marked (see <see cref="RoundPlan.Next"/>), so
  /// "IsDone" alone would suppress the port on every night with no bell work.
  /// </param>
  /// <param name="pileCount">Rows waiting at the turn-in. Zero = no errand.</param>
  /// <param name="atCounter">The turn-in's place precondition is already met.</param>
  /// <param name="destination">The GC aetheryte's place name, or null when unknown.</param>
  /// <param name="inDestinationCity">The player is already standing in that aetheryte's territory.</param>
  /// <param name="castable">The game says Teleport can be cast right now.</param>
  /// <param name="blocked">Why it cannot be cast, phrased as the "but ..." half.</param>
  /// <param name="bellCloseable">
  /// The player is held ONLY by a retainer-bell session the advisor knows how to
  /// close (OccupancyTransition.KnownOccupier). This outranks the castability
  /// read entirely: an occupied player can never cast, the block never settles
  /// itself, and the honest answer is a button whose click closes the session
  /// and ports - not a status code the player can do nothing with.
  /// </param>
  /// <param name="blockSettled">
  /// The castability block has PERSISTED past the grace window (see
  /// <see cref="GracePlan.AutoFireGraceMs"/>). False means it has only just started
  /// and is probably the previous stage still tearing down - on 07-26 the turn-in's
  /// port refused with "it answered 579" the instant the bell run completed, purely
  /// because the retainer UI was still closing, and a later attempt from the same
  /// blink answered 580. The CODE is not the signal and never was; the DURATION is,
  /// which is why any nonzero answer is treated as transient and this flag - not a
  /// status-code table - decides when to say it out loud.
  /// </param>
  internal static PortDecision Decide(
    bool bellBehind,
    int pileCount,
    bool atCounter,
    string? destination,
    bool inDestinationCity,
    bool castable,
    string blocked,
    bool blockSettled = true,
    bool bellCloseable = false)
  {
    // Not our turn yet, or no errand at the end of it. Say nothing.
    if (!bellBehind || pileCount <= 0) return PortDecision.Nothing;

    // Already there. The stage's own button is the thing to press, not a port.
    if (atCounter) return PortDecision.Nothing;

    if (string.IsNullOrEmpty(destination))
      return new(PortVerdict.Refuse,
        "Can't port - expected a Grand Company to port to, but you haven't joined one.");

    // The wrong-city case, ruled toward the WALK: inside the city the aetheryte is
    // in, a port charges gil to move you a plaza. The deck keeps its walk line and
    // says why there's no button, rather than selling a worse option honestly.
    // "In town", not "in {destination}": the flag covers the whole city cluster
    // (08-02, the Upper Decks offer), and the line must not claim a zone the
    // player may not be standing in.
    if (inDestinationCity)
      return new(PortVerdict.WalkIsNearer,
        $"you're in town already - the walk to {destination} beats the fare");

    // The bell session outranks the castability read: being occupied at the bell
    // IS the reason Teleport refuses, it holds forever on its own, and the click
    // can clear it. Checked before the castable branch so the standing block
    // becomes an offer instead of a settled refusal wearing a status code.
    // An OFFER carries no message line (ruled 08-23, the turn-in prompt trim): the
    // button label says everything the sentence said, the pile count already rides
    // the stage's own fire label, and the deck was three sentences deep at one
    // decision. One number said once; the mechanism lives on the tooltip.
    if (bellCloseable)
      return new(PortVerdict.OfferBellClose, "");

    if (!castable)
      return blockSettled
        ? new(PortVerdict.Refuse, $"Can't port - expected Teleport to be castable, but {blocked}.")
        // The blocked text (status code and all) is deliberately NOT shown yet: a
        // number the player can do nothing about, for a state that clears itself in
        // under a second, is noise dressed as diagnostics. It comes back in full the
        // moment the block outlives the window.
        : new(PortVerdict.Settling, "Teleport isn't ready yet - waiting a moment.");

    return new(PortVerdict.Offer, "");
  }

  /// <summary>The button's own text. Short, because it sits beside the walk line.</summary>
  internal static string ButtonLabel(string destination) => $"Port to {destination}";

  /// <summary>The bell-close variant: the click does two things, so the label says both.</summary>
  internal static string BellCloseButtonLabel(string destination) => $"Close bell & port to {destination}";
}
