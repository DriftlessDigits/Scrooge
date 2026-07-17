using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ECommons.DalamudServices;
using Microsoft.Data.Sqlite;

namespace Scrooge;


/// <summary>
/// Handles first-time database setup: table creation, JSON migration,
/// and seed data. Called once per startup from GilStorage.Initialize().
/// Future schema migrations go here, gated by PRAGMA user_version.
/// </summary>
internal class GilStorageBootstrap
{
  /// <summary>Entry point — runs all bootstrap steps in order.</summary>
  internal static void Run(SqliteConnection connection)
  {
    var version = GetSchemaVersion(connection);

    if (version < 1)
    {
      CreateTables(connection);
      if (!MigrateFromJson(connection))
        return; // Migration failed — don't stamp version, retry next startup
      SeedQuotes(connection);
      SeedCategoryGroups(connection);
      SetSchemaVersion(connection, 1);
    }

    if (version < 2)
    {
      MigrateV2(connection);
      SetSchemaVersion(connection, 2);
    }

    if (version < 3)
    {
      MigrateV3(connection);
      SetSchemaVersion(connection, 3);
    }

    if (version < 4)
    {
      MigrateV4(connection);
      SetSchemaVersion(connection, 4);
    }

    if (version < 5)
    {
      MigrateV5(connection);
      SetSchemaVersion(connection, 5);
    }

    if (version < 6)
    {
      MigrateV6(connection);
      SetSchemaVersion(connection, 6);
    }

    if (version < 7)
    {
      MigrateV7(connection);
      SetSchemaVersion(connection, 7);
    }

    if (version < 8)
    {
      MigrateV8(connection);
      SetSchemaVersion(connection, 8);
    }

    if (version < 9)
    {
      MigrateV9(connection);
      SetSchemaVersion(connection, 9);
    }

    if (version < 10)
    {
      MigrateV10(connection);
      SetSchemaVersion(connection, 10);
    }

    if (version < 11)
    {
      MigrateV11(connection);
      SetSchemaVersion(connection, 11);
    }

    if (version < 12)
    {
      MigrateV12(connection);
      SetSchemaVersion(connection, 12);
    }

    if (version < 13)
    {
      MigrateV13(connection);
      SetSchemaVersion(connection, 13);
    }

    if (version < 14)
    {
      MigrateV14(connection);
      SetSchemaVersion(connection, 14);
    }

    if (version < 15)
    {
      MigrateV15(connection);
      SetSchemaVersion(connection, 15);
    }

    if (version < 16)
    {
      MigrateV16(connection);
      SetSchemaVersion(connection, 16);
    }

    if (version < 17)
    {
      MigrateV17(connection);
      SetSchemaVersion(connection, 17);
    }

    if (version < 18)
    {
      MigrateV18(connection);
      SetSchemaVersion(connection, 18);
    }

    // Idempotent fixes — safe to run every startup
    using var fixDashes = new SqliteCommand(
        "UPDATE category_groups SET ui_category = REPLACE(ui_category, '–', '-') WHERE ui_category LIKE '%–%'",
        connection);
    fixDashes.ExecuteNonQuery();
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
  /// Imports data from the old gil_data.json into SQLite, then renames to .bak.
  /// Uses GilStorage write methods so all SQL stays in one place.
  /// Wrapped in a transaction for atomicity — if anything fails, the JSON is preserved.
  /// Returns true if migration succeeded or no JSON file exists. Returns false on failure
  /// (caller should NOT stamp schema version so migration retries next startup).
  /// </summary>
  private static bool MigrateFromJson(SqliteConnection connection)
  {
    var jsonPath = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "gil_data.json");
    if (!File.Exists(jsonPath)) return true; // No file to migrate — success

    try
    {
      var json = File.ReadAllText(jsonPath);
      var data = JsonSerializer.Deserialize<GilData>(json);
      if (data == null) return false; // Corrupt/empty — fail, retry next time

      using var transaction = connection.BeginTransaction();

      // Sales -> transactions
      foreach (var sale in data.Sales)
      {
        GilStorage.InsertTransaction(sale.SaleTimestamp, "earned", "retainer_sale",
            sale.TotalGil, sale.ItemId, sale.ItemName, sale.Category,
            sale.Quantity, sale.UnitPrice, sale.IsHQ, sale.RetainerName,
            sale.BuyerName, transaction);
      }

      // GilSnapshots -> gil_snapshots + retainer_snapshots
      foreach (var snap in data.GilHistory)
      {
        var snapshotId = GilStorage.InsertGilSnapshot(snap.Timestamp, snap.PlayerGil, "pinch_run", transaction);
        foreach (var (name, gil) in snap.RetainerGil)
        {
          GilStorage.InsertRetainerSnapshot(snapshotId, name, gil, transaction);
        }
      }

      // MarketSnapshots -> market_snapshots
      foreach (var ms in data.MarketHistory)
      {
        GilStorage.InsertMarketSnapshot(ms.Timestamp, ms.ItemCount,
            ms.TotalListingValue, ms.AverageListingAgeDays, "full", transaction);
      }

      // CurrentListings -> listings
      foreach (var listing in data.CurrentListings)
      {
        GilStorage.UpsertListing(listing.RetainerName, listing.SlotIndex,
            listing.ItemId, listing.ItemName, listing.Category,
            listing.UnitPrice, listing.Quantity, listing.IsHQ,
            listing.FirstSeenTimestamp, listing.LastUpdatedTimestamp, transaction);
      }

      transaction.Commit();
      File.Move(jsonPath, jsonPath + ".bak", overwrite: true);
      Svc.Log.Info($"[GilTrack] Migrated {data.Sales.Count} sales, " +
          $"{data.GilHistory.Count} snapshots from JSON to SQLite. Backup: gil_data.json.bak");
      return true;
    }
    catch (Exception ex)
    {
      Svc.Log.Error(ex, "[GilTrack] JSON migration failed — gil_data.json preserved");
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

    using var transaction = connection.BeginTransaction();
    foreach (var (text, author) in quotes)
    {
      using var cmd = new SqliteCommand(
          "INSERT INTO quotes (text, author) VALUES (@t, @a)", connection);
      cmd.Transaction = transaction;
      cmd.Parameters.AddWithValue("@t", text);
      cmd.Parameters.AddWithValue("@a", author);
      cmd.ExecuteNonQuery();
    }
    transaction.Commit();
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

    using var transaction = connection.BeginTransaction();
    foreach (var (uiCategory, displayGroup) in groups)
    {
      using var cmd = new SqliteCommand(
          "INSERT INTO category_groups (ui_category, display_group) VALUES (@ui, @dg)",
          connection);
      cmd.Transaction = transaction;
      cmd.Parameters.AddWithValue("@ui", uiCategory);
      cmd.Parameters.AddWithValue("@dg", displayGroup);
      cmd.ExecuteNonQuery();
    }
    transaction.Commit();
  }

  // =========================================================================
  // Schema V2: Add macro_group to category_groups
  // =========================================================================

  /// <summary>Schema v2: Add macro_group column to category_groups for 3-level category tree.</summary>
  private static void MigrateV2(SqliteConnection connection)
  {
    using var alterCmd = new SqliteCommand(
        "ALTER TABLE category_groups ADD COLUMN macro_group TEXT NOT NULL DEFAULT ''",
        connection);
    alterCmd.ExecuteNonQuery();

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

    using var transaction = connection.BeginTransaction();
    foreach (var (displayGroup, macroGroup) in macroMap)
    {
      using var cmd = new SqliteCommand(
          "UPDATE category_groups SET macro_group = @macro WHERE display_group = @dg",
          connection);
      cmd.Transaction = transaction;
      cmd.Parameters.AddWithValue("@macro", macroGroup);
      cmd.Parameters.AddWithValue("@dg", displayGroup);
      cmd.ExecuteNonQuery();
    }
    transaction.Commit();
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

    Svc.Log.Info($"[GilTrack] V6 migration: fixed {affected} retainer_sale amounts (UnitPrice was total, not per-unit)");
  }

  /// <summary>
  /// V7: Add is_pending column to transactions. Rows from chat-parsed retainer sale
  /// messages insert with is_pending = 1 and get promoted to 0 when the RetainerHistoryHook
  /// later reconciles them with authoritative server data (retainer name, buyer, real
  /// timestamp). Partial index keeps the promotion lookup fast without indexing the common case.
  /// </summary>
  private static void MigrateV7(SqliteConnection connection)
  {
    using var addCol = new SqliteCommand(
      "ALTER TABLE transactions ADD COLUMN is_pending INTEGER NOT NULL DEFAULT 0",
      connection);
    addCol.ExecuteNonQuery();

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

    Svc.Log.Info($"[GilTrack] V8 migration: removed {affected} catchall rows duplicating vendor_sale");
  }

  /// <summary>
  /// V9: Add desynth_runs table. One row per desynthesis run started by
  /// the DesynthOrchestrator. Tracks lifecycle (start, end, mode, total
  /// items selected) and abort state for diagnostic value.
  /// </summary>
  private static void MigrateV9(SqliteConnection connection)
  {
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
    Svc.Log.Info("[Scrooge] V9 migration: created desynth_runs table");
  }

  /// <summary>
  /// V10: Add desynth_yields table. One row per yield event observed in
  /// chat during a desynth run. attempt_seq groups yields from one act
  /// (multiple materials per desynth share the same attempt_seq).
  /// </summary>
  private static void MigrateV10(SqliteConnection connection)
  {
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
    Svc.Log.Info("[Scrooge] V10 migration: created desynth_yields table");
  }

  /// <summary>
  /// V12: Add triage_flags table. Persistent triage - outlier warnings,
  /// upward-reprice holds, and cap blocks survive restarts and stay open
  /// until acted on (repriced/pulled) or dismissed. One open flag per
  /// (item, hq, retainer, reason); re-flagging refreshes the row.
  /// </summary>
  private static void MigrateV12(SqliteConnection connection)
  {
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
    Svc.Log.Info("[Scrooge] V12 migration: created triage_flags table");
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
    Svc.Log.Info($"[Scrooge] V11 migration: desynth timestamps ms -> s ({affected} values converted)");
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
    // The one destructive migration (DROP + RENAME): atomic or not at all.
    // Without the transaction, a crash between DROP and RENAME leaves
    // user_version=12 with the v13 table present - the bare CREATE then
    // throws on every subsequent boot and storage is dead permanently.
    using var tx = connection.BeginTransaction();

    // Rows only the old table knows (their transactions were pruned) carry
    // over as NQ - their HQ split is unknowable. Count them for the log so a
    // "why is my HQ history blind" question has an answer on record.
    using var countCmd = new SqliteCommand(
      @"SELECT COUNT(*) FROM last_sale_prices WHERE item_id NOT IN (
          SELECT DISTINCT item_id FROM transactions
          WHERE direction = 'earned' AND source = 'retainer_sale' AND item_id > 0)",
      connection, tx);
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
      connection, tx);
    cmd.ExecuteNonQuery();
    tx.Commit();
    Svc.Log.Info("[Scrooge] V13 migration: last_sale_prices split by quality (item_id, is_hq) + sold_after_days");
    if (carriedAsNq > 0)
      Svc.Log.Info($"[Scrooge] V13: {carriedAsNq} pruned-history rows carried over as NQ - their HQ price history starts fresh at the next HQ sale");
  }

  /// <summary>
  /// V14: Add routing_overrides table. Every time the player overrules a
  /// routing verdict (checks a gated item in the Hawk window), the disagreement
  /// is recorded — recurring overrides suggest config tweaks, and the history
  /// is the context a future judgment hook would need. Day-one requirement of
  /// the routing brain design.
  /// </summary>
  private static void MigrateV14(SqliteConnection connection)
  {
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
    Svc.Log.Info("[Scrooge] V14 migration: created routing_overrides table");
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

    Svc.Log.Info("[Scrooge] V15 migration: venture_returns table + gil_snapshots.venture_tokens");
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

    Svc.Log.Info("[Scrooge] V16 migration: universalis_stats cache table");
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
  /// will ever re-confirm them. The self-heal sweep clears the ones whose item
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

    Svc.Log.Info($"[Scrooge] V17 migration: triage_flags.evidence column + closed {closed} dead-producer flags (upward_held/outlier_warn)");
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

    Svc.Log.Info($"[Scrooge] V18 migration: deduped triage_flags — removed {removed} duplicate open flags (oldest row kept, evidence adopted)");
  }

  // =========================================================================
  // Legacy JSON model (used only for migration from v2.2.0)
  // =========================================================================

  private class GilData
  {
    public List<SaleRecord> Sales { get; set; } = [];
    public List<GilSnapshot> GilHistory { get; set; } = [];
    public List<MarketSnapshot> MarketHistory { get; set; } = [];
    public List<ListingRecord> CurrentListings { get; set; } = [];
  }
}
