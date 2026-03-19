using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ECommons.DalamudServices;

namespace Scrooge;

/// <summary>
/// Persistent storage for gil tracking data.
/// Stored separately from IPluginConfiguration to avoid bloating the config file.
/// </summary>
internal static class GilStorage
{
  private static string FilePath => Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "gil_data.json");

  public static GilData Data { get; private set; } = new();

  public static void Load()
  {
    try
    {
      if (File.Exists(FilePath))
      {
        var json = File.ReadAllText(FilePath);
        Data = JsonSerializer.Deserialize<GilData>(json) ?? new GilData();
      }
    }
    catch (Exception ex)
    {
      Svc.Log.Error(ex, "Failed to load gil tracking data");
      Data = new GilData();
    }

    Prune();
  }

  public static void Save()
  {
    try
    {
      var dir = Path.GetDirectoryName(FilePath);
      if (!Directory.Exists(dir))
        Directory.CreateDirectory(dir!);

      var json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
      File.WriteAllText(FilePath, json);
    }
    catch (Exception ex)
    {
      Svc.Log.Error(ex, "Failed to save gil tracking data");
    }
  }

  /// <summary>Prune old data: sales older than 90 days, gil history to one-per-day after 30 days.</summary>
  private static void Prune()
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var ninetyDays = 90 * 24 * 3600L;
    var thirtyDays = 30 * 24 * 3600L;

    Data.Sales.RemoveAll(s => now - s.SaleTimestamp > ninetyDays);

    // Thin out old gil history: keep one per day for entries older than 30 days
    var oldEntries = Data.GilHistory.Where(g => now - g.Timestamp > thirtyDays).ToList();
    if (oldEntries.Count > 1)
    {
      var grouped = oldEntries.GroupBy(g => DateTimeOffset.FromUnixTimeSeconds(g.Timestamp).Date);
      var keep = grouped.Select(g => g.Last()).ToList();
      Data.GilHistory.RemoveAll(g => now - g.Timestamp > thirtyDays);
      Data.GilHistory.AddRange(keep);
      Data.GilHistory.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
    }

    // Same thinning for market history
    var oldMarket = Data.MarketHistory.Where(m => now - m.Timestamp > thirtyDays).ToList();
    if (oldMarket.Count > 1)
    {
      var grouped = oldMarket.GroupBy(m => DateTimeOffset.FromUnixTimeSeconds(m.Timestamp).Date);
      var keep = grouped.Select(g => g.Last()).ToList();
      Data.MarketHistory.RemoveAll(m => now - m.Timestamp > thirtyDays);
      Data.MarketHistory.AddRange(keep);
      Data.MarketHistory.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
    }
  }
}