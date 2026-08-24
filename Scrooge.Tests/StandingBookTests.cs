using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE STANDING BOOK (WALK unit 6). The contract: the board's live value is the
/// last ground-truth read plus our own write-side acts since, minus what sold -
/// and the resync is STRUCTURAL, meaning the next pinch's baseline retires every
/// earlier write by moving the window, with no procedure to run.
/// </summary>
public class StandingBookTests
{
  private const long Scan = 1_700_000_000; // the baseline read
  private static OwnWrite Listed(long price, int qty, long at = Scan + 60)
    => new(at, WriteKind.Listed, 1, false, "Ret", price, 0, qty, "hawk");
  private static OwnWrite Removed(long price, int qty, long at = Scan + 60)
    => new(at, WriteKind.Removed, 1, false, "Ret", price, 0, qty, "triage");
  private static OwnWrite Repriced(long now, long prior, int qty, long at = Scan + 60)
    => new(at, WriteKind.Repriced, 1, false, "Ret", now, prior, qty, "triage");

  // ========================================================================
  // The arithmetic
  // ========================================================================

  [Fact]
  public void Value_NoWritesIsJustTheBaseline()
  {
    Assert.Equal(1_000_000, StandingBook.Value(1_000_000, [], 0));
  }

  // ========================================================================
  // The COUNT (ruled 08-16 round walk): the roster a pinch would actually walk
  // ========================================================================

  [Fact]
  public void Count_NoWritesIsJustTheBaseline()
  {
    Assert.Equal(94, StandingBook.Count(94, [], 0));
  }

  [Fact]
  public void Count_OurOwnPostsSinceTheReadAreCounted()
  {
    // The 08-16 receipt: the scan said 93-ish, hawk had posted ~50 since, and the
    // pinch walked 145 while the estimate priced the stale count.
    var writes = new List<OwnWrite>();
    for (var i = 0; i < 51; i++) writes.Add(Listed(1_000, 1));
    Assert.Equal(145, StandingBook.Count(94, writes, 0));
  }

  [Fact]
  public void Count_PullsAndSalesLeaveTheRoster()
  {
    Assert.Equal(91, StandingBook.Count(94, [Removed(1_000, 1)], 2));
  }

  [Fact]
  public void Count_ARepriceMovesAnAskThatWasAlreadyStanding()
  {
    Assert.Equal(94, StandingBook.Count(94, [Repriced(900, 1_000, 1)], 0));
  }

  [Fact]
  public void Count_AStackIsOneSellListSlotWhateverItsQuantity()
  {
    Assert.Equal(95, StandingBook.Count(94, [Listed(110, 99)], 0));
  }

  [Fact]
  public void Count_ClampsAtZeroLikeTheValueDoes()
  {
    Assert.Equal(0, StandingBook.Count(1, [Removed(1_000, 1)], 5));
  }

  [Fact]
  public void Value_AHawkRunAddsWhatItPutUp()
  {
    // 20 items at 5,000 each, listed after the scan.
    var writes = new List<OwnWrite> { Listed(5_000, 20) };
    Assert.Equal(1_100_000, StandingBook.Value(1_000_000, writes, 0));
  }

  [Fact]
  public void Value_APullTakesItsAskBackOff()
  {
    var writes = new List<OwnWrite> { Removed(30_000, 2) };
    Assert.Equal(940_000, StandingBook.Value(1_000_000, writes, 0));
  }

  [Fact]
  public void Value_ARepriceMovesOnlyTheDelta()
  {
    // The listing was already standing and already in the baseline - only the
    // MOVE is news. Counting the new ask whole would book it twice.
    var writes = new List<OwnWrite> { Repriced(now: 12_000, prior: 10_000, qty: 3) };
    Assert.Equal(1_006_000, StandingBook.Value(1_000_000, writes, 0));

    var down = new List<OwnWrite> { Repriced(now: 8_000, prior: 10_000, qty: 3) };
    Assert.Equal(994_000, StandingBook.Value(1_000_000, down, 0));
  }

