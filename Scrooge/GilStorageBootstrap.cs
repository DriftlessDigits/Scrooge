using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Scrooge;


/// <summary>
/// Handles first-time database setup: table creation, JSON migration,
/// and seed data. Called once per startup from GilStorage.Initialize().
/// Future schema migrations go here, gated by PRAGMA user_version.
/// </summary>
internal class GilStorageBootstrap
{
  // --- THE SEAMS (F-migrations, 08-22): the ladder is Dalamud-free so the whole
  // climb is linked-source testable against a real temp DB - the empty-install
  // climb and the 2.6-era one-leap (user_version 12 -> current) both run in
  // Scrooge.Tests. Plugin wires the real sinks at startup; tests leave the
  // defaults (silent log, no JSON dir = V1 import skipped, which IS the fresh
  // install shape).
  internal static Action<string> LogInfo = _ => { };
  internal static Action<string> LogDebug = _ => { };
  internal static Action<string, Exception?> LogError = (_, _) => { };
  /// <summary>The directory holding gil_data.json for the V1 import, or null to skip
  /// (fresh install, tests). The plugin config dir in production.</summary>
  internal static Func<string?> JsonDirProvider = () => null;
  /// <summary>The V1 legacy-JSON import (path -> success), wired by GilStorage in
  /// production - its body writes through the GilStorage insert methods, which is
  /// exactly the dependency the ladder itself must not carry. Null = no import.</summary>
  internal static Func<string, bool>? JsonImporter = null;

  /// <summary>
  /// THE LADDER. Every schema rung in one ordered table: the version it stamps,
  /// the DDL it runs, and the line it speaks once the stamp is durable.
  ///
  /// <para>It replaced 43 hand-written <c>if (version &lt; n)</c> blocks that were
  /// structurally identical and individually forgettable - the shape a rung has to
  /// take (run, stamp, log) is now the loop's business, not each author's.</para>
  ///
  /// <para>An empty <see cref="string.Empty"/> log means the rung speaks for itself
  /// from inside its own method (V6, V8, V9... log their own row counts); the loop
  /// stays quiet rather than saying it twice.</para>
  /// </summary>
  private static readonly (int Version, Action<SqliteConnection> Apply, string Log)[] Ladder =
  [
    (1, MigrateV1, ""),
    (2, MigrateV2, ""),
    (3, MigrateV3, ""),
    (4, MigrateV4, ""),
    (5, MigrateV5, ""),
    (6, MigrateV6, ""),
    (7, MigrateV7, ""),
    (8, MigrateV8, ""),
    (9, MigrateV9, ""),
    (10, MigrateV10, ""),
    (11, MigrateV11, ""),
    (12, MigrateV12, ""),
    (13, MigrateV13, ""),
    (14, MigrateV14, ""),
    (15, MigrateV15, ""),
    (16, MigrateV16, ""),
    (17, MigrateV17, ""),
    (18, MigrateV18, ""),
    (19, MigrateV19, ""),
    (20, RoutingReceiptSchema.ApplyV20,
      "V20 migration: routing_receipts table - routing decisions with alternative scores (the 4.0 scoreboard's food)"),
    (21, StandingBookSchema.ApplyV21,
      "V21 migration: own_listing_writes table - the write side of our own board presence (the standing book)"),
    (22, RoutingReceiptSchema.ApplyV22,
      "V22 migration: routing_receipts gains the EFFECTIVE seal rate + discount flag - a receipt reads back without config archaeology"),
    (23, MigrateV23, ""),
    (24, MarketMemorySchema.ApplyV24,
      "V24 migration: decision_receipts gains the band edges + the segment's story - a receipt reads back who was still voting"),
    (25, MarketMemorySchema.ApplyV25,
      "V25 migration: decision_receipts gains queue_position + cluster_size - the spot we took becomes gradeable (A10)"),
    (26, MarketMemorySchema.ApplyV26,
      "V26 migration: decision_receipts gains the absolute ask, its close instant, and the grade stamps - the spot we took becomes scoreable (A9)"),
    (27, MeltYieldPrices.ApplyV27,
      "V27 migration: community_mat_prices table - yield materials our own boards never priced stop weighing zero (crystals still deliberately do)"),
    (28, MeltYieldPrices.ApplyV28,
      "V28 migration: vendor_mat_prices table - the melt scale gains its floor rung, so a yield the counter pays 5,000 for stops weighing zero"),
    (29, CommunityHistorySchema.ApplyV29,
      "V29 migration: community_history tables - the DC evidence the list score reads survives a reload, so a restart stops routing 11k rows to melt by forfeit"),
    (30, MarketMemorySchema.ApplyV30,
      "V30 migration: decision_receipts gains competitor_position - the spot among real sellers, banked beside the raw queue index (A11)"),
    (31, PullIntentSchema.ApplyV31,
      "V31 migration: pull_intents table - a pull-for-melt/GC ruling survives the retainer->bag crossing, so the router never re-asks"),
    (32, ContestReceiptSchema.ApplyV32,
      "V32 migration: contest_receipts table - answered contests grade the FLAGS (upheld/overruled/dismissed), and a miscalibrated flag indicts itself"),
    (33, MarketMemorySchema.ApplyV33,
      "V33 migration: paged-read phantom events deleted - a full-board turnover claimed inside a sub-30s window is a fabrication, not an observation (~2,773 rows, every day since V19; Drift: delete em)"),
    (34, MarketMemorySchema.ApplyV34,
      "V34 migration: decision_receipts gains board_total - the game's own count for the board, so depth < total reads 'this decision ran censored' straight off the row"),
    (35, MarketMemorySchema.ApplyV35,
      "V35 migration: decision_receipts gains crasher/cluster gil spans - the queue position's story in numbers (what the crashers were, what the real line asked)"),
    (36, MarketMemorySchema.ApplyV36,
      "V36 migration: pre-V19 Watch tenants retired as legacy_unprovable - a lane_held flag with no recorded container can never satisfy the zombie round's proof, and the premium ladder ended the class's inflow (Drift: the tombstones come down)"),
    (37, MarketMemorySchema.ApplyV37,
      "V37 migration: decision_receipts gains crowd_behind - the crowd that WON the outnumbering test, so a cell can stop reporting the cluster and calling it the count"),
    (38, MigrateV38,
      "V38 migration: contest_receipts gains doubt_branch, and the old Watch pile's recorded rulings rename to Defer - a pivot is only signal if you know WHICH doubt it answered, and a renamed concept must never cost the player his history"),
    (39, DecisionCacheSchema.ApplyV39,
      "V39 migration: decision_cache table - recon's banked answer per (item, quality), so the act half can spend ground truth the Look half already bought instead of re-asking the server for it"),
    (40, RoundLogSchema.ApplyV40,
      "V40 migration: round_runs + round_log tables and decision_cache.lane_median - a Round gets a DB-issued identity, its transcript survives the reload that used to eat it, and a cached post can true up the receipt recon wrote"),
    (41, MarketMemorySchema.ApplyV41,
      "V41 migration: decision_receipts gains margin_donated - the A9 verdicts stopped grading the price, so what the market paid after we left is banked as a measurement in gil (0 = measured and nothing donated, NULL = never measured) instead of a stamp that scored a prediction"),
    (42, VentureReturnsSchema.ApplyV42,
      "V42 migration: venture_returns gains venture_id/venture_cost/venture_category - the capture learns WHICH venture it was, so the seals-to-gil arithmetic can stop believing a config knob about token cost and start measuring it off the sheet (NULL on pre-stamp rows, never imputed)"),
    (43, CofferPullSchema.ApplyV43,
      "V43 migration: coffer_pulls table - the seals' other exit gets a book. A Materiel Container 3.0/4.0 is 20k seals for a random mount or minion, and until now the plugin saw only the eventual sale, never the trade that produced it. Passive capture only: nothing reads this table in 3.0 (ruled 08-15, let the data bake) - the comparison against the venture exit waits for 3.1, when there is a sample worth comparing"),
    (44, RoutingReceiptSchema.ApplyV44,
      "V44 migration: routing_receipts gains melt_grade + skillup_color - a melt score reads back as measured yields, a band average, or the skillup knob, so the 4.0 crossover can ask which way you ruled at the value you had it set to"),
    (45, MarketMemorySchema.ApplyV45,
      "V45 migration: decision_receipts gains undercut_posture - the stance the price was written under (self, caps, rail, write style), because a receipt that records only the outcome cannot grade the knobs that produced it"),
    (46, RoutingOverrideSchema.ApplyV46,
      "V46 migration: routing_overrides gains receipt_id - a ruling names the receipt it ruled against instead of leaving 4.0 to guess by item and timestamp"),
    (47, MarketMemorySchema.ApplyV47,
      "V47 migration: decision_receipts gains seat_at_write (the true 1-based seat, crashers count - competitor_position's writer retires, its zero could not say 'not first') and the shadow trio shadow_price/shadow_seat/shadow_defense - the 3.1 queue-doctrine candidate's answer on the same board, banked so the 3.1 ruling opens on paired data"),
    (48, MarketMemorySchema.ApplyV48,
      "V48 migration: back-marks standing recon phantoms with arm_id 'recon' (the Neo-Ishgardian Sword, 08-23: a Look's receipt wore ask grammar on the trail, the state line and the On Market tab) - new recon receipts are born marked, adoption's true-up clears the mark"),
    (49, CofferPullSchema.ApplyV49,
      "V49 migration: coffer_pulls gains kept_at - a pull's fate. Two of the book's first four rows (2026-09-01) were minions the player learned, not sold; a kept pull never produces a sale row, so the one outcome the machine cannot see gets a stamp instead of a zero. SOLD and HELD stay derivable; only KEPT is written"),
  ];

