using Microsoft.Data.Sqlite;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE WITNESS THE VALUE COULD NOT NAME (ruled 08-22). Two contracts: the SQL's
/// per-rung attribution (against a real in-memory database, the same four tables
/// the live query joins), and the pure voice over the sums - the mark follows the
/// dominant rung, ties keep the stronger witness, and the hover admits a mix.
/// </summary>
public class DesynthWitnessTests
{
  // --- The pure voice ---

  [Fact]
  public void Dominant_TiesKeepTheStrongerWitness()
  {
    Assert.Equal(DesynthWitness.Rung.Own, DesynthWitness.Dominant(500, 500, 500));
    Assert.Equal(DesynthWitness.Rung.Community, DesynthWitness.Dominant(0, 500, 500));
    Assert.Equal(DesynthWitness.Rung.Vendor, DesynthWitness.Dominant(0, 0, 500));
    Assert.Equal(DesynthWitness.Rung.None, DesynthWitness.Dominant(0, 0, 0));
  }

  [Fact]
  public void Mark_SpeaksTheHouseGrammar()
  {
    // Bare = your sale, "~" = DC, "(vendor)" = the floor - the same marks V7 put
    // on the desynth values, so one grammar prices the whole tab.
    Assert.Equal("1,246", DesynthWitness.Mark(1_246, own: 900, community: 300, vendor: 0));
    Assert.Equal("~1,246", DesynthWitness.Mark(1_246, own: 0, community: 1_200, vendor: 46));
    Assert.Equal("1,246 (vendor)", DesynthWitness.Mark(1_246, own: 0, community: 100, vendor: 1_146));
  }

  [Fact]
  public void Hover_NamesEveryRungThatContributed()
  {
    var mixed = DesynthWitness.Hover(own: 7_200, community: 1_500, vendor: 300);
    Assert.Contains("Your own sales priced 7,200 gil", mixed); // first clause capitalized
    Assert.Contains("~DC sales priced 1,500 gil", mixed);
    Assert.Contains("vendor floor priced 300 gil", mixed);
    Assert.Equal("Your own sales priced 7,200 gil of it.", DesynthWitness.Hover(7_200, 0, 0));
    Assert.Equal("No witness has priced these yields yet.", DesynthWitness.Hover(0, 0, 0));
  }

  // --- The SQL attribution ---

  private static SqliteConnection OpenDb()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    Exec(conn, @"CREATE TABLE desynth_yields (
        id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INTEGER NOT NULL,
        attempt_seq INTEGER NOT NULL, source_item_id INTEGER NOT NULL,
        source_is_hq INTEGER NOT NULL DEFAULT 0, yield_item_id INTEGER NOT NULL,
        yield_qty INTEGER NOT NULL, yield_is_hq INTEGER NOT NULL DEFAULT 0,
        captured_at INTEGER NOT NULL);
      CREATE TABLE last_sale_prices (item_id INTEGER, is_hq INTEGER, unit_price INTEGER);
      CREATE TABLE community_mat_prices (item_id INTEGER, is_hq INTEGER, unit_price INTEGER);
      CREATE TABLE vendor_mat_prices (item_id INTEGER, unit_price INTEGER);");
    return conn;
  }

  private static void Exec(SqliteConnection conn, string sql)
  {
    using var cmd = new SqliteCommand(sql, conn);
    cmd.ExecuteNonQuery();
  }

  private static void Yield(SqliteConnection conn, uint source, uint mat, int qty, int seq = 1)
    => Exec(conn, $"INSERT INTO desynth_yields (run_id, attempt_seq, source_item_id, yield_item_id, yield_qty, captured_at) VALUES (1, {seq}, {source}, {mat}, {qty}, 1000)");

  [Fact]
  public void ReadSourceSummary_AttributesEachYieldToItsWinningRung()
  {
    using var conn = OpenDb();
    // Mat 11: own book says 900 (beats no vendor row) -> own rung, 2x900.
    // Mat 12: no own row, DC says 300, vendor 50 -> community rung, 1x300.
    // Mat 13: own 100 but the counter pays 400 -> vendor rung wins the MAX, 1x400.
    Exec(conn, "INSERT INTO last_sale_prices VALUES (11, 0, 900), (13, 0, 100)");
    Exec(conn, "INSERT INTO community_mat_prices VALUES (12, 0, 300)");
    Exec(conn, "INSERT INTO vendor_mat_prices VALUES (12, 50), (13, 400)");
    Yield(conn, source: 5, mat: 11, qty: 2);
    Yield(conn, source: 5, mat: 12, qty: 1);
    Yield(conn, source: 5, mat: 13, qty: 1);

    var row = Assert.Single(new DesynthYieldStore(conn).ReadSourceSummary(0));
    Assert.Equal(1_800 + 300 + 400, row.YieldValue);
    Assert.Equal(1_800, row.OwnValue);
    Assert.Equal(300, row.CommunityValue);
    Assert.Equal(400, row.VendorValue);
    // The three rungs partition the value - nothing counted twice, nothing dropped.
    Assert.Equal(row.YieldValue, row.OwnValue + row.CommunityValue + row.VendorValue);
  }

  [Fact]
  public void ReadSourceSummary_UnpricedYieldJoinsNoRung()
  {
    using var conn = OpenDb();
    Yield(conn, source: 5, mat: 99, qty: 3); // no price row anywhere
    var row = Assert.Single(new DesynthYieldStore(conn).ReadSourceSummary(0));
    Assert.Equal(0, row.YieldValue);
    Assert.Equal(DesynthWitness.Rung.None,
      DesynthWitness.Dominant(row.OwnValue, row.CommunityValue, row.VendorValue));
  }
}
