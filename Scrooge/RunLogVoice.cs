using System;
using System.Collections.Generic;
using System.Globalization;

namespace Scrooge;

/// <summary>
/// ONE run-log row, in two layers: the sentence the player reads and the evidence
/// he gets by hovering it. Composed together because they are one statement split
/// by attention - the line is what the round DID, the hover is what it read to
/// decide - and a surface that composed them apart would eventually let them
/// disagree about the same board.
///
/// <para><see cref="Hover"/> is empty on rows with nothing behind them (a vendor
/// sale reads its own price off the item; there is no board census to show).</para>
/// </summary>
internal readonly record struct VoiceLine(string Line, string Hover);

/// <summary>
/// THE RUN LOG'S VOICE (Movement 1, ruled 2026-08-13). One line:
/// <b>state first, options second, verbs named, tense honest, evidence on hover.</b>
///
/// <para>The anchor Drift wrote, and the register every string here speaks:
/// <i>"Repricing from 680 to 441. You are 3rd in line. First 2 are low balls at
/// 150"</i>. Five rules come out of it, and every composer below obeys all
/// five:</para>
///
/// <list type="number">
/// <item><b>The verb prefix names the act</b> - Reprice / Recon / Skip / List /
/// Vendor / Held. The family grows only where an act genuinely differs (Held
/// earned its seat on 08-15: a checked-and-confirmed ask is not a Reprice, and
/// a guard's refusal before judging is still a Skip).</item>
/// <item><b>Tense is honest.</b> Gil that actually moved speaks in the past
/// ("999 stands" is Held's past-tense verdict; "Posted at 17,787"); a decision nobody acted on speaks as a
/// verdict ("Hold.", "Would ask 13,989"). "Reprice happened so should speak in
/// those terms. Recon is what could happen."</item>
/// <item><b>The destination seat gets an ordinal; the prior seat never does.</b>
/// "moving you from the back of the line to 3rd" - see <see cref="Behind"/>.
/// An ordinal on the old seat reads as a fact about where you WERE, which is
/// exactly the thing the reprice just made untrue.</item>
/// <item><b>Plain words.</b> "low balls", never crashers/bait/dreamers/the 3x
/// tag vocabulary. The internal names survive in the hover's prose, where they
/// are labels on evidence rather than the sentence itself.</item>
/// <item><b>Second person.</b> The log talks to the player, never about him.</item>
/// </list>
///
/// <para>Pure and Dalamud-free (the LanePricing mold, linked into
/// Scrooge.Tests): the UI renders <see cref="VoiceLine.Line"/> and hangs
/// <see cref="VoiceLine.Hover"/> off the row, and owns no wording of its own.
/// Every sentence in the house lives in this file precisely so that Drift's red
/// pen has exactly one place to land.</para>
/// </summary>
internal static class RunLogVoice
{
  /// <summary>Item name with a plain " HQ" marker, so quality-split rows self-explain.</summary>
  internal static string Name(string itemName, bool isHq) => isHq ? $"{itemName} HQ" : itemName;

