using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// The V21 own-write DDL, extracted Dalamud-free so it is linked-source testable
/// (the migration idempotency contract runs this against a real temp SQLite DB) -
/// the V19/V20 model.
///
/// <para>own_listing_writes is the WRITE SIDE of our own board presence: one row
/// per listing we placed, pulled, or repriced, stamped when the run that did it
/// finished. The listings table remains the READ side and the ground truth - a
/// pinch walking every sell list - and this table is only ever the delta layered
/// on top of it until the next pinch.</para>
///
/// <para>APPEND-ONLY, AND NEVER PRUNED BY A PROCEDURE. Rows age out of relevance
/// on their own, because every read is windowed on the last full scan's timestamp:
/// once a pinch re-reads the board, every write older than it is simply outside
/// the window. Nothing has to remember to clean up, so nothing can forget to. Old
/// rows stay as the history of what we did and when, which is what makes "gil put
/// up today" fall out for free.</para>
///
/// Every statement is CREATE ... IF NOT EXISTS, so applying it twice is a no-op
/// (diffable + idempotent, the V11 model).
/// </summary>
internal static class StandingBookSchema
{
  /// <summary>Creates the own_listing_writes table and its indexes (idempotent).</summary>
  internal static void ApplyV21(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(
      @"CREATE TABLE IF NOT EXISTS own_listing_writes (
          id            INTEGER PRIMARY KEY AUTOINCREMENT,
          written_at    INTEGER NOT NULL,
          kind          TEXT NOT NULL,
          item_id       INTEGER NOT NULL,
          is_hq         INTEGER NOT NULL DEFAULT 0,
          retainer_name TEXT NOT NULL DEFAULT '',
          unit_price    INTEGER NOT NULL DEFAULT 0,
          -- Meaningful only for kind='repriced': the ask this listing moved FROM,
          -- so the book delta is the MOVE and not the whole listing counted twice.
          prior_price   INTEGER NOT NULL DEFAULT 0,
          quantity      INTEGER NOT NULL DEFAULT 1,
          -- Which executor did it ('hawk' / 'rider' / 'triage'), for reading the
          -- book's provenance back later. Never branched on.
          source        TEXT NOT NULL DEFAULT ''
        );
        -- The only read shape there is: everything written since the last full
        -- board scan. The window IS the resync, so this index is the hot path.
        CREATE INDEX IF NOT EXISTS ix_own_listing_writes_at
          ON own_listing_writes(written_at DESC);",
      connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Appends own-write rows inside an already-open transaction and returns HOW MANY
  /// the database actually took.
  ///
  /// <para>The count is the whole point of the signature. The first cut of this
  /// bound every parameter on every row and never executed a single command: a Hawk
  /// run's 23 listings built 23 INSERTs, disposed them, and committed an empty
  /// transaction. No exception, no row, and a Listed header that read baseline-only
  /// for the rest of the day (07-25). A writer that cannot say how many rows it
  /// wrote cannot be caught writing none - so it says.</para>
  ///
  /// <para>Lives here rather than in GilStorage so the round trip is testable: this
  /// file is Dalamud-free and linked into the test project, which runs the DDL and
  /// this insert against a real SQLite database.</para>
  /// </summary>
  internal static int InsertOwnWrites(
    SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyList<OwnWrite> writes)
  {
    var inserted = 0;
    foreach (var w in writes)
    {
      using var cmd = new SqliteCommand(
        @"INSERT INTO own_listing_writes
        (written_at, kind, item_id, is_hq, retainer_name, unit_price, prior_price, quantity, source)
        VALUES (@at, @kind, @item, @hq, @ret, @price, @prior, @qty, @src)",
        connection, transaction);
      cmd.Parameters.AddWithValue("@at", w.WrittenAt);
      cmd.Parameters.AddWithValue("@kind", w.Kind.ToString().ToLowerInvariant());
      cmd.Parameters.AddWithValue("@item", w.ItemId);
      cmd.Parameters.AddWithValue("@hq", w.IsHq ? 1 : 0);
      cmd.Parameters.AddWithValue("@ret", w.Retainer);
      cmd.Parameters.AddWithValue("@price", w.UnitPrice);
      cmd.Parameters.AddWithValue("@prior", w.PriorPrice);
      cmd.Parameters.AddWithValue("@qty", w.Quantity);
      cmd.Parameters.AddWithValue("@src", w.Source);
      inserted += cmd.ExecuteNonQuery();
    }
    return inserted;
  }
}
