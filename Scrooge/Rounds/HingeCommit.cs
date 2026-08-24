using ECommons.DalamudServices;
using Scrooge.Board;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge.Rounds;

/// <summary>
/// INVARIANT B, WHOLE (the unit-5 addendum, ruled 2026-08-10; widened to four sets by
/// review ruling S4). What was ruled at Continue is what runs: the four commit
/// snapshots, the admission filter every work set passes through, the hold log, and the
/// Round's own produced-variants exception.
///
/// <para>It takes the row sets as PARAMETERS and never reaches for a scan of its own.
/// The admission's whole claim is that the number on the button and the set the run
/// spends are one set - and a filter that fetched its own rows would be deriving a
/// second answer to the question it exists to keep singular.</para>
/// </summary>
internal sealed class HingeCommit
{
  /// <summary>
  /// Has this Round's hinge committed? Injected rather than read off a cursor here,
  /// because the distinction the admission turns on - uncommitted means nothing is being
  /// held to, committed with a null snapshot means the snapshot was LOST and the bell
  /// fails closed - is a fact about the ROUND, and this class holds the board's half.
  /// </summary>
  private readonly Func<bool> _hingeCommitted;

  internal HingeCommit(Func<bool> hingeCommitted) => _hingeCommitted = hingeCommitted;

  /// <summary>
  /// THE COMMIT (invariant B). The gate-joiner variants as they stood when the human
  /// pressed Continue, or null when this Round has no snapshot. Null and "the hinge
  /// has not committed" are different facts and the admission rule reads both: an
  /// uncommitted hinge means nothing is being held to, while a committed hinge with a
  /// null snapshot means the snapshot was LOST and the bell fails closed.
  /// </summary>
  private HashSet<BellVariant>? _committedGateRows;

  /// <summary>
  /// The ROUTED half of the same commit (addendum 2). Bag gear the confidence gate had
  /// cleared when the human pressed Continue - the second half of invariant B, ruled
  /// after the first landed with this hole in it: the bell's listing leg re-reads
  /// the bags at fire time, so a Unanimous-tier gear arrival after the press rode on a
  /// verdict nobody was shown.
  ///
  /// <para>Narrower than the gate half by construction, which is why it went unnoticed:
  /// a brand-new row is Mixed or Review and fails the bulk gate anyway. It is only the
  /// confident arrival that slipped through - which is the worst one to lose, because
  /// it is the one that lists without hesitating.</para>
  /// </summary>
  private HashSet<BellVariant>? _committedRoutedRows;

  /// <summary>
  /// The MELT half of the same commit (review ruling S4, ruled 2026-08-12: <i>"four-set
  /// commit - one sentence of doctrine, all four verbs"</i>). Invariant B was honoured
  /// on the two halves that LIST and nowhere else, which left the two verbs that DESTROY
  /// reading live: the melt pile composed the salvage window's selection from whatever
  /// sat in it at melt time, so a piece of gear that reached the bags after the press was
  /// selected and desynthesised - no preview surface between the press and the loss, and
  /// no board the human had ever seen it on.
  /// </summary>
  private HashSet<BellVariant>? _committedMeltRows;

  /// <summary>
  /// The TURN-IN half of the same commit (review ruling S4). The same hole in the same
  /// shape at the GC counter: Expert Delivery consumes the item, and the churn set was a
  /// live read all the way to the executor's hand.
  /// </summary>
  private HashSet<BellVariant>? _committedChurnRows;

  /// <summary>
  /// What each half has already reported holding (review ruling S16). Not state the
  /// admission reads - the held set is derived fresh every frame and this never feeds
  /// back into it - purely the log's own memory of what it has said, so the sentence
  /// lands once per arrival instead of once per frame.
  /// </summary>
  private readonly Dictionary<string, HashSet<BellVariant>> _notedHolds = [];

