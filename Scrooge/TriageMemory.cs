using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Scrooge;

/// <summary>
/// Decision memory for triage flags — the pure core (RoutingRules/LanePricing
/// mold: no game reads, no storage, no statics, linked into Scrooge.Tests).
///
/// Two jobs, one lifecycle:
///
/// 1. SELF-HEAL. A held flag is a question awaiting the player's judgment. When
///    a later run processes the same (item, retainer) and the rule that raised
///    the flag does NOT fire again, the condition is resolved — the flag closes
///    itself. The triage inbox shows only live problems; a flag's timestamp
///    means "last confirmed," never "first seen and never revisited." Dead-
///    producer reasons (upward_held / outlier_warn, whose code left with the
///    lane rewrite) are never re-raised by anything, so a processing pass over
///    their item always sweeps them.
///
/// 2. EVIDENCE KEY. A player's decision is evidence, and evidence-unchanged
///    means never re-ask. A held flag carries a snapshot of the world it was
///    raised against (the standing listing, the sale count, the newest sale,
///    the cheapest competitor). Re-flagging compares the live snapshot to the
///    stored one and only churns the row when the world actually moved — a
///    silent no-op otherwise, so a stuck-thin item does not reset its "held
///    since" clock every pinch.
/// </summary>
internal static class TriageMemory
{
  /// <summary>
  /// Flag reasons whose producer code was deleted in the lane rewrite (branch
  /// 1). Nothing raises these anymore, so any open one is stale by definition —
  /// the self-heal sweep closes it the moment its item is processed, and the
  /// V17 migration one-shots the strays whose item never gets pinched again.
  /// </summary>
  internal static readonly IReadOnlySet<string> DeadReasons =
    new HashSet<string>(StringComparer.Ordinal) { "upward_held", "outlier_warn" };

  // =========================================================================
  // Self-heal
  // =========================================================================

  /// <summary>
  /// Given every open flag on one (item, retainer) and the set of reasons that
  /// actually fired this pass, returns the ids to close. A flag survives only
  /// when its rule re-fired; everything else — resolved live reasons and dead-
  /// producer reasons alike — heals. Pure: the caller supplies the rows and
  /// performs the close, so the decision stays testable without storage.
  /// </summary>
  internal static List<long> FlagsToClose(
    IEnumerable<(long Id, string Reason)> openFlags,
    IReadOnlySet<string> raisedThisPass)
  {
    var toClose = new List<long>();
    foreach (var (id, reason) in openFlags)
      if (!raisedThisPass.Contains(reason))
        toClose.Add(id);
    return toClose;
  }

  // =========================================================================
  // Evidence key
  // =========================================================================

