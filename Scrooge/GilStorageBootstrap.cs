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
  private const int CurrentSchemaVersion = 3;

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
                source TEXT NOT NULL DEFAULT 'pinch_run'
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