  /// <summary>
  /// Every variant this Round's melts produced - the one declared exception to the
  /// snapshot. Re-read from the bank on each melt completion and on a restore, never
  /// per frame; empty is the ordinary state on a night with no melt.
  /// </summary>
  private HashSet<BellVariant> _roundYieldVariants = [];

  /// <summary>
  /// Every variant this Round's coffer rider produced - the exception's OTHER half
  /// (ruled 08-16, Drift: "I can't think of a thing I pulled from a venture coffer
  /// that we didn't just list"). The rider is part of the same stage the human
  /// sanctioned at the hinge, so its products ride the bell exactly as the melt's
  /// yields do; holding one stream and admitting the other was the exception's
  /// scope drawn around what existed at the time rather than around its principle.
  ///
  /// <para>NOT persisted, unlike the melt's run-id join: a mid-Round reload drops
  /// it and the arrivals hold for next round - fail closed, in the direction that
  /// costs a Round rather than a wrong listing.</para>
  /// </summary>
  private readonly HashSet<BellVariant> _roundCofferVariants = [];

  /// <summary>The <c>desynth_runs.id</c> of every melt this Round fired - the yields' join key, persisted with the cursor.</summary>
  private readonly List<long> _meltRunIds = [];

  // ==========================================================================
  // The commit
  // ==========================================================================

  /// <summary>
  /// Takes the commit snapshot. Called from the Continue press and from nowhere else:
  /// the snapshot IS the press, and a second site that could take one would be a
  /// second moment the Round claims the human ruled.
  ///
  /// <para>Snapshots the JOINED / confidence-cleared sets rather than the raw scans,
  /// because those are what the hinge's own bell count was made of - a snapshot of rows
  /// the tally never included would admit things the human never saw a number for. The
  /// caller hands them in already composed for exactly that reason.</para>
  ///
  /// <para><b>ALL FOUR SETS, TAKEN TOGETHER</b> (addendum 2 for the two halves of the
  /// bell; review ruling S4 for the melt and the turn-in). They are one press, so they
  /// are one act: a gate snapshot without a routed one would hold the mats and let the
  /// gear through, and a bell commit without a melt and a turn-in one would hold the two
  /// verbs that can be undone by relisting while leaving the two that DESTROY reading a
  /// live bag. Invariant B is one sentence - what was ruled at Continue is what runs -
  /// and a sentence that covers half the verbs is not that sentence.</para>
  ///
  /// <para>Called BEFORE the hinge is marked done, so every set is read through its
  /// admission's not-yet-committed path and returns everything - the snapshot must be of
  /// the world, not of a filter that is already running.</para>
  /// </summary>
  internal void CommitTheHinge(
    IEnumerable<ListableItem> gateJoiners,
    IEnumerable<RoutedItem> routedRows,
    IEnumerable<RoutedItem> meltRows,
    IEnumerable<RoutedItem> churnRows)
  {
    _committedGateRows = gateJoiners.Select(r => new BellVariant(r.ItemId, r.IsHq)).ToHashSet();
    _committedRoutedRows = Variants(routedRows);
    _committedMeltRows = Variants(meltRows);
    _committedChurnRows = Variants(churnRows);
  }

  /// <summary>One set's variants, deduplicated - two slots of one mat are one variant to the admission rule, which admits by variant and brings every slot with it.</summary>
  private static HashSet<BellVariant> Variants(IEnumerable<RoutedItem> rows)
    => rows.Select(r => new BellVariant(r.ItemId, r.IsHq)).ToHashSet();

