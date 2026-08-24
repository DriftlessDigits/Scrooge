using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// V32: contest receipts (walk ruling 4, 08-02) - every answered contest
/// against a standing listing becomes a receipt AGAINST THE FLAG CLASS:
/// upheld (the player took the router's proposal), overruled (he took a
/// different verb), or dismissed (he re-affirmed the standing call). The book
/// answers "below-min: 14 raised, 11 dismissed" and a miscalibrated flag
/// indicts itself. No auto-tune - receipts grade, they never pre-tune (the A9
/// stamps philosophy), and nothing here ever feeds the bag exit model: "worth
/// the disruption?" is a different question from "where does this go?", and
/// inertia is legitimate on standing positions.
///
/// <para>Write-side only this release (the walk: "the writes cannot wait; the
/// read surface can"). Append-only, never pruned - bounded by human verdicts,
/// which are bounded by human patience.</para>
///
/// <para>Dalamud-free, linked into Scrooge.Tests (the StandingBookSchema mold).</para>
/// </summary>
internal static class ContestReceiptSchema
{
  internal static void ApplyV32(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(@"
      CREATE TABLE IF NOT EXISTS contest_receipts (
        id                  INTEGER PRIMARY KEY AUTOINCREMENT,
        raised_at           INTEGER NOT NULL DEFAULT 0,  -- 0 = fresh this pinch (no held flag behind it)
        verdict_at          INTEGER NOT NULL,
        item_id             INTEGER NOT NULL,
        is_hq               INTEGER NOT NULL DEFAULT 0,
        retainer_name       TEXT    NOT NULL DEFAULT '',
        reason              TEXT    NOT NULL,            -- the flag class: a StandingFlag.Reason, or a PricingResult name for fresh rows
        proposal            TEXT    NOT NULL DEFAULT '', -- the router's proposed verb at verdict time
        verdict             TEXT    NOT NULL,            -- 'upheld' | 'overruled' | 'dismissed'
        answer              TEXT    NOT NULL DEFAULT '', -- the verb the player chose ('' on a dismissal)
        -- The evidence snapshot the verdict was rendered against (nullable: not
        -- every path holds every fact, and a receipt must never invent one).
        listing_price       INTEGER,
        cheapest_competitor INTEGER,
        sale_count          INTEGER,
        latest_sale_at      INTEGER
      );
      CREATE INDEX IF NOT EXISTS idx_contest_receipts_reason
        ON contest_receipts (reason, verdict);", connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// V38: the receipt learns WHICH DOUBT the row acted through (the Defer pile,
  /// 08-06).
  ///
  /// <para>"Overruled" alone is noise. "Overruled a dead-heat call" is signal -
  /// it says the system's rules ran out at a NAMED place, and it says the human
  /// disagreed with what we did there. Grading the flag class (the V32 column
  /// <c>reason</c>) answers "is this flag miscalibrated"; grading the branch
  /// answers "is our judgment aligned with his where we had to guess", which is
  /// the question Defer exists to ask.</para>
  ///
  /// <para>NULL semantics, and they matter because this column is COUNTED:
  /// NULL (and '') mean "no branch on this receipt" - a pre-V38 row, or a
  /// contest answered on a row that had no doubt at all. Neither is a branch
  /// and neither may ever be folded into one; the tally reader filters them out
  /// rather than bucketing them as "other", because a bucket called other with
  /// every legacy row in it would cross Drift's 20-30 bar on its first day and
  /// say nothing.</para>
  ///
  /// <para>The values are the stable keys in <see cref="DeferPlan"/>
  /// (<c>dead_heat</c>, <c>no_tape</c>, <c>unconvictable_hq</c>,
  /// <c>mixed_tier</c>) and are never rephrased - a renamed key silently splits
  /// a tally in half.</para>
  ///
  /// <para>Column-guarded ALTER (the V22/V24/V25/V30/V34/V35/V37 model):
  /// re-running is a no-op. The index is (branch, verdict), the same shape the
  /// reason index has, because the tape asks exactly the same question of it.</para>
  /// </summary>
  internal static void ApplyV38(SqliteConnection connection)
  {
    var existing = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
    using (var info = new SqliteCommand("PRAGMA table_info(contest_receipts);", connection))
    using (var reader = info.ExecuteReader())
      while (reader.Read())
        existing.Add(reader.GetString(1));

    if (existing.Count == 0) return; // V32 creates the table before this runs

    if (!existing.Contains("doubt_branch"))
      using (var alter = new SqliteCommand(
        "ALTER TABLE contest_receipts ADD COLUMN doubt_branch TEXT;", connection))
        alter.ExecuteNonQuery();

    using var idx = new SqliteCommand(@"
      CREATE INDEX IF NOT EXISTS idx_contest_receipts_doubt
        ON contest_receipts (doubt_branch, verdict);", connection);
    idx.ExecuteNonQuery();
  }

  /// <summary>
  /// V38's other half: the naming sweep reaching the DATA. The teaching signals
  /// a listed row writes (<c>routing_overrides.router_verdict</c>) are the
  /// row's PILE NAME, so every ruling ever recorded against the Watch pile is
  /// sitting in the book under a name no code will ever ask for again - and the
  /// override-count refinement that reads those rows would silently lose them.
  ///
  /// <para>Silent, at load, exactly like a renamed config key: the player must
  /// not lose settings or history because we renamed a concept. 'Watch' -&gt;
  /// 'Defer' is the honest mapping - the pile's tenants moved there wholesale -
  /// and the rows keep their verdicts, their items and their timestamps.
  /// Idempotent by construction: after one pass no row matches.</para>
  /// </summary>
  internal static void MigrateWatchVerdictNames(SqliteConnection connection)
  {
    var hasTable = false;
    using (var t = new SqliteCommand(
      "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'routing_overrides';", connection))
      hasTable = System.Convert.ToInt64(t.ExecuteScalar()) > 0;
    if (!hasTable) return;

    using var cmd = new SqliteCommand(
      "UPDATE routing_overrides SET router_verdict = 'Defer' WHERE router_verdict = 'Watch';", connection);
    cmd.ExecuteNonQuery();
  }
}
