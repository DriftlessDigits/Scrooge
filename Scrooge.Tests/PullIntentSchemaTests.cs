using Microsoft.Data.Sqlite;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// V31: the pull-for-X ruling that survives the retainer->bag crossing (walk
/// ruling 2, 08-02). The receipts here are the lifecycle rules: intents are
/// banked on pull COMPLETION only, consumed one-shot by the bag scan, and a
/// re-stage overwrites - the newest verb is the standing answer.
/// </summary>
public class PullIntentSchemaTests
{
  private static SqliteConnection Open()
  {
    var conn = new SqliteConnection("Data Source=:memory:");
    conn.Open();
    Scrooge.PullIntentSchema.ApplyV31(conn);
    return conn;
  }

  [Fact]
  public void ApplyV31_IsIdempotent()
  {
    using var conn = Open();
    var ex = Record.Exception(() => Scrooge.PullIntentSchema.ApplyV31(conn));
    Assert.Null(ex);
  }

  [Fact]
  public void Upsert_KeysOnTheVariant_AndTheNewestVerbWins()
  {
    using var conn = Open();
    Exec(conn, "INSERT INTO pull_intents VALUES (100, 0, 'Melt', 1, NULL)");
    // A re-stage to Gc must overwrite AND re-open a consumed intent - the
    // player answered again, so the crossing is owed again.
    Exec(conn, "UPDATE pull_intents SET consumed_at = 5 WHERE item_id = 100");
    Exec(conn, @"INSERT INTO pull_intents (item_id, is_hq, destination, staged_at, consumed_at)
                 VALUES (100, 0, 'Gc', 9, NULL)
                 ON CONFLICT(item_id, is_hq) DO UPDATE SET
                   destination = 'Gc', staged_at = 9, consumed_at = NULL");

    using var read = new SqliteCommand(
      "SELECT destination, consumed_at FROM pull_intents WHERE item_id = 100", conn);
    using var reader = read.ExecuteReader();
    Assert.True(reader.Read());
    Assert.Equal("Gc", reader.GetString(0));
    Assert.True(reader.IsDBNull(1));
    Assert.False(reader.Read()); // one row per variant, never two
  }

  [Fact]
  public void TheTwoQualities_AreTwoIntents()
  {
    using var conn = Open();
    Exec(conn, "INSERT INTO pull_intents VALUES (100, 0, 'Melt', 1, NULL)");
    Exec(conn, "INSERT INTO pull_intents VALUES (100, 1, 'Gc', 1, NULL)");
    using var count = new SqliteCommand("SELECT COUNT(*) FROM pull_intents", conn);
    Assert.Equal(2L, count.ExecuteScalar());
  }

  private static void Exec(SqliteConnection conn, string sql)
  {
    using var cmd = new SqliteCommand(sql, conn);
    cmd.ExecuteNonQuery();
  }
}