  /// <summary>
  /// THE COMMIT IS DISCARDED (addendum 2, the Re-Look ruling). A Re-Look returns the
  /// Round to the Look half and the waist re-asserts, so what the human ruled last time
  /// stops governing: ALL FOUR snapshots go, and they go to NULL rather than to empty.
  /// Four, not two, since the four-set commit (S4) - a Re-Look that re-opened the bell
  /// while the melt and the turn-in still answered to the old press would re-open half
  /// the hinge, which is the same drift the ruling closed, arriving by the other door.
  ///
  /// <para>Null is the fail-closed value, and that is the point. Between the Re-Look and
  /// the new Continue the Round is committed-with-no-snapshot for exactly as long as the
  /// hinge is un-marked - and if it crashes in that window and restores with the mark
  /// somehow standing, the bell admits nothing it cannot vouch for instead of falling
  /// back to a snapshot that describes a board the human has since asked to re-read.</para>
  /// </summary>
  internal void DiscardTheCommit()
  {
    _committedGateRows = null;
    _committedRoutedRows = null;
    _committedMeltRows = null;
    _committedChurnRows = null;
    // The hold log forgets with the commit it was reporting against (S16). With no
    // snapshot there are no holds to derive, so nothing would clear this memory on
    // its own - and the next commit's arrivals would go unsaid because a previous
    // commit had already named those rows.
    _notedHolds.Clear();
  }

  // ==========================================================================
  // The admission
  // ==========================================================================

  /// <summary>
  /// INVARIANT B'S GATE over the bell's non-gear half. Once the hinge has committed,
  /// the gate joiners stop being a live answer and become a HELD one: the rows the
  /// human was looking at when he pressed Continue, plus whatever this Round's own
  /// melt and coffer rider made since. Everything else waits for the next Round.
  /// </summary>
  internal List<ListableItem> AdmitGate(List<ListableItem> joined)
  {
    if (!_hingeCommitted()) return joined;

    var admitted = BellCommit.Admit(
      joined.Select(r => (r, new BellVariant(r.ItemId, r.IsHq))),
      hingeCommitted: true, _committedGateRows, RoundProducedVariants(), out var held);

    NoteHolds("gate", held);
    return admitted;
  }

  /// <summary>
  /// Invariant B over the routed rows - the same predicate the gate half runs, told the
  /// routed snapshot instead. See <see cref="AdmitAgainst"/> for the rule itself.
  /// </summary>
  internal List<RoutedItem> AdmitRouted(List<RoutedItem> rows)
    => AdmitAgainst(rows, _committedRoutedRows, "routed");

  /// <summary>
  /// Invariant B over the MELT set (review ruling S4). Same predicate, told the melt
  /// snapshot. Applied at the melt pile's one definition, which every melt reader comes
  /// through - the salvage window's selection, the deck's count, and the coffer rider's
  /// "is there anything to melt at all" - so the number the hinge showed and the pile
  /// the salvage window ticks are one set.
  /// </summary>
  internal List<RoutedItem> AdmitMelt(List<RoutedItem> rows)
    => AdmitAgainst(rows, _committedMeltRows, "melt");

  /// <summary>
  /// Invariant B over the TURN-IN set (review ruling S4). Same predicate, told the churn
  /// snapshot. Applied at the churn set's one definition, which the deck derives once per
  /// frame and hands to every reader - the stage's count, the seal-wallet fit, the
  /// wallet halt's cheapest-row arithmetic, and the executor itself.
  /// </summary>
  internal List<RoutedItem> AdmitChurn(List<RoutedItem> rows)
    => AdmitAgainst(rows, _committedChurnRows, "turn-in");

  /// <summary>
  /// THE ADMISSION, ONCE, FOR EVERY ROUTED HALF (review ruling S4). Three of the four
  /// sets are lists of <see cref="RoutedItem"/> filtered against their own snapshot, and
  /// the only thing that varies between them is WHICH snapshot - so the rule is written
  /// once and told which, rather than spelled three times and drifting on the fourth
  /// reader who edits two of them.
  ///
  /// <para>The yields set is passed to all of them on purpose, even though a melt yield
  /// is materials and materials are gate-class: the exception is a fact about the ROUND,
  /// not about which scan found the row, and hard-coding "yields cannot be routed" here
  /// would be an assumption about the router's scope that this method has no business
  /// holding.</para>
  ///
  /// <para>Pre-commit - outside a Round, or inside one whose hinge has not committed -
  /// every row rides, which is exactly the behaviour each of these sets had before the
  /// ruling and the reason a night where nothing arrives mid-Round is byte-identical.</para>
  /// </summary>
  private List<RoutedItem> AdmitAgainst(
    List<RoutedItem> rows, IReadOnlySet<BellVariant>? committed, string half)
  {
    if (!_hingeCommitted()) return rows;

    var admitted = BellCommit.Admit(
      rows.Select(r => (r, new BellVariant(r.ItemId, r.IsHq))),
      hingeCommitted: true, committed, RoundProducedVariants(), out var held);

    NoteHolds(half, held);
    return admitted;
  }

