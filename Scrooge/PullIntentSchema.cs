using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// V31: pull intents - the ruling that survives the retainer->bag crossing
/// (walk ruling 2, 08-02). A standing listing staged pull-for-melt or
/// pull-for-GC is retrieved at the bell (or the pinch rider); the item then
/// lands in the bags as an ordinary routable row, and WITHOUT this table the
/// router would re-ask a question the player already answered. One row per
/// (item, quality): the destination, when it was staged, and when a bag scan
/// consumed it. Unconsumed intents whose item never lands (sold under us) go
/// stale harmlessly - consumption is the only reader.
///
/// <para>Same-variant collisions (two lanes of one item staged to DIFFERENT
/// destinations in one round) collapse to the last verb staged - the upsert is
/// keyed on the variant, deliberately: a per-lane intent would need to match
/// bag slots back to retainers, which the bags cannot do.</para>
///
/// <para>Dalamud-free, linked into Scrooge.Tests (the StandingBookSchema mold).</para>
/// </summary>
internal static class PullIntentSchema
{
  internal static void ApplyV31(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(@"
      CREATE TABLE IF NOT EXISTS pull_intents (
        item_id     INTEGER NOT NULL,
        is_hq       INTEGER NOT NULL DEFAULT 0,
        destination TEXT    NOT NULL,           -- StandingAction name: 'Melt' | 'Gc'
        staged_at   INTEGER NOT NULL,
        consumed_at INTEGER,                    -- NULL = still waiting for the item to land
        PRIMARY KEY (item_id, is_hq)
      );", connection);
    cmd.ExecuteNonQuery();
  }
}
