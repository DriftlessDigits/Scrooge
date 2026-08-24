using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// A verb the human staged on a standing listing, AND THE CALL HE STAGED IT AGAINST
/// (Rounds unit 5, addendum 3).
///
/// <para>The second half is the whole point. A staged verb used to be a bare
/// <see cref="StandingAction"/>, which meant that when a re-read changed what the
/// board was saying about a lane, the verb rode on regardless - the human's "vendor
/// this" outliving the reason he said it. Drift's ruling: <i>"if we have useful new
/// information, we should present it."</i> So the verb remembers what it was answering,
/// and a changed answer re-asks the question.</para>
/// </summary>
internal readonly record struct StagedVerb(StandingAction Action, BoardPile AgainstCall);

/// <summary>
/// THE RE-ASK (Drift, ruled 2026-08-10, addendum 3). One rule for both halves of the
/// board: <b>a ruling is staged against a call; if the call changes, ask again.</b>
///
/// <para>Bag gear has worked this way since the ruled board - its persisted rulings are
/// keyed on the router's own verdict, so a new verdict misses the key and the row draws
/// unruled. Standing listings did not, and the gap only became visible when the Re-Look
/// learned to re-open the hinge: a player who re-read every board would be walked back
/// to a hinge showing him verbs he had staged against reads that no longer existed.</para>
///
/// <para><b>THE RE-ASK FIRES AT THE FRESH PINCH</b> (review ruling S5, 2026-08-10). It
/// was first written as though the Re-Look served it, and that premise was false in both
/// directions: a Re-Look runs RECON, recon banks decisions rather than raising standing
/// rows, and no recon has ever moved a lane's call. The pinch is the only pass that
/// stamps one - so the pinch is the only door this rule needs, and it reports through it
/// on EVERY pass, including one that raised nothing (see <see cref="Reports"/>).</para>
///
/// <para><b>NO THRESHOLD, and that is deliberate.</b> "Changed" means the lane's
/// DISCRETE call changed class - the pile it is proposed for. It is not a price delta,
/// because a delta needs a knob, a knob needs a number, and a number nobody measured
/// reads like a rule (the standing refusal on this codebase - see the fit check's
/// clocks and the Look's age label, both of which state and never gate). A price that
/// wiggles inside the same call is the same call, and the verb carries. A lane that
/// moves from reprice to pull-and-vendor is a different question, and it gets asked.</para>
///
/// Pure and Dalamud-free (linked into the test project).
/// </summary>
internal static class StandingReAsk
{
  /// <summary>
  /// Does this staged verb survive the re-present? Only while the call it answers is
  /// still the call the board is making.
  /// </summary>
  internal static bool Survives(in StagedVerb staged, BoardPile callNow)
    => staged.AgainstCall == callNow;

  /// <summary>
  /// The row's own sentence when a verb was withdrawn: what he had said, and what
  /// changed under it. Said on the ROW rather than in a summary, because the row is
  /// where he will answer it - and said plainly enough that "why is this asking me
  /// again" never needs a second surface.
  ///
  /// <para>Empty when nothing was withdrawn. A note about a verb that still stands
  /// would be the board narrating its own steadiness.</para>
  /// </summary>
  internal static string Note(in StagedVerb staged, BoardPile callNow)
  {
    if (Survives(staged, callNow)) return "";
    return $"you staged {Verb(staged.Action)}; the call moved from "
      + $"{BoardLayout.GroupTitle(staged.AgainstCall)} to {BoardLayout.GroupTitle(callNow)} - "
      + "asking again";
  }

  /// <summary>
  /// DOES THIS RUN RE-PRESENT THE BOARD? (review ruling S5 - the ghost-verb fix.)
  ///
  /// <para>A PINCH always does, even one that raised nothing. The hand-off used to be
  /// gated on "this run produced standing rows", which quietly meant that the one pass
  /// able to supersede a staged verb only got to say so when it also had new rows to
  /// hand over - so a pinch that re-priced every lane cleanly left last night's verbs
  /// standing against calls that no longer existed, and the bell spent them. Zero rows
  /// is new information: it says the lanes those verbs answered are not flagged any
  /// more.</para>
  ///
  /// <para>Every other run kind reports only what it raised, unchanged: they carry no
  /// fresh call for a lane, so re-presenting on their account would withdraw verbs
  /// against nothing. The pinch classification itself stays at the call site, where the
  /// run modes live.</para>
  /// </summary>
  internal static bool Reports(bool isPinchRun, int standingCount)
    => isPinchRun || standingCount > 0;