  /// <summary>
  /// The hold line, in ONE place for all four sets (review ruling S4). Debug, not chat,
  /// and not an error: a held row is a row the next Round's recon will read properly, so
  /// the player is not being asked to do anything about it and the only reader who needs
  /// the line is whoever is reconstructing a night from the log.
  ///
  /// <para>One method rather than four copies because the sentence is one sentence - and
  /// because the review finding against it (S16: log on hold-set CHANGE, never per
  /// derivation) is then one fix in one place rather than four.</para>
  ///
  /// <para><b>ON CHANGE, NOT PER DERIVATION</b> (review ruling S16, 2026-08-12). Both
  /// callers are asked from draw sites, so this ran every frame from two of them -
  /// about 120 lines per second per held row, for as long as the round stood at the
  /// hinge. The line's one reader is somebody reconstructing a night from the log, and
  /// a flood is the one thing that makes a log unreadable; it drowned the evidence
  /// line the drift-held shake step exists to check.</para>
  ///
  /// <para>So each half remembers what it has already said, and only rows NEW to that
  /// half's hold set get a line. A row leaving the set is silent - nothing arrived, so
  /// there is nothing to report - but it is forgotten, so the same row arriving again
  /// after a Re-Look says so again. The memory is per half because the halves hold
  /// independently and one half's arrival is not the other's.</para>
  /// </summary>
  private void NoteHolds(string half, List<BellVariant> held)
  {
    if (!_notedHolds.TryGetValue(half, out var already))
      _notedHolds[half] = already = [];

    foreach (var v in held)
      if (already.Add(v))
        Svc.Log.Debug($"[Bell] {half} row {v.ItemId}{(v.IsHq ? " HQ" : "")} arrived after the hinge - held for next round");

    already.IntersectWith(held);
  }

  // ==========================================================================
  // The Round's own exception
  // ==========================================================================

  /// <summary>
  /// Did THIS Round's own melt runs yield this variant? The one provenance the round can
  /// show a receipt for - every other road into the bags is unrecorded, and the surfaces
  /// that ask refuse to guess which one it was. Narrower than
  /// <see cref="RoundProducedVariants"/> on purpose: the coffer rider's pulls are an
  /// admission exception, not a melt.
  /// </summary>
  internal bool FromRoundMelt(uint itemId, bool isHq)
    => _roundYieldVariants.Contains(new BellVariant(itemId, isHq));

  /// <summary>The one exception set the admission reads: everything this Round's own
  /// sanctioned stage produced, melt yields and coffer pulls alike.</summary>
  internal HashSet<BellVariant> RoundProducedVariants()
    => _roundCofferVariants.Count == 0
      ? _roundYieldVariants
      : [.. _roundYieldVariants, .. _roundCofferVariants];

  /// <summary>
  /// This Round's own melt just ended, so its run id joins the set the bell may admit
  /// yields from. Answers whether the id was new - the caller re-reads the yields and
  /// persists on a yes, and a repeat completion is not a second melt.
  /// </summary>
  internal bool NoteMeltRun(long runId)
  {
    if (_meltRunIds.Contains(runId)) return false;
    _meltRunIds.Add(runId);
    RefreshRoundYields();
    return true;
  }

