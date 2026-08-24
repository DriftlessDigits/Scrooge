using System;
using System.Globalization;

namespace Scrooge;

/// <summary>
/// THE STATE, IN TWO LAYERS. <see cref="Where"/> is where the item IS right now
/// and what your own book paid for one last time; <see cref="Market"/> is what
/// the rest of the data center pays. Two strings rather than one because they
/// are two different witnesses - yours and everybody's - and the pane draws them
/// in two weights so the reader can tell which is which at a glance.
///
/// <para>Either may be empty. A fact the book does not hold produces no clause
/// and no hedge, which is the same rule <see cref="RowMemoir"/> composes under.</para>
/// </summary>
internal readonly record struct StateLine(string Where, string Market);

/// <summary>
/// MOVEMENT 2 - THE STATE-FIRST BOARD (ruled 2026-08-13). Drift's diagnosis of the
/// hinge, verbatim: the awkward part is <i>"understanding the state of the state
/// to make a decision - is this thing already listed?"</i> The pane used to open
/// with the router's argument and its four exits - it offered EXITS before it
/// offered ORIENTATION - so every row started with the reader reconstructing
/// where the thing even was.
///
/// <para>So every pane leads with this, and the options follow as movements FROM
/// it. The shape Drift wrote:</para>
/// <code>
/// Standing at Elwyn for 17,787, listed today. You last sold one in April for 13,338.
/// The DC pays ~7,780 lately (9 sales).
/// </code>
/// <para>or, for a thing that is simply sitting in the bags:</para>
/// <code>
/// In your bags - from tonight's melt.
/// </code>
///
/// <para>It is the receipt trail's grammar ("3m ago @ Elwyn - asked 17,787 -
/// still standing") promoted to the top of the pane and told in sentences: the
/// pattern Drift already liked, applied to the one question the pane never
/// answered first.</para>
///
/// <para>Speaks <see cref="RunLogVoice"/>'s register and borrows its money: plain
/// words, second person, no vocabulary the player does not use out loud. Pure and
/// Dalamud-free (the RunLogVoice/RowMemoir mold, linked into Scrooge.Tests) - the
/// window gathers the facts once per selected row and this composes them, so the
/// wording has exactly one place to be red-penned.</para>
///
/// <para><b>Nothing here guesses.</b> Every operand is nullable and every missing
/// one drops its clause: an ask nobody banked, a listing whose age we never
/// stamped, a variant we have never sold, a DC that has not answered. A state
/// line that filled those in would be the fossilised-measurement failure wearing
/// the one sentence the reader trusts most.</para>
/// </summary>
internal static class BoardStateLine
{
  /// <summary>
  /// WHAT THE BOOK KNOWS ABOUT WHERE THIS ITEM IS. Nullable throughout, on
  /// purpose - see the type doc. Times are <see cref="DateTimeOffset"/> rather
  /// than raw stamps so the composer can speak calendar words ("in April")
  /// without owning a clock or a time zone.
  /// </summary>
  /// <param name="Standing">The item is on the board. False = it is in your bags.</param>
  /// <param name="Retainer">Who is holding it up. Empty when the lane never named one.</param>
  /// <param name="Ask">The price it is standing at. Null when no ask is banked.</param>
  /// <param name="ListedAt">When that ask was decided. Null = unstamped, and then unspoken.</param>
  /// <param name="FromTonightsMelt">This bag row is a yield of a melt THIS round ran.</param>
  /// <param name="LastSalePrice">What you last sold one for. Null = you never have.</param>
  /// <param name="LastSaleAt">When that sale settled. Null = no date on it.</param>
  /// <param name="CommunityMedian">The DC-scope median ask. 0 = the DC has not answered.</param>
  /// <param name="CommunitySamples">Settled DC sales behind that median.</param>
  internal readonly record struct StateFacts(
    bool Standing,
    string Retainer = "",
    long? Ask = null,
    DateTimeOffset? ListedAt = null,
    bool FromTonightsMelt = false,
    long? LastSalePrice = null,
    DateTimeOffset? LastSaleAt = null,
    long CommunityMedian = 0,
    int CommunitySamples = 0);

  /// <summary>
  /// THE OPTIONS' FRAME. One line, above the four exits and the pane's verbs,
  /// naming what they are now that the state has been said: not a menu of things
  /// the item could be, but the moves available from where it actually is.
  /// </summary>
  internal const string MovesHeader = "Your moves from here";

