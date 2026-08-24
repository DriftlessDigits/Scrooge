using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// THE NAMED DOUBT BRANCHES - membership in the Defer pile, as stable
/// vocabulary rather than an invented confidence constant.
///
/// <para>The old Watch pile answered "is this settled?" with a category
/// (races / slow sellers / bait). Defer answers a different question - "which
/// of the walk's shaky moments actually fired on this row?" - and the answer
/// has to be a KEY, not prose, because it rides a contest receipt and gets
/// counted. "Overruled" alone is noise; "overruled a dead-heat call" is
/// signal (Drift, 08-05: the alignment instrument).</para>
///
/// <para>Age is deliberately absent (ruled 08-06). A long-sitter is not a
/// doubtful decision - it is a decision the market has not answered yet, and
/// what Drift wants there is the forward-looking next-round preview Slow Movers
/// already draws, not a seat in this pile.</para>
/// </summary>
internal enum DoubtBranch
{
  /// <summary>No doubt branch fired - the silent board.</summary>
  None,
  /// <summary>The pack and the crowd behind it tied in the A11 outnumbering test. A sale settles the argument; until then we took the front of the line.</summary>
  DeadHeat,
  /// <summary>No local sales census to judge the board against - the price came off the queue (or a borrowed lane) alone.</summary>
  NoTape,
  /// <summary>The walk priced strictly under a better-quality row it could not call nonsense - the A12 fail-closed default, with no HQ tape to convict on.</summary>
  UnconvictableHq,
  /// <summary>The exits disagree about the item's fate (a Mixed ConfidenceTier) and we picked the best of them anyway.</summary>
  MixedTier,
}

/// <summary>
/// THE EYES AXIS, in three states and no fourth (Drift, 08-05: "I think we need
/// 3 states or it will be confusing").
/// </summary>
internal enum EyesState
{
  /// <summary>"I got this." The system acted; there is nothing to flag.</summary>
  Silent,
  /// <summary>"Toss up, but I have opinions." The system ACTS unless overruled - nothing is withheld, nothing waits, and the row says out loud which doubt it acted through.</summary>
  Defer,
  /// <summary>"Tough call, need your input." The action is WITHHELD until answered - the launch refuses over these.</summary>
  Review,
}

/// <summary>
/// THE DEFER PILE'S DECISION CORE (headliner, 2026-08-06). Pure - no game, no
/// storage, no ImGui (the BoardPiles / GatePlan mold, linked into
/// Scrooge.Tests).
///
/// <para><b>What died with Watch.</b> Watch meant "looks wrong, is right,
/// leave it" - settled INACTION, the system being sure. Its whole mechanism
/// was withholding, and it is gone: no lane_held staging limbo, no
/// count-summary roll-up, no pile that feeds nothing. Defer means "decided on
/// thin ice - eyes welcome, not owed": the round spends a Defer row exactly as
/// if it sat in its exit pile, and the only extra affordance is the contest
/// door, open before or after the act.</para>
///
/// <para><b>ONE HOME</b> (ruled 08-06). A Defer row is drawn in the Defer group
/// and nowhere else - no double-draw in the exit pile whose verb it is about to
/// run. The row's four-score strip still presses the planned verb, so nothing
/// is lost; the known trade is that an exit pile's header counts fewer rows
/// than the round executes.</para>
/// </summary>
internal static class DeferPlan
{
  // ---- Stable vocabulary --------------------------------------------------
  // These strings are PERSISTED (contest_receipts.doubt_branch) and counted
  // across weeks. They are the one part of this file that may never be
  // rephrased for taste - a renamed key silently splits a tally in half.

  internal const string DeadHeatKey = "dead_heat";
  internal const string NoTapeKey = "no_tape";
  internal const string UnconvictableHqKey = "unconvictable_hq";
  internal const string MixedTierKey = "mixed_tier";

  /// <summary>The receipt key for a branch. Empty for <see cref="DoubtBranch.None"/> - a row with no doubt writes no branch, never the string "none".</summary>
  internal static string KeyOf(DoubtBranch branch) => branch switch
  {
    DoubtBranch.DeadHeat => DeadHeatKey,
    DoubtBranch.NoTape => NoTapeKey,
    DoubtBranch.UnconvictableHq => UnconvictableHqKey,
    DoubtBranch.MixedTier => MixedTierKey,
    _ => "",
  };

