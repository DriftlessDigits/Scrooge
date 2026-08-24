using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// V39 and the ONE definition of fresh (Rounds unit 2, recon-run).
///
/// <para>Two things are being pinned here, and they are the two that would be
/// expensive to get wrong quietly. First, the freshness predicate: it is the
/// mechanism that decides what recon walks AND (unit 3) what the list stage may
/// post from cache, so a drift in it is a drift in both doors at once. Second, the
/// table's upsert: a later recon must SUPERSEDE an earlier one, because the act
/// half reads exactly one row per item and a second one would make "what do we do
/// with this tonight" an ambiguous question.</para>
/// </summary>
public class DecisionCacheTests
{
  // A fixed clock, so "24 hours ago" is a number and not a race.
  private const long Now = 1_700_000_000L;
  private const long Hour = 3600L;

  // ==========================================================================
  // The freshness predicate - ONE rule, two doors
  // ==========================================================================

  [Fact]
  public void IsFresh_HoldsRightUpToTheCutoffAndNotPast()
  {
    // The boundary is inclusive on the fresh side: a row banked exactly 24h ago
    // has not yet aged out. Which way the boundary falls matters less than that it
    // falls the SAME way at both doors, which is the point of there being one
    // predicate at all.
    Assert.True(ReconFreshness.IsFresh(Now - (24 * Hour), Now, 24));
    Assert.False(ReconFreshness.IsFresh(Now - (24 * Hour) - 1, Now, 24));
    Assert.True(ReconFreshness.IsFresh(Now, Now, 24));
  }

  [Fact]
  public void Cutoff_IsWhatTheStorageReadWouldFilterOn()
  {
    // The SQL read takes the cutoff from here rather than computing its own, so
    // this is the seam where "fresh" stops being pure and starts being a WHERE
    // clause. If they ever disagree, the cache would hand the act half rows recon
    // had already decided to re-walk.
    Assert.Equal(Now - (24 * Hour), ReconFreshness.Cutoff(Now, 24));
  }

  [Fact]
  public void AZeroOrNegativeKnob_ClampsTowardStale_NeverTowardBlind()
  {
    // Read literally, freshHours = 0 makes the cutoff equal to now, and any row
    // banked in the past would be stale - fine. But a NEGATIVE knob would push the
    // cutoff into the FUTURE... no: it would push it backwards, making everything
    // fresh forever, which is a config typo silently pinning the act half to
    // whatever the cache last held. The clamp turns both cases into "an hour",
    // which costs minutes instead of trust.
    Assert.False(ReconFreshness.IsFresh(Now - (2 * Hour), Now, 0));
    Assert.False(ReconFreshness.IsFresh(Now - (2 * Hour), Now, -99));
    // Still fresh inside the clamped hour - the knob is floored, not ignored.
    Assert.True(ReconFreshness.IsFresh(Now - 60, Now, 0));
  }

  [Fact]
  public void NeedsRecon_TreatsAMissingRowExactlyLikeAnAncientOne()
  {
    // Nobody ever looked, and we looked so long ago it no longer counts, are the
    // same answer to the only question recon asks. They are only different stories
    // in the narration.
    Assert.True(ReconFreshness.NeedsRecon(null, Now, 24));
    Assert.True(ReconFreshness.NeedsRecon(Now - (48 * Hour), Now, 24));
    Assert.False(ReconFreshness.NeedsRecon(Now - Hour, Now, 24));
    // A zero timestamp is not a real banking instant - treat it as no row.
    Assert.True(ReconFreshness.NeedsRecon(0, Now, 24));
  }

  // ==========================================================================
  // The work set - derived, deduped, and identical to the count that advertises it
  // ==========================================================================

  private sealed record Row(uint ItemId, bool IsHq, string Name);

  private static (uint, bool) Key(Row r) => (r.ItemId, r.IsHq);

  [Fact]
  public void WorkSet_IsTheStaleOrMissingHalf_AndNothingElse()
  {
    var listable = new[]
    {
      new Row(100, false, "fresh"),
      new Row(200, false, "stale"),
      new Row(300, false, "never banked"),
    };
    var banked = new Dictionary<(uint, bool), long>
    {
      [(100, false)] = Now - Hour,
      [(200, false)] = Now - (48 * Hour),
    };

    var work = ReconFreshness.WorkSet(listable, Key, banked, Now, 24);

    Assert.Equal(new[] { "stale", "never banked" }, work.ConvertAll(r => r.Name));
  }

