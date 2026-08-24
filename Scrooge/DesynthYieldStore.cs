using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// CRUD layer for desynth_runs and desynth_yields. Mirrors GilStorage's
/// pattern (single shared connection owned by GilStorage). Methods use
/// the borrowed connection directly — they do not dispose it.
/// </summary>
internal sealed class DesynthYieldStore
{
  private readonly SqliteConnection _connection;

  internal DesynthYieldStore(SqliteConnection connection)
  {
    _connection = connection;
  }

  /// <summary>Fires after a yield row is inserted. Subscribed to by PinchRunLog for live sub-row rendering.</summary>
  internal event Action<DesynthYield>? YieldCaptured;

  /// <summary>Invoked by DesynthYieldTracker right after InsertYield succeeds.</summary>
  internal void PublishYieldCaptured(DesynthYield yield) => YieldCaptured?.Invoke(yield);

  /// <summary>Inserts a new run row. Returns the auto-generated id.</summary>
  internal long StartRun(string mode, int totalItems, DateTimeOffset startedAt)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO desynth_runs (started_at, mode, total_items)
        VALUES (@started, @mode, @total);
        SELECT last_insert_rowid();",
      _connection);
    cmd.Parameters.AddWithValue("@started", startedAt.ToUnixTimeSeconds());
    cmd.Parameters.AddWithValue("@mode", mode);
    cmd.Parameters.AddWithValue("@total", totalItems);
    return (long)(cmd.ExecuteScalar() ?? 0L);
  }

  /// <summary>
  /// Updates a run's total_items. Auto-continue refills the queue mid-run when
  /// the desynth window repopulates, so the total grows past the initial count.
  /// </summary>
  internal void UpdateTotalItems(long runId, int totalItems)
  {
    using var cmd = new SqliteCommand(
      @"UPDATE desynth_runs
        SET total_items = @total
        WHERE id = @id;",
      _connection);
    cmd.Parameters.AddWithValue("@total", totalItems);
    cmd.Parameters.AddWithValue("@id", runId);
    cmd.ExecuteNonQuery();
  }

  /// <summary>Marks a run as complete. Sets ended_at; aborted_reason left null.</summary>
  internal void EndRun(long runId, DateTimeOffset endedAt)
  {
    using var cmd = new SqliteCommand(
      @"UPDATE desynth_runs
        SET ended_at = @ended
        WHERE id = @id;",
      _connection);
    cmd.Parameters.AddWithValue("@ended", endedAt.ToUnixTimeSeconds());
    cmd.Parameters.AddWithValue("@id", runId);
    cmd.ExecuteNonQuery();
  }

  /// <summary>Marks a run as aborted with a reason string.</summary>
  internal void AbortRun(long runId, DateTimeOffset endedAt, string reason)
  {
    using var cmd = new SqliteCommand(
      @"UPDATE desynth_runs
        SET ended_at = @ended, aborted_reason = @reason
        WHERE id = @id;",
      _connection);
    cmd.Parameters.AddWithValue("@ended", endedAt.ToUnixTimeSeconds());
    cmd.Parameters.AddWithValue("@reason", reason);
    cmd.Parameters.AddWithValue("@id", runId);
    cmd.ExecuteNonQuery();
  }

  // The recent-yields query that fed the Ledger's "Fresh yields" side door left
  // with it (WALK unit 4): a fresh yield is just a listable bag item, and the
  // one-door bell reads the bags directly through the Hawk gate. The yields table
  // stays - it is the melt's receipt trail - it just no longer has a UI consumer.

  /// <summary>
  /// EVERY VARIANT THIS ROUND'S MELTS PRODUCED - invariant B's one declared exception
  /// (the unit-5 addendum). The bell may admit a row that was not on the board the
  /// human ruled if and only if the Round's own melt made it, and this is the join
  /// that answers it: the melt's run ids, which the Round records as each melt
  /// completes, against the yield rows those runs banked.
  ///
  /// <para>Reads <c>desynth_yields</c> straight through <c>ix_desynth_yields_run</c> -
  /// the index that has covered <c>(run_id, attempt_seq)</c> since V10 - so the query
  /// is a handful of index seeks. Called on a melt's completion and on a restore,
  /// never per frame.</para>
  ///
  /// <para>The ids are inlined rather than parameterised, which is safe here in the
  /// one way that matters: they are <c>long</c>s from the caller's own list, so there
  /// is no string to escape. An empty list short-circuits rather than building
  /// <c>IN ()</c>, which SQLite rejects.</para>
  /// </summary>
  internal HashSet<(uint ItemId, bool IsHq)> YieldVariantsForRuns(IReadOnlyList<long> runIds)
  {
    var variants = new HashSet<(uint, bool)>();
    if (runIds.Count == 0) return variants;

    using var cmd = new SqliteCommand(
      $@"SELECT DISTINCT yield_item_id, yield_is_hq
         FROM desynth_yields
         WHERE run_id IN ({string.Join(",", runIds)});",
      _connection);

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      variants.Add(((uint)reader.GetInt64(0), reader.GetInt32(1) != 0));
    return variants;
  }

  /// <summary>Inserts a yield event. Called by DesynthYieldTracker.</summary>
  internal void InsertYield(DesynthYield yield)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO desynth_yields
          (run_id, attempt_seq, source_item_id, source_is_hq,
           yield_item_id, yield_qty, yield_is_hq, captured_at)
        VALUES
          (@run, @seq, @src, @srchq,
           @yield, @qty, @yieldhq, @captured);",
      _connection);
    cmd.Parameters.AddWithValue("@run", yield.RunId);
    cmd.Parameters.AddWithValue("@seq", yield.AttemptSeq);
    cmd.Parameters.AddWithValue("@src", yield.SourceItemId);
    cmd.Parameters.AddWithValue("@srchq", yield.SourceIsHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@yield", yield.YieldItemId);
    cmd.Parameters.AddWithValue("@qty", yield.YieldQty);
    cmd.Parameters.AddWithValue("@yieldhq", yield.YieldIsHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@captured", yield.CapturedAt.ToUnixTimeSeconds());
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Reads recent yield rows, joined with their run's started_at for the
  /// Desynth Yields tab. Sorted captured_at DESC. Paginated by limit/offset.
  /// </summary>
  internal List<DesynthYieldRow> ReadRecent(int limit, int offset)
  {
    var results = new List<DesynthYieldRow>();
    using var cmd = new SqliteCommand(
      @"SELECT y.id, y.run_id, y.attempt_seq,
               y.source_item_id, y.source_is_hq,
               y.yield_item_id, y.yield_qty, y.yield_is_hq,
               y.captured_at, r.started_at
        FROM desynth_yields y
        JOIN desynth_runs r ON r.id = y.run_id
        ORDER BY y.captured_at DESC
        LIMIT @limit OFFSET @offset;",
      _connection);
    cmd.Parameters.AddWithValue("@limit", limit);
    cmd.Parameters.AddWithValue("@offset", offset);

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      results.Add(new DesynthYieldRow
      {
        Id = reader.GetInt64(0),
        RunId = reader.GetInt64(1),
        AttemptSeq = reader.GetInt32(2),
        SourceItemId = (uint)reader.GetInt64(3),
        SourceIsHq = reader.GetInt32(4) != 0,
        YieldItemId = (uint)reader.GetInt64(5),
        YieldQty = reader.GetInt32(6),
        YieldIsHq = reader.GetInt32(7) != 0,
        CapturedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(8)),
        RunStartedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(9)),
      });
    }
    return results;
  }

  /// <summary>Total yield row count — for the tab's pagination footer.</summary>
  internal long Count()
  {
    using var cmd = new SqliteCommand("SELECT COUNT(*) FROM desynth_yields;", _connection);
    return (long)(cmd.ExecuteScalar() ?? 0L);
  }

  /// <summary>
  /// One yield row's realizable gil: the best market evidence (local, then
  /// community) floored by what the counter pays. The tab's rollup and the
  /// tribunal's per-attempt spread both price a melt with this, so they cannot
  /// disagree about what a melt was worth.
  /// </summary>
  private const string YieldValueExpr =
    @"MAX(COALESCE(p.unit_price, c.unit_price, 0),
                     COALESCE(v.unit_price, 0))";

  /// <summary>The three price rungs <see cref="YieldValueExpr"/> reads, in its order.</summary>
  private const string YieldPriceJoins =
    @"LEFT JOIN last_sale_prices p ON p.item_id = y.yield_item_id AND p.is_hq = y.yield_is_hq
        LEFT JOIN community_mat_prices c ON c.item_id = y.yield_item_id AND c.is_hq = y.yield_is_hq
        LEFT JOIN vendor_mat_prices v ON v.item_id = y.yield_item_id";

  /// <summary>
  /// Per-source-item rollup for the Desynth tab: how many times each item
  /// was desynthed and what the yields were worth. An attempt = one
  /// (run_id, attempt_seq) pair.
  ///
  /// <para><b>Market evidence is local, then community.</b> Our own boards price what
  /// we have actually sold; <see cref="MeltYieldPrices"/> fills the rest from
  /// Universalis DC sales. Local wins wherever it exists - the community weight is a
  /// fallback, never an override.</para>
  ///
  /// <para><b>The floor rung is the VENDOR price (V28, 07-26).</b> A yield's realizable
  /// value is <c>MAX(best market evidence, what the counter pays)</c> - not a third
  /// fallback but a FLOOR, because vendor gil is gil the melt provably realizes whether
  /// or not anyone ever lists the mat. Clear Demimateria III is the receipt: 5,000 gil
  /// each at the counter, ZERO on this scale, while Drift's Always Vendor rule collected
  /// 20,000 of it in one round. Market beats vendor where the market is better; vendor
  /// beats an empty market; a mat with neither still reads 0.</para>
  ///
  /// <para><b>The zero is not always a gap.</b> Elemental catalysts (shards,
  /// crystals, clusters - item ids 2-19) are deliberately valued at NOTHING.
  /// Drift's ruling, 07-26: <i>"they have uses but effectively are 0 gil."</i> They
  /// are never fetched, so they never get a community row, so this COALESCE
  /// lands on 0 for them by construction rather than by accident. Any OTHER
  /// yield still reading 0 here is a material Universalis has not answered for
  /// yet - see <see cref="MeltYieldPrices.MaterialsNeedingPrice"/>.
  /// <b>The vendor floor leaves the ruling standing</b>, twice over: catalysts are
  /// excluded from the vendor worklist, AND they carry PriceLow 0 - so crystals weigh
  /// nothing even if someone later deletes the exclusion.</para>
  /// </summary>
  internal List<DesynthSourceSummary> ReadSourceSummary(long sinceUnixSeconds)
  {
    var results = new List<DesynthSourceSummary>();
    using var cmd = new SqliteCommand(
      $@"SELECT y.source_item_id, y.source_is_hq,
               COUNT(DISTINCT y.run_id || '-' || y.attempt_seq) AS attempts,
               SUM(y.yield_qty * {YieldValueExpr}) AS yield_value,
               SUM(CASE WHEN p.unit_price IS NOT NULL
                         AND COALESCE(p.unit_price, 0) >= COALESCE(v.unit_price, 0)
                        THEN y.yield_qty * {YieldValueExpr} ELSE 0 END) AS own_value,
               SUM(CASE WHEN p.unit_price IS NULL AND c.unit_price IS NOT NULL
                         AND COALESCE(c.unit_price, 0) >= COALESCE(v.unit_price, 0)
                        THEN y.yield_qty * {YieldValueExpr} ELSE 0 END) AS community_value,
               SUM(CASE WHEN COALESCE(v.unit_price, 0) > COALESCE(p.unit_price, c.unit_price, 0)
                        THEN y.yield_qty * {YieldValueExpr} ELSE 0 END) AS vendor_value
        FROM desynth_yields y
        {YieldPriceJoins}
        WHERE y.captured_at >= @since
        GROUP BY y.source_item_id, y.source_is_hq
        ORDER BY attempts DESC;",
      _connection);
    cmd.Parameters.AddWithValue("@since", sinceUnixSeconds);

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      results.Add(new DesynthSourceSummary
      {
        SourceItemId = (uint)reader.GetInt64(0),
        SourceIsHq = reader.GetInt32(1) != 0,
        Attempts = reader.GetInt32(2),
        YieldValue = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
        OwnValue = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
        CommunityValue = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
        VendorValue = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
      });
    }
    return results;
  }

  /// <summary>
  /// ONE SOURCE ITEM'S MELT ROLLUP, WITH THE SPREAD (triage walk, Task 3). The Desynth
  /// tab's <see cref="ReadSourceSummary"/> answers count and total for every source at
  /// once, which is the right shape for a tab and the wrong one for a case: the
  /// tribunal's melt argument is "N of your own melts returned X on average, between
  /// LOW and HIGH", and the spread is the half that says how much the average is worth.
  /// A rollup with no spread would have to print a range nobody measured, so the query
  /// goes per attempt.
  ///
  /// <para>Same weights, same joins, same <c>MAX(market, vendor)</c> floor rung as the
  /// summary above - the case must never price a melt differently from the scorer that
  /// put the number in the cell.</para>
  ///
  /// <para>Zero attempts returns all zeroes, which is the honest "you have never melted
  /// one" the band prior fires on.</para>
  /// </summary>
  internal (int Attempts, long Average, long Low, long High) SourceRollup(uint itemId, bool isHq)
  {
    var perAttempt = new List<long>();
    using (var cmd = new SqliteCommand(
      $@"SELECT SUM(y.yield_qty * {YieldValueExpr}) AS attempt_value
        FROM desynth_yields y
        {YieldPriceJoins}
        WHERE y.source_item_id = @iid AND y.source_is_hq = @hq
        GROUP BY y.run_id, y.attempt_seq;",
      _connection))
    {
      cmd.Parameters.AddWithValue("@iid", (long)itemId);
      cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      using var reader = cmd.ExecuteReader();
      while (reader.Read())
        perAttempt.Add(reader.IsDBNull(0) ? 0 : reader.GetInt64(0));
    }

    if (perAttempt.Count == 0) return (0, 0, 0, 0);
    var total = 0L;
    var low = long.MaxValue;
    var high = long.MinValue;
    foreach (var v in perAttempt)
    {
      total += v;
      if (v < low) low = v;
      if (v > high) high = v;
    }
    return (perAttempt.Count, total / perAttempt.Count, low, high);
  }

  /// <summary>
  /// Yield materials still missing a weight - the community fetch's worklist.
  /// The rule lives in <see cref="MeltYieldPrices"/>; this only lends the
  /// connection.
  /// </summary>
  internal List<(uint ItemId, bool IsHq)> MaterialsNeedingCommunityPrice(long nowUnixSeconds)
    => MeltYieldPrices.MaterialsNeedingPrice(_connection, nowUnixSeconds);

  /// <summary>Records one material's community weight. See <see cref="MeltYieldPrices"/>.</summary>
  internal void UpsertCommunityPrice(uint itemId, bool isHq, long unitPrice, long fetchedAt)
    => MeltYieldPrices.UpsertPrice(_connection, itemId, isHq, unitPrice, fetchedAt);

  /// <summary>
  /// Yield materials with no counter price yet - the sheet fill's worklist. The rule
  /// lives in <see cref="MeltYieldPrices"/>; this only lends the connection.
  /// </summary>
  internal List<uint> MaterialsNeedingVendorPrice()
    => MeltYieldPrices.MaterialsNeedingVendorPrice(_connection);

  /// <summary>Records one material's counter price - the melt scale's floor rung.</summary>
  internal void UpsertVendorPrice(uint itemId, long unitPrice, long filledAt)
    => MeltYieldPrices.UpsertVendorPrice(_connection, itemId, unitPrice, filledAt);
}