  [Fact]
  public void Value_SalesDeflateTheBook()
  {
    // Chat capture sees sales live, so the board's value falls as things clear
    // instead of waiting for the next pinch to notice they are gone.
    Assert.Equal(750_000, StandingBook.Value(1_000_000, [], 250_000));
  }

  [Fact]
  public void Value_MixedTraffic()
  {
    var writes = new List<OwnWrite>
    {
      Listed(5_000, 20),                              // +100,000
      Removed(30_000, 1),                             //  -30,000
      Repriced(now: 12_000, prior: 10_000, qty: 5),   //  +10,000
    };
    Assert.Equal(1_000_000 + 100_000 - 30_000 + 10_000 - 40_000,
      StandingBook.Value(1_000_000, writes, 40_000));
  }

  [Fact]
  public void Value_NeverGoesNegative()
  {
    // A book that has drifted below zero is wrong, and a negative "gil on the
    // board" is a worse lie than a zero.
    Assert.Equal(0, StandingBook.Value(1_000, [Removed(50_000, 1)], 0));
    Assert.Equal(0, StandingBook.Value(1_000, [], 999_999));
  }

  [Fact]
  public void Value_AQuantityOfZeroStillCountsOneUnit()
  {
    // Flag-born rows start with an unknown stack size; treating that as zero
    // would silently book a listing as worthless.
    Assert.Equal(1_005_000, StandingBook.Value(1_000_000, [Listed(5_000, 0)], 0));
  }

  // ========================================================================
  // The structural resync
  // ========================================================================

  [Fact]
  public void Value_TheWindowIsTheResync()
  {
    // The caller only ever hands over writes AFTER the baseline scan. Once a new
    // pinch moves that baseline, yesterday's writes are outside the window and
    // are already reflected in the new listings read - nothing has to prune them.
    var beforeNextScan = new List<OwnWrite> { Listed(5_000, 20) };
    var booked = StandingBook.Value(1_000_000, beforeNextScan, 0);
    Assert.Equal(1_100_000, booked);

    // Next pinch reads the board and sees the same 1,100,000 as ground truth. The
    // window is now empty, and the answer is unchanged - no double count.
    Assert.Equal(1_100_000, StandingBook.Value(1_100_000, [], 0));
  }

  // ========================================================================
  // "Gil put up today"
  // ========================================================================

  [Fact]
  public void PutUp_CountsOnlyNewListings()
  {
    var midnight = Scan;
    var writes = new List<OwnWrite>
    {
      Listed(5_000, 20, at: midnight + 100),                       // counts
      Removed(30_000, 1, at: midnight + 200),                      // a pull is not putting up
      Repriced(12_000, 10_000, 5, at: midnight + 300),             // a move is not putting up
    };
    Assert.Equal(100_000, StandingBook.PutUp(writes, midnight));
  }

  [Fact]
  public void PutUp_IgnoresYesterday()
  {
    var midnight = Scan;
    var writes = new List<OwnWrite>
    {
      Listed(5_000, 20, at: midnight - 100), // yesterday
      Listed(1_000, 3, at: midnight + 100),  // today
    };
    Assert.Equal(3_000, StandingBook.PutUp(writes, midnight));
  }

  // ========================================================================
  // Provenance - the header must never dress an inference as a measurement
  // ========================================================================

  [Fact]
  public void IsBookKept_OnlyWhenSomethingHappenedSinceTheRead()
  {
    Assert.False(StandingBook.IsBookKept(0, 0));
    Assert.True(StandingBook.IsBookKept(1, 0));
    Assert.True(StandingBook.IsBookKept(0, 5_000));
  }

  [Fact]
  public void Headline_ABookKeptNumberSaysSo()
  {
    var line = StandingBook.Headline(1_100_000, bookKept: true, groundTruthAgeSeconds: 3600);
    Assert.Contains("~1,100,000 at ask", line);
    Assert.Contains("plus our own moves since", line);
    Assert.Contains("last look", line);
    // The provenance words stay in the code (strings-three).
    Assert.DoesNotContain("book-kept", line);
    Assert.DoesNotContain("ground-truth", line);
  }

