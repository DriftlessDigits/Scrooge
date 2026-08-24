using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// WHAT THE MIGRATION LADDER RESTS ON (stability sweep, 2026-08-16).
///
/// <para>GilStorageBootstrap.Run used to be 43 hand-written <c>if (version &lt; n)</c>
/// blocks, each running DDL and then stamping user_version as two unrelated acts. A
/// crash between the two left a database whose stamp lied about its shape, and the
/// bare CREATE on the next boot threw forever - the failure V13's own doc describes.
/// The ladder now wraps every rung in a transaction so the DDL and the stamp that
/// claims it ran land together or not at all.</para>
///
/// <para><b>Why these tests and not a climb of the real ladder:</b>
/// GilStorageBootstrap cannot be linked into this project - it reaches Svc.Log and
/// Plugin.PluginInterface, which is exactly the purity guard the linked-source setup
/// exists to enforce. So these pin the two SQLite behaviours the new orchestration
/// bets on, against a real database file. If either of them is not true, the ladder
/// does not work, and neither one is obvious from reading the code:</para>
///
/// <list type="number">
/// <item>PRAGMA user_version is TRANSACTIONAL - a rollback takes the stamp back with
/// the DDL. The whole design is worthless if the stamp survives the rollback.</item>
/// <item>A command built on a bare connection still runs inside a RAW <c>BEGIN</c>.
/// This is why the loop issues BEGIN/COMMIT as SQL instead of calling
/// SqliteConnection.BeginTransaction(): the ADO transaction OBJECT makes every
/// command that does not carry it throw, and the migration methods - plus every
/// schema file they call into - are written against a bare connection.</item>
/// </list>
/// </summary>
public class MigrationLadderTests : IDisposable
{
  private readonly string _path = Path.Combine(Path.GetTempPath(), $"scrooge_ladder_{Guid.NewGuid():N}.db");
  private readonly SqliteConnection _connection;

  public MigrationLadderTests()
  {
    _connection = new SqliteConnection($"Data Source={_path}");
    _connection.Open();
  }

  public void Dispose()
  {
    _connection.Dispose();
    SqliteConnection.ClearAllPools();
    try { File.Delete(_path); } catch { /* temp file */ }
    GC.SuppressFinalize(this);
  }

  private void Exec(string sql)
  {
    using var cmd = new SqliteCommand(sql, _connection);
    cmd.ExecuteNonQuery();
  }

  private int SchemaVersion()
  {
    using var cmd = new SqliteCommand("PRAGMA user_version;", _connection);
    return Convert.ToInt32(cmd.ExecuteScalar());
  }

  private bool TableExists(string name)
  {
    using var cmd = new SqliteCommand(
      "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @n;", _connection);
    cmd.Parameters.AddWithValue("@n", name);
    return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
  }

  [Fact]
  public void ARungThatCommits_KeepsBothTheTableAndTheStamp()
  {
    Exec("BEGIN;");
    Exec("CREATE TABLE rung_table (id INTEGER PRIMARY KEY);");
    Exec("PRAGMA user_version = 7;");
    Exec("COMMIT;");

    Assert.True(TableExists("rung_table"));
    Assert.Equal(7, SchemaVersion());
  }

  [Fact]
  public void ARungThatRollsBack_TakesTheVersionStampWithIt()
  {
    Exec("PRAGMA user_version = 6;");

    Exec("BEGIN;");
    Exec("CREATE TABLE half_climbed (id INTEGER PRIMARY KEY);");
    Exec("PRAGMA user_version = 7;");
    Exec("ROLLBACK;");

    // The whole point: the stamp never claims a shape the database does not have.
    Assert.False(TableExists("half_climbed"));
    Assert.Equal(6, SchemaVersion());
  }

  [Fact]
  public void ARungThatThrowsMidWay_LeavesNoPartialShape()
  {
    Exec("PRAGMA user_version = 12;");

    Exec("BEGIN;");
    try
    {
      Exec("CREATE TABLE last_sale_prices_v13 (item_id INTEGER PRIMARY KEY);");
      Exec("DROP TABLE does_not_exist;"); // the crash, mid-rung
      Exec("PRAGMA user_version = 13;");
    }
    catch (SqliteException)
    {
      Exec("ROLLBACK;");
    }

    // Pre-ladder this is the permanently dead database: v13's table present,
    // user_version still 12, and next boot's bare CREATE throwing forever.
    Assert.False(TableExists("last_sale_prices_v13"));
    Assert.Equal(12, SchemaVersion());
  }

  [Fact]
  public void ABareConnectionCommand_RunsInsideARawBegin()
  {
    // Every migration method builds `new SqliteCommand(sql, connection)` with no
    // transaction on it. Raw BEGIN is what lets the ladder wrap them unchanged.
    Exec("BEGIN;");
    using (var cmd = new SqliteCommand("CREATE TABLE bare (id INTEGER);", _connection))
      cmd.ExecuteNonQuery();
    Exec("COMMIT;");

    Assert.True(TableExists("bare"));
  }

  [Fact]
  public void TheAdoTransactionObject_IsWhyTheLoopDoesNotUseIt()
  {
    // The receipt for the design note above: with an ADO transaction open, a command
    // that does not carry it refuses to run. Wrapping the rungs that way would have
    // meant threading a transaction through every migration and every schema file.
    using var tx = _connection.BeginTransaction();
    using var cmd = new SqliteCommand("CREATE TABLE nope (id INTEGER);", _connection);

    Assert.Throws<InvalidOperationException>(() => cmd.ExecuteNonQuery());
  }
}
