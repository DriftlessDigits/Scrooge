using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// The V19 market-memory DDL, extracted Dalamud-free so it is linked-source testable
/// (the migration idempotency contract runs this against a real temp SQLite DB, no
/// game statics). GilStorageBootstrap.MigrateV19 calls <see cref="ApplyV19"/> and owns
/// the logging; this class owns only the schema statements.
///
/// Every statement is CREATE ... IF NOT EXISTS or a column-guarded ALTER, so applying
/// it twice is a no-op (diffable + idempotent, the V11 model).
/// </summary>
internal static class MarketMemorySchema
{
  /// <summary>Creates the three market-memory tables (idempotent) and the scope column.</summary>
  internal static void ApplyV19(SqliteConnection connection)
  {
    EnsureTables(connection);
    EnsureTriageScopeColumn(connection);
  }

  /// <summary>The current-board snapshot, the append-only event log, and receipts.</summary>
  internal static void EnsureTables(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(
      @"CREATE TABLE IF NOT EXISTS market_board_snapshot (
          id            INTEGER PRIMARY KEY AUTOINCREMENT,
          item_id       INTEGER NOT NULL,
          is_hq         INTEGER NOT NULL DEFAULT 0,
          retainer_name TEXT NOT NULL DEFAULT '',
          quantity      INTEGER NOT NULL DEFAULT 1,
          unit_price    INTEGER NOT NULL,
          is_own        INTEGER NOT NULL DEFAULT 0,
          world_id      INTEGER NOT NULL DEFAULT 0,
          observer      TEXT NOT NULL DEFAULT 'own_scan',
          seen_at       INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_market_board_snapshot_item ON market_board_snapshot(item_id);
        -- Surrogate PK, NOT a natural (item,hq,retainer,qty) key: two twin listings
        -- share that soft identity, and a natural PK would silently drop one - which
        -- would then read as a phantom disappear/appear on the next scan. The write
        -- path deletes-all-for-item then inserts, so twins persist faithfully.
        CREATE TABLE IF NOT EXISTS market_events (
          id              INTEGER PRIMARY KEY AUTOINCREMENT,
          item_id         INTEGER NOT NULL,
          is_hq           INTEGER NOT NULL DEFAULT 0,
          retainer_name   TEXT NOT NULL DEFAULT '',
          quantity        INTEGER NOT NULL DEFAULT 1,
          kind            TEXT NOT NULL,
          old_price       INTEGER,
          new_price       INTEGER,
          is_own          INTEGER NOT NULL DEFAULT 0,
          observer        TEXT NOT NULL DEFAULT 'own_scan',
          certainty       TEXT NOT NULL DEFAULT 'observed',
          resolution      TEXT,
          ambiguous_match INTEGER NOT NULL DEFAULT 0,
          seen_after      INTEGER NOT NULL DEFAULT 0,
          seen_by         INTEGER NOT NULL,
          world_id        INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_market_events_item ON market_events(item_id, is_hq);
        CREATE INDEX IF NOT EXISTS ix_market_events_kind ON market_events(kind, seen_by DESC);
        CREATE INDEX IF NOT EXISTS ix_market_events_ambiguous ON market_events(ambiguous_match) WHERE ambiguous_match = 1;
        CREATE TABLE IF NOT EXISTS decision_receipts (
          id                     INTEGER PRIMARY KEY AUTOINCREMENT,
          created_at             INTEGER NOT NULL,
          item_id                INTEGER NOT NULL,
          is_hq                  INTEGER NOT NULL DEFAULT 0,
          retainer_name          TEXT NOT NULL DEFAULT '',
          item_category          TEXT NOT NULL DEFAULT '',
          arm_id                 TEXT,
          position_in_lane       REAL,
          board_depth            INTEGER NOT NULL DEFAULT 0,
          undercut_target_ratio  REAL,
          velocity_per_day       REAL,
          lane_n                 INTEGER NOT NULL DEFAULT 0,
          lane_spread            REAL,
          weighted_lane_age_days REAL NOT NULL DEFAULT 0,
          forecast_clearing_days REAL,
          quantity               INTEGER NOT NULL DEFAULT 1,
          lane_stack_norm        REAL,
          outcome                TEXT NOT NULL DEFAULT '',
          evidence               TEXT NOT NULL DEFAULT '',
          time_to_clear_days     INTEGER,
          outcome_state          TEXT NOT NULL DEFAULT 'open'
        );
        CREATE INDEX IF NOT EXISTS ix_decision_receipts_item ON decision_receipts(item_id, is_hq, created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_decision_receipts_open ON decision_receipts(item_id, is_hq) WHERE outcome_state = 'open';",
      connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Adds triage_flags.scope (board/inventory/'') for the zombie sweep. Guarded so a
  /// re-run - or a fresh DB whose CreateTables already ships the column - is a no-op.
  /// A missing triage_flags table (bare test DB) simply reads zero columns and skips.
  /// </summary>
  internal static void EnsureTriageScopeColumn(SqliteConnection connection)
  {
    var hasTable = false;
    using (var t = new SqliteCommand(
      "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'triage_flags';", connection))
      hasTable = System.Convert.ToInt64(t.ExecuteScalar()) > 0;
    if (!hasTable) return;

    var hasScope = false;
    using (var check = new SqliteCommand("PRAGMA table_info(triage_flags);", connection))
    using (var reader = check.ExecuteReader())
      while (reader.Read())
        if (reader.GetString(1) == "scope") { hasScope = true; break; }

    if (!hasScope)
      using (var alter = new SqliteCommand(
        "ALTER TABLE triage_flags ADD COLUMN scope TEXT NOT NULL DEFAULT '';", connection))
        alter.ExecuteNonQuery();
  }
}