  /// <summary>
  /// One lane's staged verb as the re-present sees it: the row that holds it, the lane
  /// it is on, the verb-and-call pair, and whether the human raised it himself from the
  /// On Market tab (a CONTEST) rather than a pinch raising it for him.
  /// </summary>
  internal readonly record struct Staged<TRow, TLane>(
    TRow Row, TLane Lane, StagedVerb Verb, bool IsContest);

  /// <summary>
  /// THE RE-PRESENT: every staged verb held up against the call the fresh pinch is
  /// making on its lane. Returns what carried (row, lane and verb, so the caller can
  /// re-key by whatever identity it holds rows under) and what was withdrawn, with the
  /// sentence each withdrawal owes its row.
  ///
  /// <para><b>ONE LANE, ONE RULING - THE CONTEST WINS</b> (review ruling S10). A lane can
  /// hold two staged verbs at once: one on a row a pinch raised, one on the contest the
  /// human raised from the On Market tab. Two entries meant the executor queued the lane
  /// twice, on conflicting verbs, in whichever order a dictionary happened to enumerate.
  /// The contest is the most recent and most deliberate answer, so it takes the lane
  /// outright - including when it is the one being withdrawn, because a lane whose
  /// ruling just got re-asked may not fall back to an older verb underneath it.</para>
  ///
  /// <para><b>CONTESTS GO THROUGH THE SAME DOOR</b> (review ruling S17). A contest's own
  /// row is frozen at <see cref="PricingResult.PlayerContest"/>, so comparing it against
  /// itself made the rule a tautology and the contest half of the re-ask unreachable.
  /// It is compared against the FRESH PINCH's call for its lane instead, exactly like a
  /// pinch-raised row. Where the pinch made no call on the lane, nothing moved under the
  /// contest and it carries; a pinch-raised row in the same position is simply no longer
  /// raised, and drops out with no note (there is no row left to ask on).</para>
  ///
  /// <para>Order in equals order out for everything the contest rule does not decide -
  /// callers pass a stable sequence, and the same board re-presents the same way on
  /// every frame.</para>
  /// </summary>
  internal static (List<(TRow Row, TLane Lane, StagedVerb Verb)> Carried,
                   List<(TLane Lane, string Note)> Withdrawn)
    Represent<TRow, TLane>(
      IEnumerable<Staged<TRow, TLane>> staged,
      IReadOnlyDictionary<TLane, BoardPile> callsNow)
    where TLane : notnull
  {
    var owner = new Dictionary<TLane, Staged<TRow, TLane>>();
    var lanes = new List<TLane>();
    foreach (var entry in staged)
    {
      if (!owner.TryGetValue(entry.Lane, out var held))
      {
        owner[entry.Lane] = entry;
        lanes.Add(entry.Lane);
        continue;
      }
      if (entry.IsContest && !held.IsContest) owner[entry.Lane] = entry;
    }

    var carried = new List<(TRow, TLane, StagedVerb)>();
    var withdrawn = new List<(TLane, string)>();
    foreach (var lane in lanes)
    {
      var entry = owner[lane];
      BoardPile callNow;
      if (callsNow.TryGetValue(lane, out var fresh)) callNow = fresh;
      else if (entry.IsContest) callNow = entry.Verb.AgainstCall;
      else continue;

      if (Survives(entry.Verb, callNow)) carried.Add((entry.Row, lane, entry.Verb));
      else withdrawn.Add((lane, Note(entry.Verb, callNow)));
    }

    return (carried, withdrawn);
  }

  /// <summary>A staged verb in the words its own button uses.</summary>
  internal static string Verb(StandingAction action) => action switch
  {
    StandingAction.Vendor => "Vend",
    StandingAction.Pull => "Pull",
    // Renamed with the button (08-22): the board's no-abbreviation rule caught
    // "Reprc", and this method's contract is "the words its own button uses" - so
    // leaving it here would have made this doc line false the moment the button moved.
    StandingAction.Reprice => "Reprice",
    StandingAction.Melt => "Melt",
    StandingAction.Gc => "GC",
    _ => "nothing",
  };
}
