using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// The three reads every migration here opens with, written once. SQLite has no
/// ADD COLUMN IF NOT EXISTS, so idempotency is a table_info read rather than a
/// clause - and that read was spelled out longhand in every schema file, which
/// made the discipline a convention instead of a mechanism.
///
/// <para>Dalamud-free by construction (a connection and strings, nothing else), so
/// the schema files that lean on it stay linked-source testable.</para>
/// </summary>
internal static class SchemaGuards
{
  /// <summary>True when the named table exists in the connected database.</summary>
  internal static bool TableExists(SqliteConnection connection, string table)
  {
    using var cmd = new SqliteCommand(
      "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;", connection);
    cmd.Parameters.AddWithValue("@name", table);
    return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
  }

  /// <summary>
  /// The table's column names, case-insensitively. A missing table reads as an
  /// empty set rather than throwing - "nothing to widen" is the honest answer for
  /// a bare test DB, and it is the answer every caller here wants.
  /// </summary>
  internal static HashSet<string> Columns(SqliteConnection connection, string table)
  {
    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using (var info = new SqliteCommand($"PRAGMA table_info({table});", connection))
    using (var reader = info.ExecuteReader())
      while (reader.Read())
        columns.Add(reader.GetString(1));
    return columns;
  }

  /// <summary>
  /// Adds each column that is not already there, in order. Each entry is the DDL
  /// the ALTER takes verbatim ("band_low_ratio REAL"); the name is its first word.
  ///
  /// <para>A table with no columns is a table that does not exist, and this is a
  /// clean no-op on it - the birth migration creates the table before any widening
  /// runs, so an empty read means there is nothing to widen, never that a column
  /// is missing.</para>
  /// </summary>
  internal static void EnsureColumns(SqliteConnection connection, string table, params string[] columnDdl)
  {
    var existing = Columns(connection, table);
    if (existing.Count == 0) return;

    foreach (var ddl in columnDdl)
    {
      var name = ddl.Split(' ')[0];
      if (existing.Contains(name)) continue;
      using var alter = new SqliteCommand($"ALTER TABLE {table} ADD COLUMN {ddl};", connection);
      alter.ExecuteNonQuery();
    }
  }
}