  internal static string Gil(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

  /// <summary>"1st", "2nd", "3rd", "11th". Empty for a seat nobody knows.</summary>
  internal static string Ordinal(int seat)
  {
    if (seat <= 0)
      return "";
    var suffix = seat % 100 is >= 11 and <= 13
      ? "th"
      : (seat % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
    return $"{seat}{suffix}";
  }

  /// <summary>The destination, spelled out: "3rd in line".</summary>
  internal static string InLine(int seat) => $"{Ordinal(seat)} in line";

  /// <summary>
  /// WHERE YOU WERE, WITHOUT AN ORDINAL (rule 3). A seat behind everybody is the
  /// back of the line; any other old seat is just "further back", because the only
  /// thing the reader needs from the old position is the direction of travel - and
  /// numbering it would dress a position that no longer exists as a fact.
  /// Null when the item was not on the board at all.
  /// </summary>
  internal static string? Behind(int priorSeat, int sellers)
    => priorSeat <= 0 ? null
     : priorSeat > sellers ? "the back of the line"
     : "further back";

  /// <summary>
  /// The move, as a clause hung off the price ("<c>, moving you from the back of
  /// the line to 3rd</c>"). Empty when the seat is unknown - a line that cannot
  /// say where you landed says nothing about it rather than guessing.
  /// </summary>
  internal static string MoveClause(LaneCensus census)
  {
    if (census.Seat <= 0)
      return "";
    if (census.PriorSeat <= 0)
      return $", landing you {InLine(census.Seat)}";
    if (census.PriorSeat == census.Seat)
      return $", still {InLine(census.Seat)}";
    if (census.PriorSeat < census.Seat)
      return $", dropping you to {InLine(census.Seat)}";
    return $", moving you from {Behind(census.PriorSeat, census.Sellers)} to {Ordinal(census.Seat)}";
  }

  /// <summary>
  /// The rows we stepped over, in plain words. A span rather than a single price
  /// when they disagree - "at 150" would be a claim about two different numbers.
  /// </summary>
  internal static string LowBallClause(int count, long? floor, long? ceiling)
  {
    if (count <= 0 || floor is not long low)
      return "";
    var money = ceiling is long high && high != low ? $"from {Gil(low)} to {Gil(high)}" : $"at {Gil(low)}";
    return count == 1
      ? $" The one in front is a low ball {money}."
      : $" Front {count} are low balls {money}.";
  }

  /// <summary>
  /// MATCH IS NOT UNDERCUT (Drift, 2026-08-15 shake: a row Held at 444 hovered
  /// "undercut the cheapest competitor (444)"). The walk's prose is composed
  /// before the price is written - the lane picks an anchor, the undercut MODE
  /// turns it into an ask, and Gentleman's Match turns it into the same number.
  /// So the walk's verb is a claim about an operand it never had, and the one
  /// place that holds both is here: the composer that already has the census and
  /// is being handed the price that actually went on the board.
  ///
  /// <para>Only a price STRICTLY below the cheapest competitor undercut anybody.
  /// The modes only ever subtract (see MarketBoardHandler.UndercutByMode), so
  /// "not below" is "level with" in practice - but the test is written the
  /// fail-closed way round, because "undercut" is the claim that needs the proof.
  /// A no-op on any prose that never said the word.</para>
  /// </summary>
  private static string TrueVerb(string prose, long writtenPrice, long? cheapest)
    => writtenPrice <= 0 || cheapest is not long floor || writtenPrice < floor
      ? prose
      : prose.Replace("undercut the cheapest", "matched the cheapest");

  /// <summary>
  /// THE EVIDENCE LAYER. The census Drift specced - sellers, the reachable
  /// competitors and their span, what sits above the 3x ceiling, where the thing
  /// sells, how fast - and beneath it the walk's own full-grammar prose, unchanged.
  /// Nothing was deleted when the sentence got short; it moved here.
  ///
  /// <para><paramref name="writtenPrice"/> is the ask that actually went on the
  /// board, when there is one: the walk's prose was composed without it, and
  /// <see cref="TrueVerb"/> is the one seam where the two meet. 0 = nothing was
  /// written this pass (a hold, a skip), and the prose stands as composed.</para>
  /// </summary>
  internal static string Hover(LaneCensus census, string? evidence, long writtenPrice = 0)
  {
    var facts = new List<string>();
    if (census.Sellers > 0)
      facts.Add($"{census.Sellers} seller{(census.Sellers == 1 ? "" : "s")}");
    if (census.Competitors > 0 && census.CompetitorFloor is long lo)
      facts.Add(census.CompetitorCeiling is long hi && hi != lo
        ? $"{census.Competitors} from {Gil(lo)}-{Gil(hi)}"
        : $"{census.Competitors} at {Gil(lo)}");
    // THE MULTIPLE NAMES ITS BASE (V19). "3 above the 3x ceiling" is 3x of nothing -
    // the reader cannot tell whether the multiple is on the board, on the tape or on
    // his own ask, and a bare multiplier is the one fact in the census with no operand
    // behind it. The ceiling is the lane's own going rate, so the words say so - and
    // the multiple is QUOTED LIVE off the census (ruled 08-22): the knob is a slider,
    // and a literal here lies the day it moves.
    if (census.AboveCeiling > 0)
      facts.Add($"{census.AboveCeiling} asking past {census.CeilingMult:0.#}x the going rate");
    // A SPAN WHOSE EDGES AGREE IS NOT A SPAN (08-15 shake: "sells 200-200"). One
    // sale draws a band on top of itself, and two numbers printed to say one is
    // the same "at" collapse the competitor fact above already makes. Compared
    // AFTER rounding, because that is the pair the reader actually sees.
    if (census.BandLow is double bandLow && census.BandHigh is double bandHigh)
    {
      var low = (long)Math.Round(bandLow);
      var high = (long)Math.Round(bandHigh);
      facts.Add(high != low ? $"sells {Gil(low)}-{Gil(high)}" : $"sells at {Gil(low)}");
    }
    if (census.PerDay is double pace)
      facts.Add($"~{pace.ToString("0.##", CultureInfo.InvariantCulture)}/day");

    var line = string.Join("; ", facts);
    var prose = TrueVerb((evidence ?? "").Trim(), writtenPrice, census.CompetitorFloor);
    if (line.Length == 0) return prose;
    if (prose.Length == 0) return line;
    return $"{line}\n{prose}";
  }

  /// <summary>
  /// A PRICE THAT WENT ON THE BOARD. The verb follows the act, not the caller: a
  /// first listing is a List, a changed ask is a Reprice, and an ask we looked at
  /// and left standing is a HELD (Drift, 08-15: "Held: 16,750 stands, still 1st in
  /// line."). All three are past tense, because all three are acts that finished -
  /// and Held is past precisely where recon's "Hold." is present, which is the
  /// tense rule telling the reader whether anyone acted. "Reprice: Kept at X" was
  /// the first cut, and Drift's own flinch at it was the finding: the verb primed a
  /// move and the sentence took it back.
  /// </summary>
  internal static VoiceLine Priced(
    long? oldPrice, long newPrice, LaneCensus census,
    int lowBalls, long? lowBallFloor, long? lowBallCeiling, string? evidence)
  {
    var lowBallTail = LowBallClause(lowBalls, lowBallFloor, lowBallCeiling);
    if (oldPrice is not long old || old <= 0)
    {
      var seat = census.Seat > 0 ? $", {InLine(census.Seat)}" : "";
      return new VoiceLine($"List: Posted at {Gil(newPrice)}{seat}.{lowBallTail}", Hover(census, evidence, newPrice));
    }
    if (old == newPrice)
      return new VoiceLine($"Held: {Gil(newPrice)} stands{MoveClause(census)}.{lowBallTail}", Hover(census, evidence, newPrice));
    return new VoiceLine($"Reprice: From {Gil(old)} to {Gil(newPrice)}{MoveClause(census)}.{lowBallTail}", Hover(census, evidence, newPrice));
  }

  /// <summary>
  /// The climb the increase cap deferred. The destination seat is deliberately NOT
  /// spoken here: the capped step lands somewhere the walk never chose, and naming
  /// a seat we did not aim for would read as the decision rather than the brake.
  ///
  /// <para><b>THE LINE STATES THE BEHAVIOUR, NOT THE KNOB</b> (V20). "Big jumps are
  /// capped" taught the rule at the row; what the row owes the reader is what happened
  /// to THIS price - it moved one step and it is still climbing. How big a step, and
  /// whether the brake is armed at all, are the cap knob's own tooltip's business. The
  /// row only exists on a run where the cap is armed (the pipeline raises it under
  /// <c>IsPinchRun &amp;&amp; EnableMaxPriceIncreaseCap</c>), so the sentence never
  /// describes a brake nobody switched on.</para>
  /// </summary>
  internal static VoiceLine RepriceCapped(long oldPrice, long stepPrice, long wanted, LaneCensus census, string? evidence)
    => new($"Reprice: From {Gil(oldPrice)} to {Gil(stepPrice)}, climbing toward {Gil(wanted)}"
           + " - capped, one step per pinch.",
           Hover(census, evidence));

  /// <summary>
  /// NOTHING MOVED, and the line says which price is still standing. "Left at X"
  /// is the past tense a skip earns: the ask really is sitting there. An item with
  /// no listing behind it has nothing to have been left at, so it says so.
  /// </summary>
  internal static VoiceLine Skip(long? standingPrice, string reason, LaneCensus census = default, string? evidence = null)
  {
    var head = standingPrice is long p && p > 0 ? $"Left at {Gil(p)}" : "Nothing posted";
    return new VoiceLine($"Skip: {head}. {Sentence(reason)}", Hover(census, evidence));
  }

  /// <summary>
  /// A PRESS-TIME WARNING, which is not a skip (ruled 2026-08-21). The write did not
  /// happen and the row is waiting on the player - so the verb is its own, and the
  /// sentence is the question the guard actually has. "Skip: Left at 680" would say
  /// the round decided; it did not, it asked.
  /// </summary>
  internal static VoiceLine Warn(string reason, LaneCensus census = default, string? evidence = null)
    => new($"Warn: {Sentence(reason)}", Hover(census, evidence));

  /// <summary>
  /// THE HELD ROLL-UP, READ OFF ITS OWN ROWS (V12, ruled B7). A summary that asserts one
  /// reason for a class the rows already split is a roll-up overwriting the thing it
  /// counts - "{n} held (not enough sales)" said "not enough sales" over rows whose own
  /// line said the board never answered. So the split is DERIVED from the held rows'
  /// spoken lines: the same sentences the reader can scroll to, counted.
  ///
  /// <para><b>Reason-neutral when the split says nothing.</b> One reason across the whole
  /// class is not a split, it is a repetition - "3 held - 3 thin tape" teaches nobody
  /// anything - so a uniform class falls back to the bare count, which is what the
  /// ruling's own fallback asks for.</para>
  ///
  /// <para>The order is fixed (thin tape, then board silent) rather than by count: a
  /// roll-up whose clauses reorder between runs is one the reader has to re-parse every
  /// time.</para>
  /// </summary>
  /// <param name="heldLines">Every held row's spoken line, this run.</param>
  internal static string HeldRollup(IReadOnlyCollection<string> heldLines)
  {
    var n = heldLines.Count;
    var silent = 0;
    foreach (var line in heldLines)
      if (line.Contains(Reasons.BoardSilent, StringComparison.Ordinal)) silent++;
    var thin = n - silent;

    if (silent == 0 || thin == 0) return $"{n} held";
    return $"{n} held - {thin} {Reasons.ThinTapeTag}, {silent} {Reasons.BoardSilentTag}";
  }

  /// <summary>
  /// WHAT COULD HAPPEN (rule 2). Recon changed nothing, so it speaks a verdict:
  /// the ask it WOULD write and the seat that ask WOULD take. Conditional
  /// throughout - "you'd be", never "you are" - because the board it read is
  /// already minutes old by the time anyone acts on it.
  /// </summary>
  internal static VoiceLine Recon(long? wouldAsk, LaneCensus census, string reason, string? evidence)
  {
    if (wouldAsk is not long ask || ask <= 0)
      return new VoiceLine($"Recon: Hold. {Sentence(reason)}", Hover(census, evidence));

    var seat = "";
    if (census.Seat > 0)
    {
      var behind = Math.Max(0, census.Sellers - (census.Seat - 1));
      seat = behind > 0
        ? $" You'd be {InLine(census.Seat)} - {behind} seller{(behind == 1 ? "" : "s")} behind you."
        : $" You'd be {InLine(census.Seat)}.";
    }
    return new VoiceLine($"Recon: Would ask {Gil(ask)}.{seat}", Hover(census, evidence, ask));
  }

  /// <summary>
  /// A LISTING POSTED FROM A DECISION TAKEN EARLIER. Past tense on the act, and
  /// the provenance said out loud in the same breath - a cached post that read
  /// like a fresh board read is the fossilised-measurement failure the receipts
  /// already paid for once. No seat: no board was read this pass, so there is no
  /// honest position to claim.
  /// </summary>
  internal static VoiceLine Posted(long price, int seat, string provenance)
  {
    var line = seat > 0 ? $"List: Posted at {Gil(price)}, {InLine(seat)}." : $"List: Posted at {Gil(price)}.";
    return new VoiceLine($"{line} {Sentence(provenance)}", "");
  }

  /// <summary>The item left the board for the vendor's counter, at the vendor's price.</summary>
  internal static VoiceLine Vendor(int quantity, long gil, string reason)
    => new($"Vendor: Sold {quantity} for {Gil(gil)}. {Sentence(reason)}", "");

  /// <summary>
  /// THE MELT RUN'S REACHABILITY LINE (the Rattan Sofa defect, 2026-08-29). The
  /// desynthesis window shows ONE category at a time and the run can only melt
  /// what the window shows - so three routed-Melt furnishings sat invisible under
  /// the Equipment/Items filter while the run reported plain success, round after
  /// round. "You said melt was an exit when it wasn't" (Drift). The summary now
  /// names the gap and sends the player to the window's own filter - the cure
  /// rides the sentence, per the floor clause's precedent.
  ///
  /// <para>Null when the window covers the pile (equal, or MORE - a hand-widened
  /// selection hides nothing), because a line printed on every clean run is one
  /// the reader learns to skip past.</para>
  /// </summary>
  internal static string? MeltUnreachable(int routed, int reachable)
  {
    var missing = routed - reachable;
    if (missing <= 0)
      return null;
    var verb = missing == 1 ? "isn't" : "aren't";
    // Re-worded with the decision walk (08-30). The run walks every bag
    // category itself now, and the preview renders the hidden rows, so a pile
    // item this line fires for is in NO bag at all - retainer stock is the
    // known case (melt has no lane to it), and the old "cycle the filter"
    // advice would send the player hunting a category that cannot help.
    return $"{missing} of the {routed} routed to melt {verb} in your bags - "
         + "the melt can only reach bag items. If they're in retainer stock, "
         + "pull them to your bags first.";
  }

  /// <summary>
  /// Phase B (2026-08-30): the run cycles the filter itself. The pickup line
  /// states the decision and its operands - which category it switched to and
  /// how many pile items it found there. Null when the category held nothing:
  /// a walk that finds nothing says nothing, and the residual
  /// <see cref="MeltUnreachable"/> line at run end is the honest report for a
  /// pile still out of reach.
  /// </summary>
  internal static string? MeltWalkPickup(int count, string categoryLabel)
  {
    if (count <= 0)
      return null;
    var noun = count == 1 ? "item" : "items";
    return $"Cycled the filter to {categoryLabel} - {count} more {noun} from the melt pile.";
  }

  /// <summary>
  /// THE RECURRING SECOND SENTENCES, in one place. Every one of these used to be an
  /// interpolated fragment at its own call site - "below the minimum listing price
  /// (75 gil)", "history too thin to build a lane" - which is how a house voice
  /// becomes six voices. They are plain words with no internal vocabulary in them
  /// (rule 4) and they are short, because the numbers behind them are on the hover.
  /// </summary>
  internal static class Reasons
  {
    internal const string TooFewSales = "Not enough sales to judge the board against.";
    // "The market board" by name (ruled 08-22): "the board never answered" read as a
    // market claim - an empty board - when the fact is the GAME's board request timing
    // out. And it must never read as Universalis: that pipe's failures get the amber
    // dashboard advisory, never a held row. One constant; the roll-up's split matcher
    // and every seat move with it.
    internal const string BoardSilent = "The market board didn't respond - trying again next pinch.";
    /// <summary>The held roll-up's two-word names for the same two reasons - a summary
    /// line has no room for a sentence, and inventing a third spelling would be how the
    /// roll-up and its rows come to disagree.</summary>
    internal const string ThinTapeTag = "thin tape";
    /// <summary>See <see cref="ThinTapeTag"/>.</summary>
    internal const string BoardSilentTag = "board silent";
    internal const string NoBoardData = "Nothing came back from the board.";
    /// <summary>A routed Vendor verdict executing at the counter - the router's words, never dressed as a player rule.</summary>
    internal const string NoBetterExit = "No better exit in evidence.";
    internal const string NothingWorthStandingBehind =
      "The board makes no sense right now - nothing worth standing behind.";
    internal const string BelowWhatTheBoardPays = "The line sits under what the vendor pays.";

    /// <summary>
    /// THE BINDING FLOOR, in the player's words - "your 75 minimum", "the vendor's
    /// 1,159", "the Enclave's 2,318". The number rides the clause because the whole
    /// job of the sentence is to send him to the right knob.
    ///
    /// <para><b>None speaks for itself</b> (the mechanical pile, 3b). A floor that
    /// bound nothing has no number worth naming and must never borrow another
    /// binding's - it is unreachable today, because the verdict this feeds only fires
    /// when <see cref="EffectiveFloor.Refuses"/> did, and that needs a floor above
    /// zero. The arm is written out anyway so a reordering cannot quietly fold it into
    /// whichever wording happens to sit last.</para>
    /// </summary>
    internal static string FloorClause(EffectiveFloor floor) => floor.Binding switch
    {
      FloorBinding.PlayerMinimum => $"your {Gil(floor.Floor)} minimum",
      FloorBinding.DomanEnclave => $"the Enclave's {Gil(floor.Floor)}",
      FloorBinding.Vendor => $"the vendor's {Gil(floor.Floor)}",
      FloorBinding.None => "the floor",
      _ => "the floor",
    };

    /// <summary>
    /// ONE FLOOR, ONE SENTENCE (ruled 2026-08-21). "Below your minimum." and
    /// "Repricing would land under what the vendor pays." were two spellings of one
    /// verdict - no legal ask exists - and which one a player read depended on which
    /// of two checks happened to run first. The ask is never clamped up to the floor,
    /// so the line says what actually happens: List sits out, and every other exit is
    /// still on the table.
    ///
    /// <para>The operand is the HONEST ask - the price the full lane produced - never
    /// the board read and never the floor. Naming the wrong number here is the Mossy
    /// Stone Daggers lie in a different costume.</para>
    /// </summary>
    internal static string BelowFloor(EffectiveFloor floor, long honestAsk)
      => $"No legal ask - honest price {Gil(honestAsk)}/ea sits under {FloorClause(floor)}. "
       + "List sits out; the other exits compete.";

    /// <summary>
    /// The auto-vendor line's reason when a failed floor sent the item to the counter.
    /// Names the binding floor rather than always saying "the vendor pays more", which
    /// was a lie the moment the player's own minimum was what bound.
    /// </summary>
    internal static string SoldUnderFloor(EffectiveFloor floor)
      => $"The ask sits under {FloorClause(floor)}.";

    /// <summary>
    /// The cached post's provenance. Spelled out in words rather than the "23m old"
    /// shorthand: the whole job of this clause is to stop a decision taken hours ago
    /// from reading like a fresh board read, and an abbreviation is easier to skim
    /// past than a sentence.
    /// </summary>
    internal static string FromTheLook(string age) => $"Priced from your Look, {age}.";
  }

  /// <summary>
  /// How old a banked decision is, in the coarsest unit that is still honest and in
  /// plain words. Deliberately not precise: the age is a trust cue, not a
  /// measurement, and "2 hours old" is the true sentence where "2h 14m old" would
  /// claim a precision the freshness rule does not use.
  ///
  /// <para>The BUCKETING is <see cref="Durations.Coarse"/>'s, the WORDS are this
  /// file's. Sharing the arithmetic is what keeps this rung and the Ledger tables'
  /// from drifting apart; keeping the wording here is the anti-abbreviation ruling -
  /// "23m old" is easier to skim past than a sentence, and skimming past it is the
  /// one thing this clause exists to prevent.</para>
  /// </summary>
  internal static string Age(long seconds)
    => Durations.Coarse(seconds, Durations.RunLogDaysBeginAt) switch
    {
      (AgeUnit.JustNow, _) => "just now",
      (AgeUnit.Minutes, var n) => Plural(n, "minute") + " old",
      (AgeUnit.Hours, var n) => Plural(n, "hour") + " old",
      var (_, n) => Plural(n, "day") + " old",
    };

  private static string Plural(long count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

  /// <summary>
  /// One trailing period, never two. The reason clauses come from four different
  /// places - the guards, the spine, the config - and half of them already end in
  /// one.
  /// </summary>
  private static string Sentence(string text)
  {
    var trimmed = (text ?? "").Trim();
    if (trimmed.Length == 0)
      return "";
    return trimmed[^1] is '.' or '!' or '?' ? trimmed : trimmed + ".";
  }
}
