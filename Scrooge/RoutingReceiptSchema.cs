using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// The V20 routing-receipt DDL, extracted Dalamud-free so it is linked-source
/// testable (the migration idempotency contract runs this against a real temp
/// SQLite DB, no game statics) - the V19/MarketMemorySchema model.
///
/// routing_receipts is the 4.0 scoreboard's food: one row per routing DECISION
/// with the ALTERNATIVE scores at decision time (the counterfactual is the
/// point - "what did churning this leave on the table?"), the seal-rate and
/// venture context the tilt ran under, and the evidence phase (community vs
/// world - the finding-5 oscillation tag). executed_action stays null until an
/// executor fires; player_overrode flips on a ruling. Reads the scoreboard
/// wants: churn-vs-list gil left on table, verdict stability across phases,
/// override rate by confidence tier.
///
/// <para>venture_stock is an OPERAND (the seal S-curve reads it); weekly_burn
/// beside it is CONTEXT - the world the tilt ran under, not an input any score
/// consulted - and the two are named apart here so a reader never mistakes the
/// second for the first. melt_grade and skillup_color (V44) are the scoreboard's
/// other food: its skillup-crossover derivation asks which way a ruling went at
/// the knob value it was made under, and melt_score alone is a number with no
/// units.</para>
///
/// Every statement is CREATE ... IF NOT EXISTS, so applying it twice is a no-op
/// (diffable + idempotent, the V11 model).
///
/// <para>SEAM CLOSED (V22, WALK unit 7). seal_rate was the BASE rate the batch
/// carried, NOT the rate the GC score was computed at — above the runway line the
/// router scores seals at base x factor (see <see cref="SealRunway"/>). Nothing
/// was strictly lost, since venture_stock and weekly_burn sit on the same row, but
/// reading a receipt back also required knowing the threshold and factor that were
/// configured that night, and those were never persisted. V22 adds the two columns
/// the seam asked for. HISTORY IS NOT REWRITTEN: rows written before the bump keep
/// a zero effective rate and an unset flag, which is honestly "we didn't record
/// this" rather than a back-filled guess about a config value nobody saved.</para>
/// </summary>
internal static class RoutingReceiptSchema
{
  /// <summary>Creates the routing_receipts table and its indexes (idempotent).</summary>
  internal static void ApplyV20(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(
      @"CREATE TABLE IF NOT EXISTS routing_receipts (
          id                  INTEGER PRIMARY KEY AUTOINCREMENT,
          created_at          INTEGER NOT NULL,
          item_id             INTEGER NOT NULL,
          is_hq               INTEGER NOT NULL DEFAULT 0,
          ilvl                INTEGER NOT NULL DEFAULT 0,
          exit                TEXT NOT NULL,
          reason              TEXT NOT NULL DEFAULT '',
          is_review           INTEGER NOT NULL DEFAULT 0,
          confidence_tier     TEXT NOT NULL DEFAULT '',
          player_overrode     INTEGER NOT NULL DEFAULT 0,
          executed_action     TEXT,
          list_score          INTEGER,
          gc_score            INTEGER,
          melt_score          INTEGER,
          vendor_score        INTEGER,
          seal_rate           INTEGER NOT NULL DEFAULT 0,
          seal_rate_empirical INTEGER NOT NULL DEFAULT 0,
          venture_stock       INTEGER,
          weekly_burn         INTEGER,
          evidence_phase      TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS ix_routing_receipts_item
          ON routing_receipts(item_id, is_hq, created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_routing_receipts_exit
          ON routing_receipts(exit, created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_routing_receipts_unexecuted
          ON routing_receipts(item_id, is_hq) WHERE executed_action IS NULL;",
      connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// V22: the rate the GC score was ACTUALLY computed at, and whether the runway
  /// discount was in force when it was.
  ///
  /// <para>SQLite has no ADD COLUMN IF NOT EXISTS, so idempotency is a
  /// table_info read rather than a clause - same contract as every migration
  /// here (applying it twice is a clean no-op), just spelled out by hand.</para>
  ///
  /// <para>Defaults are 0 / 0 and nothing back-fills them. A pre-V22 row means
  /// "this wasn't recorded", and that reads honestly; a computed back-fill would
  /// be a guess about config values from a night nobody saved, wearing the same
  /// column as measured ones.</para>
  /// </summary>
  internal static void ApplyV22(SqliteConnection connection)
    // The table itself is V20's business; a DB that somehow has no receipts table
    // has nothing to widen, and V20 runs before this on any real ladder.
    => SchemaGuards.EnsureColumns(connection, "routing_receipts",
        "effective_seal_rate REAL NOT NULL DEFAULT 0",
        "seal_discounted INTEGER NOT NULL DEFAULT 0");

  /// <summary>
  /// V44: WHAT THE MELT NUMBER IS, and the skillup standing behind it. The 4.0
  /// scoreboard's skillup-crossover derivation is the consumer: it has to ask
  /// "at the value you had the knob set to, which way did you actually rule?",
  /// and melt_score alone cannot answer it - 100,000 gil in that column reads
  /// identically whether it came off measured yields, an ilvl band average, or
  /// the SkillupWorthRed knob outbidding both.
  ///
  /// <para>melt_grade is <see cref="MeltGrade"/>'s name (the fourth score's
  /// provenance, already carried on <see cref="RoutingScores"/> and previously
  /// dropped at the insert); skillup_color is the row's skillup standing
  /// (<see cref="DesynthSkillupColor"/>), which is the peg the crossover is
  /// about - yellow and red are separate knobs and a derivation that cannot
  /// tell them apart is deriving one number from two.</para>
  ///
  /// <para>THE TWO READ TOGETHER, which is what keeps the NULL honest: a
  /// recorded row always has a melt_grade (even '' + no scores says the
  /// pre-value exits ran), so melt_grade non-empty with skillup_color NULL is
  /// "recorded, not skillup-eligible", and melt_grade '' is "this build did not
  /// record it". Nothing back-fills; a pre-V44 row is silent on both, and that
  /// reads as the absence it is.</para>
  /// </summary>
  internal static void ApplyV44(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, "routing_receipts",
        "melt_grade TEXT NOT NULL DEFAULT ''",
        "skillup_color TEXT");
}