  /// <summary>
  /// Thrown by a rung that has already reported its own failure and wants the
  /// climb to stop QUIETLY - the JSON import is the only one. It is a stop signal,
  /// not an error: nothing after it runs (that is the point), but storage is not
  /// declared dead over it, because the next startup retries from the same rung.
  /// </summary>
  private sealed class BootstrapHalt : Exception;

  /// <summary>
  /// The JSON file the V1 import consumed, renamed to .bak only once its rung's
  /// transaction is durable. Renaming inside the transaction would hand a rollback
  /// the power to lose the player's only copy.
  /// </summary>
  private static string? _jsonToArchive;

  /// <summary>
  /// Entry point — climbs the ladder, then runs the idempotent fixes.
  /// <paramref name="ceiling"/> stops the climb at a given rung - the test seam
  /// that lets the ladder build its OWN historical shapes (a 2.6.2.0-era database
  /// is rungs 1..12 of this very ladder; the rungs are append-only history).
  /// Production never passes it.
  /// </summary>
  internal static void Run(SqliteConnection connection, int? ceiling = null)
  {
    var version = GetSchemaVersion(connection);

    foreach (var (stepVersion, apply, log) in Ladder)
    {
      if (ceiling is int cap && stepVersion > cap) break;
      if (version >= stepVersion) continue;

      // A rung is all-or-nothing: the DDL and the stamp that claims it ran commit
      // together, so a crash mid-migration can never leave a database whose
      // user_version lies about its shape. Raw BEGIN/COMMIT rather than
      // SqliteConnection.BeginTransaction() deliberately - the ADO transaction
      // object makes every command that does not carry it throw, and the DDL in
      // these methods (and in the schema files they call) is written against a
      // bare connection.
      RunSql(connection, "BEGIN;");
      try
      {
        apply(connection);
        SetSchemaVersion(connection, stepVersion);
      }
      catch (BootstrapHalt)
      {
        Rollback(connection);
        return; // Already reported. Nothing after this rung runs, including the fixes.
      }
      catch
      {
        Rollback(connection);
        throw;
      }
      RunSql(connection, "COMMIT;");

      if (_jsonToArchive is string archived)
      {
        _jsonToArchive = null;
        File.Move(archived, archived + ".bak", overwrite: true);
      }

      if (log.Length > 0) LogInfo(log);
    }

    // Idempotent fixes — safe to run every startup
    using var fixDashes = new SqliteCommand(
        "UPDATE category_groups SET ui_category = REPLACE(ui_category, '–', '-') WHERE ui_category LIKE '%–%'",
        connection);
    fixDashes.ExecuteNonQuery();
  }