  /// <summary>
  /// THE EMPTY PANE, in the same order the full one reads: where it stands, what
  /// your book knows, then the moves. The old invitation opened on "what each of
  /// the four numbers is made of" - it advertised the exits first, which is the
  /// exact ordering this movement was ruled to undo.
  /// </summary>
  internal const string NoSelection =
    "Pick a row to see where it stands - whether it's already listed and for how much, "
    + "what your book paid for one last time, and the moves you have from there.";

  /// <summary>The lead, composed. See the type doc for the shape and the rules.</summary>
  internal static StateLine Compose(in StateFacts f, DateTimeOffset now)
    => new(Where(f, now), Market(f));

  // ==========================================================================
  // Layer one: where it is, and what you last got for one
  // ==========================================================================

  private static string Where(in StateFacts f, DateTimeOffset now)
  {
    var here = f.Standing ? OnTheBoard(f, now) : InTheBags(f);
    var mine = OwnSale(f, now);
    return mine.Length == 0 ? here : $"{here} {mine}";
  }

  /// <summary>
  /// "Standing at Elwyn for 17,787, listed today." Each clause is earned
  /// separately: an unnamed retainer, an unbanked ask and an unstamped listing
  /// each drop only themselves, so the worst case is a bare "Standing on the
  /// board." - which is still the answer to the question the pane opens on.
  /// </summary>
  private static string OnTheBoard(in StateFacts f, DateTimeOffset now)
  {
    var line = f.Retainer.Length > 0 ? $"Standing at {f.Retainer}" : "Standing on the board";
    if (f.Ask is long ask && ask > 0)
      line += $" for {RunLogVoice.Gil(ask)}";
    if (f.ListedAt is DateTimeOffset listed)
      line += $", {ListedWhen(listed, now)}";
    return line + ".";
  }

  /// <summary>
  /// "In your bags", and the one provenance the round can actually prove: a
  /// variant this Round's own melt runs yielded. Everything else in the bags
  /// arrived by a road nobody recorded, and the honest line for those is the
  /// short one - a provenance we cannot show a receipt for is exactly the kind
  /// of sentence this pane exists to stop inventing.
  /// </summary>
  private static string InTheBags(in StateFacts f)
    => f.FromTonightsMelt ? "In your bags - from tonight's melt." : "In your bags.";

  /// <summary>
  /// "You last sold one in April for 13,338." YOUR sale, not the board's tape:
  /// the tape's observed sales are the memoir's line and they mean something
  /// different (anyone's), so this one says "you" and means it. Silent when the
  /// book has no sale, and silent about the date when the sale has no stamp.
  /// </summary>
  private static string OwnSale(in StateFacts f, DateTimeOffset now)
  {
    if (f.LastSalePrice is not long price || price <= 0)
      return "";
    var when = f.LastSaleAt is DateTimeOffset at ? $" {SoldWhen(at, now)}" : "";
    return $"You last sold one{when} for {RunLogVoice.Gil(price)}.";
  }

  // ==========================================================================
  // Layer two: what everybody else pays
  // ==========================================================================

  /// <summary>
  /// "The DC pays ~7,780 lately (9 sales)." The tilde and "lately" are load
  /// bearing: it is a median over a window nobody chose row by row, and writing
  /// it as a price would claim a precision Universalis never offered. A median
  /// that arrived without its sample count says so by omitting the count rather
  /// than by printing a zero.
  /// </summary>
  private static string Market(in StateFacts f)
  {
    if (f.CommunityMedian <= 0)
      return "";
    var backing = f.CommunitySamples > 0
      ? $" ({f.CommunitySamples} sale{(f.CommunitySamples == 1 ? "" : "s")})"
      : "";
    return $"The DC pays ~{RunLogVoice.Gil(f.CommunityMedian)} lately{backing}.";
  }

  // ==========================================================================
  // Calendar words
  // ==========================================================================

  /// <summary>
  /// How long the ask has been up, in the words a player uses. Days, never
  /// hours: the question this clause answers is "is this ask old news", and
  /// nothing at the hinge turns on the difference between four hours and nine.
  /// A stamp from the future (clock skew, a restored DB) reads as today - the
  /// one thing it certainly is not is old.
  /// </summary>
  internal static string ListedWhen(DateTimeOffset listed, DateTimeOffset now)
  {
    var days = WholeDays(listed, now);
    return days switch
    {
      <= 0 => "listed today",
      1 => "listed yesterday",
      _ => $"listed {days} days ago",
    };
  }

