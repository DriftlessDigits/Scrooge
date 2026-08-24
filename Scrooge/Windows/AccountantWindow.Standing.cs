using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Linq;

using Scrooge.Board;

namespace Scrooge.Windows;

/// <summary>
/// THE ABSORBED TRIAGE LIFECYCLE (design Section 7). Held flags and this run's standing
/// listings become board rows here, a human's answer becomes a STAGED VERB here, and the
/// round spends that verb through the executors below. There is no second surface: the
/// TriageWindow is gone and its whole action set (Vend / Pull / Reprice / Dismiss) lives
/// on the board's rows and its detail pane.
///
/// <para><b>Nothing here fires on its own.</b> Every verb writes into
/// <see cref="_actions"/> and waits; the ROUND is what spends it, at the retainer, at the
/// bell. Out-of-round execution was deliberately killed (07-26 / stage 2a) - one launch
/// control, one errand - and the executors at the bottom of this file are only ever
/// reached through the conductor's own fire path.</para>
/// </summary>
internal sealed partial class AccountantWindow
{
  // --- Absorbed triage state (was TriageWindow) ---
  private List<PricingItem> _standingItems = [];
  private readonly Dictionary<long, PricingItem> _flagItems = []; // flag id -> synthetic (stable identity)
  private Dictionary<PricingItem, StagedVerb> _actions = [];

  private readonly HashSet<(uint, bool, string)> _signalsRecorded = [];

  /// <summary>
  /// THE CALL A STANDING LANE IS MAKING - the discrete class a verb is staged against
  /// (addendum 3). It is the row's NATURAL pile, which is the same thing the board
  /// draws it in and the same thing <see cref="BuildBoardRows"/> derives its call text
  /// from, so "the call changed" means exactly what the player saw change.
  ///
  /// <para>The natural pile rather than the raw <see cref="PricingResult"/> on purpose:
  /// CapBlocked and UndercutTooDeep are two reasons for one instruction ("reprice
  /// this"), and re-asking because the reason behind an unchanged instruction moved
  /// would be the board asking a question it has not actually got.</para>
  /// </summary>
  private static BoardPile CallClassOf(PricingItem item)
    => BoardPiles.ForStanding(item.Result);

  /// <summary>
  /// LANES WHOSE VERB WAS WITHDRAWN BY A RE-READ (addendum 3), and the sentence each
  /// owes. Keyed by lane rather than by item reference for the reason the carry-forward
  /// is: the item objects are rebuilt by the run that changed the call.
  ///
  /// <para>Session state, deliberately not persisted: it is a NOTE about something that
  /// just happened on screen, and a re-ask note surviving a reload would be explaining
  /// a change the player has no memory of making. What survives the reload is the thing
  /// that matters - the verb is gone and the row is in the rulings count.</para>
  /// </summary>
  private readonly Dictionary<(uint ItemId, bool IsHq, string Retainer), string> _reAsked = [];

  /// <summary>
  /// The human answered this lane - the re-ask note has done its job and goes. Called
  /// from every staging site, which is what keeps "the note is on screen" and "the row
  /// is still waiting" the same fact rather than two that can drift apart.
  /// </summary>
  private void Answered(PricingItem item)
    => _reAsked.Remove((item.ItemId, item.IsHq, item.RetainerName));

  /// <summary>
  /// The unified triage row list: fresh run items, then the player's own
  /// contests (unit 4: a standing ask he clicked a cell on), then held flags
  /// not already shown as a live row.
  ///
  /// <para><b>ONE LANE, ONE ROW - THE CONTEST WINS</b> (the minors batch, 2026-08-12,
  /// matching review ruling S10 on the staging side). The contest skip used to be a
  /// REFERENCE test against the run's items, and a contest is a synthetic item this
  /// window builds itself - so it never matched, and a lane the pinch had raised AND
  /// the player had contested drew twice. S10 already ruled which of the two answers
  /// governs when they conflict; this is the same rule at the display, so the board
  /// cannot show a lane the executor will only ever act on once. Keyed by lane like
  /// the held-flag skip two lines below, which had it right from the start.</para>
  /// </summary>
  private List<InboxRow> BuildStandingRows()
  {
    var rows = new List<InboxRow>();
    var contested = _playerContests
      .Select(c => (c.ItemId, c.IsHq, c.RetainerName))
      .ToHashSet();
    foreach (var item in _standingItems)
      if (!contested.Contains((item.ItemId, item.IsHq, item.RetainerName)))
        rows.Add(new InboxRow(item, null));
    foreach (var item in _playerContests)
      rows.Add(new InboxRow(item, null));
    foreach (var flag in _cache.HeldFlags)
    {
      if (_standingItems.Any(t => t.ItemId == flag.ItemId && t.IsHq == flag.IsHq && t.RetainerName == flag.RetainerName))
        continue;
      if (_playerContests.Any(t => t.ItemId == flag.ItemId && t.IsHq == flag.IsHq && t.RetainerName == flag.RetainerName))
        continue;
      rows.Add(new InboxRow(SyntheticItem(flag), flag));
    }
    return rows;
  }

