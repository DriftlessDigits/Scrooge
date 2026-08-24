using Microsoft.Data.Sqlite;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// THE MELT SCALE'S MISSING WEIGHTS (07-26).
///
/// <para><b>The hole.</b> Melt value prices every yield through
/// <c>last_sale_prices</c>, and that cache only ever fills from boards WE
/// pinched. Of the 18 distinct yield materials in the ledger, three had a
/// price. Everything else valued at ZERO - so every melt score was a floor
/// with holes in it, and GC turn-in kept winning comparisons on arithmetic
/// against a half-empty scale.</para>
///
/// <para><b>The fill.</b> A yield material with no local price gets a
/// COMMUNITY price: the median of the DC-scope settled sales Universalis
/// already serves the lane fallback. Median, not the last single sale,
/// because one crazy settle should not become a material's standing weight -
/// and it is the same statistic the community lane already prices with, so
/// the melt scale and the listing scale read the market the same way. Local
/// always wins where it exists; this is a fallback, never an override.</para>
///
/// <para><b>The deliberate zero.</b> Elemental catalysts - shards, crystals,
/// clusters (<see cref="CatalystFirstItemId"/>-<see cref="CatalystLastItemId"/>)
/// - are NEVER fetched and NEVER priced. Drift's ruling: <i>"they have uses but
/// effectively are 0 gil."</i> Their zero is a decision, not a data gap, and
/// it is enforced structurally: no fetch, so no row, so the COALESCE lands on
/// 0 for exactly the materials that should weigh nothing.</para>
///
/// <para>Dalamud-free so the seam is linked-source testable (the
/// SaleHistorySchema / MarketMemorySchema model): this owns the DDL, the
/// candidate pick, the upsert and the median. The shell owns only the fetch,
/// which is <see cref="UniversalisHistory"/>'s existing queue - there is no
/// second client, no second cadence, no second rate limit.</para>
/// </summary>
internal static class MeltYieldPrices
{
  /// <summary>
  /// First elemental catalyst item id. Shards/crystals/clusters occupy the
  /// contiguous 2-19 block; item 1 is gil and 0 is nothing.
  /// </summary>
  internal const uint CatalystFirstItemId = 2;

  /// <summary>Last elemental catalyst item id (Water Cluster).</summary>
  internal const uint CatalystLastItemId = 19;

  /// <summary>
  /// How long a fetched community price stands before it is re-asked. A week:
  /// mat prices drift slowly and the point of this table is to stop the melt
  /// scale reading empty, not to track the tape tick by tick.
  /// </summary>
  internal const long MaxAgeSeconds = 7 * 86400;

