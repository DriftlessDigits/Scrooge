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
  /// <summary>Venture Coffer rider: opens coffers at the front of the round's disposition and narrates each unlocked item. No pricing/lane semantics - a bag-unlock pass.</summary>
  Coffer,

  /// <summary>
  /// RECON (Rounds unit 2): the hawk's per-item loop run all the way to the spine
  /// and then CANCELLED instead of priced. Every read a hawk item makes, recon
  /// makes - the board packet, the proxy completion, the tape, the decision
  /// receipt, the market-memory diff, the self-heal round - because recon's reads
  /// ARE pinch-grade evidence and that is the entire point of paying for them.
  /// What it never does is write a price or confirm a panel.
  ///
  /// <para>It is a RunMode rather than a flag because
  /// <see cref="ItemPricingPipeline"/> asks "which run am I in" to route the
  /// pricing path, and recon's path is genuinely a third one - not a pinch (there
  /// is no standing listing to reprice) and not a hawk (nothing is being
  /// listed).</para>
  /// </summary>
  Recon,
}

/// <summary>
/// Holds all per-run business state: the current item, run statistics, price cache,
/// log entries, and triage data. Owned by the run executors, read by LedgerWindow for
/// display. Replaces scattered state across LedgerWindow, ItemPricingPipeline,
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

  /// <summary>
  /// This is the TRIAGE executor's run - pulls, vendors and reprices off the
  /// Ledger's listed rows (the header's Go button, or the round's reprice stage).
  ///
  /// <para>It is deliberately NOT a RunMode value. <see cref="Mode"/> is the
  /// PRICING PIPELINE's concept, and a triage reprice must keep reading as
  /// <see cref="RunMode.Pinch"/> there - that is what routes it through the pinch
  /// price path (ItemPricingPipeline.IsPinchRun). This flag changes only how the
  /// run NARRATES, which is the entire gap: on 07-24 a Go run cleared three
  /// sections of the Ledger without a single line in the run log - "same errand,
  /// half narrated, half invisible".</para>
  /// </summary>
  public bool IsStandingRun { get; init; }

  /// <summary>The item currently being evaluated. Set before each item enters the pipeline.</summary>
  public PricingItem? CurrentItem { get; set; }

  /// <summary>Name of the retainer currently being processed.</summary>
  public string CurrentRetainer { get; set; } = "";

  // --- Run statistics ---

  /// <summary>Items whose price actually MOVED (or were freshly listed, on a hawk).</summary>
  public int ItemsAdjusted { get; set; }

  /// <summary>
  /// Items evaluated and left standing at their ask (ruled 2026-08-15: "114
  /// adjusted" was counting rows where nothing moved). A held row is a decision,
  /// not an adjustment - the summary speaks them apart, same tense rule that
  /// split Held from Reprice in the run log.
  /// </summary>
  public int ItemsHeld { get; set; }

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

  // --- The bell's two registers (F9, ruled 08-22) ---
  // The gauge read "0 gil" over a run that had listed a million and vendored 17k,
  // because the vendor gil accrued on the HAWK's own lifecycle (which nothing
  // renders mid-round) and the listing value accrued nowhere. Both registers live
  // HERE - on the run the wizard actually draws - and the progress line composes
  // them: "24 listed, ~1.1M asked · 17k vendored", each clause only when nonzero.

  /// <summary>How many rows this run has put on the board.</summary>
  public int ListedCount { get; private set; }

  /// <summary>The gil this run has ASKED across those listings (ask x stack) -
  /// a would-earn, never summed with earned gil (first law).</summary>
  public long GilAsked { get; private set; }

  /// <summary>One listing posted: count it and bank its ask.</summary>
  public void RecordListed(long unitAsk, int quantity)
  {
    ListedCount++;
    GilAsked += unitAsk * Math.Max(1, quantity);
  }

  /// <summary>Vendor gil earned by this run - a value beat on THIS lifecycle,
  /// the one the gauge renders (the drop the lap caught: TrackVendorSale fed a
  /// lifecycle nobody was drawing).</summary>
  public void RecordVendored(long gil)
  {
    Lifecycle.EnsureRunning(RunValueUnit.Gil, DateTime.UtcNow);
    Lifecycle.RecordProgress(0, gil, DateTime.UtcNow);
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
  public void AddLogEntry(ItemOutcome outcome, string itemName, string message, string? hover = null)
  {
    InsertRetainerHeaderIfNeeded();
    LogEntries.Add(new LogEntry(outcome, CurrentRetainer, itemName, message) { Hover = hover });
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

  // --- Standing listings (Phase 2) ---

  /// <summary>Collected PricingItems for skipped/error results. Powers the post-run triage UI.</summary>
  public List<PricingItem> StandingItems { get; } = [];

  /// <summary>Checks if a PricingResult should be collected for triage review.</summary>
  public static bool IsStandingResult(PricingResult result) => result switch
  {
    PricingResult.BelowFloor => true,
    PricingResult.CapBlocked => true,
    PricingResult.LaneHeld => true,
    PricingResult.NoData => true,
    _ => false,
  };
}
