using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE MELT SCALE'S MISSING WEIGHTS (07-26). Melt value priced yields from
/// <c>last_sale_prices</c> alone - boards we pinched - so 15 of 18 yield
/// materials weighed nothing and turn-in won on arithmetic against a
/// half-empty scale. These run the real V27 DDL and the real valuation SQL
/// against a temp SQLite DB.
///
/// The contracts: local beats community beats zero; elemental catalysts are
/// never even asked about (their zero is Drift's ruling, not a gap); and a
/// weight that has stood a week gets re-asked.
/// </summary>
public class MeltYieldPricesTests
{
  private static SqliteConnection OpenTempDb()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    using var cmd = new SqliteCommand(
      @"CREATE TABLE desynth_runs (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          started_at INTEGER NOT NULL,
          ended_at INTEGER,
          mode TEXT NOT NULL DEFAULT '',
          total_items INTEGER NOT NULL DEFAULT 0,
          aborted_reason TEXT
        );
        CREATE TABLE desynth_yields (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          run_id INTEGER NOT NULL,
          attempt_seq INTEGER NOT NULL,
          source_item_id INTEGER NOT NULL,
          source_is_hq INTEGER NOT NULL DEFAULT 0,
          yield_item_id INTEGER NOT NULL,
          yield_qty INTEGER NOT NULL,
          yield_is_hq INTEGER NOT NULL DEFAULT 0,
          captured_at INTEGER NOT NULL
        );
        CREATE TABLE last_sale_prices (
          item_id INTEGER NOT NULL,
          is_hq INTEGER NOT NULL DEFAULT 0,
          unit_price INTEGER NOT NULL,
          timestamp INTEGER NOT NULL,
          sold_after_days INTEGER,
          PRIMARY KEY (item_id, is_hq)
        );", conn);
    cmd.ExecuteNonQuery();
    Scrooge.MeltYieldPrices.ApplyV27(conn);
    Scrooge.MeltYieldPrices.ApplyV28(conn);
    return conn;
  }

  /// <summary>One melt of <paramref name="sourceItemId"/> that returned one yield.</summary>
  private static void Melted(SqliteConnection conn, uint sourceItemId, uint yieldItemId,
    int qty = 1, bool yieldIsHq = false, int attemptSeq = 1, long capturedAt = 1000)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO desynth_yields
          (run_id, attempt_seq, source_item_id, source_is_hq, yield_item_id, yield_qty, yield_is_hq, captured_at)
        VALUES (1, @seq, @src, 0, @yid, @qty, @yhq, @at)", conn);
    cmd.Parameters.AddWithValue("@seq", attemptSeq);
    cmd.Parameters.AddWithValue("@src", (long)sourceItemId);
    cmd.Parameters.AddWithValue("@yid", (long)yieldItemId);
    cmd.Parameters.AddWithValue("@qty", qty);
    cmd.Parameters.AddWithValue("@yhq", yieldIsHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@at", capturedAt);
    cmd.ExecuteNonQuery();
  }

  private static void LocalPrice(SqliteConnection conn, uint itemId, long price, bool isHq = false)
  {
    using var cmd = new SqliteCommand(
      "INSERT INTO last_sale_prices (item_id, is_hq, unit_price, timestamp) VALUES (@i, @h, @p, 1)", conn);
    cmd.Parameters.AddWithValue("@i", (long)itemId);
    cmd.Parameters.AddWithValue("@h", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@p", price);
    cmd.ExecuteNonQuery();
  }

  private static long YieldValueOf(SqliteConnection conn, uint sourceItemId)
  {
    var store = new Scrooge.DesynthYieldStore(conn);
    foreach (var s in store.ReadSourceSummary(0))
      if (s.SourceItemId == sourceItemId)
        return s.YieldValue;
    return -1;
  }

  private static List<string> Columns(SqliteConnection conn, string table)
  {
    var cols = new List<string>();
    using var cmd = new SqliteCommand($"PRAGMA table_info({table});", conn);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      cols.Add(reader.GetString(1));
    return cols;
  }

  // ========================================================================
  // V27: the table
  // ========================================================================

  [Fact]
  public void ApplyV27_CreatesTheCommunityPriceTable()
  {
    using var conn = OpenTempDb();

    var cols = Columns(conn, "community_mat_prices");
    Assert.Contains("item_id", cols);
    Assert.Contains("is_hq", cols);      // mirrors last_sale_prices' key exactly
    Assert.Contains("unit_price", cols);
    Assert.Contains("fetched_at", cols); // the staleness gate's operand
  }

  [Fact]
  public void ApplyV27_IsIdempotent()
  {
    using var conn = OpenTempDb();
    var ex = Record.Exception(() => Scrooge.MeltYieldPrices.ApplyV27(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void ApplyV27_OnALegacyDbWithRowsAlreadyPresent_LeavesThemAlone()
  {
    using var conn = OpenTempDb();
    Scrooge.MeltYieldPrices.UpsertPrice(conn, 8144, false, 4200, 500);

    Scrooge.MeltYieldPrices.ApplyV27(conn);

    using var read = new SqliteCommand(
      "SELECT unit_price, fetched_at FROM community_mat_prices WHERE item_id = 8144", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal(4200L, reader.GetInt64(0));
    Assert.Equal(500L, reader.GetInt64(1));
  }

  [Fact]
  public void UpsertPrice_RefreshesAnExistingWeightRatherThanDuplicatingIt()
  {
    using var conn = OpenTempDb();
    Scrooge.MeltYieldPrices.UpsertPrice(conn, 8144, false, 4200, 500);
    Scrooge.MeltYieldPrices.UpsertPrice(conn, 8144, false, 5100, 900);

    using var read = new SqliteCommand(
      "SELECT COUNT(*), MAX(unit_price), MAX(fetched_at) FROM community_mat_prices WHERE item_id = 8144", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal(1L, reader.GetInt64(0));
    Assert.Equal(5100L, reader.GetInt64(1));
    Assert.Equal(900L, reader.GetInt64(2));
  }

  // ========================================================================
  // The valuation ladder: local -> community -> zero
  // ========================================================================

  [Fact]
  public void Valuation_LocalPriceWins_TheCommunityWeightIsAFallbackNotAnOverride()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 100, yieldItemId: 8144);
    LocalPrice(conn, 8144, 7000);
    Scrooge.MeltYieldPrices.UpsertPrice(conn, 8144, false, 4200, 500);

    Assert.Equal(7000L, YieldValueOf(conn, 100));
  }

  [Fact]
  public void Valuation_WithNoLocalPrice_TheCommunityWeightCarriesTheYield()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 100, yieldItemId: 8144, qty: 3);
    Scrooge.MeltYieldPrices.UpsertPrice(conn, 8144, false, 4200, 500);

    // This is the whole fix: three demimateria used to weigh 0.
    Assert.Equal(12600L, YieldValueOf(conn, 100));
  }

  [Fact]
  public void Valuation_WithNeitherPrice_StillReadsZero()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 100, yieldItemId: 8144);

    Assert.Equal(0L, YieldValueOf(conn, 100));
  }

  [Fact]
  public void Valuation_TheCommunityWeightIsQualitySplit_LikeTheLocalOne()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 100, yieldItemId: 8146, yieldIsHq: true);
    // An NQ weight must not price an HQ yield.
    Scrooge.MeltYieldPrices.UpsertPrice(conn, 8146, false, 4200, 500);
    Assert.Equal(0L, YieldValueOf(conn, 100));

    Scrooge.MeltYieldPrices.UpsertPrice(conn, 8146, true, 9000, 500);
    Assert.Equal(9000L, YieldValueOf(conn, 100));
  }

  [Fact]
  public void Valuation_CrystalsKeepWeighingNothing_ByRulingNotByAccident()
  {
    using var conn = OpenTempDb();
    // Fire Crystal (8) alongside a demimateria (8147): the crystal is never
    // fetched, so it never gets a row, so only the demimateria's weight lands.
    Melted(conn, sourceItemId: 100, yieldItemId: 8, qty: 5);
    Melted(conn, sourceItemId: 100, yieldItemId: 8147, attemptSeq: 1);
    Scrooge.MeltYieldPrices.UpsertPrice(conn, 8147, false, 3000, 500);

    Assert.Equal(3000L, YieldValueOf(conn, 100));
  }

  // ========================================================================
  // The worklist: who gets asked, and who never does
  // ========================================================================

  [Fact]
  public void Worklist_PicksTheUnpricedYieldMaterials()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 100, yieldItemId: 8144);
    Melted(conn, sourceItemId: 101, yieldItemId: 8146, attemptSeq: 2);

    var wanted = Scrooge.MeltYieldPrices.MaterialsNeedingPrice(conn, 100_000);

    Assert.Equal(new List<(uint, bool)> { (8144u, false), (8146u, false) }, wanted);
  }

  [Fact]
  public void Worklist_SkipsMaterialsOurOwnBoardsAlreadyPriced()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 100, yieldItemId: 8144);
    LocalPrice(conn, 8144, 7000);

    Assert.Empty(Scrooge.MeltYieldPrices.MaterialsNeedingPrice(conn, 100_000));
  }

  [Fact]
  public void Worklist_NeverAsksAboutElementalCatalysts_TheDeliberateZero()
  {
    using var conn = OpenTempDb();
    // The whole 2-19 block: shards, crystals, clusters. Drift's ruling - "they
    // have uses but effectively are 0 gil" - is enforced by never fetching.
    for (uint id = 2; id <= 19; id++)
      Melted(conn, sourceItemId: 100, yieldItemId: id, attemptSeq: (int)id);
    Melted(conn, sourceItemId: 100, yieldItemId: 8144, attemptSeq: 99);

    var wanted = Scrooge.MeltYieldPrices.MaterialsNeedingPrice(conn, 100_000);

    Assert.Equal(new List<(uint, bool)> { (8144u, false) }, wanted);
  }

  [Fact]
  public void Worklist_TheQualitySidesAreAskedSeparately()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 100, yieldItemId: 8144, yieldIsHq: false);
    Melted(conn, sourceItemId: 100, yieldItemId: 8144, yieldIsHq: true, attemptSeq: 2);
    LocalPrice(conn, 8144, 7000, isHq: false);

    // Our board priced the NQ side only; the HQ side is still unweighted.
    Assert.Equal(new List<(uint, bool)> { (8144u, true) },
      Scrooge.MeltYieldPrices.MaterialsNeedingPrice(conn, 100_000));
  }

  // ========================================================================
  // The staleness gate
  // ========================================================================

  [Fact]
  public void Worklist_AFreshCommunityWeightIsNotReasked()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 100, yieldItemId: 8144);

    var now = 100 * 86400L;
    Scrooge.MeltYieldPrices.UpsertPrice(conn, 8144, false, 4200, now - 86400); // yesterday

    Assert.Empty(Scrooge.MeltYieldPrices.MaterialsNeedingPrice(conn, now));
  }

  [Fact]
  public void Worklist_AnEightDayOldWeightIsReasked()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 100, yieldItemId: 8144);

    var now = 100 * 86400L;
    Scrooge.MeltYieldPrices.UpsertPrice(conn, 8144, false, 4200, now - 8 * 86400);

    Assert.Equal(new List<(uint, bool)> { (8144u, false) },
      Scrooge.MeltYieldPrices.MaterialsNeedingPrice(conn, now));
  }

  [Fact]
  public void Worklist_AStaleWeightStillPricesTheMeltWhileItWaitsForAnAnswer()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 100, yieldItemId: 8144);
    var now = 100 * 86400L;
    Scrooge.MeltYieldPrices.UpsertPrice(conn, 8144, false, 4200, now - 30 * 86400);

    // Re-queued, yes - but a month-old weight still beats a zero on the scale.
    Assert.Equal(4200L, YieldValueOf(conn, 100));
  }

  // ========================================================================
  // The statistic
  // ========================================================================

  [Fact]
  public void MedianPrice_OfAnOddRun_IsTheMiddleSale()
  {
    Assert.Equal(300L, Scrooge.MeltYieldPrices.MedianPrice([500, 100, 300]));
  }

  [Fact]
  public void MedianPrice_OfAnEvenRun_SplitsTheMiddlePair()
  {
    Assert.Equal(250L, Scrooge.MeltYieldPrices.MedianPrice([100, 200, 300, 400]));
  }

  [Fact]
  public void MedianPrice_ShrugsOffOneCrazySettle()
  {
    // The reason this is not "last sale": one 9,000,000 gil outlier would
    // otherwise become the material's standing weight on the melt scale.
    Assert.Equal(4200L, Scrooge.MeltYieldPrices.MedianPrice([4000, 4100, 4200, 4300, 9_000_000]));
  }

  [Fact]
  public void MedianPrice_OfNoSalesIsNull_NeverAFabricatedZero()
  {
    // A zero here would be indistinguishable from the catalysts' ruling.
    Assert.Null(Scrooge.MeltYieldPrices.MedianPrice([]));
  }

  [Fact]
  public void IsElementalCatalyst_CoversTheWholeShardToClusterBlockAndNothingElse()
  {
    Assert.False(Scrooge.MeltYieldPrices.IsElementalCatalyst(1));  // gil
    for (uint id = 2; id <= 19; id++)
      Assert.True(Scrooge.MeltYieldPrices.IsElementalCatalyst(id));
    Assert.False(Scrooge.MeltYieldPrices.IsElementalCatalyst(20));
    Assert.False(Scrooge.MeltYieldPrices.IsElementalCatalyst(8144)); // demimateria
  }

  // ========================================================================
  // V28: THE FLOOR RUNG - vendor price (07-26)
  // ========================================================================
  //
  // Drift's receipt: Clear Demimateria III vendor-sells at 5,000 gil each - his Always
  // Vendor rule collected 20,000 of it in one round - and the melt scale weighed it at
  // ZERO. Vendor gil is gil the melt provably realizes, and it is static sheet data
  // (Item.PriceLow). So realizable yield value is MAX(market evidence, vendor), not a
  // third fallback below the other two.

  private static void VendorPrice(SqliteConnection conn, uint itemId, long price)
    => Scrooge.MeltYieldPrices.UpsertVendorPrice(conn, itemId, price, 1);

  private static void CommunityPrice(SqliteConnection conn, uint itemId, long price, bool isHq = false)
    => Scrooge.MeltYieldPrices.UpsertPrice(conn, itemId, isHq, price, 1);

  [Fact]
  public void ApplyV28_CreatesTheVendorPriceTable()
  {
    using var conn = OpenTempDb();

    var cols = Columns(conn, "vendor_mat_prices");
    Assert.Contains("item_id", cols);
    Assert.Contains("unit_price", cols);
    Assert.Contains("filled_at", cols);
    // Keyed by ITEM alone - the counter has one PriceLow per item, unlike a market.
    Assert.DoesNotContain("is_hq", cols);
  }

  [Fact]
  public void ApplyV28_IsIdempotent()
  {
    using var conn = OpenTempDb();
    var ex = Record.Exception(() => Scrooge.MeltYieldPrices.ApplyV28(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void TheDemimateriaReceipt_AYieldWithNoMarketAtAll_NowWeighsItsCounterPrice()
  {
    using var conn = OpenTempDb();
    const uint demimateria = 5670;
    Melted(conn, sourceItemId: 9001, yieldItemId: demimateria);

    Assert.Equal(0, YieldValueOf(conn, 9001)); // the hole, before the floor

    VendorPrice(conn, demimateria, 5_000);

    Assert.Equal(5_000, YieldValueOf(conn, 9001));
  }

  [Fact]
  public void MarketEvidenceBeatsTheVendorFloorWhenItIsBetter()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 9001, yieldItemId: 5670);
    LocalPrice(conn, 5670, 12_000);
    VendorPrice(conn, 5670, 5_000);

    Assert.Equal(12_000, YieldValueOf(conn, 9001));
  }

  [Fact]
  public void TheVendorFloorBeatsMarketEvidenceThatIsWorse()
  {
    // The floor is a FLOOR, not a fallback: a mat that last sold for 900 on our board
    // is still worth 5,000 to the melt, because the counter will pay that today.
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 9001, yieldItemId: 5670);
    LocalPrice(conn, 5670, 900);
    VendorPrice(conn, 5670, 5_000);

    Assert.Equal(5_000, YieldValueOf(conn, 9001));
  }

  [Fact]
  public void TheFloorLiftsACommunityReadToo_NotJustALocalOne()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 9001, yieldItemId: 5670);
    CommunityPrice(conn, 5670, 1_200);
    VendorPrice(conn, 5670, 5_000);

    Assert.Equal(5_000, YieldValueOf(conn, 9001));
  }

  [Fact]
  public void LocalStillOutranksCommunityUnderneathTheFloor()
  {
    // The floor did not flatten the ladder it sits under.
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 9001, yieldItemId: 5670);
    LocalPrice(conn, 5670, 8_000);
    CommunityPrice(conn, 5670, 20_000); // must NOT win - local is our own board
    VendorPrice(conn, 5670, 5_000);

    Assert.Equal(8_000, YieldValueOf(conn, 9001));
  }

  [Fact]
  public void NeitherMarketNorCounter_StillReadsZero()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 9001, yieldItemId: 5670);

    Assert.Equal(0, YieldValueOf(conn, 9001));
  }

  [Fact]
  public void TheFloorScalesWithQuantity()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 9001, yieldItemId: 5670, qty: 4);
    VendorPrice(conn, 5670, 5_000);

    Assert.Equal(20_000, YieldValueOf(conn, 9001)); // Drift's round, exactly
  }

  [Fact]
  public void OneItemsFloorAppliesToBothQualities()
  {
    // The counter has one PriceLow per item. The HQ side's floor is conservative
    // (HQ really fetches more), which is the right direction for a floor to be wrong.
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 9001, yieldItemId: 5670, yieldIsHq: true);
    VendorPrice(conn, 5670, 5_000);

    Assert.Equal(5_000, YieldValueOf(conn, 9001));
  }

  // ---- Drift's crystals-stay-zero ruling, still standing ----

  [Fact]
  public void ElementalCatalysts_AreNeverOfferedAVendorPrice()
  {
    using var conn = OpenTempDb();
    for (uint id = Scrooge.MeltYieldPrices.CatalystFirstItemId;
         id <= Scrooge.MeltYieldPrices.CatalystLastItemId; id++)
      Melted(conn, sourceItemId: 9001, yieldItemId: id, attemptSeq: (int)id);

    var wanted = Scrooge.MeltYieldPrices.MaterialsNeedingVendorPrice(conn);

    Assert.Empty(wanted);
  }

  [Fact]
  public void CrystalsStayZero_EvenWithTheFloorInPlace()
  {
    // The ruling survives twice over: catalysts are excluded from the worklist, and
    // their PriceLow is 0 anyway - so no row can be written for them by the fill.
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 9001, yieldItemId: 8, qty: 99);           // Fire Crystal
    Melted(conn, sourceItemId: 9001, yieldItemId: 19, qty: 99, attemptSeq: 2); // Water Cluster

    Assert.Equal(0, YieldValueOf(conn, 9001));
  }

  [Fact]
  public void ANonCatalystYieldWithoutAFloor_IsStillOnTheWorklist()
  {
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 9001, yieldItemId: 5670);

    Assert.Equal(new List<uint> { 5670 }, Scrooge.MeltYieldPrices.MaterialsNeedingVendorPrice(conn));
  }

  [Fact]
  public void AMaterialAlreadyFloored_DropsOffTheWorklist()
  {
    // No staleness clause: PriceLow is static sheet data, so there is nothing to re-ask.
    using var conn = OpenTempDb();
    Melted(conn, sourceItemId: 9001, yieldItemId: 5670);
    VendorPrice(conn, 5670, 5_000);

    Assert.Empty(Scrooge.MeltYieldPrices.MaterialsNeedingVendorPrice(conn));
  }

  [Fact]
  public void UpsertVendorPrice_RefreshesRatherThanDuplicating()
  {
    using var conn = OpenTempDb();
    VendorPrice(conn, 5670, 5_000);
    Scrooge.MeltYieldPrices.UpsertVendorPrice(conn, 5670, 6_000, 900);

    using var read = new SqliteCommand(
      "SELECT COUNT(*), MAX(unit_price) FROM vendor_mat_prices WHERE item_id = 5670", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal(1, reader.GetInt32(0));
    Assert.Equal(6_000L, reader.GetInt64(1));
  }
}