  private PricingItem SyntheticItem(StandingFlag flag)
  {
    if (_flagItems.TryGetValue(flag.Id, out var cached)) return cached;

    var item = new PricingItem
    {
      SlotIndex = flag.SlotIndex,
      ItemId = flag.ItemId,
      IsHq = flag.IsHq,
      ItemName = GilTracker.GetItemName(flag.ItemId),
      RetainerName = flag.RetainerName,
      Quantity = 0,
      CurrentListingPrice = flag.OldPrice > 0 ? flag.OldPrice : null,
      MbPrice = flag.FlaggedPrice > 0 ? flag.FlaggedPrice : null,
      VendorPrice = LedgerCache.VendorPriceOf(flag.ItemId),
      Result = flag.Reason switch
      {
        // Legacy flags whose producer left with the lane rewrite (2026-07-13).
        // They fold to the honest held state: the row kept its ask, and no live
        // rule claims to know which guard once stopped it.
        "upward_held" or "outlier_warn" => PricingResult.LaneHeld,
        "lane_held" => PricingResult.LaneHeld,
        // The crasher seat is a held ask too - the flag's own detail line
        // carries the gap sentence; the held family gives it the right verbs
        // (reprice / pull / dismiss).
        "crasher_seat" => PricingResult.LaneHeld,
        "cap_blocked" => PricingResult.CapBlocked,
        BoardListings.MeltBeatsAskReason => PricingResult.MeltBeatsAsk,
        _ => PricingResult.NoData,
      },
    };
    _flagItems[flag.Id] = item;
    return item;
  }

  /// <summary>
  /// THE SYNTHETICS OUTLIVE NOTHING (3-3b). A flag's synthetic item is cached by flag id
  /// so staging stays reference-stable while the flag is open; a flag that has been
  /// dismissed, actioned, or settled by the melt contest is closed, will never be drawn
  /// again, and its entry is dead weight for the rest of the session. Hung off the
  /// cache's own re-read rather than spelled at each mutation site, for the same reason
  /// the row caches are: a flag closed by the SCORING pass (see
  /// <c>LedgerCache.SettleMeltContests</c>) never passes through this window at all.
  ///
  /// <para>The staged verb goes with it. A verb against a row nothing will ever draw is
  /// a ghost in the count the completion report reads - the same orphan
  /// <see cref="DismissRow"/> has always cleared by hand on the one path it knew about.</para>
  /// </summary>
  private void PruneFlagItems()
  {
    if (_flagItems.Count == 0) return;
    var open = _cache.HeldFlags.Select(f => f.Id).ToHashSet();
    foreach (var id in _flagItems.Keys.Where(id => !open.Contains(id)).ToList())
    {
      _actions.Remove(_flagItems[id]);
      _flagItems.Remove(id);
    }
  }

  /// <summary>
  /// THE BELL'S STANDING-LISTING LEG, staged and handed off. The deck's two work sets
  /// become staged verbs here - beside whatever the human staged himself - and the whole
  /// staging dictionary is drained to the triage executor, because every triage verb is
  /// retainer work and the bell is the retainer errand.
  ///
  /// <para>Stays on the window because a staged verb is a fact about the BOARD's
  /// conversation with the player - the call it was staged against, the re-ask note it
  /// answers, the teaching signal it writes. The round only needs to know whether a run
  /// is now in flight.</para>
  /// </summary>
  public bool StageStandingLeg(IReadOnlyList<InboxRow> repriceStage, IReadOnlyList<InboxRow> pullStage)
  {
    foreach (var r in repriceStage)
    {
      _actions[r.Item] = new StagedVerb(StandingAction.Reprice, CallClassOf(r.Item));
      Answered(r.Item);
      RecordStandingSignal(r.Item, BoardPiles.ForStanding(r.Item.Result), StandingAction.Reprice);
    }
    foreach (var r in pullStage)
    {
      // Only rows the human has not already given a verb - his Pull stays a Pull.
      if (_actions.ContainsKey(r.Item)) continue;
      _actions[r.Item] = new StagedVerb(StandingAction.Vendor, CallClassOf(r.Item));
      Answered(r.Item);
      RecordStandingSignal(r.Item, BoardPiles.ForStanding(r.Item.Result), StandingAction.Vendor);
    }

    // A row the pinch's vendor rider already took is long gone from these sets - the
    // rider retires what it vendors.
    return ExecuteStandingBatch();
  }

  private BoardPile EffectiveStandingPile(PricingItem item)
  {
    var natural = BoardPiles.ForStanding(item.Result);
    return BoardPiles.Effective(natural, _cache.ScoreStanding(item, natural));
  }

