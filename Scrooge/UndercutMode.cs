using System;

namespace Scrooge;

// Its own file rather than a corner of Configuration.cs, and for one reason: nothing
// here touches Dalamud, so the test project can link it. Configuration.cs cannot be
// linked - it opens with Dalamud usings and would drag the game into the tests - and a
// migration that can only be checked by launching FFXIV is a migration nobody checks.

/// <summary>
/// HOW THE HAND WRITES THE NUMBER, once the seat is already chosen. The lane picks
/// the SEAT - which row of the board we sit in front of - and that decision is the
/// whole of the pricing judgement. This enum is downstream of it: given the seat,
/// how does the hand actually write the price on the ticket?
///
/// <para>None of these modes moves us to a different place in the queue; they
/// differ only in the last few gil of the number and in how machine-made it looks
/// to somebody reading the board. That is why a mode may be swapped at any time
/// without re-deriving anything upstream - it is a write style, not a price
/// calculation.</para>
///
/// <para><b>Numbers are frozen.</b> Every member carries its explicit ordinal
/// because a persisted config stores the int, not the name. Value 1 is retired and
/// must never be reissued - see <see cref="LegacyUndercutMode"/>.</para>
/// </summary>
public enum UndercutMode
{
  /// <summary>Subtract a fixed gil amount from the lowest listing.</summary>
  FixedAmount = 0,
  /// <summary>Match the lowest listing exactly — no undercut.</summary>
  GentlemansMatch = 2,
  /// <summary>Undercut by rounding down to a clean number. Interval scales with price.</summary>
  CleanNumbers = 3,
  /// <summary>Randomly picks Random Pinch, Gentleman's Match, or Clean Numbers per item.</summary>
  Humanized = 4
}

/// <summary>
/// THE ONE-WAY FOLD off the retired Percentage mode (value 1). Percentage subtracted
/// a share of whatever the lowest listing happened to ask, which made the written
/// price a function of a stranger's ask rather than of the seat we chose - a
/// prediction-era surface. Percentage retired by the doctrine sweep, 2026-08-15.
///
/// <para>The fold is structural, not procedural: it runs in the
/// <see cref="Configuration.UndercutMode"/> setter, so a config from the wild that
/// still carries the int 1 is normalized the moment Dalamud deserializes it and can
/// never reach the mode arithmetic. Deserialization of an out-of-range enum int
/// yields the raw value silently - nothing throws, nothing warns - so guarding at
/// the door is the only guard that holds.</para>
///
/// <para>It lands on <see cref="UndercutMode.FixedAmount"/> with
/// <see cref="Configuration.UndercutAmount"/> untouched. That is deliberate: the
/// stored amount was a percent and is now read as gil, which for the values this
/// mode allowed (1-99) is a small, safe pinch - the honest neighbour of what the
/// player asked for, and never a deeper cut than the percentage would have taken.
/// Anything cleverer would be us guessing at a knob the player can see and set.</para>
///
/// <para>Pure and public so the migration is testable with no game running, which
/// is what this class is for - the failure it guards is invisible until a config in
/// the wild loads.</para>
/// </summary>
public static class LegacyUndercutMode
{
  /// <summary>The retired Percentage ordinal. Frozen; never reissue it.</summary>
  public const int RetiredPercentage = 1;

  /// <summary>
  /// The stored mode as it should be honoured. Any value that is not a live member
  /// - the retired 1, or garbage from a hand-edited config - folds to
  /// <see cref="UndercutMode.FixedAmount"/>, the default the plugin ships with.
  /// </summary>
  public static UndercutMode Fold(UndercutMode stored)
    => Enum.IsDefined(stored) ? stored : UndercutMode.FixedAmount;
}