  private static void RunSql(SqliteConnection connection, string sql)
  {
    using var cmd = new SqliteCommand(sql, connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Undoes the open rung. SQLite auto-rolls-back on some errors, so a ROLLBACK
  /// that finds nothing open is expected - and must never replace the real
  /// exception on its way out.
  /// </summary>
  private static void Rollback(SqliteConnection connection)
  {
    _jsonToArchive = null;
    try { RunSql(connection, "ROLLBACK;"); }
    catch (Exception ex) { LogDebug($"[GilTrack] rollback no-op: {ex.Message}"); }
  }

  /// <summary>
  /// V1: the birth rung — tables, the one-time JSON import, and the seeds.
  /// A failed import halts the climb without stamping, so the next startup
  /// retries against the preserved gil_data.json.
  /// </summary>
  private static void MigrateV1(SqliteConnection connection)
  {
    CreateTables(connection);
    if (!MigrateFromJson(connection))
      throw new BootstrapHalt();
    SeedQuotes(connection);
    SeedCategoryGroups(connection);
  }

  /// <summary>V38: the column and the rename of the rulings that column has to describe.</summary>
  private static void MigrateV38(SqliteConnection connection)
  {
    ContestReceiptSchema.ApplyV38(connection);
    ContestReceiptSchema.MigrateWatchVerdictNames(connection);
  }

  private static int GetSchemaVersion(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand($"PRAGMA user_version;", connection);
    return Convert.ToInt32(cmd.ExecuteScalar());
  }

  private static void SetSchemaVersion(SqliteConnection connection, int version)
  {
    using var cmd = new SqliteCommand($"PRAGMA user_version = {version};", connection);
    cmd.ExecuteNonQuery();
  }

  // =========================================================================
  // Table Creation
  // =========================================================================
  private static void CreateTables(SqliteConnection connection)
  {
    string[] statements =
    [
        @"CREATE TABLE IF NOT EXISTS gil_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp INTEGER NOT NULL,
                player_gil INTEGER NOT NULL,
                source TEXT NOT NULL DEFAULT 'pinch_run',
                venture_tokens INTEGER
            )",
            @"CREATE TABLE IF NOT EXISTS venture_returns (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                captured_at INTEGER NOT NULL,
                retainer_name TEXT NOT NULL,
                item_id INTEGER NOT NULL,
                quantity INTEGER NOT NULL,
                is_hq INTEGER NOT NULL DEFAULT 0
            )",
            @"CREATE INDEX IF NOT EXISTS ix_venture_returns_captured
                ON venture_returns(captured_at DESC)",
            @"CREATE TABLE IF NOT EXISTS universalis_stats (
                item_id INTEGER NOT NULL,
                world_id INTEGER NOT NULL,
                nq_velocity REAL NOT NULL DEFAULT 0,
                hq_velocity REAL NOT NULL DEFAULT 0,
                last_sale_at INTEGER,
                last_upload_at INTEGER,
                fetched_at INTEGER NOT NULL,
                PRIMARY KEY (item_id, world_id)
            )",
            @"CREATE TABLE IF NOT EXISTS retainer_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                snapshot_id INTEGER NOT NULL REFERENCES gil_snapshots(id),
                retainer_name TEXT NOT NULL,
                gil INTEGER NOT NULL
            )",
            @"CREATE TABLE IF NOT EXISTS transactions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp INTEGER NOT NULL,
                direction TEXT NOT NULL,
                source TEXT NOT NULL,
                amount INTEGER NOT NULL,
                item_id INTEGER NOT NULL DEFAULT 0,
                item_name TEXT NOT NULL DEFAULT '',
                category TEXT NOT NULL DEFAULT '',
                quantity INTEGER NOT NULL DEFAULT 0,
                unit_price INTEGER NOT NULL DEFAULT 0,
                is_hq INTEGER NOT NULL DEFAULT 0,
                retainer_name TEXT NOT NULL DEFAULT '',
                counterparty TEXT NOT NULL DEFAULT ''
            )",
            @"CREATE TABLE IF NOT EXISTS listings (
                retainer_name TEXT NOT NULL,
                slot_index INTEGER NOT NULL,
                item_id INTEGER NOT NULL,
                item_name TEXT NOT NULL,
                category TEXT NOT NULL,
                unit_price INTEGER NOT NULL,
                quantity INTEGER NOT NULL,
                is_hq INTEGER NOT NULL DEFAULT 0,
                first_seen INTEGER NOT NULL,
                last_updated INTEGER NOT NULL,
                PRIMARY KEY (retainer_name, slot_index, item_id)
            )",
            @"CREATE TABLE IF NOT EXISTS market_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp INTEGER NOT NULL,
                item_count INTEGER NOT NULL,
                total_listing_value INTEGER NOT NULL,
                avg_listing_age_days REAL NOT NULL,
                source TEXT NOT NULL DEFAULT 'full'
            )",
            @"CREATE TABLE IF NOT EXISTS category_groups (
                ui_category TEXT PRIMARY KEY,
                display_group TEXT NOT NULL
            )",
            @"CREATE TABLE IF NOT EXISTS last_sale_prices (
                item_id INTEGER NOT NULL,
                is_hq INTEGER NOT NULL DEFAULT 0,
                unit_price INTEGER NOT NULL,
                timestamp INTEGER NOT NULL,
                sold_after_days INTEGER,
                PRIMARY KEY (item_id, is_hq)
            )",
            @"CREATE TABLE IF NOT EXISTS quotes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                text TEXT NOT NULL,
                author TEXT NOT NULL,
                last_displayed INTEGER NOT NULL DEFAULT 0
            )",
            // Indexes for per-frame queries and deduplication
            "CREATE INDEX IF NOT EXISTS idx_txn_dedup ON transactions(item_id, timestamp, retainer_name)",
            "CREATE INDEX IF NOT EXISTS idx_txn_dir_src_ts ON transactions(direction, source, timestamp)",
            "CREATE INDEX IF NOT EXISTS idx_listings_first_seen ON listings(first_seen)"
    ];

    foreach (var sql in statements)
    {
      using var cmd = new SqliteCommand(sql, connection);
      cmd.ExecuteNonQuery();
    }
  }

  // =========================================================================
  // JSON Migration (one-time)
  // =========================================================================

  /// <summary>
  /// Imports data from the old gil_data.json into SQLite, then queues the rename
  /// to .bak for after the rung commits.
  /// Uses GilStorage write methods so all SQL stays in one place.
  /// Atomicity comes from the ladder's rung transaction — if anything fails, the
  /// whole rung rolls back and the JSON is preserved.
  /// Returns true if migration succeeded or no JSON file exists. Returns false on failure
  /// (the rung then halts without stamping, so migration retries next startup).
  /// </summary>
  private static bool MigrateFromJson(SqliteConnection connection)
  {
    if (JsonDirProvider() is not string jsonDir)
      return true; // no config dir (fresh install / tests) - nothing to import
    var jsonPath = Path.Combine(jsonDir, "gil_data.json");
    if (!File.Exists(jsonPath)) return true; // No file to migrate — success
    if (JsonImporter is not { } import)
      return true; // nobody wired an importer (tests) - nothing to import

    try
    {
      if (!import(jsonPath)) return false; // Corrupt/empty — fail, retry next time
      _jsonToArchive = jsonPath;
      return true;
    }
    catch (Exception ex)
    {
      LogError("[GilTrack] JSON migration failed — gil_data.json preserved", ex);
      return false;
    }
  }

  // =========================================================================
  // Seed Data
  // =========================================================================

  /// <summary>Seeds approved quotes. Only called when schema version is below 1.</summary>
  private static void SeedQuotes(SqliteConnection connection)
  {
    var quotes = new (string text, string author)[]
    {
            // Real-World Tycoons
            ("How much money is enough? Just a little bit more.", "John D. Rockefeller"),
            ("I would rather earn 1% off a hundred people's efforts than 100% of my own efforts.", "John D. Rockefeller"),
            ("The way to make money is to buy when blood is running in the streets.", "John D. Rockefeller"),
            ("Competition is a sin.", "John D. Rockefeller"),
            ("What do I care about the law? Ain't I got the power?", "Cornelius Vanderbilt"),
            ("We are not in business for our health.", "J.P. Morgan"),
            ("I owe the public nothing.", "J.P. Morgan"),
            ("A man always has two reasons for doing anything: a good reason and the real reason.", "J.P. Morgan"),
            ("Gold is money. Everything else is credit.", "J.P. Morgan"),
            ("It requires a great deal of boldness and a great deal of caution to make a great fortune; and when you have got it, it requires ten times as much wit to keep it.", "Nathan Mayer Rothschild"),
            ("Let me issue and control a nation's money, and I care not who writes the laws.", "Mayer Amschel Rothschild"),
            ("If you can count your money, you don't have a billion dollars.", "J. Paul Getty"),
            ("Formula for success: rise early, work hard, strike oil.", "J. Paul Getty"),
            ("Competition is the keen cutting edge of business, always shaving away at costs.", "Henry Ford"),
            ("The first man gets the oyster, the second man gets the shell.", "Andrew Carnegie"),
            ("The big money is not in the buying and selling, but in the waiting.", "Charlie Munger"),
            ("The best investment you can make is in yourself.", "Warren Buffett"),
            // Wits & Philosophers
            ("When I was young I thought that money was the most important thing in life; now that I am old I know that it is.", "Oscar Wilde"),
            ("Honesty is the best policy — when there is money in it.", "Mark Twain"),
            ("What is the chief end of man? To get rich. In what way? Dishonestly if we can; honestly if we must.", "Mark Twain"),
            ("No nation was ever ruined by trade.", "Benjamin Franklin"),
            ("Money is the god of our time, and Rothschild is his prophet.", "Heinrich Heine"),
            // Film & Fiction
            ("Greed, for lack of a better word, is good.", "Gordon Gekko"),
            ("What's worth doing is worth doing for money.", "Gordon Gekko"),
            ("Work smarter, not harder, and you'll have all the money you'll ever need.", "Scrooge McDuck"),
            ("I made my money by being tougher than the toughies and smarter than the smarties.", "Scrooge McDuck"),
            ("There is no nobility in poverty. I've been a poor man, and I've been a rich man. And I choose rich every time.", "Jordan Belfort"),
            ("The name of the game: moving the money from the client's pocket to your pocket.", "Mark Hanna"),
            ("What's Christmas-time to you but a time for paying bills without money; a time for finding yourself a year older and not a penny richer.", "Ebenezer Scrooge"),
            ("There is no such thing as rich enough, only poor enough.", "Ebenezer Scrooge"),
            ("A gil saved is a gil earned.", "Scrooge"),
            // Ferengi Rules of Acquisition
            ("Once you have their money, you never give it back.", "Rule of Acquisition #1"),
            ("Never pay more for an acquisition than you have to.", "Rule of Acquisition #3"),
            ("Never allow family to stand in the way of opportunity.", "Rule of Acquisition #6"),
            ("A deal is a deal... until a better one comes along.", "Rule of Acquisition #16"),
            ("Never place friendship above profit.", "Rule of Acquisition #21"),
            ("Nothing is more important than your health... except for your money.", "Rule of Acquisition #23"),
            ("There is no substitute for success.", "Rule of Acquisition #26"),
            ("There is nothing more dangerous than an honest businessman.", "Rule of Acquisition #27"),
            ("The riskier the road, the greater the profit.", "Rule of Acquisition #62"),
            ("Knowledge equals profit.", "Rule of Acquisition #74"),
            ("Ask not what your profits can do for you, but what you can do for your profits.", "Rule of Acquisition #89"),
            ("Know your enemies, but do business with them always.", "Rule of Acquisition #177"),
            ("Not even dishonesty can tarnish the shine of profit.", "Rule of Acquisition #181"),
            ("Let others keep their reputation. You keep their money.", "Rule of Acquisition #189"),
            // FFXIV
            ("You might say that everythin's for sale here in Ul'dah — as long as you've got the gil.", "Momodi Modi"),
            ("The wealth of Ul'dah is not without limits, my friends.", "Lolorito Nanarito"),
            ("'Twould seem Your Grace has matured beyond acts of earnest yet misplaced charity.", "Lolorito Nanarito"),
            ("What profit is there for Ul'dah in this arrangement?", "Godbert Manderville"),
            ("For all our potential, we are indolent creatures by nature. If unconditional charity is all we know, then we begin to rely upon it — to expect it.", "Godbert Manderville"),
            ("The Scions need gil, and lots of it!", "Tataru Taru"),
            ("I'm sorry — did I hear that right? You used Scion funds to buy Gosetsu's katana... at the asking price?", "Tataru Taru"),
            ("For coin and country!", "Immortal Flames"),
            ("We need people workin' and spendin' and bickerin' like the old days!", "Momodi Modi"),
            // Villains
            ("The lion does not concern himself with the opinions of the sheep.", "Tywin Lannister"),
            ("I'll keep it short and sweet. Family, religion, friendship. These are the three demons you must slay if you wish to succeed in business.", "Mr. Burns"),
            ("I don't have to be nice. I'm loaded.", "Mr. Burns"),
            ("You can't spell 'Montgomery' without M-O-N-E-Y.", "Mr. Burns"),
            ("I will not part with a single coin! Not one piece of it!", "Smaug"),
            ("You are a wealthy man now. And one must learn to be rich. To be poor, anyone can manage.", "Gus Fring"),
            ("Lesson number one: Don't underestimate the other guy's greed.", "Frank Lopez"),
            ("Greed may not be good, but it's not so bad either. You humans think greed is just for money and power, but everyone wants something they don't have.", "Greed"),
            // Misc
            ("There is one and only one social responsibility of business — to increase its profits.", "Milton Friedman"),
            ("A penny saved is a penny earned.", "Benjamin Franklin"),
            // New — Real-World Tycoons
            ("The ability to deal with people is as purchasable a commodity as sugar or coffee, and I will pay more for that ability than for any other under the sun.", "John D. Rockefeller"),
            ("Any fool can make a fortune. It takes a man of brains to hold onto it.", "Cornelius Vanderbilt"),
            ("A business absolutely devoted to service will have only one worry about profits. They will be embarrassingly large.", "Henry Ford"),
            ("It takes character to sit with all that cash and to do nothing. I didn't get to where I am by going after mediocre opportunities.", "Charlie Munger"),
            ("I take pride in the creation of my wealth, in its existence, and in the uses to which it has been and is being put.", "J. Paul Getty"),
            ("After a certain point, money is meaningless. It ceases to be the goal. The game is what counts.", "Aristotle Onassis"),
            ("The secret of business is to know something that nobody else knows.", "Aristotle Onassis"),
            ("To be successful, keep looking tanned, live in an elegant building even if you're in the cellar, be seen in smart restaurants even if you nurse one drink, and if you borrow, borrow big.", "Aristotle Onassis"),
            ("Every man has his price, or a guy like me couldn't exist.", "Howard Hughes"),
            ("If you're not a risk taker, you should get the hell out of business.", "Ray Kroc"),
            ("Some people get rich studying artificial intelligence. Me, I make money studying natural stupidity.", "Carl Icahn"),
            ("Luxury goods are the only area in which it is possible to make luxury margins.", "Bernard Arnault"),
            // New — Wits & Philosophers
            ("It is better to have a permanent income than to be fascinating.", "Oscar Wilde"),
            ("Lack of money is the root of all evil.", "George Bernard Shaw"),
            ("When somebody says it's not about the money, it's about the money.", "H.L. Mencken"),
            ("Money frees you from doing things you dislike. Since I dislike doing nearly everything, money is handy.", "Groucho Marx"),
            ("While money can't buy happiness, it certainly lets you choose your own form of misery.", "Groucho Marx"),
            ("It's morally wrong to allow a sucker to keep his money.", "W.C. Fields"),
            ("If you want to know what God thinks of money, just look at the people He gives it to.", "Dorothy Parker"),
            // New — Film & Fiction
            ("Money never sleeps, pal.", "Gordon Gekko"),
            ("I dive around in it like a porpoise! And I burrow through it like a gopher! And I toss it up and let it hit me on the head!", "Scrooge McDuck"),
            ("I want you to deal with your problems by becoming rich!", "Jordan Belfort"),
            ("In this country, you gotta make the money first. Then when you get the money, you get the power.", "Tony Montana"),
            ("Hello, I like money.", "Mr. Krabs"),
            ("Time is money! And if you boys are wasting time, then you're wasting money!", "Mr. Krabs"),
            ("Screw the rules, I have money!", "Seto Kaiba"),
            ("Gold wins wars, not soldiers.", "Littlefinger"),
            ("I want to earn enough money so I can get away from everyone.", "Daniel Plainview"),
            // New — Star Wars
            ("Every situation has the potential to be profitable.", "Hondo Ohnaka"),
            ("Mind tricks don't work on me. Only money.", "Watto"),
            // New — Pirates
            ("Take what you can, give nothing back!", "Captain Jack Sparrow"),
            // New — Ferengi Rules of Acquisition
            ("Opportunity plus instinct equals profit.", "Rule of Acquisition #9"),
            ("Only fools pay retail.", "Rule of Acquisition #141"),
    };

    foreach (var (text, author) in quotes)
    {
      using var cmd = new SqliteCommand(
          "INSERT INTO quotes (text, author) VALUES (@t, @a)", connection);
      cmd.Parameters.AddWithValue("@t", text);
      cmd.Parameters.AddWithValue("@a", author);
      cmd.ExecuteNonQuery();
    }
  }

  /// <summary>Seeds category_groups display mapping. Only called when schema version is below 1.</summary>
  private static void SeedCategoryGroups(SqliteConnection connection)
  {
    var groups = new (string uiCategory, string displayGroup)[]
    {
            // Crafting Materials
            ("Lumber", "Crafting Materials"),
            ("Stone", "Crafting Materials"),
            ("Metal", "Crafting Materials"),
            ("Cloth", "Crafting Materials"),
            ("Leather", "Crafting Materials"),
            ("Bone", "Crafting Materials"),
            ("Reagent", "Crafting Materials"),
            ("Part", "Crafting Materials"),
            ("Catalyst", "Crafting Materials"),
            ("Crystal", "Crafting Materials"),
            ("Ingredient", "Crafting Materials"),
            ("Demimateria", "Crafting Materials"),

            // Consumables
            ("Meal", "Consumables"),
            ("Medicine", "Consumables"),
            ("Seafood", "Consumables"),

            // Gear — Armor
            ("Head", "Armor"),
            ("Body", "Armor"),
            ("Hands", "Armor"),
            ("Legs", "Armor"),
            ("Feet", "Armor"),
            ("Shield", "Armor"),
            ("Outfits", "Armor"),

            // Gear — Accessories
            ("Necklace", "Accessories"),
            ("Earrings", "Accessories"),
            ("Bracelets", "Accessories"),
            ("Ring", "Accessories"),

            // Gear — Weapons (combat)
            ("Pugilist's Arm", "Weapons"),
            ("Gladiator's Arm", "Weapons"),
            ("Marauder's Arm", "Weapons"),
            ("Archer's Arm", "Weapons"),
            ("Lancer's Arm", "Weapons"),
            ("One-handed Thaumaturge's Arm", "Weapons"),
            ("Two-handed Thaumaturge's Arm", "Weapons"),
            ("One-handed Conjurer's Arm", "Weapons"),
            ("Two-handed Conjurer's Arm", "Weapons"),
            ("Arcanist's Grimoire", "Weapons"),
            ("Scholar's Arm", "Weapons"),
            ("Rogue's Arm", "Weapons"),
            ("Dark Knight's Arm", "Weapons"),
            ("Machinist's Arm", "Weapons"),
            ("Astrologian's Arm", "Weapons"),
            ("Samurai's Arm", "Weapons"),
            ("Red Mage's Arm", "Weapons"),
            ("Gunbreaker's Arm", "Weapons"),
            ("Dancer's Arm", "Weapons"),
            ("Reaper's Arm", "Weapons"),
            ("Sage's Arm", "Weapons"),
            ("Blue Mage's Arm", "Weapons"),
            ("Viper's Arm", "Weapons"),
            ("Pictomancer's Arm", "Weapons"),

            // Gear — Tools (DoH/DoL)
            ("Carpenter's Primary Tool", "Tools"),
            ("Carpenter's Secondary Tool", "Tools"),
            ("Blacksmith's Primary Tool", "Tools"),
            ("Blacksmith's Secondary Tool", "Tools"),
            ("Armorer's Primary Tool", "Tools"),
            ("Armorer's Secondary Tool", "Tools"),
            ("Goldsmith's Primary Tool", "Tools"),
            ("Goldsmith's Secondary Tool", "Tools"),
            ("Leatherworker's Primary Tool", "Tools"),
            ("Leatherworker's Secondary Tool", "Tools"),
            ("Weaver's Primary Tool", "Tools"),
            ("Weaver's Secondary Tool", "Tools"),
            ("Alchemist's Primary Tool", "Tools"),
            ("Alchemist's Secondary Tool", "Tools"),
            ("Culinarian's Primary Tool", "Tools"),
            ("Culinarian's Secondary Tool", "Tools"),
            ("Miner's Primary Tool", "Tools"),
            ("Miner's Secondary Tool", "Tools"),
            ("Botanist's Primary Tool", "Tools"),
            ("Botanist's Secondary Tool", "Tools"),
            ("Fisher's Primary Tool", "Tools"),
            ("Fisher's Secondary Tool", "Tools"),
            ("Fishing Tackle", "Tools"),

            // Housing
            ("Furnishing", "Housing"),
            ("Outdoor Furnishing", "Housing"),
            ("Table", "Housing"),
            ("Tabletop", "Housing"),
            ("Wall-mounted", "Housing"),
            ("Rug", "Housing"),
            ("Ceiling Light", "Housing"),
            ("Construction Permit", "Housing"),
            ("Roof", "Housing"),
            ("Exterior Wall", "Housing"),
            ("Window", "Housing"),
            ("Door", "Housing"),
            ("Roof Decoration", "Housing"),
            ("Exterior Wall Decoration", "Housing"),
            ("Placard", "Housing"),
            ("Fence", "Housing"),
            ("Interior Wall", "Housing"),
            ("Flooring", "Housing"),
            ("Painting", "Housing"),

            // Collectibles
            ("Minion", "Collectibles"),
            ("Orchestrion Roll", "Collectibles"),
            ("Triple Triad Card", "Collectibles"),

            // Other
            ("Materia", "Materia"),
            ("Dye", "Dye"),
            ("Gardening", "Gardening"),
            ("Miscellany", "Miscellany"),
            ("Seasonal Miscellany", "Miscellany"),
    };

    foreach (var (uiCategory, displayGroup) in groups)
    {
      using var cmd = new SqliteCommand(
          "INSERT INTO category_groups (ui_category, display_group) VALUES (@ui, @dg)",
          connection);
      cmd.Parameters.AddWithValue("@ui", uiCategory);
      cmd.Parameters.AddWithValue("@dg", displayGroup);
      cmd.ExecuteNonQuery();
    }
  }

  // =========================================================================
  // Schema V2: Add macro_group to category_groups
  // =========================================================================

  /// <summary>
  /// Schema v2: Add macro_group column to category_groups for 3-level category tree.
  /// Column-guarded: a database left half-climbed by a pre-transaction crash still
  /// gets past this rung instead of throwing "duplicate column" forever.
  /// </summary>
  private static void MigrateV2(SqliteConnection connection)
  {
    SchemaGuards.EnsureColumns(connection, "category_groups",
      "macro_group TEXT NOT NULL DEFAULT ''");

    var macroMap = new Dictionary<string, string>
    {
      { "Armor", "Gear" },
      { "Weapons", "Gear" },
      { "Tools", "Gear" },
      { "Accessories", "Gear" },
      { "Crafting Materials", "Crafting" },
      { "Consumables", "Consumables" },
      { "Housing", "Housing" },
      { "Collectibles", "Collectibles" },
      { "Materia", "Other" },
      { "Dye", "Other" },
      { "Gardening", "Other" },
      { "Miscellany", "Other" },
    };

    foreach (var (displayGroup, macroGroup) in macroMap)
    {
      using var cmd = new SqliteCommand(
          "UPDATE category_groups SET macro_group = @macro WHERE display_group = @dg",
          connection);
      cmd.Parameters.AddWithValue("@macro", macroGroup);
      cmd.Parameters.AddWithValue("@dg", displayGroup);
      cmd.ExecuteNonQuery();
    }
  }

  // =========================================================================
  // Schema V3: Add source to market_snapshots
  // =========================================================================

  /// <summary>Schema v3: Add source column to market_snapshots to distinguish full vs single-retainer runs.</summary>
  private static void MigrateV3(SqliteConnection connection)
  {
    // Column may already exist from CreateTables (fresh installs / DB resets)
    using var check = new SqliteCommand(
        "SELECT COUNT(*) FROM pragma_table_info('market_snapshots') WHERE name='source'", connection);
    if ((long)check.ExecuteScalar()! > 0) return;

    using var cmd = new SqliteCommand(
        "ALTER TABLE market_snapshots ADD COLUMN source TEXT NOT NULL DEFAULT 'full'",
        connection);
    cmd.ExecuteNonQuery();
  }

  /// <summary>V4: Add last_sale_prices table and populate from existing transactions.</summary>
  private static void MigrateV4(SqliteConnection connection)
  {
    using var create = new SqliteCommand(
        @"CREATE TABLE IF NOT EXISTS last_sale_prices (
            item_id INTEGER PRIMARY KEY,
            unit_price INTEGER NOT NULL,
            timestamp INTEGER NOT NULL
        )", connection);
    create.ExecuteNonQuery();

    // Backfill from existing transaction history
    using var backfill = new SqliteCommand(
        @"INSERT OR REPLACE INTO last_sale_prices (item_id, unit_price, timestamp)
          SELECT item_id, unit_price, MAX(timestamp)
          FROM transactions
          WHERE direction = 'earned' AND source = 'retainer_sale' AND item_id > 0
          GROUP BY item_id", connection);
    backfill.ExecuteNonQuery();
  }

  /// <summary>V5: Add indexes for snapshot dedup and transaction range queries.</summary>
  private static void MigrateV5(SqliteConnection connection)
  {
    using var idx1 = new SqliteCommand(
        "CREATE INDEX IF NOT EXISTS idx_gil_snapshots_ts ON gil_snapshots(timestamp DESC)",
        connection);
    idx1.ExecuteNonQuery();

    using var idx2 = new SqliteCommand(
        "CREATE INDEX IF NOT EXISTS idx_txn_ts_dir ON transactions(timestamp, direction)",
        connection);
    idx2.ExecuteNonQuery();
  }

  /// <summary>
  /// V6: Fix retainer_sale amounts. UnitPrice from RetainerHistoryHook is the total
  /// sale price, not per-unit. Existing records with qty > 1 have inflated amounts.
  /// Corrects: amount = old unit_price (which was actually the total),
  ///           unit_price = old unit_price / quantity (real per-unit price).
  /// Also fixes last_sale_prices which stored the inflated "unit" price.
  /// </summary>
  private static void MigrateV6(SqliteConnection connection)
  {
    using var fix = new SqliteCommand(
      @"UPDATE transactions
        SET amount = unit_price,
            unit_price = unit_price / quantity
        WHERE source = 'retainer_sale' AND quantity > 1",
      connection);
    var affected = fix.ExecuteNonQuery();

    using var fixLsp = new SqliteCommand(
      @"UPDATE last_sale_prices
        SET unit_price = (
          SELECT t.unit_price FROM transactions t
          WHERE t.source = 'retainer_sale'
            AND t.item_id = last_sale_prices.item_id
          ORDER BY t.timestamp DESC LIMIT 1
        )
        WHERE item_id IN (
          SELECT DISTINCT item_id FROM transactions
          WHERE source = 'retainer_sale' AND quantity > 1
        )",
      connection);
    fixLsp.ExecuteNonQuery();

    LogInfo($"[GilTrack] V6 migration: fixed {affected} retainer_sale amounts (UnitPrice was total, not per-unit)");
  }

  /// <summary>
  /// V7: Add is_pending column to transactions. Rows from chat-parsed retainer sale
  /// messages insert with is_pending = 1 and get promoted to 0 when the RetainerHistoryHook
  /// later reconciles them with authoritative server data (retainer name, buyer, real
  /// timestamp). Partial index keeps the promotion lookup fast without indexing the common case.
  /// </summary>
  private static void MigrateV7(SqliteConnection connection)
  {
    SchemaGuards.EnsureColumns(connection, "transactions",
      "is_pending INTEGER NOT NULL DEFAULT 0");

    using var addIdx = new SqliteCommand(
      "CREATE INDEX IF NOT EXISTS idx_txn_pending ON transactions(is_pending, item_id, quantity, amount) WHERE is_pending = 1",
      connection);
    addIdx.ExecuteNonQuery();
  }

  /// <summary>
  /// V8: Delete catchall rows that duplicate a vendor_sale row. Pre-fix the vendor
  /// sell orchestrators didn't Block the catchall around the sell action, so every
  /// vendor sale got a paired (source='catchall', direction='earned') row at the
  /// same amount and within ~1s of the vendor_sale row. One-shot cleanup.
  /// </summary>
  private static void MigrateV8(SqliteConnection connection)
  {
    using var fix = new SqliteCommand(
      @"DELETE FROM transactions
        WHERE source = 'catchall'
          AND direction = 'earned'
          AND EXISTS (
            SELECT 1 FROM transactions v
            WHERE v.source = 'vendor_sale'
              AND v.amount = transactions.amount
              AND ABS(v.timestamp - transactions.timestamp) <= 1
          )",
      connection);
    var affected = fix.ExecuteNonQuery();

    LogInfo($"[GilTrack] V8 migration: removed {affected} catchall rows duplicating vendor_sale");
  }

  /// <summary>
  /// V9: Add desynth_runs table. One row per desynthesis run started by
  /// the DesynthOrchestrator. Tracks lifecycle (start, end, mode, total
  /// items selected) and abort state for diagnostic value.
  /// </summary>
  private static void MigrateV9(SqliteConnection connection)
  {
    if (SchemaGuards.TableExists(connection, "desynth_runs")) return;

    using var cmd = new SqliteCommand(
      @"CREATE TABLE desynth_runs (
          id              INTEGER PRIMARY KEY AUTOINCREMENT,
          started_at      INTEGER NOT NULL,
          ended_at        INTEGER,
          mode            TEXT NOT NULL,
          total_items     INTEGER NOT NULL,
          aborted_reason  TEXT
        );
        CREATE INDEX ix_desynth_runs_started_at ON desynth_runs(started_at DESC);",
      connection);
    cmd.ExecuteNonQuery();
    LogInfo("V9 migration: created desynth_runs table");
  }

  /// <summary>
  /// V10: Add desynth_yields table. One row per yield event observed in
  /// chat during a desynth run. attempt_seq groups yields from one act
  /// (multiple materials per desynth share the same attempt_seq).
  /// </summary>
  private static void MigrateV10(SqliteConnection connection)
  {
    if (SchemaGuards.TableExists(connection, "desynth_yields")) return;

    using var cmd = new SqliteCommand(
      @"CREATE TABLE desynth_yields (
          id              INTEGER PRIMARY KEY AUTOINCREMENT,
          run_id          INTEGER NOT NULL,
          attempt_seq     INTEGER NOT NULL,
          source_item_id  INTEGER NOT NULL,
          source_is_hq    INTEGER NOT NULL DEFAULT 0,
          yield_item_id   INTEGER NOT NULL,
          yield_qty       INTEGER NOT NULL,
          yield_is_hq     INTEGER NOT NULL DEFAULT 0,
          captured_at     INTEGER NOT NULL,
          FOREIGN KEY (run_id) REFERENCES desynth_runs(id)
        );
        CREATE INDEX ix_desynth_yields_run ON desynth_yields(run_id, attempt_seq);
        CREATE INDEX ix_desynth_yields_source ON desynth_yields(source_item_id);
        CREATE INDEX ix_desynth_yields_captured ON desynth_yields(captured_at DESC);",
      connection);
    cmd.ExecuteNonQuery();
    LogInfo("V10 migration: created desynth_yields table");
  }

  /// <summary>
  /// V12: Add triage_flags table. Persistent triage - outlier warnings,
  /// upward-reprice holds, and cap blocks survive restarts and stay open
  /// until acted on (repriced/pulled) or dismissed. One open flag per
  /// (item, hq, retainer, reason); re-flagging refreshes the row.
  /// </summary>
  private static void MigrateV12(SqliteConnection connection)
  {
    if (SchemaGuards.TableExists(connection, "triage_flags")) return;

    using var cmd = new SqliteCommand(
      @"CREATE TABLE triage_flags (
          id            INTEGER PRIMARY KEY AUTOINCREMENT,
          created_at    INTEGER NOT NULL,
          item_id       INTEGER NOT NULL,
          is_hq         INTEGER NOT NULL DEFAULT 0,
          retainer_name TEXT NOT NULL DEFAULT '',
          slot_index    INTEGER NOT NULL DEFAULT -1,
          reason        TEXT NOT NULL,
          detail        TEXT NOT NULL DEFAULT '',
          old_price     INTEGER NOT NULL DEFAULT 0,
          flagged_price INTEGER NOT NULL DEFAULT 0,
          status        TEXT NOT NULL DEFAULT 'open',
          acted_at      INTEGER
        );
        CREATE INDEX ix_triage_flags_status ON triage_flags(status, created_at DESC);",
      connection);
    cmd.ExecuteNonQuery();
    LogInfo("V12 migration: created triage_flags table");
  }

  /// <summary>
  /// V11: Convert desynth timestamps from Unix milliseconds to Unix seconds
  /// so the whole ledger shares one convention (cross-table joins with
  /// transactions/gil_snapshots). Not a bug fix — the store was internally
  /// consistent in ms — pure convention cleanup. Diffable: same rows, any
  /// value that looks like ms (> 1e11) is divided by 1000; second-based
  /// values pass through untouched, so re-running is a no-op.
  /// </summary>
  private static void MigrateV11(SqliteConnection connection)
  {
    using var cmd = new SqliteCommand(
      @"UPDATE desynth_runs   SET started_at  = started_at  / 1000 WHERE started_at  > 100000000000;
        UPDATE desynth_runs   SET ended_at    = ended_at    / 1000 WHERE ended_at    > 100000000000;
        UPDATE desynth_yields SET captured_at = captured_at / 1000 WHERE captured_at > 100000000000;",
      connection);
    var affected = cmd.ExecuteNonQuery();
    LogInfo($"V11 migration: desynth timestamps ms -> s ({affected} values converted)");
  }

  /// <summary>
  /// V13: Split last_sale_prices by quality. NQ and HQ sell at different
  /// prices and rates (the listing gate judges them separately), so the key
  /// becomes (item_id, is_hq). Also adds sold_after_days — how long the
  /// listing sat before selling, captured forward from sale reconciliation
  /// (null for historical rows). Backfill: per-quality latest sale from
  /// transactions; rows only present in the old table (pruned transactions)
  /// carry over as NQ rather than being dropped.
  /// </summary>
  private static void MigrateV13(SqliteConnection connection)
  {
    // The one destructive migration (DROP + RENAME): atomic or not at all. The
    // ladder's rung transaction is what makes it so - this method used to open
    // its own, back when it was the only rung that had one.

    // Rows only the old table knows (their transactions were pruned) carry
    // over as NQ - their HQ split is unknowable. Count them for the log so a
    // "why is my HQ history blind" question has an answer on record.
    using var countCmd = new SqliteCommand(
      @"SELECT COUNT(*) FROM last_sale_prices WHERE item_id NOT IN (
          SELECT DISTINCT item_id FROM transactions
          WHERE direction = 'earned' AND source = 'retainer_sale' AND item_id > 0)",
      connection);
    var carriedAsNq = Convert.ToInt32(countCmd.ExecuteScalar());

    using var cmd = new SqliteCommand(
      @"CREATE TABLE last_sale_prices_v13 (
          item_id         INTEGER NOT NULL,
          is_hq           INTEGER NOT NULL DEFAULT 0,
          unit_price      INTEGER NOT NULL,
          timestamp       INTEGER NOT NULL,
          sold_after_days INTEGER,
          PRIMARY KEY (item_id, is_hq)
        );
        INSERT OR REPLACE INTO last_sale_prices_v13 (item_id, is_hq, unit_price, timestamp)
          SELECT item_id, is_hq, unit_price, MAX(timestamp)
          FROM transactions
          WHERE direction = 'earned' AND source = 'retainer_sale' AND item_id > 0
          GROUP BY item_id, is_hq;
        INSERT OR IGNORE INTO last_sale_prices_v13 (item_id, is_hq, unit_price, timestamp)
          SELECT item_id, 0, unit_price, timestamp FROM last_sale_prices;
        DROP TABLE last_sale_prices;
        ALTER TABLE last_sale_prices_v13 RENAME TO last_sale_prices;",
      connection);
    cmd.ExecuteNonQuery();
    LogInfo("V13 migration: last_sale_prices split by quality (item_id, is_hq) + sold_after_days");
    if (carriedAsNq > 0)
      LogInfo($"V13: {carriedAsNq} pruned-history rows carried over as NQ - their HQ price history starts fresh at the next HQ sale");
  }

  /// <summary>
  /// V14: Add routing_overrides table. Every time the player overrules a
  /// routing verdict (checks a gated item in the Hawk window), the disagreement
  /// is recorded. Day-one requirement of the routing brain design.
  ///
  /// <para>WHAT ACTUALLY READS IT TODAY: the confidence tier's demotion count
  /// (GetRoutingOverrideCounts) and the persisted-ruling replay
  /// (GetLatestRoutingRulings). A third reader - the triage case's override-history
  /// trap - died with the traps register (3b-1). Nothing suggests a config tweak off these rows, and
  /// nothing ever has - this doc used to say recurring overrides do, which read
  /// as a description of shipped behavior. The knob-tuning read is the 4.0
  /// scoreboard's, and it is why the table keeps banking rulings the tier loop
  /// deliberately ignores (the Desynth&lt;-&gt;Vendor reshuffles); V46 gives those
  /// rulings the receipt link that read will need.</para>
  /// </summary>
  private static void MigrateV14(SqliteConnection connection)
  {
    if (SchemaGuards.TableExists(connection, "routing_overrides")) return;

    using var cmd = new SqliteCommand(
      @"CREATE TABLE routing_overrides (
          id             INTEGER PRIMARY KEY AUTOINCREMENT,
          created_at     INTEGER NOT NULL,
          item_id        INTEGER NOT NULL,
          is_hq          INTEGER NOT NULL DEFAULT 0,
          ilvl           INTEGER NOT NULL DEFAULT 0,
          router_verdict TEXT NOT NULL,
          router_reason  TEXT NOT NULL DEFAULT '',
          player_verdict TEXT NOT NULL
        );
        CREATE INDEX ix_routing_overrides_item ON routing_overrides(item_id, is_hq);",
      connection);
    cmd.ExecuteNonQuery();
    LogInfo("V14 migration: created routing_overrides table");
  }

  /// <summary>
  /// V15: venture-return tracking. New venture_returns table (one row per
  /// collected quick-venture result) + nullable venture_tokens column on
  /// gil_snapshots so token stock rides the existing bell snapshots.
  /// Fresh installs get both via CreateTables; the ALTER is guarded so a
  /// fresh DB that already has the column migrates cleanly.
  /// </summary>
  private static void MigrateV15(SqliteConnection connection)
  {
    using (var cmd = new SqliteCommand(
      @"CREATE TABLE IF NOT EXISTS venture_returns (
          id            INTEGER PRIMARY KEY AUTOINCREMENT,
          captured_at   INTEGER NOT NULL,
          retainer_name TEXT NOT NULL,
          item_id       INTEGER NOT NULL,
          quantity      INTEGER NOT NULL,
          is_hq         INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_venture_returns_captured
          ON venture_returns(captured_at DESC);",
      connection))
      cmd.ExecuteNonQuery();

    var hasColumn = false;
    using (var check = new SqliteCommand("PRAGMA table_info(gil_snapshots);", connection))
    using (var reader = check.ExecuteReader())
      while (reader.Read())
        if (reader.GetString(1) == "venture_tokens") { hasColumn = true; break; }

    if (!hasColumn)
      using (var alter = new SqliteCommand(
        "ALTER TABLE gil_snapshots ADD COLUMN venture_tokens INTEGER;", connection))
        alter.ExecuteNonQuery();

    LogInfo("V15 migration: venture_returns table + gil_snapshots.venture_tokens");
  }

  /// <summary>
  /// V16: the Universalis almanac cache. One row per (item, world) holding
  /// per-quality sale velocity, most recent sale, and lastUploadTime for the
  /// trust gate — so routing verdicts survive restarts without refetching.
  /// Null last_upload_at = Universalis has no data for the item.
  /// </summary>
  private static void MigrateV16(SqliteConnection connection)
  {
    using (var cmd = new SqliteCommand(
      @"CREATE TABLE IF NOT EXISTS universalis_stats (
          item_id        INTEGER NOT NULL,
          world_id       INTEGER NOT NULL,
          nq_velocity    REAL NOT NULL DEFAULT 0,
          hq_velocity    REAL NOT NULL DEFAULT 0,
          last_sale_at   INTEGER,
          last_upload_at INTEGER,
          fetched_at     INTEGER NOT NULL,
          PRIMARY KEY (item_id, world_id)
        );",
      connection))
      cmd.ExecuteNonQuery();

    LogInfo("V16 migration: universalis_stats cache table");
  }

  /// <summary>
  /// V17: decision memory on triage_flags. Adds an evidence column — the
  /// snapshot of the world a hold was judged against (standing listing, sale
  /// count, newest sale, cheapest competitor) — so a re-flag can ask "did
  /// anything change?" instead of firing every pinch. The ALTER is guarded so
  /// a fresh DB that already has the column (future CreateTables) migrates
  /// cleanly.
  ///
  /// Same migration one-shots the lane-rewrite legacy: upward_held and
  /// outlier_warn lost their producer code in branch 1, so no processing pass
  /// will ever re-confirm them. The self-heal round clears the ones whose item
  /// gets pinched again; this closes the strays whose item never does, so the
  /// triage inbox stops rendering dead questions immediately rather than
  /// waiting on a trigger that may never fire. Idempotent — a second run
  /// matches zero open rows.
  /// </summary>
  private static void MigrateV17(SqliteConnection connection)
  {
    var hasColumn = false;
    using (var check = new SqliteCommand("PRAGMA table_info(triage_flags);", connection))
    using (var reader = check.ExecuteReader())
      while (reader.Read())
        if (reader.GetString(1) == "evidence") { hasColumn = true; break; }

    if (!hasColumn)
      using (var alter = new SqliteCommand(
        "ALTER TABLE triage_flags ADD COLUMN evidence TEXT NOT NULL DEFAULT '';", connection))
        alter.ExecuteNonQuery();

    using var cleanup = new SqliteCommand(
      @"UPDATE triage_flags
        SET status = 'resolved', acted_at = @now
        WHERE status = 'open' AND reason IN ('upward_held', 'outlier_warn')",
      connection);
    cleanup.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    var closed = cleanup.ExecuteNonQuery();

    LogInfo($"V17 migration: triage_flags.evidence column + closed {closed} dead-producer flags (upward_held/outlier_warn)");
  }

  /// <summary>
  /// V18: dedupes open triage flags. DecideUpsert treated a legacy row's empty
  /// evidence as "no open flag" and INSERTED a second row next to it (fixed in
  /// the same commit), leaving duplicate open questions per
  /// (item, hq, retainer, reason) key. One-shot: the keeper is the OLDEST row
  /// ("held since" stays honest), it adopts the newest non-empty evidence +
  /// detail from its group, and the younger duplicates are deleted — they are
  /// bug artifacts describing the same open question, not player history.
  /// Idempotent — a deduped table matches zero rows.
  /// </summary>
  private static void MigrateV18(SqliteConnection connection)
  {
    // Keepers with a legacy '' snapshot adopt the best evidence in their group
    // (newest evidenced duplicate) before the duplicates are removed.
    using (var adopt = new SqliteCommand(
      @"UPDATE triage_flags
        SET evidence = (SELECT t2.evidence FROM triage_flags t2
                        WHERE t2.status = 'open'
                          AND t2.item_id = triage_flags.item_id
                          AND t2.is_hq = triage_flags.is_hq
                          AND t2.retainer_name = triage_flags.retainer_name
                          AND t2.reason = triage_flags.reason
                          AND t2.evidence <> ''
                        ORDER BY t2.created_at DESC, t2.id DESC LIMIT 1),
            detail   = (SELECT t2.detail FROM triage_flags t2
                        WHERE t2.status = 'open'
                          AND t2.item_id = triage_flags.item_id
                          AND t2.is_hq = triage_flags.is_hq
                          AND t2.retainer_name = triage_flags.retainer_name
                          AND t2.reason = triage_flags.reason
                          AND t2.evidence <> ''
                        ORDER BY t2.created_at DESC, t2.id DESC LIMIT 1)
        WHERE status = 'open' AND evidence = ''
          AND EXISTS (SELECT 1 FROM triage_flags t2
                      WHERE t2.status = 'open'
                        AND t2.item_id = triage_flags.item_id
                        AND t2.is_hq = triage_flags.is_hq
                        AND t2.retainer_name = triage_flags.retainer_name
                        AND t2.reason = triage_flags.reason
                        AND t2.evidence <> '')",
      connection))
      adopt.ExecuteNonQuery();

    // Delete every open row that has an OLDER open sibling on the same key.
    using var dedup = new SqliteCommand(
      @"DELETE FROM triage_flags
        WHERE status = 'open'
          AND EXISTS (SELECT 1 FROM triage_flags t2
                      WHERE t2.status = 'open'
                        AND t2.item_id = triage_flags.item_id
                        AND t2.is_hq = triage_flags.is_hq
                        AND t2.retainer_name = triage_flags.retainer_name
                        AND t2.reason = triage_flags.reason
                        AND (t2.created_at < triage_flags.created_at
                             OR (t2.created_at = triage_flags.created_at
                                 AND t2.id < triage_flags.id)))",
      connection);
    var removed = dedup.ExecuteNonQuery();

    LogInfo($"V18 migration: deduped triage_flags — removed {removed} duplicate open flags (oldest row kept, evidence adopted)");
  }

  /// <summary>
  /// V19: market memory + decision receipts (M4, [[Scrooge - Market Memory - Design]]).
  /// Creates three new tables and one guarded column; the old listings table's shape
  /// is NOT touched. This migration RETIRES the listings-table tripwire: writes to
  /// market memory through the append-diff path below are expected and correct, while
  /// ad hoc writes to the old listings table remain wrong.
  ///
  /// - market_board_snapshot: the current-board read model (the "cache of the last
  ///   diff"). One row per live foreign/own board listing, keyed by soft identity
  ///   (item, hq, retainer, qty); price mutable. The design's "snapshot table" - it
  ///   did not exist per-listing before M4 (the board was in-memory only), so it is
  ///   created here rather than repurposed.
  /// - market_events: the append-only diff log (appeared/disappeared/price_moved),
  ///   with observation-window columns (seen_after/seen_by - no foreign point
  ///   timestamp), observer provenance (own_scan now, community is the 4.0 seam), and
  ///   certainty tier + disappearance resolution (own upgrades to sold via GilTrack;
  ///   foreign stays gone).
  /// - decision_receipts: one row per pricing decision, all coordinates RELATIVE,
  ///   carrying arm_id + item_category + stack coords from day one; the outcome join
  ///   (time_to_clear / outcome_state) fills later, never at write time.
  /// - triage_flags.scope: the container a lane_held flag points at (board vs
  ///   inventory) so the zombie round only closes what the observing run can prove
  ///   absent. Guarded ALTER; legacy rows default '' (Unknown = never zombie-closed).
  ///
  /// Diffable + idempotent (V11 model): every CREATE is IF NOT EXISTS and the ALTER is
  /// column-guarded, so a re-run is a no-op.
  /// </summary>
  private static void MigrateV19(SqliteConnection connection)
  {
    MarketMemorySchema.ApplyV19(connection);
    LogInfo("V19 migration: market_board_snapshot + market_events + decision_receipts tables, triage_flags.scope column; listings-table tripwire retired");
  }

  /// <summary>
  /// V23: two halves of the same regime-anchor substrate
  /// ([[Scrooge - Lane Pricing - Design]], amendment).
  ///
  /// First, the cleanup: V13's quality split carried old quality-blind rows over
  /// as NQ, so any item whose last pre-split sale was HQ got a phantom NQ twin -
  /// identical price and timestamp to its HQ row - and those phantoms have fed
  /// the lane's LastSale evidence ever since (138 in the live DB, all predating
  /// V13's ship date; the live writer is quality-correct and produces none).
  ///
  /// Second, the tape: the sale_history table banks the MB history packet that
  /// every pinch already receives and discards. Substrate only - no readers yet.
  /// SQL lives in SaleHistorySchema (Dalamud-free, linked-source tested).
  /// </summary>
  private static void MigrateV23(SqliteConnection connection)
  {
    var deleted = SaleHistorySchema.DeleteV13PhantomNqRows(connection);
    LogInfo($"V23: {deleted} phantom NQ rows deleted (V13 carry)");

    SaleHistorySchema.ApplyV23(connection);
    LogInfo("V23 migration: sale_history table - the banked tape of the board's settled sales");
  }

}
