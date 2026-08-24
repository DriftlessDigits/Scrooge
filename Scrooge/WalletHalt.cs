using System;

namespace Scrooge;

/// <summary>
/// THE ONE HALT THAT CLEARS ITSELF (Drift, 2026-07-26, after spending seals by hand to
/// unblock a round: <i>"it didn't auto-resume"</i>).
///
/// <para>The turn-in stops when the seal wallet cannot hold the next item's reward -
/// correctly; the game eats the overflow. That stop halts the round, and until now the
/// halt sat there while the player did the one thing that fixes it (spend seals) right
/// in front of it. Nothing was watching.</para>
///
/// <para><b>Why this halt and no other.</b> Drift's own words about the part of the round
/// that already works: <i>"once I got to the right GC window, the process resumed."</i>
/// A location gate is a SENSED condition that re-offers a stage when it clears, and the
/// wallet is the same shape - a number the plugin can read, that the player changes
/// deliberately, with an unambiguous "it's fine now". Most halts are not that: a server
/// timeout, a stuck dialog, an unreadable inventory have no sensor that says "cleared",
/// and re-offering on a guess would be the flow walking past a corpse. So this file is
/// deliberately about ONE halt, and generalizing it is a design walk, not an edit.</para>
///
/// <para><b>Initiation still stands.</b> The round was pressed; a halt freezes it but
/// does not un-press it. Clearing a gap the round itself named, when the plugin can see
/// the gap is gone, is the same contract as arriving at the counter.</para>
///
/// <para>Pure and Dalamud-free: the shell reads the wallet and the pile, this answers
/// "re-offer or stay put".</para>
/// </summary>
internal static class WalletHalt
{
  /// <summary>
  /// The phrase the turn-in writes into its wallet-full stop reason, and the ONE place
  /// either side spells it. A halt carries its reason as prose (RoundHalt is a message,
  /// by design - it is composed for a human to read), so recognizing this one halt means
  /// matching that prose. Keeping the literal here rather than at both ends is what stops
  /// the recognizer and the writer from drifting apart silently.
  /// </summary>
  internal const string Marker = "seal wallet nearly full";

  /// <summary>Is the round frozen on the wallet, specifically?</summary>
  internal static bool IsWalletHalt(RoundStage? haltStage, string? haltMessage)
    => haltStage == RoundStage.TurnIn
    && haltMessage != null
    && haltMessage.Contains(Marker, StringComparison.Ordinal);

  /// <summary>
  /// Should the wallet halt be cleared and the turn-in re-offered?
  /// </summary>
  /// <param name="roundActive">A round is underway (a cancelled round has no stage to re-offer).</param>
  /// <param name="haltStage">The halted stage, or null when flowing.</param>
  /// <param name="haltMessage">The halt's verbatim message.</param>
  /// <param name="current">Seals held, or null when the wallet is unreadable.</param>
  /// <param name="max">Seal cap for the player's rank, or null when unreadable.</param>
  /// <param name="cheapestPending">
  /// The SMALLEST seal reward among the rows still waiting at the turn-in, or 0 when
  /// there are none. Cheapest rather than "the next one in the list": the stage re-reads
  /// the delivery list at the counter and works whatever it finds, so the honest question
  /// is whether the wallet can hold ANY of the waiting work - if the cheapest fits, the
  /// stage has something to do. A pile whose every row is too big keeps the halt, which
  /// is the same answer the run would reach on its own a second later.
  /// </param>
  /// <param name="lastAutoResumeSeals">
  /// The wallet reading at the last auto-resume, or null if we have not auto-resumed.
  ///
  /// <para>THE ANTI-SPIN. "The cheapest pending row fits" and "the row the run reaches
  /// first fits" are not the same question, and only the run can answer the second - so
  /// a resume can legitimately end in the same halt without turning anything in. The
  /// flow tick runs at frame rate, and that pairing is a loop. Requiring the wallet to
  /// have MOVED since the last auto-resume breaks it without a timer or an attempt
  /// counter: the player spending seals moves it, and a partial run turning items in
  /// moves it, so every case that deserves another try gets one and the case that does
  /// not (nothing changed) gets none.</para>
  /// </param>
  internal static bool ShouldReoffer(
    bool roundActive,
    RoundStage? haltStage,
    string? haltMessage,
    uint? current,
    uint? max,
    int cheapestPending,
    uint? lastAutoResumeSeals = null)
  {
    if (!roundActive) return false;
    if (!IsWalletHalt(haltStage, haltMessage)) return false;
    if (current is not uint held || max is not uint cap) return false; // unreadable = stay put
    if (cheapestPending <= 0) return false;                            // nothing waiting to fit
    if (lastAutoResumeSeals == held) return false;                     // nothing moved - don't spin
    return held + (uint)cheapestPending <= cap;
  }
}
