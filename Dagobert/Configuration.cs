using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using System;
using System.Collections.Generic;

namespace Dagobert;

/// <summary>
/// Determines how the undercut price is calculated relative to the lowest MB listing.
/// </summary>
public enum UndercutMode
{
  /// <summary>Subtract a fixed gil amount from the lowest listing.</summary>
  FixedAmount,
  /// <summary>Subtract a percentage of the lowest listing's price.</summary>
  Percentage,
  /// <summary>Match the lowest listing exactly — no undercut.</summary>
  GentlemansMatch,
  /// <summary>Undercut by rounding down to a clean number. Interval scales with price.</summary>
  CleanNumbers
}

/// <summary>
/// Determines the minimum price floor when listing items.
/// Items priced below the floor are skipped during auto-pinch.
/// </summary>
public enum PriceFloorMode
{
  /// <summary>No price floor — list at any price.</summary>
  None,
  /// <summary>Skip if undercut price falls below vendor sell price (Item.PriceLow).</summary>
  Vendor,
  /// <summary>Skip if undercut price falls below 2x vendor sell price (Doman Enclave rate).</summary>
  DomanEnclave
}

/// <summary>
/// Persisted plugin configuration. Serialized to JSON by Dalamud.
/// Default values are used both for new installs and when deserializing
/// older configs that are missing newly added properties.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
  public int Version { get; set; } = 0;

  // --- Pricing behavior ---

  /// <summary>Use HQ price when the listed item is HQ.</summary>
  public bool HQ { get; set; } = true;

  public UndercutMode UndercutMode { get; set; } = UndercutMode.FixedAmount;

  /// <summary>
  /// Amount to undercut by. Interpreted as gil (FixedAmount) or percent (Percentage).
  /// Ignored in GentlemansMatch mode.
  /// </summary>
  public int UndercutAmount { get; set; } = 1;

  /// <summary>
  /// Safety cap: skip the item if the undercut would exceed this percentage.
  /// Prevents catastrophic price drops from outlier listings.
  /// </summary>
  public float MaxUndercutPercentage { get; set; } = 100.0f;

  /// <summary>If true, undercut your own retainer listings too.</summary>
  public bool UndercutSelf { get; set; } = false;

  /// <summary>
  /// Determines the price floor check mode. None disables the check (default),
  /// Vendor skips items below vendor sell price,
  /// DomanEnclave skips items below 2x vendor sell price.
  /// </summary>
  public PriceFloorMode PriceFloorMode { get; set; } = PriceFloorMode.None;

  /// <summary>
  /// Minimum gil price for any listing. Items below this price are skipped.
  /// Set to 0 to disable (default).
  /// </summary>
  public int MinimumListingPrice { get; set; } = 0;

  // --- Outlier detection ---

  /// <summary>
  /// When enabled, detects and skips abnormally low "bait" listings on the MB.
  /// Uses largest gap detection across the top listings to find suspicious price drops.
  /// </summary>
  public bool OutlierDetection { get; set; } = true;

  /// <summary>
  /// Threshold for outlier detection. A price cliff is detected when the gap between
  /// two adjacent listings exceeds this percentage.
  /// Example: 50 means a listing at 40 gil is bait if the next is 100 gil (60% gap > 50%).
  /// Higher = more tolerant. Lower = catches smaller gaps.
  /// </summary>
  public float OutlierThresholdPercent { get; set; } = 50f;

  /// <summary>
  /// How many additional listings past the first to check for a price cliff.
  /// Range 1–9. A value of 3 means: compare the first listing against the next 3.
  /// </summary>
  public int OutlierSearchWindow { get; set; } = 3;

  // --- Timing ---

  /// <summary>Delay before opening the MB price list. Too low = prices fail to load.</summary>
  public int GetMBPricesDelayMS { get; set; } = 3000;

  /// <summary>How long to keep the MB open when fetching prices.</summary>
  public int MarketBoardKeepOpenMS { get; set; } = 1000;

  // --- Hotkeys ---

  /// <summary>Enable hotkey to start auto-pinch from the retainer sell list.</summary>
  public bool EnablePinchKey { get; set; } = false;

  public VirtualKey PinchKey { get; set; } = VirtualKey.Q;

  /// <summary>Enable hotkey to auto-pinch when posting a new item.</summary>
  public bool EnablePostPinchkey { get; set; } = true;

  public VirtualKey PostPinchKey { get; set; } = VirtualKey.SHIFT;

  // --- Chat output ---

  public bool ShowErrorsInChat { get; set; } = true;

  public bool ShowPriceAdjustmentsMessages { get; set; } = true;

  public bool ShowOutlierDetectionMessages { get; set; } = true;

  public bool ShowRetainerNames { get; set; } = true;

  // --- Text-to-speech ---

  public bool TTSWhenAllDone { get; set; } = false;

  public string TTSWhenAllDoneMsg { get; set; } = "Finished auto pinching all retainers";

  public bool TTSWhenEachDone { get; set; } = false;

  public string TTSWhenEachDoneMsg { get; set; } = "Auto Pinch done";

  public int TTSVolume { get; set; } = 20;

  /// <summary>Auto-set to true on platforms where System.Speech is unavailable.</summary>
  public bool DontUseTTS { get; set; } = false;

  /// <summary>
  /// Set of retainer names that are enabled for auto pinch.
  /// If empty or null, all retainers are enabled by default.
  /// If contains ALL_DISABLED_SENTINEL, all retainers are disabled.
  /// </summary>
  public const string ALL_DISABLED_SENTINEL = "__ALL_DISABLED__";
  
  public HashSet<string> EnabledRetainerNames { get; set; } = [];

  /// <summary>
  /// List of retainer names that were last fetched from the game.
  /// Used to display retainer selection even when the retainer list is not open.
  /// </summary>
  public List<string> LastKnownRetainerNames { get; set; } = [];

  public void Save()
  {
    Plugin.PluginInterface.SavePluginConfig(this);
  }
}