  /// <summary>The inverse, for reading the tape back. Anything unrecognized is <see cref="DoubtBranch.None"/> - a key we no longer understand is not a branch, and guessing would fold a stranger's count into a real one.</summary>
  internal static DoubtBranch BranchOf(string? key) => key switch
  {
    DeadHeatKey => DoubtBranch.DeadHeat,
    NoTapeKey => DoubtBranch.NoTape,
    UnconvictableHqKey => DoubtBranch.UnconvictableHq,
    MixedTierKey => DoubtBranch.MixedTier,
    _ => DoubtBranch.None,
  };

  /// <summary>The branch's short name, for a report line a human reads.</summary>
  internal static string ShortName(DoubtBranch branch) => branch switch
  {
    DoubtBranch.DeadHeat => "dead heat",
    DoubtBranch.NoTape => "no tape",
    DoubtBranch.UnconvictableHq => "unconvictable HQ",
    DoubtBranch.MixedTier => "mixed exits",
    _ => "",
  };

  // ---- The classifier -----------------------------------------------------

  /// <summary>
  /// Which doubt this row acted through. The LANE's own branch wins outright:
  /// it names a specific thing that happened while the price was being decided,
  /// and a Mixed tier over the top of it would replace a fact with a summary.
  /// <see cref="DoubtBranch.MixedTier"/> is the fallback for the rows the lane
  /// never spoke about - the bag gear, whose four exits are scored without a
  /// board walk at all.
  ///
  /// <para>A Contradicted tier is NOT a branch. The market disagreeing with the
  /// verdict is not thin ice, it is evidence against the verdict, and it has
  /// always been demoted to Review - which is where it stays.</para>
  /// </summary>
  internal static DoubtBranch Classify(DoubtBranch laneBranch, ConfidenceTier tier)
    => laneBranch != DoubtBranch.None
      ? laneBranch
      : tier == ConfidenceTier.Mixed ? DoubtBranch.MixedTier : DoubtBranch.None;

  // ---- The three-state assignment -----------------------------------------

  /// <summary>
  /// Which of the three states a row is in.
  ///
  /// <para><paramref name="inReview"/> - the ROUTER declined to call it (or a
  /// Contradicted tier demoted it). Nobody has made a call, so there is nothing
  /// to defer TO, and the row keeps Review's contract: the action is withheld
  /// and the launch refuses over it.</para>
  ///
  /// <para><paramref name="hasWinner"/> - a verb is actually on the table. A row
  /// with no scored winner is genuinely unrulable by the system, which is
  /// Review's whole population; deferring it would be the system claiming an
  /// opinion it does not hold.</para>
  ///
  /// <para><paramref name="playerResolved"/> - the human already ruled. His call
  /// is not thin ice, whatever ours was.</para>
  ///
  /// <para><paramref name="ridesWholePile"/> - a stage that takes its pile
  /// ENTIRE (the melt run) spends every tier identically, so a Mixed row there
  /// was never waiting on anything and has no doubt worth flagging. Kept out of
  /// Defer for the same reason it is kept out of the judgment queue: a flag
  /// nobody can act on differently is noise.</para>
  /// </summary>
  internal static EyesState State(DoubtBranch branch, bool inReview, bool hasWinner,
    bool playerResolved, bool ridesWholePile = false)
  {
    if (!hasWinner || inReview) return EyesState.Review;
    if (playerResolved || ridesWholePile) return EyesState.Silent;
    return branch != DoubtBranch.None ? EyesState.Defer : EyesState.Silent;
  }

  /// <summary>Shorthand for the one question every consumer actually asks.</summary>
  internal static bool IsDeferred(DoubtBranch branch, bool inReview, bool hasWinner,
    bool playerResolved, bool ridesWholePile = false)
    => State(branch, inReview, hasWinner, playerResolved, ridesWholePile) == EyesState.Defer;

  // ---- What the row says --------------------------------------------------

  /// <summary>
  /// THE DOUBT, IN ONE LINE. The branch is the key; this is the sentence the
  /// row wears beside its name. Every one of them ends the same way on purpose:
  /// the contract is "eyes welcome, not owed", and a row that only stated the
  /// doubt would read like a warning the player owes an answer to.
  /// </summary>
  internal static string RowLine(DoubtBranch branch) => branch switch
  {
    DoubtBranch.DeadHeat =>
      "a dead heat on the board - we took the front of the line and let a sale settle it; overrule freely.",
    DoubtBranch.NoTape =>
      "no sales on record to judge the board against - priced on principle; overrule freely.",
    DoubtBranch.UnconvictableHq =>
      "no HQ tape - priced under a better item we couldn't call nonsense; overrule freely.",
    DoubtBranch.MixedTier =>
      "the exits disagree about this one - we took the best of them; overrule freely.",
    _ => "",
  };

