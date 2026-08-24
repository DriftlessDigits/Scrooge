using Scrooge.Board;
using System.Collections.Generic;

namespace Scrooge.Rounds;

/// <summary>
/// Everything the round needs to know about what there is to do right now:
/// the work sets, the bell's tally, the fit check's two clocks, and the two
/// derived questions the cursor asks (how many, and is it armed).
///
/// <para>ONE definition, because there are now TWO surfaces reading the same
/// round - the deck in the Ledger and the stage rail in the run log. Computing
/// this twice is how two windows end up describing the same errand differently
/// on the same frame.</para>
/// </summary>
internal readonly record struct DeckState(
  List<InboxRow> RepriceStage,
  List<InboxRow> PullStage,
  int MeltCount,
  List<RoutedItem> ChurnSet,
  BellTally Bell,
  int CofferCount,
  bool RiderArmed,
  FitCheck Fit,
  long? EstRoundMs,
  int ListedAsks,
  long? VentureSecs,
  SealFit? Seals,
  IReadOnlySet<RoundStage> Skipped,
  int ReconStale,
  int RulingsNeeded,
  /// <summary>
  /// THE JUDGMENT QUEUE, BANKED - which stage each unruled row would have ridden, one
  /// entry per row, so the count is the queue's count and the distinct stages are what
  /// the Continue's refusal names.
  ///
  /// <para>On the deck rather than at each draw site for the reason the deck exists at
  /// all: ONE answer per frame. The refusal's headline, the completion report's
  /// still-waiting tally and (absent a walk) <see cref="RulingsNeeded"/> itself are
  /// three readings of one derivation, and three surfaces each re-walking every row for
  /// themselves is how two of them come to describe one board differently - on a hover,
  /// sixty times a second.</para>
  ///
  /// <para>Distinct from <see cref="RulingsNeeded"/> on purpose: while a walk is live
  /// the hinge refuses over the WALK's undecided cases, which is a deliberately looser
  /// number. This stays the queue's own.</para>
  /// </summary>
  IReadOnlyList<RoundStage> JudgmentStages)
{
  /// <summary>
  /// THE PINCH'S LOAD (SF-P2, 2026-08-15; count reworked 08-16) - how many listed
  /// asks a pinch would walk: the BOOK-KEPT roster (the last scan plus our own
  /// placings/pullings since, minus sales), not the raw scan count. It
  /// rides on the deck rather than being read again at the rail because it is also
  /// the fit check's own pinch operand (<see cref="EstRoundMs"/> prices this count at
  /// the pinch's banked pace, inside the whole-round sum): the rail and the fit line
  /// must not price the same errand off two reads of the same book.
  ///
  /// <para>It is NOT <see cref="CountOf"/>(Pinch), which is structurally zero - the
  /// pinch stages nothing, it re-reads a board - and zero is what made every pinch row
  /// say "no timing yet" whether or not it was running.</para>
  /// </summary>
  internal int PinchAsks => ListedAsks;

  internal int CountOf(RoundStage s) => s switch
  {
    RoundStage.BellRun => Bell.Total,
    RoundStage.Desynth => MeltCount,
    RoundStage.TurnIn => ChurnSet.Count,
    // The hinge holds DECISIONS, not rows: the judgment queue's own count, already
    // narrowed to the stages tonight will visit. It is the number the Continue
    // refuses over, so the rail and the gate quote one arithmetic (unit 5 moved
    // that count off the launch button and onto this stage - ruled Q1).
    RoundStage.Triage => RulingsNeeded,
    // The stale half of the listable bag - what recon would actually walk, never
    // the whole scan. "Recon (12)" over an 87-item bag is the honest number
    // because 12 is what the pass costs; reporting 87 would price a stage at
    // seven times its own ETA and read as though the freshness filter did not
    // exist.
    RoundStage.Recon => ReconStale,
    _ => 0,
  };

  /// <summary>
  /// The pinch has work unless the board is fresh - the honest seam where the
  /// cadence gate lands (the pinch's HasWork is otherwise always-true). A skipped
  /// pinch is not marked done: if the board goes stale mid-round the cursor picks
  /// it back up like any other stage that gained work.
  /// </summary>
  internal bool HasWork(RoundStage s)
    => RoundSkips.HasWork(s, RawHasWork(s), Skipped);

  /// <summary>
  /// What the WORLD says, before this run's deferrals. The banner's tally reads
  /// this - a deferred stage's rows are exactly what the round left behind, so
  /// counting them through the skipped answer would report zero of them.
  /// </summary>
  internal bool RawHasWork(RoundStage s)
    => s switch
    {
      RoundStage.Pinch => !Fit.SkipPinch,
      // The bell counts all three of its verbs now - list, reprice, pull.
      RoundStage.BellRun => BellPlan.HasWork(Bell),
      // Coffers ARM the melt (Drift 07-24, restaged 07-25 with the ruled order):
      // unopened coffers are hidden routable inventory, so a 0-row melt with
      // coffers in the bags still has work and the cursor must stop there.
      RoundStage.Desynth => BellPlan.MeltHasWork(MeltCount, CofferCount, RiderArmed),
      // THE HINGE HAS WORK WHEN THE ACT HALF DOES (spec section 4, unit 5). It is a
      // checkpoint, not a pile: what it is FOR is standing between the reads and the
      // irreversible half, so its question is "is there anything about to be spent?"
      // rather than "is anything in my own bucket?". A night that reads and lists
      // nothing has nothing to check, and the cursor skips an empty hinge silently
      // like any other empty stage.
      //
      // Asked of the SKIP-AWARE answer on purpose, unlike its three siblings above:
      // "tonight's act half" is what the hinge guards, and a hinge that stood open
      // over three stages the player deliberately deferred would be gating a round
      // that is not going to spend anything at all.
      RoundStage.Triage => HasWork(RoundStage.Desynth) || HasWork(RoundStage.BellRun)
        || HasWork(RoundStage.TurnIn),
      _ => CountOf(s) > 0,
    };
}
