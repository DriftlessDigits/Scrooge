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
/// Every statement is CREATE ... IF NOT EXISTS, so applying it twice is a no-op
/// (diffable + idempotent, the V11 model).
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
}