  /// <summary>
  /// The router's PROPOSED exit on a contested listing, from its natural pile:
  /// a reprice contest proposes List (relist at the number the round would write), a
  /// pull-and-vendor contest proposes Vendor. Review proposes nothing - the
  /// router declined - and rides the List placeholder with proposalWasReview
  /// carrying the truth, exactly like the bag rows' review grammar.
  /// </summary>
  private static RoutingExit ListedProposal(BoardPile natural) => natural switch
  {
    BoardPile.PullAndVendor => RoutingExit.Vendor,
    // The melt-beats-ask contest proposes the exit it is ABOUT (2026-08-06) - a
    // contest that proposed List would grade every answer as an overrule.
    BoardPile.Melt => RoutingExit.Desynth,
    _ => RoutingExit.List,
  };

  private void DismissRow(InboxRow row)
  {
    // A dismissal re-affirms the standing call - and is itself a verdict
    // against the flag class (unit 6): "you raised this, and I say it stands."
    BookContestVerdict(row.Item, StandingAction.None, dismissed: true);

    if (row.IsFresh)
    {
      _standingItems.Remove(row.Item);
      _playerContests.Remove(row.Item);
      _actions.Remove(row.Item);
    }
    else
    {
      try { GilStorage.SetStandingFlagStatus(row.Flag!.Id, "dismissed"); } catch { /* storage unavailable */ }
      // The re-read below prunes the synthetic and its staged verb - the flag is
      // closed, so it will not come back in the open set.
      RefreshHeldFlags();
    }
    RecordStandingSignal(row.Item, EffectiveStandingPile(row.Item), StandingAction.None);
  }

