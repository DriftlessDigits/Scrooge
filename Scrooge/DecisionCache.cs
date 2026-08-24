using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// ONE banked recon decision: what the spine answered about an item the last time
/// a Round actually read its board, and when.
///
/// <para><see cref="DecidedPrice"/> is nullable because HELD is a real answer, not a
/// missing one. A row banked with a null price says "we looked, the evidence would
/// not carry a price" - which is exactly what the act half needs to know, and is a
/// different fact from having no row at all (nobody looked). Collapsing the two into
/// "no price" would make a held item indistinguishable from an unseen one, and the
/// act half would pay the full board round trip to re-learn something recon already
/// established minutes ago.</para>
/// </summary>
/// <para><b>LaneMedian rode here from V40 until the doctrine sweep (2026-08-15).</b>
/// It was never evidence the act half priced on - the price is already decided - it
/// was one operand for one thing: recomputing the receipt's <c>position_in_lane</c>
/// when a cached post trued the receipt up hours later. That ratio's writer is
/// retired, so the median had nothing left to feed and the whole carrier chain
/// (this field, the <c>decision_cache.lane_median</c> write, the plan's rider, and
/// the item's ReceiptLaneMedian) collapsed with it. <see cref="ReceiptId"/> still
/// rides - the decided_price true-up needs it, and that half of the true-up is what
/// the 08-10 ruling actually asked for. The COLUMN stays in the table (no
/// migration); nothing writes it now.</para>
internal readonly record struct DecisionCacheRow(
  uint ItemId,
  bool IsHq,
  long? DecidedPrice,
  string Outcome,
  string Evidence,
  long? ReceiptId,
  long RunId,
  long BankedAt);

