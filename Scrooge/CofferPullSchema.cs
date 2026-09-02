using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// V43: coffer_pulls - the seals' OTHER exit, measured.
///
/// <para>Grand Company seals leave the wallet two ways worth modeling. V42 wired
/// the first (venture tokens, and what the ventures they buy return). The second is
/// the Materiel Container 3.0/4.0 box at the quartermaster: 20k seals for a random
/// mount or minion. What comes out is marketable, so its eventual sale already rides
/// the receipts pipeline - what has never existed is the other half of the trade,
/// the record that says which box was opened and what it gave up.</para>
///
/// <para>One row per opened box: when, which container, what came out, how much and
/// what quality. No cost column - the box's seal price is a constant of the
/// quartermaster, not a property of the pull, and banking it per row would fossilize
/// today's price into history the day Square changes it.</para>
///
/// <para><b>Nothing reads this table in 3.0.</b> Ruled 08-15 (Drift): <i>let the data
/// bake.</i> The wiring ships now so that by the time a seals-to-gil comparison
/// between the two exits is worth making, there is a book to make it from; the
/// valuation and the advice wait for 3.1. A reader added here before then would be
/// advising off a sample of one.</para>
///
/// <para>Dalamud-free, linked into Scrooge.Tests (the PullIntentSchema mold).
/// GilStorageBootstrap owns the version gate and the logging; this class owns the
/// statements. CREATE ... IF NOT EXISTS: re-running is a no-op.</para>
/// </summary>
internal static class CofferPullSchema
{
  internal static void ApplyV43(SqliteConnection connection)
  {
    string[] statements =
    [
      @"CREATE TABLE IF NOT EXISTS coffer_pulls (
          id                INTEGER PRIMARY KEY AUTOINCREMENT,
          opened_at         INTEGER NOT NULL,   -- unix seconds
          container_item_id INTEGER NOT NULL,   -- which Materiel Container was opened
          pulled_item_id    INTEGER NOT NULL,
          quantity          INTEGER NOT NULL,
          is_hq             INTEGER NOT NULL DEFAULT 0
        );",
      // Every read this table will ever serve is "the last N days of pulls" - the
      // window is the access pattern, so the index is on the window's column.
      "CREATE INDEX IF NOT EXISTS ix_coffer_pulls_opened ON coffer_pulls(opened_at DESC);",
    ];

    foreach (var sql in statements)
    {
      using var cmd = new SqliteCommand(sql, connection);
      cmd.ExecuteNonQuery();
    }
  }

  /// <summary>
  /// V49: the pull gets a fate. <c>kept_at</c> (unix seconds, NULL = not kept)
  /// stamps the one outcome the machine cannot observe: the player learned the
  /// mount or minion instead of selling it.
  ///
  /// <para>2026-09-01, the night the book got its first four rows, two of them were
  /// minions the player did not have and claimed. A kept pull never produces a sale
  /// row, and a reader that joins pulls to sales would score it zero - wrong, the
  /// seals bought something wanted. SOLD is derivable (a retainer sale of the item
  /// after the pull) and HELD is derivable (still in the bags or listed); KEPT is the
  /// fact only the player knows, so it is a stamp, never an inference from a bag
  /// slot going empty. Valued, when 3.1 reads it, at the board's ask when it left -
  /// a board read, not a prediction.</para>
  /// </summary>
  internal static void ApplyV49(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, "coffer_pulls", "kept_at INTEGER");
}