  /// <summary>
  /// V27: <c>community_mat_prices</c> - the fallback weight for yield materials
  /// our own boards have never priced. Keyed by (item, quality) exactly like
  /// <c>last_sale_prices</c>, so the valuation join is a mirror of the local one.
  /// CREATE ... IF NOT EXISTS, so re-running is a no-op (the V11 model).
  /// </summary>
  internal static void ApplyV27(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(
      @"CREATE TABLE IF NOT EXISTS community_mat_prices (
          item_id    INTEGER NOT NULL,
          is_hq      INTEGER NOT NULL DEFAULT 0,
          unit_price INTEGER NOT NULL,
          fetched_at INTEGER NOT NULL,
          PRIMARY KEY (item_id, is_hq)
        );",
      connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// V28: <c>vendor_mat_prices</c> - the melt scale's FLOOR RUNG.
  ///
  /// <para><b>The hole this closes (Drift, 07-26).</b> Clear Demimateria III vendor-sells
  /// at 5,000 gil apiece - his Always Vendor rule collected 20,000 in a single round -
  /// and the melt valuation weighed it at ZERO, because nobody had ever listed one on
  /// our boards and Universalis has thin history for a mat people simply vendor. A
  /// yield's realizable value is never less than what the counter pays for it, and the
  /// counter's price is not evidence to be gathered: it is <c>Item.PriceLow</c>, static
  /// sheet data. No fetch, no queue, no staleness.</para>
  ///
  /// <para><b>Keyed by item alone, unlike its two neighbours.</b>
  /// <c>last_sale_prices</c> and <c>community_mat_prices</c> are keyed (item, quality)
  /// because a MARKET prices the two qualities separately. The vendor counter does not -
  /// there is one PriceLow column per item. A quality column here would mean writing the
  /// same number twice and inviting a reader to believe the HQ side was measured. (HQ
  /// really does fetch more than PriceLow at the counter, so an HQ yield's floor comes
  /// out conservative, which is the right direction for a floor to be wrong.)</para>
  ///
  /// <para>CREATE ... IF NOT EXISTS, so re-running is a no-op (the V11 model).</para>
  /// </summary>
  internal static void ApplyV28(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(
      @"CREATE TABLE IF NOT EXISTS vendor_mat_prices (
          item_id    INTEGER NOT NULL PRIMARY KEY,
          unit_price INTEGER NOT NULL,
          filled_at  INTEGER NOT NULL
        );",
      connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Yield materials with no counter price on file yet - the sheet fill's worklist.
  ///
  /// <para>No staleness clause, deliberately: <c>Item.PriceLow</c> is static sheet
  /// data, so a row once written is right until the client's own sheets change. There
  /// is nothing to re-ask and nobody to ask.</para>
  ///
  /// <para>Elemental catalysts are excluded here exactly as they are from the community
  /// worklist. They would fill to nothing anyway - shards, crystals and clusters carry
  /// PriceLow 0 - but excluding them keeps their zero a RULING (Drift: <i>"they have uses
  /// but effectively are 0 gil"</i>) rather than an accident of the sheet happening to
  /// agree with us this patch.</para>
  ///
  /// <para>An item whose PriceLow is 0 gets no row and so returns here every batch. On
  /// purpose: "the counter does not buy this" is not a fact worth a sentinel row, and
  /// the re-ask is one Lumina lookup over a list that has never been more than a few
  /// dozen entries long.</para>
  /// </summary>
  internal static List<uint> MaterialsNeedingVendorPrice(SqliteConnection connection)
  {
    var results = new List<uint>();
    using var cmd = new SqliteCommand(
      @"SELECT DISTINCT y.yield_item_id
        FROM desynth_yields y
        LEFT JOIN vendor_mat_prices v ON v.item_id = y.yield_item_id
        WHERE v.item_id IS NULL
          AND y.yield_item_id NOT BETWEEN @catFirst AND @catLast
        ORDER BY y.yield_item_id;",
      connection);
    cmd.Parameters.AddWithValue("@catFirst", (long)CatalystFirstItemId);
    cmd.Parameters.AddWithValue("@catLast", (long)CatalystLastItemId);

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      results.Add((uint)reader.GetInt64(0));
    return results;
  }

  /// <summary>Writes (or refreshes) one material's counter price.</summary>
  internal static void UpsertVendorPrice(SqliteConnection connection,
    uint itemId, long unitPrice, long filledAt)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO vendor_mat_prices (item_id, unit_price, filled_at)
        VALUES (@iid, @price, @at)
        ON CONFLICT(item_id) DO UPDATE
          SET unit_price = excluded.unit_price, filled_at = excluded.filled_at;",
      connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@price", unitPrice);
    cmd.Parameters.AddWithValue("@at", filledAt);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// The yield materials whose weight is currently missing: they appear in
  /// <c>desynth_yields</c>, have NO local sale price, are not elemental
  /// catalysts (the deliberate zero), and carry no community price fresher
  /// than <see cref="MaxAgeSeconds"/>.
  ///
  /// <para>Quality-granular because the yields are: a material that sold NQ on
  /// our board but never HQ still has an unweighted HQ side.</para>
  ///
  /// <para>Unchanged by the vendor floor (V28): a mat with a counter price is still
  /// worth asking the market about, because the market is what makes it worth MORE than
  /// the counter. The floor is a floor, never a reason to stop looking up.</para>
  /// </summary>
  internal static List<(uint ItemId, bool IsHq)> MaterialsNeedingPrice(
    SqliteConnection connection, long nowUnixSeconds)
  {
    var results = new List<(uint, bool)>();
    using var cmd = new SqliteCommand(
      @"SELECT DISTINCT y.yield_item_id, y.yield_is_hq
        FROM desynth_yields y
        LEFT JOIN last_sale_prices p
          ON p.item_id = y.yield_item_id AND p.is_hq = y.yield_is_hq
        LEFT JOIN community_mat_prices c
          ON c.item_id = y.yield_item_id AND c.is_hq = y.yield_is_hq
        WHERE p.item_id IS NULL
          AND y.yield_item_id NOT BETWEEN @catFirst AND @catLast
          AND (c.item_id IS NULL OR c.fetched_at < @cutoff)
        ORDER BY y.yield_item_id, y.yield_is_hq;",
      connection);
    cmd.Parameters.AddWithValue("@catFirst", (long)CatalystFirstItemId);
    cmd.Parameters.AddWithValue("@catLast", (long)CatalystLastItemId);
    cmd.Parameters.AddWithValue("@cutoff", nowUnixSeconds - MaxAgeSeconds);

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      results.Add(((uint)reader.GetInt64(0), reader.GetInt32(1) != 0));
    return results;
  }

  /// <summary>Writes (or refreshes) one material's community weight.</summary>
  internal static void UpsertPrice(SqliteConnection connection,
    uint itemId, bool isHq, long unitPrice, long fetchedAt)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO community_mat_prices (item_id, is_hq, unit_price, fetched_at)
        VALUES (@iid, @hq, @price, @at)
        ON CONFLICT(item_id, is_hq) DO UPDATE
          SET unit_price = excluded.unit_price, fetched_at = excluded.fetched_at;",
      connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@price", unitPrice);
    cmd.Parameters.AddWithValue("@at", fetchedAt);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// The representative price of a settled-sale run: the median. Returns null
  /// on an empty run - no sales is no weight, and a fabricated zero would be
  /// indistinguishable from the catalysts' deliberate one.
  /// </summary>
  internal static long? MedianPrice(List<long> prices)
  {
    if (prices.Count == 0) return null;
    prices.Sort();
    var mid = prices.Count / 2;
    return prices.Count % 2 == 1 ? prices[mid] : (prices[mid - 1] + prices[mid]) / 2;
  }

  /// <summary>True for the shards/crystals/clusters that are deliberately worth zero.</summary>
  internal static bool IsElementalCatalyst(uint itemId)
    => itemId >= CatalystFirstItemId && itemId <= CatalystLastItemId;
}
