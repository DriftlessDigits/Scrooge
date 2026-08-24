using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE KNOB'S REPLACEMENT (V42, 2026-08-15).
///
/// <para>The seals-to-gil rate used to divide gil-per-venture by a config value that
/// declared every venture costs 2 tokens - "VERIFY in-game; believed 2", shipped as a
/// setting. Drift ruled it dead: <b>we don't need a mod knob for a thing we can directly
/// measure in game.</b> The capture now stamps each row with the venture it ran and
/// that venture's sheet cost, and the rate is a real fraction over real spend.</para>
///
/// <para>What's pinned here is the pair of rules that make the replacement honest: the
/// category the stamp records, and the refusal to let unstamped history near the
/// arithmetic. A pre-V42 row has no declared cost, so counting its gil would inflate
/// the rate by exactly the history we refuse to guess at.</para>
/// </summary>
public class VentureStampTests
{
  private static List<(long Value, int? VentureCost)> Rows(params (long Value, int? Cost)[] rows)
    => [.. rows];

  /// <summary>Ten stamped rows at 20k each, 2 tokens each: the shape the gate opens on.</summary>
  private static List<(long Value, int? VentureCost)> TenStamped(long value = 20_000, int cost = 2)
  {
    var rows = new List<(long, int?)>();
    for (var i = 0; i < 10; i++) rows.Add((value, cost));
    return rows;
  }

  // --- the category the stamp records ---

  [Fact]
  public void QuickExploration_IsQuick()
    => Assert.Equal("quick", VentureStamp.Category(isRandom: true, "Quick Exploration"));

  [Theory]
  [InlineData("Field Exploration XIV", "field")]
  [InlineData("Highland Exploration VII", "highland")]
  [InlineData("Woodland Exploration III", "woodland")]
  [InlineData("Waterside Exploration XI", "waterside")]
  public void AnExploration_IsItsTerrain(string name, string expected)
    => Assert.Equal(expected, VentureStamp.Category(isRandom: true, name));

  [Fact]
  public void ANonRandomVenture_IsTargeted()
  {
    // Item hunts don't have a RetainerTaskRandom row at all, so the name is moot.
    Assert.Equal("targeted", VentureStamp.Category(isRandom: false, null));
    Assert.Equal("targeted", VentureStamp.Category(isRandom: false, "Quick Exploration"));
  }

  [Fact]
  public void ARandomVentureWithNoName_StampsNothing()
  {
    // The sheet lookup failed. NULL is the honest answer; nothing downstream fills it.
    Assert.Null(VentureStamp.Category(isRandom: true, null));
    Assert.Null(VentureStamp.Category(isRandom: true, "   "));
  }

  // --- the stamped-only arithmetic ---

  [Fact]
  public void TenStampedVentures_Measure()
  {
    // 200,000 gil over 20 tokens = 4,000 seals.
    Assert.Equal(50, VentureStamp.StampedSealToGilRate(TenStamped()));
  }

  [Fact]
  public void UnstampedRows_AreExcludedWholeSale()
  {
    // The pre-V42 rows carry gil and no declared cost. If their value counted, the
    // rate would double here - which is precisely the guess the knob died for.
    var mixed = TenStamped();
    for (var i = 0; i < 10; i++) mixed.Add((20_000, null));
    Assert.Equal(50, VentureStamp.StampedSealToGilRate(mixed));
  }

  [Fact]
  public void NineStamped_IsNotEnoughEvenWithAPileOfHistoryBehindIt()
  {
    // The gate counts STAMPED rows. A book full of type-blind history doesn't buy
    // the sample; it just isn't evidence about token cost.
    var rows = Rows((20_000, 2), (20_000, 2), (20_000, 2), (20_000, 2), (20_000, 2),
                    (20_000, 2), (20_000, 2), (20_000, 2), (20_000, 2));
    for (var i = 0; i < 50; i++) rows.Add((20_000, null));
    Assert.Null(VentureStamp.StampedSealToGilRate(rows));
  }

  [Fact]
  public void ZeroTotalCost_IsNotARate()
  {
    // Free ventures shouldn't exist, but a sheet read of 0 must not become a divide.
    Assert.Null(VentureStamp.StampedSealToGilRate(TenStamped(cost: 0)));
  }

  [Fact]
  public void AValueTooSmallToBuyOneGilPerSeal_ReportsNothing()
  {
    // Truncation to 0 is not a measurement of "seals are worthless".
    Assert.Null(VentureStamp.StampedSealToGilRate(TenStamped(value: 100)));
  }

  [Fact]
  public void NoRowsAtAll_IsNull()
    => Assert.Null(VentureStamp.StampedSealToGilRate(Rows()));

  // --- the column widening ---

  private static SqliteConnection OpenWithVentureReturns()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    // The V15 birth shape, verbatim - what an existing player's table looks like.
    using var cmd = new SqliteCommand(
      @"CREATE TABLE venture_returns (
          id            INTEGER PRIMARY KEY AUTOINCREMENT,
          captured_at   INTEGER NOT NULL,
          retainer_name TEXT NOT NULL,
          item_id       INTEGER NOT NULL,
          quantity      INTEGER NOT NULL,
          is_hq         INTEGER NOT NULL DEFAULT 0
        );", conn);
    cmd.ExecuteNonQuery();
    return conn;
  }

  private static HashSet<string> Columns(SqliteConnection conn)
  {
    var cols = new HashSet<string>();
    using var info = new SqliteCommand("PRAGMA table_info(venture_returns);", conn);
    using var reader = info.ExecuteReader();
    while (reader.Read()) cols.Add(reader.GetString(1));
    return cols;
  }

  [Fact]
  public void ApplyV42_AddsTheThreeStampColumns_AndIsIdempotent()
  {
    using var conn = OpenWithVentureReturns();
    VentureReturnsSchema.ApplyV42(conn);
    Assert.Null(Record.Exception(() => VentureReturnsSchema.ApplyV42(conn)));

    var cols = Columns(conn);
    Assert.Contains("venture_id", cols);
    Assert.Contains("venture_cost", cols);
    Assert.Contains("venture_category", cols);
  }

  [Fact]
  public void ApplyV42_LeavesLegacyRowsReadingNull()
  {
    using var conn = OpenWithVentureReturns();
    using (var seed = new SqliteCommand(
      "INSERT INTO venture_returns (captured_at, retainer_name, item_id, quantity, is_hq) " +
      "VALUES (1, 'Bender', 5, 1, 0);", conn))
      seed.ExecuteNonQuery();

    VentureReturnsSchema.ApplyV42(conn);

    using var read = new SqliteCommand(
      "SELECT venture_id, venture_cost, venture_category FROM venture_returns;", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.True(reader.IsDBNull(0));
    Assert.True(reader.IsDBNull(1));
    Assert.True(reader.IsDBNull(2));
  }

  [Fact]
  public void ApplyV42_SkipsABareDbWithNoVentureTable()
  {
    using var bare = new SqliteConnection("Data Source=:memory:");
    bare.Open();
    Assert.Null(Record.Exception(() => VentureReturnsSchema.ApplyV42(bare)));
  }
}
