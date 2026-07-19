using System;
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
  /// <summary>GC Expert Delivery turn-in. Renders in the one run window (ruling 9); seals value/ETA stay on the orchestrator's own RunLifecycle for the inline Ledger glance.</summary>
  Gc,
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

  /// <summary>Listings whose cut was deepened by slow-mover pressure this run.</summary>
  public int SlowMoversDeepened { get; set; }

  /// <summary>Running total of gil value across all processed listings.</summary>
  public long TotalListingGil { get; set; }

  /// <summary>Number of items vendor-sold during the run.</summary>
  public int VendorSoldCount { get; set; }

  /// <summary>Total gil earned from vendor sales during the run.</summary>
  public long VendorSoldGil { get; set; }

  // --- Lifecycle (run-host contract, M6 session 1) ---
  // The RunLifecycle core is the ONE owner of run state, counts and ETA - the old
  // TotalItems/ItemsProcessed/IsComplete fields delegate so no counter exists twice.
  // 60s stall bound: a generous multiple of the MB retry ladder's longest window.

  /// <summary>The run-host lifecycle this run rides (state, progress, ETA, summary).</summary>
  internal RunLifecycle Lifecycle { get; } = new(TimeSpan.FromSeconds(60));

  /// <summary>Total items expected in this run (from pre-scan). Setter starts the lifecycle when idle.</summary>
  public int TotalItems
  {
    get => Lifecycle.Total;
    set => Lifecycle.SetTotal(value, RunValueUnit.Gil, DateTime.UtcNow,
      seededMsPerItem: Plugin.Configuration?.AvgMsPerItem ?? 0);
  }

  /// <summary>Items processed so far (success or skip). Advance with <see cref="Beat"/>.</summary>
  public int ItemsProcessed => Lifecycle.Done;

  /// <summary>Whether the run has ended (complete or cancelled). Set via <see cref="MarkComplete"/>/<see cref="MarkCancelled"/>.</summary>
  public bool IsComplete => Lifecycle.IsTerminal;

  /// <summary>One item processed. Starts the lifecycle if the path never declared a total (desynth).</summary>
  public void Beat()
  {
    Lifecycle.EnsureRunning(RunValueUnit.Gil, DateTime.UtcNow);
    Lifecycle.RecordProgress(1, 0, DateTime.UtcNow);
  }

  /// <summary>The run finished its work. Safe on never-started paths (starts then completes).</summary>
  public void MarkComplete()
  {
    Lifecycle.EnsureRunning(RunValueUnit.Gil, DateTime.UtcNow);
    Lifecycle.Complete(DateTime.UtcNow);
  }

  /// <summary>The player cancelled. Safe on never-started paths.</summary>
  public void MarkCancelled()
  {
    Lifecycle.EnsureRunning(RunValueUnit.Gil, DateTime.UtcNow);
    Lifecycle.Cancel(DateTime.UtcNow);
  }

  /// <summary>
  /// FK into desynth_runs.id while a desynth run is in flight. Null for
  /// non-desynth runs and outside any run.
  /// </summary>
  public long? DesynthRunId { get; set; }

  // --- Timing ---

  /// <summary>Tracks total run duration (display + the AvgMsPerItem persistence blend).</summary>
  public Stopwatch RunStopwatch { get; } = new();

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

  /// <summary>Adds a run-level event entry (start, end, summary).</summary>
  public void AddRunEntry(RunEvent eventType, string message)
  {
    LogEntries.Add(new RunEntry(eventType, message));
  }

  /// <summary>Running total of desynth material value this run (cached last-sale prices).</summary>
  public long MaterialsValueGil { get; set; }

  /// <summary>Adds a desynth yield sub-row and accumulates its cached value.</summary>
  public void AddYieldEntry(string yieldName, int qty, bool isHq, long value)
  {
    LogEntries.Add(new YieldEntry(yieldName, qty, isHq, value));
    MaterialsValueGil += value;
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

  /// <summary>Count of log entries with the given outcome. Powers the per-type lane summary lines.</summary>
  public int CountOutcome(ItemOutcome outcome) => LogEntries.OfType<LogEntry>().Count(e => e.Outcome == outcome);

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
    PricingResult.UpwardHeld => true,
    PricingResult.LaneHeld => true,
    PricingResult.NoData => true,
    _ => false,
  };
}