  /// <summary>
  /// The world a held decision was made against. Serialized onto the flag row
  /// so the next pass can ask "did anything change?" — the four cases (manual
  /// price, undercut, new sales, nothing) all read out of these four numbers.
  /// </summary>
  internal readonly record struct EvidenceSnapshot(
    long ListingPrice,       // the standing anchor — our live listing IS the decision record
    int SaleCount,           // history depth the hold was judged against
    long LatestSaleUnix,     // newest settled sale seen (0 = none)
    long CheapestCompetitor) // cheapest foreign board listing (0 = none) — the undercut probe
  {
    /// <summary>
    /// Compact, stable, diffable serialization for the evidence column
    /// (field order fixed; a migration or a human can eyeball a change).
    /// </summary>
    public string Serialize()
      => string.Create(CultureInfo.InvariantCulture,
           $"L{ListingPrice}|n{SaleCount}|s{LatestSaleUnix}|c{CheapestCompetitor}");

    /// <summary>
    /// Parses a serialized snapshot. Null on empty/garbage (an unparseable
    /// stored value is treated as "no prior evidence" by callers, which fails
    /// toward re-asking rather than silently swallowing a flag).
    /// </summary>
    public static EvidenceSnapshot? TryParse(string? s)
    {
      if (string.IsNullOrEmpty(s)) return null;
      var parts = s.Split('|');
      if (parts.Length != 4) return null;
      if (parts[0].Length < 1 || parts[1].Length < 1 || parts[2].Length < 1 || parts[3].Length < 1)
        return null;
      if (long.TryParse(parts[0].AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var listing)
          && int.TryParse(parts[1].AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
          && long.TryParse(parts[2].AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var latest)
          && long.TryParse(parts[3].AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cheapest))
        return new EvidenceSnapshot(listing, count, latest, cheapest);
      return null;
    }
  }

  /// <summary>How the world moved between a stored snapshot and the live one.</summary>
  internal enum EvidenceChange
  {
    /// <summary>Every field identical — the same unanswered question. Silent.</summary>
    Unchanged,
    /// <summary>Only the listing moved: the player set a price. That live listing IS the decision record — don't re-ask.</summary>
    ManualPrice,
    /// <summary>A competitor undercut since the decision. Ordinary lane pinch follows it (guards extremes) — no new flag.</summary>
    Undercut,
    /// <summary>History grew past MinHistorySamples: the lane can price it normally now — no hold, no flag.</summary>
    NewSalesResolved,
    /// <summary>New sales arrived but the lane is still thin: the evidence moved, so re-ask (the world changed).</summary>
    NewSalesContradict,
  }

  /// <summary>
  /// Classifies the move from a prior snapshot to the current one, exactly the
  /// four cases the decision-memory spec names (plus "nothing changed"). The
  /// order is deliberate: new sales are the loudest signal (history is the
  /// model), then a fresh undercut, then a bare manual reprice, then silence.
  /// </summary>
  internal static EvidenceChange Classify(EvidenceSnapshot prior, EvidenceSnapshot current, int minHistorySamples)
  {
    if (current == prior)
      return EvidenceChange.Unchanged;

    // History grew — a new settled sale, or the sample count climbed. This is
    // the strongest evidence, so it's judged first: enough samples to price
    // means the lane takes over (no flag); still thin means the world moved
    // under a still-open question, so re-ask.
    var historyGrew = current.SaleCount > prior.SaleCount
                      || current.LatestSaleUnix > prior.LatestSaleUnix;
    if (historyGrew)
      return current.SaleCount >= minHistorySamples
        ? EvidenceChange.NewSalesResolved
        : EvidenceChange.NewSalesContradict;

    // A competitor appeared, or the cheapest one dropped, with no new sales.
    // The lane follows the live board (its guards cap the extremes), so there
    // is nothing new to ask the player about.
    var undercut = current.CheapestCompetitor != 0
                   && (prior.CheapestCompetitor == 0 || current.CheapestCompetitor < prior.CheapestCompetitor);
    if (undercut)
      return EvidenceChange.Undercut;

    // Nothing but the listing moved: the player repriced by hand. The live
    // listing is the standing anchor now — the decision is recorded, don't nag.
    if (current.ListingPrice != prior.ListingPrice)
      return EvidenceChange.ManualPrice;

    // Something moved that none of the above name (e.g. a competitor cleared
    // out, or the listing dropped to 0). Nothing actionable changed — silent.
    return EvidenceChange.Unchanged;
  }

  /// <summary>What UpsertTriageFlag should do with an evidence-keyed flag.</summary>
  internal enum FlagAction
  {
    /// <summary>No open flag yet for this key — write a fresh one.</summary>
    Insert,
    /// <summary>Evidence unchanged (or only the standing decision moved) — leave the row untouched.</summary>
    Silent,
    /// <summary>The world moved under an open question — refresh detail/prices/evidence and re-stamp "confirmed".</summary>
    Refresh,
  }

  /// <summary>
  /// The upsert gate for an evidence-keyed flag. Null stored evidence means no
  /// open row exists → Insert. A non-null but unparseable value (pre-V17 legacy
  /// rows carry '', corruption is conceivable) means an open row EXISTS without
  /// a usable snapshot → Refresh, adopting that row and stamping evidence into
  /// it. Insert here would file the same question twice (the 07-16 soak found
  /// exactly those duplicate pairs). Otherwise the change kind decides: only a
  /// genuinely moved world (new sales) re-asks; a manual reprice or an
  /// unchanged world stays silent so the "held since" clock and the inbox stay
  /// honest.
  /// </summary>
  internal static FlagAction DecideUpsert(string? storedEvidence, EvidenceSnapshot current, int minHistorySamples)
  {
    if (storedEvidence is null)
      return FlagAction.Insert;

    var prior = EvidenceSnapshot.TryParse(storedEvidence);
    if (prior is not EvidenceSnapshot p)
      return FlagAction.Refresh;

    return Classify(p, current, minHistorySamples) switch
    {
      EvidenceChange.NewSalesResolved => FlagAction.Refresh,
      EvidenceChange.NewSalesContradict => FlagAction.Refresh,
      _ => FlagAction.Silent, // Unchanged, ManualPrice, Undercut — the question is unchanged
    };
  }

  /// <summary>
  /// The per-item no-op guard for triage batch chains: a step runs only when the
  /// item still carries a real queued action and has not marked itself Skipped
  /// (sold since flagging). QueuedAction is a CONTRACT - every path that queues an
  /// item for chain execution must stamp its action, or the whole chain silently
  /// no-ops step by step (the 2026-07-18 batch-reprice regression: vendor/pull
  /// stamped it, reprice never did, and four reprices "completed" without ever
  /// touching a price).
  /// </summary>
  internal static bool ItemSkipped(TriageAction queuedAction, PricingResult result)
    => queuedAction == TriageAction.None || result == PricingResult.Skipped;

  // =========================================================================
  // Zombie flag heal (M4)
  // =========================================================================

  /// <summary>
  /// The container a lane_held flag points at - and therefore the run type that can
  /// prove its item absent. A pinch observes the market BOARD (the retainer's live
  /// listings); a Hawk run observes the retainer's sell INVENTORY (unlisted items a
  /// pinch never sees). <see cref="Unknown"/> is a pre-M4 flag whose container was
  /// never recorded: absence is unprovable, so it is never zombie-closed.
  /// </summary>
  internal enum FlagScope { Unknown, Board, Inventory }

  /// <summary>Parses the stored scope tag; anything unrecognized (incl. legacy '') reads Unknown.</summary>
  internal static FlagScope ParseScope(string? scope) => scope switch
  {
    "board" => FlagScope.Board,
    "inventory" => FlagScope.Inventory,
    _ => FlagScope.Unknown,
  };

  /// <summary>The persisted tag for a scope (Unknown persists as '').</summary>
  internal static string ScopeTag(FlagScope scope) => scope switch
  {
    FlagScope.Board => "board",
    FlagScope.Inventory => "inventory",
    _ => "",
  };

  /// <summary>An open lane_held flag as the zombie sweep sees it.</summary>
  internal readonly record struct ZombieFlagRow(long Id, uint ItemId, FlagScope Scope);

  /// <summary>
  /// Whether a run may close ONE open lane_held flag as item_gone. The rule fails
  /// toward OPEN flags at every branch - it closes only when the run genuinely
  /// observed the flag's own container and the item was absent from it:
  ///
  /// - Unknown scope → leave open. A pre-M4 flag's container is unprovable.
  /// - Run walked a different container than the flag points at → leave open. This
  ///   is the Molybdenum trap: a pinch (board) run must never close an inventory
  ///   flag for an unlisted item it structurally cannot see.
  /// - Item still present in what the run observed → leave open. It was merely
  ///   skipped (mannequin/bound/banned) or re-held, not gone.
  /// - Otherwise → close as item_gone.
  /// </summary>
  internal static bool ShouldCloseAsItemGone(FlagScope runScope, FlagScope flagScope, bool itemObservedPresent)
  {
    if (flagScope == FlagScope.Unknown) return false;
    if (runScope != flagScope) return false;
    if (itemObservedPresent) return false;
    return true;
  }

  /// <summary>
  /// The batch zombie sweep (pure/tested; the storage layer reads rows and stamps the
  /// closes). Given the container this run walked and the set of item ids it actually
  /// observed in that container, returns the ids of open lane_held flags to close as
  /// item_gone. A flag pointing at a container this run did not walk, or whose item was
  /// observed present, is left open - fail toward open flags, never toward silent closes.
  /// </summary>
  internal static List<long> ZombieFlagsToClose(
    FlagScope runScope,
    IReadOnlySet<uint> observedItemIds,
    IEnumerable<ZombieFlagRow> openLaneHeldFlags)
  {
    var toClose = new List<long>();
    foreach (var flag in openLaneHeldFlags)
      if (ShouldCloseAsItemGone(runScope, flag.Scope, observedItemIds.Contains(flag.ItemId)))
        toClose.Add(flag.Id);
    return toClose;
  }
}