  [Fact]
  public void WorkSet_DedupesByVariant_BecauseABoardBelongsToAnItemNotASlot()
  {
    // Two stacks of one ingot in two bag slots are ONE market question. Walking
    // both would pay a second four-second server round trip for the same board -
    // and would learn less, because the pipeline's per-run price cache
    // short-circuits the second visit and produces no lane to bank at all.
    var listable = new[]
    {
      new Row(100, false, "slot 3"),
      new Row(100, false, "slot 7"),
      new Row(100, true, "slot 9 HQ"),
    };

    var work = ReconFreshness.WorkSet(listable, Key, new Dictionary<(uint, bool), long>(), Now, 24);

    // First slot wins; the HQ variant is a different board and rides separately.
    Assert.Equal(new[] { "slot 3", "slot 9 HQ" }, work.ConvertAll(r => r.Name));
  }

  [Fact]
  public void StaleCount_CanNeverDisagreeWithTheRunItAdvertises()
  {
    // The rail says "Recon - N stale" and then a press walks a work set. Those two
    // numbers come from separate methods for one reason only (the count is asked
    // every frame and must not allocate), so the contract that they agree is worth
    // a test rather than a comment.
    var listable = new[]
    {
      new Row(100, false, "fresh"),
      new Row(200, false, "stale"),
      new Row(200, false, "stale, second stack"),
      new Row(300, true, "never banked HQ"),
    };
    var banked = new Dictionary<(uint, bool), long>
    {
      [(100, false)] = Now - Hour,
      [(200, false)] = Now - (48 * Hour),
    };

    Assert.Equal(
      ReconFreshness.WorkSet(listable, Key, banked, Now, 24).Count,
      ReconFreshness.StaleCount(listable, Key, banked, Now, 24));
    Assert.Equal(2, ReconFreshness.StaleCount(listable, Key, banked, Now, 24));
  }

  [Fact]
  public void EverythingFresh_MeansTheStageSelfSkips()
  {
    // The skip-when-fresh mechanism IS the filter (ruled 08-10) - there is no
    // procedural "should we recon tonight" anywhere. A bag whose every decision is
    // current answers zero, the deck's HasWork answers false off that zero, and
    // the cursor steps over the stage exactly as it steps over an empty melt.
    var listable = new[] { new Row(100, false, "a"), new Row(200, true, "b") };
    var banked = new Dictionary<(uint, bool), long>
    {
      [(100, false)] = Now - Hour,
      [(200, true)] = Now - (23 * Hour),
    };

    Assert.Empty(ReconFreshness.WorkSet(listable, Key, banked, Now, 24));
    Assert.Equal(0, ReconFreshness.StaleCount(listable, Key, banked, Now, 24));
  }

  // ==========================================================================
  // The table
  // ==========================================================================