  /// <summary>The coffer rider's gains, banked into the exception before the re-read that first sees them.</summary>
  internal void NoteCofferGain(uint itemId, bool isHq)
    => _roundCofferVariants.Add(new BellVariant(itemId, isHq));

  /// <summary>
  /// Re-reads this Round's melt yields from the bank. Cheap and indexed
  /// (<c>ix_desynth_yields_run</c>); called when a melt completes and when a Round is
  /// restored, so the set is current whenever the bell composes. Storage down reads as
  /// EMPTY, which holds the yields back rather than admitting them - fail closed, in
  /// the direction that costs a Round rather than a wrong listing.
  /// </summary>
  internal void RefreshRoundYields()
  {
    if (_meltRunIds.Count == 0 || Plugin.DesynthYieldStore is not { } store)
    {
      _roundYieldVariants = [];
      return;
    }

    try
    {
      _roundYieldVariants = store.YieldVariantsForRuns(_meltRunIds)
        .Select(v => new BellVariant(v.ItemId, v.IsHq))
        .ToHashSet();
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Round] Couldn't read this round's melt yields: {ex.Message}");
      _roundYieldVariants = [];
    }
  }

  /// <summary>Everything a Round's ending or beginning forgets: the snapshot, its melts, and what they made.</summary>
  internal void ClearForRound()
  {
    DiscardTheCommit();
    _meltRunIds.Clear();
    _roundYieldVariants = [];
    _roundCofferVariants.Clear();
  }

  // ==========================================================================
  // The wire
  // ==========================================================================

  /// <summary>
  /// Writes the commit onto the cursor's persisted state. It rides with the cursor it
  /// belongs to (invariant B, the addendum) and is written from outside the pure plan
  /// because it is a fact about the BOARD, not about the cursor.
  /// </summary>
  internal void WriteTo(RoundState state)
  {
    state.CommittedGateRows = Wire(_committedGateRows);
    state.CommittedRoutedRows = Wire(_committedRoutedRows);
    state.CommittedMeltRows = Wire(_committedMeltRows);
    state.CommittedChurnRows = Wire(_committedChurnRows);
    state.MeltRunIds = [.. _meltRunIds];
  }

  /// <summary>
  /// THE COMMIT COMES BACK WITH THE CURSOR (invariant B, the addendum). An EMPTY list is
  /// not an empty snapshot: it means no snapshot was ever written, which the admission
  /// rule reads as fail-closed once the hinge is marked done. That distinction is the
  /// whole reason this is a nullable set here and a plain list on the wire - a config
  /// from before this build has no key, reads as empty, and a Round restored past its
  /// own hinge then admits only what its melts made rather than guessing.
  /// </summary>
  internal void Restore(RoundState saved)
  {
    _committedGateRows = FromWire(saved.CommittedGateRows);
    _committedRoutedRows = FromWire(saved.CommittedRoutedRows);
    _committedMeltRows = FromWire(saved.CommittedMeltRows);
    _committedChurnRows = FromWire(saved.CommittedChurnRows);
    _meltRunIds.Clear();
    _meltRunIds.AddRange(saved.MeltRunIds);
    RefreshRoundYields();
  }

  /// <summary>One snapshot, on the wire. Null becomes an empty list - see the note on <see cref="RoundState.CommittedRoutedRows"/> for why that loses nothing.</summary>
  private static List<long> Wire(HashSet<BellVariant>? snapshot)
    => snapshot is null ? [] : snapshot.Select(BellCommit.Encode).ToList();

  /// <summary>
  /// One snapshot, off the wire. EMPTY reads back as NULL, which is the fail-closed
  /// value: a config from before the addendum has no key at all, and a Round restored
  /// past its own hinge then admits only what its melts made rather than guessing.
  /// A genuinely empty commit admits the same set, so nothing is lost by the collapse.
  /// </summary>
  private static HashSet<BellVariant>? FromWire(List<long>? encoded)
    => encoded is { Count: > 0 } rows ? rows.Select(BellCommit.Decode).ToHashSet() : null;
}