  /// <summary>
  /// WHEN YOU SOLD ONE, on the calendar rather than on a stopwatch. A sale is
  /// remembered as a month ("in April"), because that is how the player
  /// remembers it and because the tape it came off is not precise enough to
  /// deserve a day count. The year appears only once it has to - past twelve
  /// months, "in April" would be a claim about the wrong April.
  /// </summary>
  internal static string SoldWhen(DateTimeOffset sold, DateTimeOffset now)
  {
    if (sold >= now || WholeDays(sold, now) <= 0)
      return "today";
    if (sold.Year == now.Year && sold.Month == now.Month)
      return "earlier this month";
    var month = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(sold.Month);
    // The year is dropped only when the bare month cannot be misread: within the
    // past twelve months AND not the month we are standing in - a sale from last
    // August, read in August, would wear this month's name.
    return sold > now.AddYears(-1) && sold.Month != now.Month ? $"in {month}" : $"in {month} {sold.Year}";
  }

  private static int WholeDays(DateTimeOffset then, DateTimeOffset now)
    => (int)Math.Max(0, Math.Floor((now - then).TotalDays));
}

/// <summary>
/// MOVEMENT 4 - THE ASK, IN THE LIST COLUMN (ruled 2026-08-13). The board's List
/// cell said what a RELIST would fetch and never what the row is asking right now,
/// so the one question a standing row raises first - <i>"is this thing already
/// listed, and for how much?"</i> - was answered everywhere except the column
/// named after it. The reader had to open the pane to read a number the row was
/// already holding.
///
/// <para>So a standing row's List cell draws THE ASK, and the relist preview moves
/// to the hover: the same re-layering <see cref="BoardStateLine"/> made of the
/// pane and <see cref="RunLogVoice"/> made of the log - state in the cell,
/// evidence one hover away, nothing deleted.</para>
///
/// <para><b><see cref="Number"/> is the one truth for that cell.</b> The board
/// draws it and the List column SORTS on it, through this same call - a column
/// that ordered rows by a number it was not showing would be the dual-state bug
/// wearing a sort arrow.</para>
///
/// <para>A bag row has no ask and is untouched: <see cref="Number"/> hands back
/// the preview it always drew.</para>
/// </summary>
internal static class BoardAskCell
{
  /// <summary>
  /// What the List cell shows: the standing ask when there is one, otherwise the
  /// relist preview. A non-positive ask is no ask - the lane banked nothing - and
  /// falls through rather than drawing a zero.
  /// </summary>
  internal static long? Number(long? ask, long? proposal)
    => ask is long a && a > 0 ? a : proposal;

  /// <summary>Whether the cell's number is the ask (state) rather than the preview (a move).</summary>
  internal static bool ShowsAsk(long? ask) => ask is long a && a > 0;

  /// <summary>
  /// The cell's two layers on hover: what you are asking, then what the round
  /// would do about it. Present tense on the ask because the ask is standing;
  /// the preview keeps the run log's "the round writes it", because that is the
  /// tense in which it is still only a proposal.
  ///
  /// <para>A preview that lands on the ask says so instead of printing the same
  /// number twice - "reprice to 17,787" against an ask of 17,787 reads as a move
  /// and is a no-op.</para>
  /// </summary>
  internal static string Hint(long ask, long? proposal)
  {
    var head = $"You're asking {RunLogVoice.Gil(ask)} for this one.";
    string move;
    if (proposal is not long p || p <= 0)
      // A REPORT, NOT A FORECAST (ruled 08-16 walk: "does that mean we _won't_
      // reprice later?"). The missing number is an attempt already made - the last
      // read ran the spine on this lane and kept the ask - and saying "tonight"
      // read as a prediction about the rest of the round. "The last read" rather
      // than "the pinch" on purpose: a skipped pinch leaves an older read holding
      // this answer, and the flatter word is never wrong.
      move = "The last read tried this lane and held your ask - the floors refuse a new number, or the lane's answer was to keep it.";
    else if (p == ask)
      move = "The reprice lands on the same number - nothing to write.";
    else
      move = $"Reprice to {RunLogVoice.Gil(p)} - the round writes it.";
    return $"{head}\n{move}";
  }
}