/// <summary>
/// V39: the decision cache - the banked output of recon's spine pass, one row per
/// (item, quality).
///
/// <para>Recon reads a real board, runs the whole pricing spine on it, writes its
/// decision receipt exactly as a pinch or a hawk item would, and then CANCELS the
/// panel instead of pricing. Everything that pass learned about the market banks
/// itself through the machinery that already exists (market memory at
/// <c>ApplyBoardScan</c>, the tape at <c>OnHistoryReceived</c>, the receipt at
/// <c>InsertDecisionReceipt</c>). The one thing that had no home was the DECISION -
/// "what would we have listed this at, and why" - which is this table, and the only
/// new banking machinery the whole recon build needed.</para>
///
/// <para>UPSERT ON THE KEY, deliberately: a later recon SUPERSEDES an earlier one.
/// There is no history here and there should not be - the receipts are the history,
/// with their own coordinates and their own grading pass. This table answers one
/// question ("what does the act half do with this item tonight") and a second row
/// for the same item could only ever make that question ambiguous.</para>
///
/// <para>Dalamud-free, linked into Scrooge.Tests (the <see cref="PullIntentSchema"/>
/// mold).</para>
/// </summary>
internal static class DecisionCacheSchema
{
  internal static void ApplyV39(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(@"
      CREATE TABLE IF NOT EXISTS decision_cache (
        item_id       INTEGER NOT NULL,
        is_hq         INTEGER NOT NULL DEFAULT 0,
        decided_price INTEGER,                  -- NULL = the spine HELD; nothing to post from
        outcome       TEXT    NOT NULL,         -- LaneOutcome name, verbatim
        evidence      TEXT    NOT NULL,         -- the narration line the receipt carries, verbatim
        receipt_id    INTEGER,                  -- decision_receipts.id from the same pass
        run_id        INTEGER NOT NULL DEFAULT 0,
        banked_at     INTEGER NOT NULL,         -- the freshness clock for BOTH doors
        PRIMARY KEY (item_id, is_hq)
      );", connection);
    cmd.ExecuteNonQuery();

    // The staleness sweep reads banked_at across the whole table every refresh
    // (the rail's "N stale" count), which is the only access path that is not a
    // primary-key hit.
    using var idx = new SqliteCommand(
      "CREATE INDEX IF NOT EXISTS idx_decision_cache_banked ON decision_cache(banked_at);",
      connection);
    idx.ExecuteNonQuery();
  }

  /// <summary>
  /// THE SLOW BURN, CAPPED (the minors batch, 2026-08-12). The table upserts on
  /// (item, quality), so it never grows within a night - but it never SHRINKS either,
  /// and one row is kept forever for every variant that has ever passed through the
  /// bags. A row is spendable for <c>ReconFreshHours</c> (a day by default) and read
  /// by nothing afterwards except the staleness count, which is happier without it.
  ///
  /// <para>Retention is measured in months rather than in the freshness window on
  /// purpose: this is startup housekeeping on the same schedule as every other prune
  /// (transactions at 90 days), and deleting a row the moment it goes stale would put
  /// the maintenance sweep in the business of deciding what recon walks. The freshness
  /// rule already answers that, per item, at both doors.</para>
  ///
  /// <para>Returns the number of rows dropped, for the caller's log line.</para>
  /// </summary>
  internal static int PruneStale(SqliteConnection connection, long cutoff)
  {
    using var cmd = new SqliteCommand(
      "DELETE FROM decision_cache WHERE banked_at < @cutoff", connection);
    cmd.Parameters.AddWithValue("@cutoff", cutoff);
    return cmd.ExecuteNonQuery();
  }
}

/// <summary>
/// THE ONE DEFINITION OF FRESH (ruled 2026-08-10, "do it right the first time").
///
/// <para>Two doors ask the same question and must never answer it differently.
/// Recon's work set derives from "is this item's banked decision stale" - the
/// skip-when-fresh mechanism IS the filter, so nothing procedural decides what
/// recon walks. The list stage's cached post (unit 3) asks "is this row fresh
/// enough to post from" - and the ruling was that those are ONE rule at two doors,
/// not two knobs: fresh enough to skip re-reading is fresh enough to act on. A
/// second threshold would be a second thing to tune wrong, and the first night the
/// two disagreed the round would re-read an item it was about to post from cache
/// anyway, or worse, post from a row it had just declared too old to trust.</para>
///
/// <para><b>The knob clamps to at least one hour, and that direction is the safe
/// one.</b> A zero or negative <c>ReconFreshHours</c> read literally would make
/// every row fresh forever (now - 0 = now, so any banked_at passes), which is a
/// config typo silently pinning the act half to whatever the cache last held. The
/// clamp turns that same typo into "almost everything is stale" - recon walks more
/// than it needs to and the round is slow, which is a cost the player can see and
/// complain about rather than a lie he cannot.</para>
///
/// <para>Pure and Dalamud-free (linked into the test project): the caller supplies
/// the wall clock, the config knob, the listable scan and the banked timestamps.</para>
/// </summary>
internal static class ReconFreshness
{
  /// <summary>The oldest banked_at that still counts as fresh, at this instant.</summary>
  internal static long Cutoff(long nowUnix, int freshHours)
    => nowUnix - Math.Max(1, freshHours) * 3600L;

  /// <summary>A banked row is fresh when it exists and is no older than the cutoff.</summary>
  internal static bool IsFresh(long bankedAt, long nowUnix, int freshHours)
    => bankedAt > 0 && bankedAt >= Cutoff(nowUnix, freshHours);

  /// <summary>
  /// The recon question, asked of one item: null banked_at means NO ROW - nobody
  /// ever looked - which needs recon exactly as much as an ancient row does. The
  /// two cases are deliberately one answer here; they are only different stories
  /// in the narration.
  /// </summary>
  internal static bool NeedsRecon(long? bankedAt, long nowUnix, int freshHours)
    => bankedAt is not long banked || !IsFresh(banked, nowUnix, freshHours);

