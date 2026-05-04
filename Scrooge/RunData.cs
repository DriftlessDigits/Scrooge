using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Scrooge.Windows;

namespace Scrooge;

/// <summary>
/// Determines what kind of run is in progress.
/// </summary>
public enum RunMode
{
  /// <summary>Repricing existing retainer listings.</summary>
  Pinch,
  /// <summary>Listing new items from inventory onto retainers.</summary>
  Hawk,
  /// <summary>Hands-off desynthesis automation over the SalvageItemSelector addon.</summary>
  Desynth,
}

/// <summary>
/// Holds all per-run business state: the current item, run statistics, price cache,
/// log entries, and triage data. Owned by AutoPinch, read by PinchRunLogWindow for
/// display. Replaces scattered state across PinchRunLogWindow, ItemPricingPipeline,
/// and Communicator.
///
/// Lifecycle: created at run start, populated during the run, read after run ends.
/// A new RunData is created for each run — no reset needed.
/// </summary>
internal class RunData
{
  // --- Run identity ---

  /// <summary>What kind of run this is.</summary>
  public RunMode Mode { get; init; }

  /// <summary>The item currently being evaluated. Set before each item enters the pipeline.</summary>
  public PricingItem? CurrentItem { get; set; }

  /// <summary>Name of the retainer currently being processed.</summary>
  public string CurrentRetainer { get; set; } = "";

  // --- Run statistics ---

  /// <summary>Items that were successfully repriced or listed.</summary>
  public int ItemsAdjusted { get; set; }

  /// <summary>Number of outlier detections during the run.</summary>
  public int OutliersDetected { get; set; }

  /// <summary>Running total of gil value across all processed listings.</summary>
  public long TotalListingGil { get; set; }

  /// <summary>Number of items vendor-sold during the run.</summary>
  public int VendorSoldCount { get; set; }

  /// <summary>Total gil earned from vendor sales during the run.</summary>
  public long VendorSoldGil { get; set; }

  /// <summary>Total items expected in this run (from pre-scan).</summary>
  public int TotalItems { get; set; }

  /// <summary>Items processed so far (success or skip).</summary>
  public int ItemsProcessed { get; set; }

  /// <summary>Whether the run has completed (or was cancelled).</summary>
  public bool IsComplete { get; set; }

  // --- Timing ---

  /// <summary>Tracks total run duration.</summary>
  public Stopwatch RunStopwatch { get; } = new();

  /// <summary>Tracks time since last ETA update.</summary>
  public Stopwatch EtaStopwatch { get; } = new();

  /// <summary>Estimated remaining time in milliseconds.</summary>
  public float EtaCountdownMs { get; set; }

  // --- Price cache (survives across items within a run) ---

  /// <summary>
  /// Cache of item name → calculated price. Allows skipping MB queries for
  /// items already priced in this run. Replaces _cachedPrices on ItemPricingPipeline.
  /// </summary>
  public Dictionary<string, int?> CachedPrices { get; } = [];

  // --- Log entries ---

  /// <summary>All log entries for the run (items, retainer headers, run events).</summary>
  public List<ILogItem> LogEntries { get; } = [];

  /// <summary>Tracks which retainer header was last inserted (prevents duplicates).</summary>
  private string _lastRetainerHeader = "";

  // --- Log methods ---

  /// <summary>Adds an item entry to the log, inserting a retainer header if needed.</summary>
  public void AddLogEntry(ItemOutcome outcome, string itemName, string message)
  {
    InsertRetainerHeaderIfNeeded();
    LogEntries.Add(new LogEntry(outcome, CurrentRetainer, itemName, message));
  }

  /// <summary>Adds an outlier entry with bait/used prices for colored rendering.</summary>
  public void AddOutlierEntry(string itemName, int baitPrice, int usedPrice)
  {
    InsertRetainerHeaderIfNeeded();
    LogEntries.Add(new LogEntry(ItemOutcome.Outlier, CurrentRetainer, itemName, "")
    {
      BaitPrice = baitPrice,
      UsedPrice = usedPrice,
    });
  }

  /// <summary>Adds a run-level event entry (start, end, summary).</summary>
  public void AddRunEntry(RunEvent eventType, string message)
  {
    LogEntries.Add(new RunEntry(eventType, message));
  }

  private void InsertRetainerHeaderIfNeeded()
  {
    if (CurrentRetainer != _lastRetainerHeader && !string.IsNullOrEmpty(CurrentRetainer))
    {
      LogEntries.Add(new RetainerHeader(CurrentRetainer));
      _lastRetainerHeader = CurrentRetainer;
    }
  }

  // --- Summary ---

  /// <summary>Number of items skipped due to rule violations.</summary>
  public int SkippedCount => LogEntries.OfType<LogEntry>().Count(e => e.Outcome == ItemOutcome.Skipped);

  /// <summary>Number of items with no MB data.</summary>
  public int NoDataCount => LogEntries.OfType<LogEntry>().Count(e => e.Outcome == ItemOutcome.NoData);

  // --- Triage (Phase 2) ---

  /// <summary>Collected PricingItems for skipped/error results. Powers the post-run triage UI.</summary>
  public List<PricingItem> TriageItems { get; } = [];

  /// <summary>Checks if a PricingResult should be collected for triage review.</summary>
  public static bool IsTriageResult(PricingResult result) => result switch
  {
    PricingResult.BelowFloor => true,
    PricingResult.BelowMinimum => true,
    PricingResult.CapBlocked => true,
    PricingResult.UndercutTooDeep => true,
    PricingResult.NoData => true,
    _ => false,
  };
}