/// <summary>
/// Per-source-item rollup row for the Desynth tab.
/// </summary>
internal sealed class DesynthSourceSummary
{
  public uint SourceItemId { get; init; }
  public bool SourceIsHq { get; init; }
  public int Attempts { get; init; }
  /// <summary>Total yield value across all attempts (cached prices, 0 = unknown).</summary>
  public long YieldValue { get; init; }
  /// <summary>The slice of <see cref="YieldValue"/> priced by your own sale book.</summary>
  public long OwnValue { get; init; }
  /// <summary>The slice priced by the DC community rung (the "~" witness).</summary>
  public long CommunityValue { get; init; }
  /// <summary>The slice priced by the vendor floor - realizable, never a valuation.</summary>
  public long VendorValue { get; init; }
}

/// <summary>
/// THE WITNESS THE VALUE COULD NOT NAME (ruled 08-22). The summary's number is a
/// MAX over three price rungs inside the SQL, and until the per-rung sums above the
/// cell wore a blanket "~" - claiming the DC's weaker witness for values your own
/// sale book had priced. Pure voice over the three sums: the MARK follows the rung
/// that priced the most of the value (own outranks community outranks vendor on a
/// tie - the stronger witness keeps ties), and the hover names every rung that
/// contributed, in gil, so a mixed number says it is mixed.
/// </summary>
internal static class DesynthWitness
{
  internal enum Rung { None, Own, Community, Vendor }