  /// <summary>
  /// RECON'S WORK SET: the listable scan, deduped by (item, quality), filtered to
  /// the rows whose banked decision is missing or stale.
  ///
  /// <para><b>The dedupe is not an optimisation, it is the unit of work.</b> Recon
  /// reads a BOARD, and a board belongs to an (item, quality) pair - not to a bag
  /// slot. Two stacks of the same ingot in two slots are one market question, and
  /// walking both would pay a second four-second server round trip to receive the
  /// same board, then bank the second answer over the first. Worse, the pricing
  /// pipeline's per-run price cache would short-circuit the second visit, so it
  /// would produce no lane, no receipt and nothing to bank - a visibly slower run
  /// that learned strictly less. First slot wins; the rest of the stack rides its
  /// answer, which is what "one row per (item_id, is_hq)" already promised.</para>
  ///
  /// <para>Order is preserved from the caller's scan (which sorts by name), so the
  /// run log reads alphabetically like every other bag walk.</para>
  /// </summary>
  internal static List<T> WorkSet<T>(
    IEnumerable<T> listable,
    Func<T, (uint ItemId, bool IsHq)> keyOf,
    IReadOnlyDictionary<(uint ItemId, bool IsHq), long> bankedAt,
    long nowUnix,
    int freshHours)
  {
    var seen = new HashSet<(uint, bool)>();
    var work = new List<T>();
    foreach (var row in listable)
    {
      var key = keyOf(row);
      if (!seen.Add(key)) continue;
      var banked = bankedAt.TryGetValue(key, out var ts) ? ts : (long?)null;
      if (NeedsRecon(banked, nowUnix, freshHours))
        work.Add(row);
    }
    return work;
  }

  /// <summary>
  /// THE RE-LOOK'S WORK SET: the same scan, the same dedupe, NO freshness filter.
  ///
  /// <para>A Re-Look is the player saying the banked reads are not good enough for
  /// what he is about to do, and the freshness rule is precisely the thing he is
  /// overruling. Running it through the filter would answer "nothing needs recon" -
  /// true, by a rule he just rejected - and hand back a run that reads zero items and
  /// reports a completed recon. So the verb bypasses the filter (ruled 08-10), and it
  /// bypasses it HERE rather than by passing a sentinel freshness of 0 into the
  /// filter: the clamp on <see cref="Cutoff"/> exists to stop a 0 from meaning
  /// anything, and re-purposing it as a bypass would put two meanings on one knob.</para>
  ///
  /// <para>The dedupe is NOT bypassed. It is not part of the filter - it is the unit
  /// of work (a board belongs to an item variant, not a bag slot), and re-reading the
  /// same board twice would cost a second round trip to learn the same thing.</para>
  /// </summary>
  internal static List<T> AllVariants<T>(
    IEnumerable<T> listable, Func<T, (uint ItemId, bool IsHq)> keyOf)
  {
    var seen = new HashSet<(uint, bool)>();
    var work = new List<T>();
    foreach (var row in listable)
      if (seen.Add(keyOf(row)))
        work.Add(row);
    return work;
  }

  /// <summary>
  /// How many distinct (item, quality) pairs a Re-Look would walk - the whole
  /// listable set, deduped. The deck says this number while a Re-Look is armed, from
  /// the same walk the run will make, for the same reason
  /// <see cref="StaleCount"/> shares its predicate with <see cref="WorkSet"/>.
  /// </summary>
  internal static int VariantCount<T>(
    IEnumerable<T> listable, Func<T, (uint ItemId, bool IsHq)> keyOf)
  {
    var seen = new HashSet<(uint, bool)>();
    var count = 0;
    foreach (var row in listable)
      if (seen.Add(keyOf(row))) count++;
    return count;
  }

  /// <summary>
  /// How many distinct (item, quality) pairs recon would walk right now - the
  /// number the rail says out loud ("Recon - 12 stale"). Derived from the SAME
  /// predicate the work set is, because a count that could disagree with the run
  /// it advertises is the two-surfaces bug the round already paid for once.
  /// </summary>
  internal static int StaleCount<T>(
    IEnumerable<T> listable,
    Func<T, (uint ItemId, bool IsHq)> keyOf,
    IReadOnlyDictionary<(uint ItemId, bool IsHq), long> bankedAt,
    long nowUnix,
    int freshHours)
  {
    // The same walk as WorkSet, without building the list - this one is asked
    // every frame the deck draws, and the rows it would collect are thrown away.
    var seen = new HashSet<(uint, bool)>();
    var stale = 0;
    foreach (var row in listable)
    {
      var key = keyOf(row);
      if (!seen.Add(key)) continue;
      var banked = bankedAt.TryGetValue(key, out var ts) ? ts : (long?)null;
      if (NeedsRecon(banked, nowUnix, freshHours)) stale++;
    }
    return stale;
  }
}