  /// <summary>
  /// Books one answered contest (walk ruling 4): upheld when the answer is the
  /// router's own proposal, overruled otherwise, dismissed via
  /// <see cref="DismissRow"/>'s overload. Grades the FLAGS, never the exits -
  /// nothing here touches the routing model. The player's own contests
  /// (PlayerContest) book nothing: he cannot uphold or overrule himself.
  ///
  /// <para>SINCE V38 the receipt also carries the DOUBT BRANCH the row acted
  /// through - the pivot's coordinate on the alignment instrument. "Overruled"
  /// is noise; "overruled a dead-heat call" says our rules ran out at a named
  /// place and the human went the other way. Nothing reads it back yet - the
  /// writes cannot wait, the read surface is the open pre-3.0 question of how
  /// Scrooge learns from rulings, and the LEVER is never built.</para>
  ///
  /// <para>THE FLAG IS FOUND BY LANE (3-3b). It used to be a reference scan over the
  /// synthetic cache, which answered only for rows the flag itself had raised - and the
  /// board has ruled since S10 that a lane is ONE row whoever raised it
  /// (<see cref="BuildStandingRows"/>). Keying the lookup the way the board keys the row
  /// means the receipt names the contest the player was actually answering, on the lane
  /// he answered it about, whether the pinch or the flag put it in front of him.</para>
  /// </summary>
  private void BookContestVerdict(PricingItem item, StandingAction answer, bool dismissed = false)
  {
    if (item.Result == PricingResult.PlayerContest) return;

    var lane = LaneOf(item);
    var flag = _cache.HeldFlags.FirstOrDefault(
      f => (f.ItemId, f.IsHq, f.RetainerName) == lane);
    var natural = BoardPiles.ForStanding(item.Result);
    var proposal = BoardCalls.ActionOfExit(ListedProposal(natural));
    var verdict = dismissed ? "dismissed"
      : answer == proposal ? "upheld"
      : "overruled";

    // The branch this row's price was decided through: the live walk's answer
    // when the lane is still standing, else the branch its flag class implies.
    // The tier is folded in the same way the board folds it, so a pivot on a
    // mixed-exits row is counted as one - the tape and the board must agree
    // about what the player was actually answering.
    var laneDoubt = _cache.ListedDoubtFor(item.ItemId, item.IsHq, item.RetainerName)
      ?? BoardListings.DoubtOfFlagReason(flag?.Reason ?? "");
    var branch = DeferPlan.Classify(laneDoubt, _cache.ScoreStanding(item, natural));

    var ev = item.LaneEvidence;
    long? listingPrice = item.CurrentListingPrice is int lp && lp > 0 ? lp
      : flag is { OldPrice: > 0 } ? flag.OldPrice : null;
    long? cheapest = ev is { CheapestCompetitor: > 0 } e ? e.CheapestCompetitor
      : item.MbPrice is int mb && mb > 0 ? mb
      : flag is { FlaggedPrice: > 0 } ? flag.FlaggedPrice : null;
    try
    {
      GilStorage.InsertContestReceipt(item.ItemId, item.IsHq, item.RetainerName,
        flag?.Reason ?? item.Result.ToString(),
        proposal.ToString(), verdict, dismissed ? "" : answer.ToString(),
        flag?.CreatedAt ?? 0,
        listingPrice, cheapest,
        saleCount: ev?.SaleCount,
        latestSaleAt: ev is { LatestSaleUnix: > 0 } l ? l.LatestSaleUnix : null,
        doubtBranch: DeferPlan.KeyOf(branch));
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Contest] Receipt not booked for {item.ItemName}: {ex.Message}");
    }
  }

  // ==========================================================================
  // Actions + teaching signals
  // ==========================================================================

  /// <summary>
  /// Records a teaching signal for a bag verdict (design Section 4). Confirmations
  /// (player == router) and overrides (player != router) both write to V14
  /// routing_overrides; the confidence read side counts only the disagreements.
  /// Once per (item, target) per session so a held view does not spam the table.
  /// </summary>
  public void RecordRoutedSignal(RoutedItem item, RoutingExit playerExit)
  {
    var routerVerdict = item.Verdict.IsReview ? "Review" : item.Verdict.Exit.ToString();
    var key = (item.ItemId, item.IsHq, $"{routerVerdict}->{playerExit}");
    if (!_signalsRecorded.Add(key)) return;
    item.OverrideRecorded = true;
    try
    {
      GilStorage.InsertRoutingOverride(item.ItemId, item.IsHq, item.Ilvl,
        routerVerdict, item.Verdict.Reason, playerExit.ToString());
      // V20: flag the standing receipt so override rate by confidence tier is queryable.
      if (routerVerdict != playerExit.ToString())
        GilStorage.MarkRoutingReceiptOverridden(item.ItemId, item.IsHq);
    }
    catch { /* storage unavailable - the move still applies, just unrecorded */ }
  }

  /// <summary>
  /// Records a teaching signal for an absorbed triage row - the verdict class is
  /// its natural pile.
  ///
  /// <para>THE ILVL IS THE LEDGER'S, not a zero. It used to bank 0 outright, which
  /// put a made-up item level on every standing ruling and left the 4.0 scoreboard
  /// unable to band them the way it bands the bag rulings
  /// (<see cref="RecordRoutedSignal"/> has always banked the real number off its
  /// RoutedItem). The listed lanes carry the same fact: the scoring pass drops its
  /// operands on <see cref="LedgerCache.ListedFactsFor"/> on the way past, which is
  /// where the board table already reads an ilvl for these rows. A dictionary hit
  /// on the current refresh's own answer - no game read, no second Collect.</para>
  ///
  /// <para>0 survives for a row this refresh never scored: a synthetic flag row
  /// whose item is no longer listed anywhere. That zero is now "the ledger has no
  /// ilvl for this variant" rather than "nobody asked", and it is the only honest
  /// answer available without a sheet read this method has no business making.</para>
  /// </summary>
  private void RecordStandingSignal(PricingItem item, BoardPile natural, StandingAction action)
  {
    var routerVerdict = natural.ToString();
    var key = (item.ItemId, item.IsHq, $"{routerVerdict}->{action}");
    if (!_signalsRecorded.Add(key)) return;
    try
    {
      GilStorage.InsertRoutingOverride(item.ItemId, item.IsHq,
        _cache.ListedFactsFor(item.ItemId, item.IsHq)?.Ilvl ?? 0,
        routerVerdict, item.Result.ToString(), action.ToString());
    }
    catch { /* storage unavailable */ }
  }

  // ==========================================================================
  // Executors (existing paths, now confidence-gated)
  // ==========================================================================

  /// <summary>
  /// The bell run's trigger: the routed gear the confidence gate cleared, plus the
  /// other two thirds of the one-door bell (fresh yields + gate-passing non-gear).
  /// Only the bell STAGE calls it now - the pile's own confirm button is gone with
  /// the rest of the per-pile launches (stage 2a).
  /// </summary>
  public void FireBellRun(List<RoutedItem> listSet, List<RoutedItem> vendSet,
    List<ListableItem> gateJoiners)
  {
    var all = listSet.Concat(vendSet).ToList();
    foreach (var item in all)
      RecordRoutedSignal(item, item.Pile); // a bulk confirm is mass agreement
    RunHawkForRouted(all, gateJoiners);
  }

  // THE IN-LIST BULK STAGE IS GONE (Task 4, ruled 08-15). BulkStageStanding put a
  // "stage all N" verb inside the Reprice and Pull-and-Vendor group headers, with
  // StageAll's zero-state costume when nothing was eligible and a "N need a row click"
  // strip beside it. All three died with the pile groups that hosted them: the walk has
  // no group headers to hang a bulk verb off, the riders page IS the bulk affordance
  // ("N ride unless you pull one" - the whole page is one press), and the row-click
  // strip was a count of exactly the rows Bag decisions now walks one at a time. Keeping a
  // second bulk path beside the riders page would be the tech debt the panel died to
  // avoid.

  /// <summary>
  /// Hands the bell's rows to the Hawk run (List selected, Vendor flagged). Routed
  /// gear is translated into Hawk rows here; <paramref name="gateJoiners"/> already
  /// ARE Hawk rows (the gate's own scan) and only need their selection stamped -
  /// the orchestrator routes on IsAlwaysVendor, and Selected is kept honest so the
  /// row means the same thing wherever it is read.
  /// </summary>
  private void RunHawkForRouted(List<RoutedItem> rows,
    List<ListableItem>? gateJoiners = null)
  {
    var hawkItems = new List<ListableItem>();
    foreach (var item in rows.Where(i => i.Pile is RoutingExit.List or RoutingExit.Vendor))
    {
      hawkItems.Add(new ListableItem
      {
        ItemId = item.ItemId,
        Name = item.Name,
        Quantity = item.Quantity,
        IsHq = item.IsHq,
        Selected = item.Pile == RoutingExit.List,
        // PROVENANCE, NOT MECHANISM (Drift, 08-23: "do I really always vendor
        // that one?" - he didn't; the router chose vendor and the spoken reason
        // dressed the verdict as a standing rule). RoutedVendor rides the same
        // direct-sell path; IsAlwaysVendor is the config list's claim alone.
        IsAlwaysVendor = Plugin.Configuration.AlwaysVendorItemIds.Contains(
          item.ItemId + (item.IsHq ? 1_000_000u : 0u)),
        RoutedVendor = item.Pile == RoutingExit.Vendor,
        Container = item.Container,
        SlotIndex = item.SlotIndex,
        LastSalePrice = item.LastSalePrice,
      });
    }
    foreach (var g in gateJoiners ?? [])
    {
      g.Selected = !g.IsAlwaysVendor;
      hawkItems.Add(g);
    }
    if (hawkItems.Count == 0)
    {
      Svc.Chat.PrintError("[Scrooge] Nothing the Hawk would list - no routed List/Vendor rows in the bags.");
      return;
    }
    // The Hawk window opens alongside; the rounds window stays put and keeps
    // narrating its own progress (F8, ruled 08-22: a working surface that
    // vanishes reads as a malfunction - nothing closes here).
    Plugin.PinchHost.NavigateAndStartHawkRun(hawkItems);
  }

  /// <summary>Hands a specific set of Churn rows to the GC turn-in orchestrator.</summary>
  public bool ExecuteChurn(List<RoutedItem> rows)
  {
    var churnItems = rows
      .Select(i => new GcTurnInOrchestrator.GcTurnInItem(
        i.ItemId, i.IsHq, i.Name, GcSeals.For(i.ItemId) ?? 0))
      .Where(i => i.SealReward > 0)
      .OrderByDescending(i => i.SealReward)
      .ToList();
    var skipped = rows.Count - churnItems.Count;
    if (skipped > 0)
      Svc.Chat.Print($"[Scrooge] Turn-in: skipped {skipped} {(skipped == 1 ? "item" : "items")} with no resolvable seal value - {(skipped == 1 ? "it stays" : "they stay")} in your bags.");
    // False = no run to fire; the round's TurnIn stage marks done at the fire site
    // on this answer (SF2), because there is no completion coming to say it.
    if (churnItems.Count == 0) return false;
    foreach (var item in rows)
      RecordRoutedSignal(item, RoutingExit.Gc);
    Plugin.GcTurnIn.StartRun(churnItems,
      _conductor.Deck?.Seals is { Overflows: true } sf ? sf.Fits : null);
    return true;
  }

  /// <summary>
  /// Executes staged triage actions, and says whether it handed anything off.
  /// Drains everything, which is what the header's Go button and the BELL both want -
  /// the bell because every triage verb is retainer work and the bell is the retainer
  /// errand, the Go button because that is what "Go (2 vendor, 1 reprice)" promises.
  ///
  /// <para>The RETURN is the bell's chaining signal: true means a run is now in
  /// flight and its completion is worth waiting for.</para>
  /// </summary>
  private bool ExecuteStandingBatch()
  {
    // Projected back to the bare verb for the orchestrator: the call a verb was staged
    // against is the BOARD's business (it decides whether to re-ask), and the executor
    // only ever needed to know what to do at the retainer.
    var batch = _actions.ToDictionary(a => a.Key, a => a.Value.Action);
    if (batch.Count == 0) return false;
    if (!Plugin.StandingOrchestrator.QueueStandingBatch(batch))
      return false;
    // The spend is the irreversible moment, so it is also the verdict's (unit
    // 6): each handed-off answer books a contest receipt against its flag
    // class. Re-stagings before this point were the player thinking out loud.
    foreach (var (item, action) in batch)
      BookContestVerdict(item, action);
    // Rows are retired per-item on completion (RemoveItem callback), never here -
    // this only un-stages what was handed off.
    foreach (var item in batch.Keys)
      _actions.Remove(item);
    return true;
  }

  // ==========================================================================
  // Standing-listing row lifecycle (called by the orchestrator + the run log)
  // ==========================================================================

  /// <summary>
  /// The standing-listing Pull &amp; Vendor rows the pinch's vendor rider may drain
  /// (WALK unit 3), each paired with its confidence tier AND the player's own
  /// ruling, so the rider keeps to the rows every other confirm would take -
  /// unanimous, or hand-staged to Vendor by a human click (WALK unit 5: the
  /// front-load gate presents these rows as rulings that change this run, so a
  /// ruling the rider ignored would make the gate a liar). Bag-gear Vendor rows
  /// are deliberately NOT here - they have no listing to pull and ride the bell run.
  /// The rider snapshots this at pinch start and only removes a row it actually
  /// vendors (via <see cref="RemoveItem"/>); rows it never reaches stay in the pile,
  /// so an aborted pinch leaves the pile exactly as it found it.
  /// </summary>
  internal List<(PricingItem Item, ConfidenceTier Tier, bool PlayerResolved)> PullVendorRiderCandidates()
  {
    var rows = new List<(PricingItem, ConfidenceTier, bool)>();
    foreach (var row in BuildStandingRows())
    {
      // Two ways onto the rider: the pile's own unanimous rows, and ANY row a
      // human explicitly staged to a retainer-retrieve verb - Vendor as always,
      // and since unit 5 the pull-for-X family too (Pull / Melt / Gc: the rider
      // learned the retrieve; the exit fires at its own stop). The ruling path
      // matters because a Contradicted row the player ruled is drawn in Review,
      // so a pile-only test would drop exactly the row he just ruled.
      var ruled = _actions.TryGetValue(row.Item, out var staged)
        && staged.Action is StandingAction.Vendor or StandingAction.Pull or StandingAction.Melt or StandingAction.Gc;
      if (!ruled && EffectiveStandingPile(row.Item) != BoardPile.PullAndVendor) continue;
      // Stamp the intended verb NOW, while the staging dictionary is in reach -
      // the rider's snapshot only fills in Vendor where nothing was chosen.
      if (ruled) row.Item.QueuedAction = staged.Action;
      else row.Item.QueuedAction = StandingAction.None;
      rows.Add((row.Item,
        _cache.ScoreStanding(row.Item, BoardPiles.ForStanding(row.Item.Result)),
        ruled));
    }
    return rows;
  }

  /// <summary>
  /// Stores this run's standing-listing items so they survive after CurrentRun clears.
  /// Called by the Ledger at a run's end.
  ///
  /// <para><b>STAGED VERBS NOW CARRY ACROSS THE SWAP (addendum 2).</b> This used to
  /// clear <see cref="_actions"/> outright, and the reason was an identity accident
  /// rather than a decision: the dictionary is keyed by <see cref="PricingItem"/>
  /// REFERENCE, a new run builds new item objects for the same lanes, and the old keys
  /// went dangling - so clearing was tidier than leaving a dictionary full of ghosts.
  /// The cost only became visible when the hinge learned to re-open: a second pass
  /// reports its own standing items, and every verb the human had staged at the first
  /// hinge vanished on the way back to the second. He would have re-ruled a board he
  /// had already ruled, with no sign that anything had been dropped.</para>
  ///
  /// <para>So the verbs are re-keyed by LANE - item, quality, retainer - which is the
  /// identity a standing listing actually has; the reference was only ever a
  /// convenience. A lane the new run does not carry drops out, which is correct: it is
  /// no longer listed, so there is nothing left to reprice or pull.</para>
  ///
  /// <para><b>AND A CHANGED CALL RE-ASKS IT</b> (addendum 3, Drift: <i>"if we have useful
  /// new information, we should present it"</i>). An earlier pass of this said a verb
  /// was an INSTRUCTION rather than an agreement and therefore outlived the call it
  /// answered. That was wrong, and wrong in the direction that costs money: the human
  /// staged "vendor this" against a board that said pull-and-vendor, the re-read now
  /// says reprice, and carrying the verb would spend his answer on a question nobody
  /// asked him. A verb is staged against a call; new information re-presents.</para>
  ///
  /// <para><b>THE DOOR IS THE FRESH PINCH</b> (review ruling S5). The addendum's comment
  /// said the Re-Look served this, and it never could: a Re-Look runs recon, recon banks
  /// decisions instead of raising standing rows, and the call class this rule compares is
  /// stamped by the pinch alone. The pinch reports here on every pass now - including one
  /// that raised nothing, which is the pass that used to let last night's verbs ghost
  /// straight through to the bell (see <see cref="StandingReAsk.Reports"/>).</para>
  ///
  /// <para>This makes the standing rule and the bag-gear rule ONE rule. Bag rulings have
  /// been keyed on the router's verdict since the ruled board - a new verdict misses the
  /// key and the row draws unruled - and this is that, for lanes.</para>
  /// </summary>
  internal void SetRun(RunData run)
  {
    // WHAT THE PINCH JUST SAID about each lane it raised. This is the only call in the
    // comparison that is new information; everything else is what the human answered
    // against last time.
    var callsNow = new Dictionary<(uint, bool, string), BoardPile>();
    foreach (var item in run.StandingItems)
      callsNow[LaneOf(item)] = CallClassOf(item);

    // Pinch-raised verbs re-key by LANE. Ordered before grouping so two ghosts on one
    // lane - a synthetic flag row and a run row, say - can never resolve differently on
    // different frames; the contest that may also be on the lane is fed separately and
    // wins it outright (review ruling S10).
    var stagedByLane = _actions
      .Where(kv => !_playerContests.Contains(kv.Key))
      .OrderBy(kv => (int)kv.Key.Result).ThenBy(kv => kv.Key.SlotIndex)
      .GroupBy(kv => LaneOf(kv.Key))
      .ToDictionary(g => g.Key, g => g.First().Value);

    // The player's own contests keep their EXISTING item objects (StandingItem caches
    // them per lane, precisely so staging stays reference-stable), so they are fed by
    // identity rather than re-keyed. Re-keying them through the new run's list would
    // have dropped every contest the run did not happen to touch - a verb the human
    // staged from the On Market tab, silently gone because another run ended.
    var entries = new List<StandingReAsk.Staged<PricingItem, (uint, bool, string)>>();
    foreach (var contest in _playerContests)
      if (_actions.TryGetValue(contest, out var verb))
        entries.Add(new(contest, LaneOf(contest), verb, IsContest: true));
    foreach (var item in run.StandingItems)
      if (stagedByLane.TryGetValue(LaneOf(item), out var verb))
        entries.Add(new(item, LaneOf(item), verb, IsContest: false));

    var (carriedRows, withdrawn) = StandingReAsk.Represent(entries, callsNow);

    _standingItems = run.StandingItems;

    var carried = new Dictionary<PricingItem, StagedVerb>();
    foreach (var (row, lane, verb) in carriedRows)
    {
      carried[row] = verb;
      _reAsked.Remove(lane);
    }

    // The re-ask. The verb is dropped, which is what puts the row back into the
    // judgment queue (the queue counts a lane as ruled iff _actions holds it) - and the
    // row keeps a sentence saying what he had said and what moved under it, so "why is
    // this asking me again" never needs a second surface.
    foreach (var (lane, note) in withdrawn)
      _reAsked[lane] = note;

    _actions = carried;
  }

  /// <summary>
  /// A standing listing's real identity: item, quality, retainer. Item references are
  /// rebuilt by every run; the lane is the thing that persists.
  /// </summary>
  private static (uint ItemId, bool IsHq, string RetainerName) LaneOf(PricingItem item)
    => (item.ItemId, item.IsHq, item.RetainerName);

  /// <summary>
  /// Retires a row on COMPLETION of its action (vendored/pulled/repriced) or when the
  /// orchestrator skipped it as no-longer-listed. Closes any matching held flags.
  /// Called by StandingOrchestrator - the ONLY place actions resolve rows.
  /// </summary>
  internal void RemoveItem(PricingItem item)
  {
    _standingItems.Remove(item);
    _actions.Remove(item);
    foreach (var flag in _cache.HeldFlags.Where(f => f.ItemId == item.ItemId && f.IsHq == item.IsHq && f.RetainerName == item.RetainerName))
      try { GilStorage.SetStandingFlagStatus(flag.Id, "actioned"); } catch { /* storage unavailable */ }
    RefreshHeldFlags();
  }

  // ==========================================================================
  // Reason strings (absorbed from the triage inbox)
  // ==========================================================================

  private static string BuildReason(PricingItem item)
  {
    return item.Result switch
    {
      // RejectedPrice is the operand the guard actually compared (lane path) -
      // MbPrice is the board read, which can sit far above the rejected lane
      // candidate. Board path leaves RejectedPrice null; there MbPrice IS the
      // compared operand. (The Mossy Stone Daggers fix, 07-23.)
      //
      // The operand is NAMED for whose number it is (08-02): "45 gil/ea" bare read
      // as OUR ask when it was a crasher's - Drift and the shake both misread it. A
      // board number says so, and the standing ask is said alongside when we have
      // one, so the card can never imply we are the crasher.
      PricingResult.PlayerContest =>
        $"Your contest - you raised this from the On Market tab{StandingAsk(item)}. The staged verb is the answer; Dismiss withdraws it.",
      // ONE FLOOR, ONE ROW (ruled 2026-08-21). The minimum and the mode floor are one
      // law with one verdict; the row names which of them bound and both operands.
      PricingResult.BelowFloor => FloorRow(item),
      // Same operand rule as the floor lines: the guard blocked a PROPOSAL, so the
      // arrow's target is the blocked proposal (RejectedPrice), never the board
      // read. "Cap (306 -> 200, 880%)" read like a cut; the truth was 306 listed,
      // ~2,998 proposed, cap refused the raise (07-24). MbPrice stays the fallback
      // for rows persisted before the operand was kept.
      PricingResult.CapBlocked =>
        $"Cap ({item.CurrentListingPrice:N0} -> {(item.RejectedPrice ?? item.MbPrice):N0} blocked, {item.PriceChangePercent:F0}%)",
      // THE CRASHER-GUARD'S QUESTION (ruled 2026-08-21). Not a blocked row - a row
      // waiting on a press, carrying the price the confirm will write verbatim.
      PricingResult.UndercutTooDeep =>
        $"Cutting {Math.Abs(item.PriceChangePercent ?? 0):F0}% under the anchor - "
        + $"{item.CurrentListingPrice:N0} down to {(item.RejectedPrice ?? item.MbPrice):N0}. "
        + "Competition or crasher? Confirm to follow the price.",
      // THE FALLBACK ARM CARRIES THE OPERANDS THE ROW ALREADY HAS (08-22). With no
      // lane evidence banked the row said "Held (not enough sales)" and nothing else
      // - a verdict with no numbers on a board where every other row shows its
      // arithmetic. The ask it is holding and the board read it was measured against
      // are both on the item; there is no reason for the reader to have to open the
      // pane to see them.
      PricingResult.LaneHeld =>
        item.Lane?.Evidence is { } ev
          ? $"Held (not enough sales) - {ev}"
          : $"Held (not enough sales){HeldOperands(item)}",
      PricingResult.NoData => "No Data (no listings)",
      // Pending is only reachable mid-reprice: StandingOrchestrator clears Result
      // before the run and restores the prior verdict if it fails. Say what is
      // happening instead of reading the cleared field as "Unknown" - the same
      // read-after-clobber class, in its mildest form.
      PricingResult.Pending => "Repricing - waiting on the board",
      // THE REASON-LESS ARM SPEAKS HONESTLY (08-22). Every state with no card of its
      // own fell through to a bare "Unknown", which reads as a verdict about the ITEM
      // ("we cannot tell what this is worth") when the fact is about the ROW: no
      // pricing pass reached it with a verdict this card knows how to state. Naming
      // the state it DID carry is the whole difference between a dead end and
      // something the reader (or a sitrep) can act on.
      _ => $"No reason card for this row - its pricing state is {item.Result}{StandingAsk(item)}.",
    };
  }

  /// <summary>
  /// What a HELD row is holding, for the arm that banked no lane evidence. The ask
  /// standing on the board and the board read the hold was measured against - both
  /// already on the item, neither previously drawn, and without them a "held" row is
  /// the one row on the board with a verdict and no arithmetic.
  /// </summary>
  private static string HeldOperands(PricingItem item)
  {
    var ask = item.CurrentListingPrice > 0 ? $"your ask stands at {item.CurrentListingPrice:N0}" : "";
    var board = item.MbPrice > 0 ? $"board's cheapest {item.MbPrice:N0}{CheapestSellerName(item)}" : "";
    return (ask, board) switch
    {
      ("", "") => "",
      ("", _) => $" - {board}",
      (_, "") => $" - {ask}",
      _ => $" - {ask}, {board}",
    };
  }

  /// <summary>
  /// THE FLOOR ROW, in the one law's words. The honest ask that lost, the floor that
  /// bound, and which rule it was - because "below floor" alone sends the player
  /// hunting through three settings for the one that fired.
  /// </summary>
  private static string FloorRow(PricingItem item)
  {
    var floor = PriceFloor.Effective(
      Plugin.Configuration.PriceFloorMode, item.VendorPrice,
      Plugin.Configuration.MinimumListingPrice);
    var clause = floor.Binding switch
    {
      FloorBinding.PlayerMinimum => $"your {floor.Floor:N0} gil minimum",
      FloorBinding.DomanEnclave => $"the Enclave's {floor.Floor:N0} gil (2x vendor)",
      FloorBinding.Vendor => $"the vendor's {floor.Floor:N0} gil",
      // None never fires here (the verdict needs a floor above zero to refuse), but
      // the catch-all naming a vendor counter that never bound is the same fold the
      // chat line un-made - kept honest at every seat for the day a fifth binding lands.
      _ => "the floor",
    };
    return $"No legal ask ({FlagOperand(item)} < {clause}{StandingAsk(item)}). "
      + "List sits out; the other exits compete.";
  }

  /// <summary>
  /// The guard's compared operand, named for whose number it is: a blocked lane
  /// proposal is ours ("would list at"), a bare board read is the board's
  /// ("board's cheapest"). The distinction the Mossy Stone Daggers comment kept
  /// in code now reaches the card.
  /// </summary>
  private static string FlagOperand(PricingItem item)
    => item.RejectedPrice is int rejected
      ? $"would list at {rejected:N0} gil/ea"
      : $"board's cheapest {item.MbPrice:N0} gil/ea{CheapestSellerName(item)}";

  /// <summary>
  /// Names the seller whose number the card is quoting (unit 7: both of us
  /// misread Pitch's 45 as our own ask on the 08-02 shake). From the banked
  /// snapshot, and ONLY when its cheapest still matches the quoted price - a
  /// stale snapshot must not pin a fresher number on the wrong seller.
  /// </summary>
  private static string CheapestSellerName(PricingItem item)
  {
    try
    {
      var (board, _) = GilStorage.GetBoardSnapshot(item.ItemId);
      var cheapest = board.Where(l => !l.IsOwn && l.IsHq == item.IsHq)
        .OrderBy(l => l.UnitPrice).FirstOrDefault();
      return cheapest is { Retainer.Length: > 0 }
          && item.MbPrice is int mb && cheapest.UnitPrice == mb
        ? $" ({cheapest.Retainer})"
        : "";
    }
    catch { return ""; }
  }

  /// <summary>"; your ask stands at 100" - only when a standing ask exists.</summary>
  private static string StandingAsk(PricingItem item)
    => item.CurrentListingPrice > 0 ? $"; your ask stands at {item.CurrentListingPrice:N0}" : "";
}