  /// <summary>
  /// THE DOUBT, AS A TAG (SF-P4, ruled 2026-08-15). Same disease Movement 4
  /// cured on On Market (<see cref="OnMarket.QueueTag"/>): a full sentence drawn
  /// inline in a table cell is a paragraph on a surface whose whole job is a
  /// scan, and the board clipped it mid-word. Nothing is deleted - the sentence
  /// moves to the hover and the cell keeps this: what the row DID, in the fewest
  /// words that still say it.
  ///
  /// <para>The tag is compressed out of <see cref="RowLine"/>, never authored
  /// beside it - one voice, one fact, and a tag that could drift from the
  /// sentence it summarises would be two stories about one row.</para>
  ///
  /// <para><b>Statement-shaped</b> (SF-P5, Drift: <i>"are those questions? or
  /// statements"</i>). A Defer row asks nothing - it reports a call already
  /// made - so every tag reads as a completed act in the past tense. The
  /// invitation ("overrule freely") stays on the sentence: it is the contract,
  /// not the fact, and repeating it on every row would make the scan an
  /// argument.</para>
  /// </summary>
  internal static string RowTag(DoubtBranch branch) => branch switch
  {
    DoubtBranch.DeadHeat => "took the front of a dead heat",
    DoubtBranch.NoTape => "priced with no sales on record",
    DoubtBranch.UnconvictableHq => "priced under a better item, no HQ tape",
    DoubtBranch.MixedTier => "took the best of disagreeing exits",
    _ => "",
  };

  /// <summary>
  /// The kept-ask row's tag - the same trade as <see cref="RowTag"/>, over
  /// <see cref="KeptAskLine"/>. The number IS the fact here (the row's whole act
  /// was keeping it), so it survives the compression and the "no lane to judge it
  /// against" half goes to the hover.
  /// </summary>
  internal static string KeptAskTag(long? ask)
    => ask is long a && a > 0 ? $"kept your ask at {a:N0}" : "kept your ask";

  /// <summary>
  /// THE DEFER BADGE'S GLYPH (SF-P5, ruled 2026-08-15). It was a question mark,
  /// and a question mark is a promise the row is asking something. It is not: it
  /// runs either way and nothing is waiting on the player. The "?" is reserved
  /// for the rows that genuinely ask - which on this board is the row that draws
  /// no badge of its own and sits in Review.
  ///
  /// <para>A chevron, because the one thing the badge has to say is that the row
  /// GOES: it is already on its way to its exit pile, flagged. It stays in the
  /// board's ASCII vocabulary beside "*", "!", "~", "-" and "x".</para>
  /// </summary>
  internal const string Badge = ">";

  /// <summary>
  /// What the badge says on hover: the state, then the row's own sentence, then
  /// the contract said out loud - because the contract is the part the glyph
  /// cannot carry, and it is the opposite of what a badge on a board usually
  /// means.
  /// </summary>
  internal static string BadgeHint(string rowLine)
    => rowLine.Length > 0
      ? $"Defer - {rowLine} This one runs either way; nothing is waiting on you."
      : "Defer - this one runs either way; nothing is waiting on you.";

  /// <summary>
  /// The LABELING-ONLY line for a rehomed pinch-side lane_held row (ruled
  /// 08-06, interim). Keeping the standing ask IS the act here - there is no
  /// behavior to change and none was changed - so the row says what was kept
  /// and against what nothing. The standing-book optimization walk owns
  /// whether that ask should have moved; tonight it only stops pretending the
  /// silence was a settled verdict.
  /// </summary>
  internal static string KeptAskLine(long? ask)
    => ask is long a && a > 0
      ? $"kept your ask at {a:N0} - no lane to judge it against; overrule freely."
      : "kept your ask where it stands - no lane to judge it against; overrule freely.";

  /// <summary>
  /// The Defer group's header hint. States the contract, because the contract
  /// is the entire reason the pile exists and it is the opposite of Review's.
  /// </summary>
  internal const string GroupHint =
    "Decided on thin ice. The round runs these exactly as if they sat in their exit pile - "
  + "nothing here is waiting on you. Each row says which doubt it acted through; overrule any of "
  + "them from its score cells, before or after, or never.";
}