  /// <summary>Which rung priced the most of the value. Ties keep the stronger witness.</summary>
  internal static Rung Dominant(long own, long community, long vendor)
  {
    if (own <= 0 && community <= 0 && vendor <= 0) return Rung.None;
    if (own >= community && own >= vendor) return Rung.Own;
    return community >= vendor ? Rung.Community : Rung.Vendor;
  }

  /// <summary>The house provenance grammar: bare = your sale, "~" = DC, "(vendor)" = the floor.</summary>
  internal static string Mark(long perAttempt, long own, long community, long vendor)
    => Dominant(own, community, vendor) switch
    {
      Rung.Community => $"~{perAttempt:N0}",
      Rung.Vendor => $"{perAttempt:N0} (vendor)",
      Rung.Own => $"{perAttempt:N0}",
      _ => "?",
    };

  /// <summary>One hover line per rung that contributed - a mixed number says so.</summary>
  internal static string Hover(long own, long community, long vendor)
  {
    var parts = new List<string>(3);
    if (own > 0) parts.Add($"your own sales priced {own:N0} gil of it");
    if (community > 0) parts.Add($"~DC sales priced {community:N0} gil");
    if (vendor > 0) parts.Add($"the vendor floor priced {vendor:N0} gil");
    return parts.Count == 0
      ? "No witness has priced these yields yet."
      : char.ToUpperInvariant(parts[0][0]) + parts[0][1..] + (parts.Count > 1 ? "; " + string.Join("; ", parts.Skip(1)) : "") + ".";
  }
}

/// <summary>
/// Joined row used by the Desynth Yields tab — yield + run start time.
/// </summary>
internal sealed class DesynthYieldRow
{
  public long Id { get; init; }
  public long RunId { get; init; }
  public int AttemptSeq { get; init; }
  public uint SourceItemId { get; init; }
  public bool SourceIsHq { get; init; }
  public uint YieldItemId { get; init; }
  public int YieldQty { get; init; }
  public bool YieldIsHq { get; init; }
  public DateTimeOffset CapturedAt { get; init; }
  public DateTimeOffset RunStartedAt { get; init; }
}