  private static SqliteConnection Open()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    DecisionCacheSchema.ApplyV39(conn);
    return conn;
  }

  [Fact]
  public void ApplyV39_IsIdempotent()
  {
    using var conn = Open();
    var ex = Record.Exception(() => DecisionCacheSchema.ApplyV39(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void Upsert_KeysOnTheVariant_AndTheLaterReconSupersedes()
  {
    using var conn = Open();
    Bank(conn, 100, hq: false, price: 500, outcome: "Undercut", bankedAt: Now - (48 * Hour));
    Bank(conn, 100, hq: false, price: 900, outcome: "EmptyBoard", bankedAt: Now);

    using var read = new SqliteCommand(
      "SELECT decided_price, outcome, banked_at FROM decision_cache WHERE item_id = 100", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal(900L, reader.GetInt64(0));
    Assert.Equal("EmptyBoard", reader.GetString(1));
    Assert.Equal(Now, reader.GetInt64(2));
    Assert.False(reader.Read()); // one row per variant, never two
  }

  [Fact]
  public void AHeldDecision_BanksWithANullPrice_WhichIsAnAnswerNotAnAbsence()
  {
    // "We looked and the evidence would not carry a price" is a different fact
    // from "nobody looked", and the act half needs both: the first is fresh (it
    // suppresses another recon for a day) and unpostable; the second is stale.
    using var conn = Open();
    Bank(conn, 100, hq: false, price: null, outcome: "HeldThinHistory", bankedAt: Now);

    using var read = new SqliteCommand(
      "SELECT decided_price, banked_at FROM decision_cache WHERE item_id = 100", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.True(reader.IsDBNull(0));      // nothing to post from
    Assert.Equal(Now, reader.GetInt64(1)); // but the LOOK was fresh
  }

  [Fact]
  public void TheTwoQualities_AreTwoDecisions()
  {
    // NQ and HQ are separate boards with separate lanes; one row for both would
    // post an HQ item at its NQ answer.
    using var conn = Open();
    Bank(conn, 100, hq: false, price: 500, outcome: "Undercut", bankedAt: Now);
    Bank(conn, 100, hq: true, price: 5000, outcome: "PremiumFromNq", bankedAt: Now);

    using var count = new SqliteCommand("SELECT COUNT(*) FROM decision_cache", conn);
    Assert.Equal(2L, count.ExecuteScalar());
  }

  [Fact]
  public void TheFreshRead_UsesTheSameCutoffThePredicateDoes()
  {
    using var conn = Open();
    Bank(conn, 100, hq: false, price: 500, outcome: "Undercut", bankedAt: Now - Hour);
    Bank(conn, 200, hq: false, price: 700, outcome: "Undercut", bankedAt: Now - (48 * Hour));

    using var cmd = new SqliteCommand(
      "SELECT item_id FROM decision_cache WHERE banked_at >= @cutoff ORDER BY item_id", conn);
    cmd.Parameters.AddWithValue("@cutoff", ReconFreshness.Cutoff(Now, 24));
    var fresh = new List<long>();
    using (var reader = cmd.ExecuteReader())
      while (reader.Read()) fresh.Add(reader.GetInt64(0));

    Assert.Equal(new[] { 100L }, fresh);
  }

  // ==========================================================================
  // THE RE-LOOK's work set (Rounds unit 4): the filter bypassed, the dedupe kept
  // ==========================================================================

  /// <summary>
  /// THE WHOLE POINT OF THE VERB. A Re-Look pressed twenty minutes after the Look
  /// would find every row inside the freshness window and walk NOTHING - a run that
  /// read zero boards while reporting a completed recon, which is worse than not
  /// offering the verb at all. So it ignores the bank entirely.
  /// </summary>
  [Fact]
  public void AllVariants_ReadsEverythingHoweverFreshTheBankIs()
  {
    var listable = new[]
    {
      (ItemId: 100u, IsHq: false),
      (ItemId: 200u, IsHq: false),
      (ItemId: 100u, IsHq: true),
    };
    var allFresh = new Dictionary<(uint, bool), long>
    {
      [(100u, false)] = Now,
      [(200u, false)] = Now,
      [(100u, true)] = Now,
    };

    // The ordinary filter answers "nothing to do" on exactly this input...
    Assert.Empty(ReconFreshness.WorkSet(listable, r => (r.ItemId, r.IsHq), allFresh, Now, 24));
    // ...and the Re-Look answers with the whole set.
    Assert.Equal(3, ReconFreshness.AllVariants(listable, r => (r.ItemId, r.IsHq)).Count);
  }

  /// <summary>
  /// The dedupe is NOT part of the filter and is not bypassed with it: a board belongs
  /// to an (item, quality) pair, not to a bag slot, so two stacks of one ingot are one
  /// market question however the work set was chosen.
  /// </summary>
  [Fact]
  public void AllVariants_StillDedupesByVariant()
  {
    var listable = new[]
    {
      (ItemId: 100u, IsHq: false),
      (ItemId: 100u, IsHq: false),   // a second stack in another slot
      (ItemId: 100u, IsHq: true),    // a different board
    };

    var work = ReconFreshness.AllVariants(listable, r => (r.ItemId, r.IsHq));

    Assert.Equal(2, work.Count);
    Assert.Equal(2, ReconFreshness.VariantCount(listable, r => (r.ItemId, r.IsHq)));
  }

  /// <summary>
  /// The count the deck says while a Re-Look is armed comes from the same walk the run
  /// makes - the two-surfaces bug the round already paid for once (see StaleCount).
  /// </summary>
  [Fact]
  public void VariantCount_AgreesWithTheWorkSetItAdvertises()
  {
    var listable = new[]
    {
      (ItemId: 100u, IsHq: false),
      (ItemId: 200u, IsHq: true),
      (ItemId: 200u, IsHq: true),
      (ItemId: 300u, IsHq: false),
    };

    Assert.Equal(
      ReconFreshness.AllVariants(listable, r => (r.ItemId, r.IsHq)).Count,
      ReconFreshness.VariantCount(listable, r => (r.ItemId, r.IsHq)));
  }

  [Fact]
  public void AllVariants_PreservesTheScansOrder()
  {
    var listable = new[]
    {
      (ItemId: 300u, IsHq: false),
      (ItemId: 100u, IsHq: false),
      (ItemId: 200u, IsHq: false),
    };

    Assert.Equal(new[] { 300u, 100u, 200u },
      ReconFreshness.AllVariants(listable, r => (r.ItemId, r.IsHq)).Select(r => r.ItemId));
  }

  private static void Bank(SqliteConnection conn, uint itemId, bool hq, long? price,
    string outcome, long bankedAt)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO decision_cache
          (item_id, is_hq, decided_price, outcome, evidence, receipt_id, run_id, banked_at)
        VALUES (@iid, @hq, @price, @outcome, 'evidence', 42, 7, @at)
        ON CONFLICT(item_id, is_hq) DO UPDATE SET
          decided_price = @price, outcome = @outcome, banked_at = @at",
      conn);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", hq ? 1 : 0);
    cmd.Parameters.AddWithValue("@price", (object?)price ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@outcome", outcome);
    cmd.Parameters.AddWithValue("@at", bankedAt);
    cmd.ExecuteNonQuery();
  }
}
