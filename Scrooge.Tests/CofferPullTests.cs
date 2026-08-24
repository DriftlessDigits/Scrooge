using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE SEALS' OTHER EXIT (V43, 2026-08-15).
///
/// <para>Grand Company seals leave two ways: venture tokens (V42 measured those) and
/// the quartermaster's 20k-seal Materiel Container 3.0/4.0, which returns a random
/// marketable mount or minion. The sale of the pull was always visible; the trade that
/// bought it never was. V43 gives it a table and a passive watcher.</para>
///
/// <para>Ruled 08-15 (Drift): <b>let the data bake.</b> 3.0 gathers, 3.1 acts. So what is
/// pinned here is not a valuation - there isn't one - it is the shape of the book and
/// the one rule that keeps junk out of it: a bag count the game didn't answer is NOT a
/// box being opened. A zone change reads N -> unreadable -> N, and if "unreadable"
/// collapsed to 0 or to silence, every loading screen would bank a pull that never
/// happened.</para>
/// </summary>
public class CofferPullTests
{
  // --- the arm/baseline decision ---

  [Fact]
  public void ACountThatFalls_Arms()
  {
    // A box left the bags. This - and only this - opens a capture window.
    Assert.Equal(CofferCountMove.Arm, CofferPullPairing.Decide(baseline: 3, reading: 2));
    Assert.Equal(CofferCountMove.Arm, CofferPullPairing.Decide(baseline: 1, reading: 0));
  }

  [Fact]
  public void ACountThatRises_JustRebaselines()
  {
    // Buying boxes at the quartermaster is not evidence about any pull.
    Assert.Equal(CofferCountMove.Rebaseline, CofferPullPairing.Decide(baseline: 0, reading: 5));
    Assert.Equal(CofferCountMove.Rebaseline, CofferPullPairing.Decide(baseline: 2, reading: 3));
  }

  [Fact]
  public void AnUnchangedCount_DoesNothing()
    => Assert.Equal(CofferCountMove.Nothing, CofferPullPairing.Decide(baseline: 4, reading: 4));

  [Fact]
  public void AnUnreadableBag_DropsTheBaselineAndNeverArms()
  {
    // Zoning, login, a null InventoryManager. "No answer" is not "zero boxes".
    Assert.Equal(CofferCountMove.Forget, CofferPullPairing.Decide(baseline: 3, reading: null));
    Assert.Equal(CofferCountMove.Forget, CofferPullPairing.Decide(baseline: null, reading: null));
  }

  [Fact]
  public void TheFirstReadableCount_BaselinesWithoutArming()
  {
    // Login lands here: there is nothing to compare against yet, so nothing fires.
    Assert.Equal(CofferCountMove.Rebaseline, CofferPullPairing.Decide(baseline: null, reading: 3));
    Assert.Equal(CofferCountMove.Rebaseline, CofferPullPairing.Decide(baseline: null, reading: 0));
  }

  [Fact]
  public void AZoneChange_CannotFireAPhantomPull()
  {
    // The whole reason this decision is a function: 3 -> unreadable -> 3 is one
    // loading screen, not a box. Walk it the way the poll walks it.
    int? baseline = 3;

    var move = CofferPullPairing.Decide(baseline, reading: null);
    Assert.Equal(CofferCountMove.Forget, move);
    baseline = null;

    move = CofferPullPairing.Decide(baseline, reading: 3);
    Assert.Equal(CofferCountMove.Rebaseline, move);
    baseline = 3;

    // And on the far side, the watcher is armed-capable again for a real open.
    Assert.Equal(CofferCountMove.Arm, CofferPullPairing.Decide(baseline, reading: 2));
  }

  [Fact]
  public void AZoneChangeStraightIntoALowerCount_StillDoesNotArm()
  {
    // Boxes used on another character, or a stack moved to the saddlebag while the
    // bags were unreadable: the baseline is gone, so the low reading is just a new
    // baseline. We would rather miss a pull than invent one.
    Assert.Equal(CofferCountMove.Forget, CofferPullPairing.Decide(baseline: 5, reading: null));
    Assert.Equal(CofferCountMove.Rebaseline, CofferPullPairing.Decide(baseline: null, reading: 1));
  }

  // --- the table ---

  private static SqliteConnection OpenBare()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    return conn;
  }

  private static HashSet<string> Columns(SqliteConnection conn)
  {
    var cols = new HashSet<string>();
    using var info = new SqliteCommand("PRAGMA table_info(coffer_pulls);", conn);
    using var reader = info.ExecuteReader();
    while (reader.Read()) cols.Add(reader.GetString(1));
    return cols;
  }

  [Fact]
  public void ApplyV43_CreatesTheTableWithTheCaptureItBanks()
  {
    using var conn = OpenBare();
    CofferPullSchema.ApplyV43(conn);

    var cols = Columns(conn);
    Assert.Contains("id", cols);
    Assert.Contains("opened_at", cols);
    Assert.Contains("container_item_id", cols);
    Assert.Contains("pulled_item_id", cols);
    Assert.Contains("quantity", cols);
    Assert.Contains("is_hq", cols);
  }

  [Fact]
  public void ApplyV43_IsIdempotent()
  {
    // The migration re-runs on any DB that never stamped 43. Twice must be once.
    using var conn = OpenBare();
    CofferPullSchema.ApplyV43(conn);
    Assert.Null(Record.Exception(() => CofferPullSchema.ApplyV43(conn)));
  }

  [Fact]
  public void ApplyV43_RunsOnABareDbWithNothingElseInIt()
  {
    // Unlike the V42 column widening, this table depends on no prior table - a fresh
    // install's bootstrap reaches it with the same result as a five-year-old book's.
    using var conn = OpenBare();
    CofferPullSchema.ApplyV43(conn);

    using var count = new SqliteCommand(
      "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'coffer_pulls';", conn);
    Assert.Equal(1L, (long)count.ExecuteScalar()!);
  }

  [Fact]
  public void ApplyV43_IndexesTheWindowTheReaderWillAskFor()
  {
    using var conn = OpenBare();
    CofferPullSchema.ApplyV43(conn);

    using var idx = new SqliteCommand(
      "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_coffer_pulls_opened';", conn);
    Assert.Equal(1L, (long)idx.ExecuteScalar()!);
  }

  [Fact]
  public void ApplyV43_DefaultsQualityToNq()
  {
    // is_hq is the one column the writer may omit shape-wise; a NOT NULL with no
    // default would make a partial insert throw instead of banking an NQ pull.
    using var conn = OpenBare();
    CofferPullSchema.ApplyV43(conn);

    using (var insert = new SqliteCommand(
      "INSERT INTO coffer_pulls (opened_at, container_item_id, pulled_item_id, quantity) " +
      "VALUES (1000, 111, 222, 1);", conn))
      insert.ExecuteNonQuery();

    using var read = new SqliteCommand("SELECT is_hq FROM coffer_pulls;", conn);
    Assert.Equal(0L, (long)read.ExecuteScalar()!);
  }
}