  [Fact]
  public void Headline_AnUntouchedBoardReadsAsGroundTruth()
  {
    // Nothing has happened since the scan, so the number IS the scan - it must
    // not hedge about book-keeping it did not do.
    var line = StandingBook.Headline(1_000_000, bookKept: false, groundTruthAgeSeconds: 3600);
    Assert.DoesNotContain("moves", line);
    Assert.DoesNotContain("~", line);
  }

  [Fact]
  public void Headline_NeverScannedSaysSoOutright()
  {
    var line = StandingBook.Headline(0, bookKept: false, groundTruthAgeSeconds: null);
    Assert.Contains("no look at the retainers yet", line);
  }

  // ========================================================================
  // V21 migration contract (the V11 model: diffable + idempotent)
  // ========================================================================

  private static SqliteConnection OpenTempDb()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    return conn;
  }

  [Fact]
  public void ApplyV21_CreatesTheWriteTable()
  {
    using var conn = OpenTempDb();
    StandingBookSchema.ApplyV21(conn);

    var cols = new List<string>();
    using (var cmd = new SqliteCommand("PRAGMA table_info(own_listing_writes);", conn))
    using (var reader = cmd.ExecuteReader())
      while (reader.Read()) cols.Add(reader.GetString(1));

    Assert.Contains("written_at", cols);
    Assert.Contains("kind", cols);
    Assert.Contains("unit_price", cols);
    Assert.Contains("prior_price", cols); // the reprice operand - books the move, not the listing
    Assert.Contains("quantity", cols);
  }

  [Fact]
  public void ApplyV21_IsIdempotent()
  {
    using var conn = OpenTempDb();
    StandingBookSchema.ApplyV21(conn);
    var ex = Record.Exception(() => StandingBookSchema.ApplyV21(conn));
    Assert.Null(ex);
  }

  // ========================================================================
  // The flush actually writes (07-25 regression)
  //
  // A Hawk run listed 23 items and own_listing_writes held ZERO rows, with no
  // exception anywhere: the insert bound every parameter and never executed the
  // command, so each flush committed an empty transaction. These run the real
  // DDL and the real insert against a real database and count the rows.
  // ========================================================================

  [Fact]
  public void InsertOwnWrites_ActuallyLandsEveryRow()
  {
    using var conn = OpenTempDb();
    StandingBookSchema.ApplyV21(conn);

    var batch = new List<OwnWrite> { Listed(1000, 2), Removed(500, 1), Repriced(900, 800, 3) };
    var inserted = StandingBookSchema.InsertOwnWrites(conn, null, batch);

    Assert.Equal(3, inserted); // the count is the guard - an empty commit reported 0
    using var cmd = new SqliteCommand("SELECT COUNT(*) FROM own_listing_writes;", conn);
    Assert.Equal(3L, (long)cmd.ExecuteScalar()!);
  }

  [Fact]
  public void InsertOwnWrites_RoundTripsEveryOperand()
  {
    using var conn = OpenTempDb();
    StandingBookSchema.ApplyV21(conn);
    StandingBookSchema.InsertOwnWrites(conn, null, [Repriced(900, 800, 3)]);

    using var cmd = new SqliteCommand(
      "SELECT kind, unit_price, prior_price, quantity, source FROM own_listing_writes;", conn);
    using var reader = cmd.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal("repriced", reader.GetString(0)); // the kind the read path parses back
    Assert.Equal(900L, reader.GetInt64(1));
    Assert.Equal(800L, reader.GetInt64(2)); // the reprice operand - the move, not the listing
    Assert.Equal(3, reader.GetInt32(3));
    Assert.Equal("triage", reader.GetString(4));
  }

  [Fact]
  public void InsertOwnWrites_EmptyBatchWritesNothing()
  {
    using var conn = OpenTempDb();
    StandingBookSchema.ApplyV21(conn);
    Assert.Equal(0, StandingBookSchema.InsertOwnWrites(conn, null, []));
  }
}
