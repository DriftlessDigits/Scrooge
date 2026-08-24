using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System.Linq;
using System.Diagnostics;
using ECommons.DalamudServices;

namespace Scrooge.Windows
{

  // The log row types (ItemOutcome, RunEvent, ILogItem and the four row records) and
  // the banked-shape translations live in RunLogEntries.cs now - none of them touches
  // ImGui, and parking them in this file was the whole reason the flatten/restore
  // mapping could not be linked into the test project. See RoundLogEntry.

  /// <summary>
  /// THE LEDGER - the Round's transcript, and the one surface that reads it whole.
  ///
  /// <para>The name was REISSUED here on 2026-08-10 (Rounds unit 5). It used to belong
  /// to the judgment desk - piles, verbs, rulings - which is a desk, not a book; the
  /// Accountant took that job and its own name with it. This window is the book: the
  /// banked <c>round_log</c> rehydrated across a reload, plus the run in hand, in one
  /// continuous read. The Accountant makes the rounds and writes the Ledger.</para>
  ///
  /// <para>Outside a Round it is exactly what it always was - one run's log, retitled
  /// per run mode. Nothing about the single-run path changed with the name.</para>
  /// </summary>
  internal class LedgerWindow : Window
  {

    // UI-only state (not business data)
    private bool _autoScroll = true;

    // Convenience accessor — falls back to reading RunData, keeps UI working after run ends
    private RunData? Run => Plugin.CurrentRun ?? _lastRun;
    private RunData? _lastRun;

    /// <summary>
    /// THE ROUND'S ONE TRANSCRIPT (2026-07-26). Finished stages' entries, kept beside
    /// the live run rather than inside it - see <see cref="RunLogCarry{T}"/> for why
    /// the per-run lists must stay untouched.
    /// </summary>
    private readonly RunLogCarry<ILogItem> _carry = new();

    /// <summary>The run whose entries are already in the transcript - adopting twice would double it.</summary>
    private RunData? _carriedRun;

    /// <summary>
    /// The banked run this transcript belongs to (round_runs.id), or 0 when nothing is
    /// banking - a standalone run, a pre-V40 round, or a session whose storage never
    /// opened. Zero degrades to exactly the pre-unit-4 behaviour: one in-memory
    /// transcript that dies with the session.
    /// </summary>
    private long _runId;

    /// <summary>
    /// The stage whose lines are being banked right now. Set immediately before each
    /// <see cref="RunLogCarry{T}.Adopt"/> because the batch IS a stage - a run's
    /// entries are adopted whole, at that run's own end - and the stage is the column
    /// per-stage durations are later derived from.
    /// </summary>
    private string _bankStage = "";

    /// <summary>
    /// Everything the window shows, oldest first: the round so far, then the run in
    /// hand.
    ///
    /// <para>A run whose entries have already been adopted is NOT concatenated again.
    /// Before unit 4 adoption happened at the next run's start, so the finished run
    /// was never both carried and current; banking at the checkpoint moved adoption
    /// to the run's own end, and without this guard the last stage would render twice
    /// for as long as the player stood between stages - which is most of a round.</para>
    /// </summary>
    private IEnumerable<ILogItem> VisibleEntries(RunData? run)
      => run is null ? _carry.Entries
        : ReferenceEquals(run, _carriedRun) ? _carry.Entries
        : _carry.Entries.Count == 0 ? run.LogEntries
        : _carry.Entries.Concat(run.LogEntries);

    /// <summary>
    /// A round started: from here to its end the log APPENDS. The round is one errand
    /// and its stages are chapters - the stage dividers the log already writes
    /// ("--- Desynth Run Complete - 5:11 PM ---") are exactly the chapter breaks, so
    /// nothing new has to be drawn to make it read as one thing.
    ///
    /// <para>And from here the transcript is BANKED as well as carried (unit 4): the
    /// write-through sink is wired to the round's own run id, so every chapter lands
    /// in round_log the moment it is adopted. A round with no banked run (id 0) wires
    /// nothing and behaves exactly as it always did.</para>
    /// </summary>
    public void HoldForRound(long runId = 0)
    {
      _runId = runId;
      _carry.WriteThrough = runId > 0 ? BankLines : null;
      _carry.Hold();
      _carriedRun = null;
    }

    /// <summary>
    /// A RESTORED round's transcript, read back from the bank - the reload no longer
    /// eats it. Puts the carry into holding around the banked lines rather than
    /// clearing them, and re-wires the sink so the stages still ahead bank too.
    /// </summary>
    public void RehydrateForRound(long runId, IReadOnlyList<RoundLogLine> banked)
    {
      _runId = runId;
      _carry.WriteThrough = runId > 0 ? BankLines : null;
      _carry.Rehydrate(banked.Select(RoundLogEntry.Restore).ToList());
      _carriedRun = null;
    }

    /// <summary>
    /// The round ended - cancelled or done. The banking stops here and the book STAYS
    /// ON SCREEN (review ruling S11, 2026-08-12: "don't overcomplicate: delete the
    /// wipe").
    ///
    /// <para>The comment beside the cancel always said the banked transcript was
    /// deliberately left standing, and it was - but this method cleared the carry in
    /// the same breath, and the only other reader of <c>round_log</c> is the restore,
    /// which never runs for a round whose Active flag the cancel just dropped. So the
    /// player pressed done, opened the Ledger, and read an empty book about the round
    /// he had just finished.</para>
    ///
    /// <para>Nothing replaces the wipe, because the pinch log's own model already is
    /// the rule: a log stands until the next run supersedes it. Stretched over a
    /// round, that is exactly what the ruling asked for - the next round's
    /// <see cref="HoldForRound"/> clears it, and so does the next standalone run's
    /// <see cref="StartNewRun"/>. <see cref="_carriedRun"/> is kept for the same
    /// reason it exists during the round: the last stage is already IN the carry, and
    /// forgetting which run it came from would draw that chapter twice.</para>
    /// </summary>
    public void ReleaseRoundHold()
    {
      _carry.Release();
      _carry.WriteThrough = null;
      _runId = 0;
    }

    /// <summary>
    /// AN ENDED ROUND'S BOOK, READ BACK ACROSS A RELOAD (ruled 08-15: "I might want
    /// to read the most recent one if I closed it by mistake"). The S11 rule - the
    /// book stays on screen until the next run supersedes it - used to be true only
    /// within a session, because the restore reads round_log for ACTIVE rounds and an
    /// ended round's transcript sat in the table unread until the next launch wiped
    /// it. This is the missing read: same lines, same window, landing in exactly the
    /// released post-round state, so every supersede rule (next round's Hold, next
    /// standalone run's start, the Clear button) applies unchanged.
    ///
    /// <para>Refuses to clobber anything already on screen: a session that has a run
    /// or a carried book has nothing missing to restore.</para>
    /// </summary>
    public void ShowEndedBook(IReadOnlyList<RoundLogLine> banked)
    {
      if (_carry.Entries.Count > 0 || Plugin.CurrentRun != null || _lastRun != null) return;
      _carry.Rehydrate(banked.Select(RoundLogEntry.Restore).ToList());
      _carry.Release();
      _carry.WriteThrough = null;
      _runId = 0;
      _carriedRun = null;
    }

    /// <summary>
    /// THE WRITE-THROUGH. One adopted chapter, flattened to rows and appended to the
    /// banked transcript.
    ///
    /// <para>Its own guard, and a quiet one: the transcript on screen is the product,
    /// and a storage hiccup must not cost the player a chapter of it or take a run
    /// down. What a failed bank costs is exactly one chapter at the next reload, which
    /// is the state the plugin was in before this unit existed.</para>
    /// </summary>
    private void BankLines(IReadOnlyList<ILogItem> entries)
    {
      var store = Plugin.RoundLogStore;
      if (store == null || _runId <= 0) return;

      try
      {
        // EACH ROW CARRIES ITS OWN STAMP (review ruling S12). This used to read the
        // clock ONCE here and hand that instant to every row in the chapter, which
        // made every line of a stage share a timestamp and every per-stage duration
        // derived from round_log structurally zero. The stamp belongs to the row and
        // is set where the row is written; the bank only copies it.
        var lines = new List<RoundLogLine>(entries.Count);
        foreach (var entry in entries)
          if (RoundLogEntry.Flatten(entry, _bankStage) is { } line) lines.Add(line);
        store.Append(_runId, lines);
      }
      catch (Exception ex)
      {
        Svc.Log.Warning($"[Round] Failed to bank a transcript chapter: {ex.Message}");
      }
    }

    /// <summary>
    /// THE ADOPTION POINT (unit 4). One finished stage becomes the transcript's next
    /// chapter - carried in memory and banked in the same act.
    ///
    /// <para>It moved here, to the run's own END, from the NEXT run's start. That is
    /// the whole of "state and run log bank at the checkpoint": the Look half
    /// finishing is a stage ending, not a stage beginning, and a round that pauses at
    /// the hinge - which is what the hinge is FOR - never starts another run to
    /// trigger the old adoption. Under the old timing the reload at the pause ate
    /// exactly the chapter the player was about to act on.</para>
    ///
    /// <para>Idempotent by reference, as it always was: adopting one run twice would
    /// double it, and every caller here is a path a run can take more than once.</para>
    /// </summary>
    private void AdoptChapter(RunData run)
    {
      if (!_carry.Holding || ReferenceEquals(run, _carriedRun) || run.LogEntries.Count == 0)
        return;
      _bankStage = StageForRate(run)?.ToString() ?? "Round";
      _carry.Adopt(run.LogEntries);
      _carriedRun = run;
    }

    /// <summary>
    /// What the log calls this run. Derived from the run itself rather than passed
    /// in: every caller set Plugin.CurrentRun's Mode and then passed a flag saying
    /// the same thing again, and two spellings of one fact is one too many.
    /// </summary>
    private static string LabelOf(RunData run) => run.IsStandingRun ? "Standing Run" : run.Mode switch
    {
      RunMode.Coffer => "Coffer Rider",
      RunMode.Gc => "GC Turn-In Run",
      RunMode.Desynth => "Desynth Run",
      RunMode.Hawk => "Hawk Run",
      RunMode.Recon => "Recon Run",
      _ => "Run",
    };

    /// <summary>The window title for a run - the label, in the window's own grammar.</summary>
    private static string TitleOf(RunData run) => run.IsStandingRun ? "Scrooge - Ledger (standing listings)" : run.Mode switch
    {
      RunMode.Coffer => "Scrooge - Ledger (coffer rider)",
      RunMode.Gc => "Scrooge - Ledger (GC turn-in)",
      RunMode.Desynth => "Scrooge - Ledger (desynth run)",
      RunMode.Hawk => "Scrooge - Ledger (hawk run)",
      RunMode.Recon => "Scrooge - Ledger (recon run)",
      _ => "Scrooge - Ledger (pinch run)",
    };

    /// <summary>
    /// Clears the log and opens the window. Called at the start of each run - every
    /// run, now: the triage executor was the one errand that ran silently, and
    /// "half narrated, half invisible" was the whole complaint.
    /// </summary>
    public void StartNewRun()
    {
      if (!Plugin.Configuration.EnableLedger)
        return;

      var run = Plugin.CurrentRun;
      if (run == null) return;

      // Mid-round, the run that just finished becomes the transcript's next chapter
      // instead of being dropped when the window follows the new run. Since unit 4
      // that has normally already happened at the previous run's own end (see
      // AdoptChapter); this stays as the backstop for a run that ended by some path
      // that never called EndRun or CancelRun.
      if (_lastRun is { } prior) AdoptChapter(prior);

      // AND OUTSIDE A ROUND, THIS RUN SUPERSEDES THE LAST BOOK (review ruling S11).
      // A finished round's transcript stays readable after the round ends, which is
      // the pinch log's own model stretched over a round - and the other half of that
      // model is that the next run replaces it. Without this the standing carry would
      // be drawn in front of every standalone run from here to the next round.
      if (!_carry.Holding)
      {
        _carry.Clear();
        _carriedRun = null;
      }

      _lastRun = run;
      run.RunStopwatch.Restart();

      // The title names the ERRAND during a round. Retitling per stage was the loudest
      // half of "each stage resets the run log": the window changed its name and its
      // contents at the same moment, which reads as a different window entirely.
      WindowName = _carry.Holding ? "Scrooge - Ledger (this Round)" : TitleOf(run);
      run.AddRunEntry(RunEvent.Start, $"{LabelOf(run)} Started — {DateTime.Now:h:mm tt}");
      IsOpen = true;
    }

    /// <summary>
    /// Updates the current retainer name. Subsequent log entries will be tagged with this name.
    /// </summary>
    public void SetCurrentRetainer(string retainerName)
    {
      var run = Run;
      if (run != null)
        run.CurrentRetainer = retainerName;
    }

    /// <summary>
    /// Adds an entry to the log. Inserts a retainer header automatically
    /// on the first entry for each retainer. Guards on EnableLedger.
    /// </summary>
    public void AddEntry(ItemOutcome outcome, string itemName, string message, string? hover = null)
    {
      if (!Plugin.Configuration.EnableLedger)
        return;

      Run?.AddLogEntry(outcome, itemName, message, hover);
    }

    /// <summary>
    /// One row in the house voice (Movement 1). The composer already decided what is
    /// said and what is only shown on hover, so this overload exists to make the pair
    /// impossible to split at a call site - the whole point of composing them together.
    /// </summary>
    public void AddEntry(ItemOutcome outcome, string itemName, VoiceLine voice)
      => AddEntry(outcome, itemName, voice.Line, voice.Hover);

    /// <summary>
    /// Live yield sub-row during a desynth run. Subscribed to
    /// DesynthYieldStore.YieldCaptured in the Plugin constructor.
    /// </summary>
    public void OnYieldCaptured(DesynthYield yield)
    {
      if (!Plugin.Configuration.EnableLedger)
        return;

      var run = Run;
      if (run == null || run.Mode != RunMode.Desynth || run.IsComplete)
        return;

      var value = (long)(GilStorage.GetLastSalePrice(yield.YieldItemId, yield.YieldIsHq) ?? 0) * yield.YieldQty;
      run.AddYieldEntry(GilTracker.GetItemName(yield.YieldItemId), yield.YieldQty, yield.YieldIsHq, value);
    }

    /// <summary>
    /// Increments the successful adjustment counter. Called from Communicator.PrintPriceUpdate.
    /// </summary>
    public void IncrementAdjusted()
    {
      if (!Plugin.Configuration.EnableLedger)
        return;

      var run = Run;
      if (run != null) run.ItemsAdjusted++;
    }

    /// <summary>
    /// Increments the held counter - a row evaluated and left standing at its ask
    /// (ruled 2026-08-15). Called from the pipeline's apply path when the written
    /// price equals the standing one.
    /// </summary>
    public void IncrementHeld()
    {
      if (!Plugin.Configuration.EnableLedger)
        return;

      var run = Run;
      if (run != null) run.ItemsHeld++;
    }

    /// <summary>
    /// Adds to the running total of gil currently listed on the market board.
    /// Called from ItemPricingPipeline.SetNewPrice for every item — adjusted or skipped.
    /// </summary>
    public void AddListingValue(int gil)
    {
      if (!Plugin.Configuration.EnableLedger)
        return;

      var run = Run;
      if (run != null) run.TotalListingGil += gil;
    }

    /// <summary>Tracks a vendor sale for the run summary.</summary>
    public void AddVendorSale(long gil)
    {
      if (!Plugin.Configuration.EnableLedger)
        return;

      var run = Run;
      if (run != null)
      {
        run.VendorSoldCount++;
        run.VendorSoldGil += gil;
      }
    }

    /// <summary>
    /// Adds end marker and summary lines to the log. Called from the run executors when all tasks finish.
    /// </summary>
    public void EndRun()
    {
      var run = Run;
      if (run == null) return;

      var isHawkRun = run.Mode == RunMode.Hawk;
      var isDesynthRun = run.Mode == RunMode.Desynth;
      var isGcRun = run.Mode == RunMode.Gc;
      var isCofferRun = run.Mode == RunMode.Coffer;
      var isReconRun = run.Mode == RunMode.Recon;
      var isStandingRun = run.IsStandingRun;

      run.AddRunEntry(RunEvent.End, $"{LabelOf(run)} Complete — {DateTime.Now:h:mm tt}");
      run.AddRunEntry(RunEvent.Summary, isCofferRun
        ? $"{run.ItemsProcessed} coffer{(run.ItemsProcessed == 1 ? "" : "s")} opened"
        : isGcRun
          ? $"{run.ItemsProcessed} turned in"
          : isDesynthRun
            ? $"{run.ItemsProcessed} desynthed"
            : isReconRun
              // "banked", never "listed" or "adjusted": the pass ended with the
              // market exactly as it found it, and the only honest headline is how
              // many decisions it bought.
              ? $"{run.CountOutcome(ItemOutcome.Reconned)} decision{(run.CountOutcome(ItemOutcome.Reconned) == 1 ? "" : "s")} banked"
            : isHawkRun
              ? $"{run.ItemsAdjusted} listed"
              : isStandingRun
                ? $"{run.ItemsProcessed} worked"
                // Repriced and held are different acts and count apart (ruled
                // 08-15, off the "114 adjusted" morning run where half the rows
                // were Held) - same tense rule that split the verbs.
                : run.ItemsAdjusted > 0 && run.ItemsHeld > 0
                  ? $"{run.ItemsAdjusted} repriced, {run.ItemsHeld} held"
                  : run.ItemsHeld > 0
                    ? $"{run.ItemsHeld} held"
                    : $"{run.ItemsAdjusted} repriced");

      // Desynth headline = what the materials are worth, not vendor forfeit —
      // vendor prices for gear are jokingly cheap, so "net vs vendor" always wins.
      if (isDesynthRun && run.MaterialsValueGil > 0)
        run.AddRunEntry(RunEvent.Summary, $"~{run.MaterialsValueGil:N0} gil in materials (cached prices)");

      // Desynth, GC, and coffer runs don't carry vendor / listing / lane semantics —
      // skip those summary lines for them (GC's seals summary is added by the
      // orchestrator, which owns the seal-value RunLifecycle; the coffer rider's own
      // "N coffers → N items" summary is added by CofferOrchestrator before EndRun).
      // A triage run works standing listings one at a time - it has no board-wide
      // "gil on market" total to report, and claiming 0 would read as a board that
      // emptied. Its vendor line is added below with everyone else's.
      if (isStandingRun && run.VendorSoldCount > 0)
        run.AddRunEntry(RunEvent.Summary, $"{run.VendorSoldCount} vendor-sold for {run.VendorSoldGil:N0} gil");

      // Recon joins the exclusion list for the same reason the others are on it: it
      // carries no listing semantics. Its held items are counted in its own banked
      // headline (a hold IS a banked decision), and a "0 gil on market" line under a
      // pass that never listed would read as a board that emptied.
      if (!isDesynthRun && !isGcRun && !isCofferRun && !isStandingRun && !isReconRun)
      {
        if (run.SkippedCount > 0)
          run.AddRunEntry(RunEvent.Summary, $"{run.SkippedCount} skipped");
        if (run.NoDataCount > 0)
          run.AddRunEntry(RunEvent.Summary, $"{run.NoDataCount} no data");

        // Lane outcomes — each type counted separately, per the lane design.
        // "stepped past crashers", not "lone lowballs" (strings pass, 08-02): under
        // A11 a step can clear a whole pack, and the count was never crazies.
        // "ITEMS", not "rows" (3b-6): a row is a thing this window draws; the count
        // is of items the run walked, and the transcript's own vocabulary should not
        // make the player learn ours.
        AddLaneSummary(run, ItemOutcome.CrazySkipped, "items stepped past crashers",
          "item stepped past crashers");
        AddLaneSummary(run, ItemOutcome.EmptyBoard, "empty boards listed",
          "empty board listed");
        AddHeldSummary(run);
        AddLaneSummary(run, ItemOutcome.PremiumFromNq, "HQ priced off its NQ history");
        // THE WARNED ROLL-UP (3b-7). Phase 2 gave the crasher guard its own outcome
        // and left the run summary silent about it, so a pass that ASKED about three
        // items reported nothing about them at all. Present tense on purpose: a skip
        // is finished and a warning is still owed an answer.
        AddLaneSummary(run, ItemOutcome.Warned, "warnings waiting on you",
          "warning waiting on you");
        AddLaneSummary(run, ItemOutcome.RepriceCapped, "climbs capped to one step",
          "climb capped to one step");
        // Rounds unit 3: how much of tonight's listing was paid for by the Look
        // half. The summary line IS the cache's receipt - a round where this number
        // is small is a round whose recon bought nothing, which is the evidence that
        // will eventually tune ReconFreshHours.
        AddLaneSummary(run, ItemOutcome.PostedFromRecon, "posted from banked recon");

        if (run.VendorSoldCount > 0)
          run.AddRunEntry(RunEvent.Summary, $"{run.VendorSoldCount} vendor-sold for {run.VendorSoldGil:N0} gil");

        // A FLOW, SAID AS ONE (V13). "417,000 gil on market" reads as the stock
        // standing out there right now; it is what THIS pass posted, and a reader who
        // takes it for the former is reading his whole listed book off one run. The
        // hawk half already said "put on market" - two spellings of one arithmetic,
        // which is how a surface starts disagreeing with itself - so both say it once.
        run.AddRunEntry(RunEvent.Summary, $"{run.TotalListingGil:N0} gil put on market this run");
      }

      // Hand off triage data to the board - AND, for a pinch, the re-ask (review ruling
      // S5). A pinch is the only pass that stamps a lane's call, so it re-presents the
      // staged verbs on every pass, including one that raised nothing: zero rows is the
      // news that the lanes those verbs answered are clean now, and the gate that waited
      // for rows is what let them ghost through to the bell. The standing (triage) run
      // is a pinch by pricing mode but not by errand - it works the rows the board
      // already has and retires them itself, so it reports on the old terms.
      var isPinchRun = run.Mode == RunMode.Pinch && !isStandingRun;
      if (StandingReAsk.Reports(isPinchRun, run.StandingItems.Count))
        Plugin.Accountant.SetRun(run);

      // Stop timer and mark run complete (lifecycle terminal - fail closed)
      run.MarkComplete();
      run.RunStopwatch.Stop();

      // What this run taught about pace: the overall seed a pinch quotes before its
      // first item, and the per-stage key the round's rail quotes per row (WALK unit
      // 6 - one blended number could not stand in for five different errands). The
      // blend, the first-run adoption and the GC/coffer exclusion all live in
      // PaceBank; what stays here is applying it and the one save.
      var paceUpdate = PaceBank.Record(
        run.RunStopwatch.ElapsedMilliseconds, run.ItemsProcessed,
        isGcRun, isCofferRun,
        StageForRate(run)?.ToString(),
        Plugin.Configuration.AvgMsPerItem,
        Plugin.Configuration.AvgMsPerItemByStage);

      if (paceUpdate.Overall is float overallPace)
        Plugin.Configuration.AvgMsPerItem = overallPace;
      if (paceUpdate.Stage is { } stagePace)
        Plugin.Configuration.AvgMsPerItemByStage[stagePace.Key] = stagePace.Value;
      if (paceUpdate.Any)
        Plugin.Configuration.Save();

      // The chapter closes here, in memory AND in the bank. A stage finishing is the
      // checkpoint; nothing later in the round is required to make its lines durable.
      AdoptChapter(run);
    }

    /// <summary>
    /// The round stage whose pace this run just measured, or null when the run is
    /// not a stage (the coffer rider rides ahead of the bell and is not one).
    /// </summary>
    private static RoundStage? StageForRate(RunData run)
    {
      // A triage run is the bell's standing-listing leg (07-25: the bell absorbed
      // the reprice stage). Its pace blends into the bell's key, which is honest -
      // the bell is what spends that time now. Out-of-round Go runs blend in too,
      // exactly as they used to blend into the reprice key.
      if (run.IsStandingRun) return RoundStage.BellRun;
      return run.Mode switch
      {
        RunMode.Pinch => RoundStage.Pinch,
        RunMode.Hawk => RoundStage.BellRun,
        RunMode.Desynth => RoundStage.Desynth,
        RunMode.Gc => RoundStage.TurnIn,
        // Recon learns its own pace like every other stage - and it needs to more
        // than any of them, because it is the slowest per-item errand in the round
        // (a full market-board round trip per item with nothing to show for it on
        // the market afterwards). The rail's measured minutes are what turn "this is
        // the price of ground truth" from a claim into a number.
        RunMode.Recon => RoundStage.Recon,
        _ => null,
      };
    }

    /// <summary>
    /// True when the entry's Message is a composed <see cref="VoiceLine"/> - a verb
    /// prefix and a sentence, with no item name in it (Movement 1). The row supplies
    /// the name from its own field, which is where it always lived; the old grammar
    /// re-stated it inside the string as well, and two copies of one fact is exactly
    /// what a row shape exists to prevent.
    ///
    /// <para>Legacy outcomes - the melt, the turn-in, the coffer, the pull - still
    /// carry a bare message and keep the "Item — message" shape they always had.
    /// Their acts differ from all five verbs, so re-voicing them means naming new
    /// verbs, and that is not Movement 1's ruling to make.</para>
    /// </summary>
    private static bool IsVoiceLine(ItemOutcome outcome) => outcome switch
    {
      ItemOutcome.CrazySkipped or ItemOutcome.EmptyBoard or ItemOutcome.LaneHeld
        or ItemOutcome.PremiumFromNq or ItemOutcome.Skipped or ItemOutcome.Warned
        or ItemOutcome.RepriceCapped
        or ItemOutcome.Reconned or ItemOutcome.PostedFromRecon or ItemOutcome.NoData
        or ItemOutcome.VendorSold => true,
      _ => false,
    };

    /// <summary>
    /// Adds a run-summary line for one lane outcome type, when any occurred.
    /// <paramref name="singular"/> covers the count-of-one grammar ("1 empty boards
    /// listed", live shake 08-15); labels that read the same at any count omit it.
    /// </summary>
    private static void AddLaneSummary(
      RunData run, ItemOutcome outcome, string label, string? singular = null)
    {
      var n = run.CountOutcome(outcome);
      if (n > 0)
        run.AddRunEntry(RunEvent.Summary, $"{n} {(n == 1 ? singular ?? label : label)}");
    }

    /// <summary>
    /// THE HELD ROLL-UP, SPLIT BY THE REASON ITS OWN ROWS GAVE (V12, ruled B7).
    /// "{n} held (not enough sales)" asserted ONE reason for a class
    /// <see cref="PricingVoice.HeldReason"/> already splits per row - a board that never
    /// answered is not a thin tape - so the summary was overwriting the very rows it
    /// counts. It reads them instead: the split comes from the held rows' own spoken
    /// lines, which makes it structurally impossible for the roll-up and the rows to
    /// disagree, and reason-neutral when they are all one reason anyway.
    /// </summary>
    private static void AddHeldSummary(RunData run)
    {
      var held = run.LogEntries.OfType<LogEntry>()
        .Where(e => e.Outcome == ItemOutcome.LaneHeld)
        .Select(e => e.Message)
        .ToList();
      if (held.Count == 0) return;
      run.AddRunEntry(RunEvent.Summary, RunLogVoice.HeldRollup(held));
    }

    /// <summary>
    /// Sets the total expected item count for the run. Called once before
    /// retainer processing begins, using pre-scanned counts from the
    /// RetainerList addon's AtkValues. Calculates initial ETA countdown.
    /// </summary>
    public void SetTotalItems(int total)
    {
      var run = Run;
      if (run == null) return;

      // Starts the lifecycle when idle (seeded with the persisted AvgMsPerItem);
      // revises Total in place on a live run - the lifecycle ETA self-calibrates
      // off observed pace once the first item lands.
      run.TotalItems = total;
    }

    /// <summary>
    /// Marks one item as processed (success or skip). Called from ItemPricingPipeline.SetNewPrice.
    /// </summary>
    public void IncrementProcessed()
    {
      var run = Run;
      if (run == null) return;

      run.Beat();
    }

    /// <summary>
    /// Cancels the current run and stops the timers
    /// </summary>
    public void CancelRun()
    {
      var run = Run;
      if (run == null) return;

      run.MarkCancelled();
      run.RunStopwatch.Stop();
      // A death is a checkpoint too - the round HALTS over it and the player resumes
      // from exactly here, so the lines leading up to the corpse are the ones he most
      // needs to still have after a reload.
      AdoptChapter(run);
    }

    public LedgerWindow() : base("Scrooge - Ledger")
    {
      SizeConstraints = new WindowSizeConstraints
      {
        MinimumSize = new(300, 200),
        MaximumSize = new(800, 600)
      };
      Size = new System.Numerics.Vector2(400, 300);
      SizeCondition = ImGuiCond.FirstUseEver;
      IsOpen = false;
    }

    /// <summary>
    /// THE ROUND'S FACE (WALK unit 6). While a round is underway the run log leads
    /// with the whole errand - every stage, where it stands, what it holds and how
    /// long it should take - above whichever single run happens to be in flight.
    /// Before this, the log knew only about the one run and a player mid-round had
    /// no surface that said where the errand stood.
    ///
    /// <para>Drawn even between runs, because "between stages is just walking" and
    /// the walk is exactly when you want to look.</para>
    /// </summary>
    private static void DrawStageRail()
    {
      if (Plugin.Accountant.Conductor.RoundRail() is not { } rail) return;

      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Header);
      ImGui.TextUnformatted(StageRail.HeaderText(StageRail.RemainingMs(rail.Rows)));
      ImGui.PopStyleColor();

      foreach (var row in rail.Rows)
      {
        var color = row.State switch
        {
          RailState.Halted => ScroogeColors.Spent,
          RailState.Done => ScroogeColors.Earned,
          RailState.Current => ScroogeColors.Amber,
          _ => ScroogeColors.Muted,
        };
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(StageRail.RowText(row));
        ImGui.PopStyleColor();
      }

      // The halt banner: what died, and what would clear it. Same halt-name-resume
      // vocabulary the deck renders, on the surface the player is already watching
      // when a run dies under them.
      if (rail.Halt is { } halt)
      {
        ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Spent);
        ImGui.TextWrapped(halt.Message);
        ImGui.PopStyleColor();

        // THE BANNER ACTS (Fix 4a, 2026-07-26). It used to say "Resume it from the
        // Ledger's deck" - to a player watching THIS window, which had no way to get
        // to that deck. Drift: "I had to re-open ledger (which doesn't have a button
        // from the run log)." A button beside the prose beats prose about a button
        // somewhere else, so the instruction shrank to the half that is still news
        // (what clears the gap) and the two acts became buttons.
        if (ImGui.Button("Resume###railResume"))
          Plugin.Accountant.Conductor.ResumeRound();
        ImGui.SameLine();
        if (ImGui.Button("Open the Round###railOpenRound"))
          Plugin.OpenRoundDoor();
      }

      // THE PORT (WALK unit 7), landed on the seam unit 6 left here. The rail is
      // the surface a player watches mid-round, so it is where "port me as close
      // as you can" belongs - but it asks the ACCOUNTANT for the decision rather than
      // making its own, exactly as it does for the cursor above. One decision, two
      // surfaces; the rail can never offer a port the deck is refusing.
      //
      // Only the turn-in ports: it is the one stage whose place is a different
      // city. Every other stage happens at a bell or wherever you stand, and the
      // game has no teleport that helps with those.
      AccountantWindow.DrawPortOffer(Plugin.Accountant.Conductor.TurnInPort(), "railPort");

      ImGui.Separator();
    }

    public override void Draw()
    {
      DrawStageRail();

      // A null run with a carried book is the ended-book restore (ShowEndedBook):
      // the transcript draws, and everything below that is about a live run's own
      // state (progress, ETA, standing summary) simply doesn't.
      var run = Run;
      if (run == null && _carry.Entries.Count == 0) return;

      // Scrollable child region for log entries
      // Reserve space at the bottom for the status bar
      var footerHeight = ImGui.GetFrameHeightWithSpacing() + 4;
      ImGui.BeginChild("##logEntries", new System.Numerics.Vector2(0, -footerHeight), false);

      bool treeOpen = false;

      foreach (var item in VisibleEntries(run))
      {
        switch (item)
        {
          case RunEntry runEntry:
          {
            if (treeOpen) { ImGui.TreePop(); treeOpen = false; }

            if (runEntry.EventType == RunEvent.Start || runEntry.EventType == RunEvent.End)
            {
              ImGui.Spacing();
              ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
              ImGui.TextWrapped($"--- {runEntry.Message} ---");
              ImGui.PopStyleColor();
              ImGui.Spacing();
            }
            else if (runEntry.EventType == RunEvent.Summary)
            {
              ImGui.Indent(16);
              ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Info);
              ImGui.TextWrapped(runEntry.Message);
              ImGui.PopStyleColor();
              ImGui.Unindent(16);
            }
            break;
          }

          case RetainerHeader header:
          {
            if (treeOpen) ImGui.TreePop();
            treeOpen = ImGui.TreeNodeEx(header.RetainerName, ImGuiTreeNodeFlags.DefaultOpen);
            break;
          }

          case LogEntry entry:
          {
            if (!treeOpen) break;

            var color = entry.Outcome switch
            {
              ItemOutcome.Skipped => ScroogeColors.Spent,
              // ALARM-RED, the color reserved for a protective press-time warning -
              // the same one the desynth preview counts protected items in.
              ItemOutcome.Warned => ScroogeColors.Protected,
              ItemOutcome.NoData => ScroogeColors.Amber,
              ItemOutcome.VendorSold => ScroogeColors.Earned,
              ItemOutcome.TurnedIn => ScroogeColors.Earned,
              ItemOutcome.Banned => ScroogeColors.Banned,
              ItemOutcome.Desynthed => ScroogeColors.Muted,
              ItemOutcome.Unlocked => ScroogeColors.Earned,
              ItemOutcome.CrazySkipped => ScroogeColors.Info,
              ItemOutcome.EmptyBoard => ScroogeColors.Earned,
              ItemOutcome.LaneHeld => ScroogeColors.Amber,
              ItemOutcome.PremiumFromNq => ScroogeColors.Earned,
              ItemOutcome.Reconned => ScroogeColors.Info,
              ItemOutcome.PostedFromRecon => ScroogeColors.Earned,
              // A price went on the board, so it is green like every other listing -
              // the line itself carries the cap ("climbing toward X - capped").
              ItemOutcome.RepriceCapped => ScroogeColors.Earned,
              _ => new System.Numerics.Vector4(1f, 1f, 1f, 1f)
            };

            ImGui.PushStyleColor(ImGuiCol.Text, color);
            // Item first, then the spoken line - one shape for every row, voiced or
            // legacy. The voice composers deliberately leave the name out (the
            // specimen Drift ruled has none) and the row puts it back exactly once.
            ImGui.TextWrapped($"{entry.ItemName} — {entry.Message}");
            ImGui.PopStyleColor();

            // EVIDENCE ON HOVER (Movement 1, rule 6). Nothing was deleted when the
            // line got short: the seller census, the ceiling counts, the sales range
            // and the walk's own full prose all live here.
            if (!string.IsNullOrWhiteSpace(entry.Hover) && ImGui.IsItemHovered())
              ImGui.SetTooltip(entry.Hover);

            break;
          }

          case YieldEntry yield:
          {
            if (!treeOpen) break;

            var label = Format.Hq(yield.YieldName, yield.IsHq);
            var qty = yield.Qty > 1 ? $"{yield.Qty}x " : "";
            var value = yield.Value > 0 ? $"  (~{yield.Value:N0}g)" : "";
            ImGui.Indent(16);
            ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
            ImGui.TextWrapped($"→ {qty}{label}{value}");
            ImGui.PopStyleColor();
            ImGui.Unindent(16);
            break;
          }
        }
      }

      if (treeOpen) ImGui.TreePop();

      // --- Standing-listing summary + launch button ---
      if (run is { IsComplete: true } && run.StandingItems.Count > 0)
      {
        ImGui.Spacing();
        ImGui.Indent(16);
        ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
        ImGui.Text($"{run.StandingItems.Count} {(run.StandingItems.Count == 1 ? "item needs" : "items need")} a ruling");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.SmallButton("Open the Round"))
          Plugin.OpenRoundDoor();
        ImGui.Unindent(16);
      }

      // Auto-scroll to bottom when new entries appear
      if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
        ImGui.SetScrollHereY(1.0f);

      ImGui.EndChild();

      // Bottom bar: Clear button + entry count + progress + timer + copy all
      ImGui.Separator();
      ImGui.BeginDisabled(Plugin.CurrentRun != null);
      if (ImGui.Button("Clear"))
      {
        // Always wipes - the transcript included. The button says Clear.
        _lastRun = null;
        _carry.Clear();
        _carriedRun = null;
        ImGui.EndDisabled();
        return;
      }
      // WHAT THE BUTTON COSTS, AND WHAT IT DOES NOT (V26). "Clear" over a transcript
      // reads like a delete, and the hesitation it buys is the reason a player scrolls
      // a stale log all night. The rounds are banked either way; only this pane empties.
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip("Clears the window. The bank keeps every round's log.");
      ImGui.EndDisabled();
      ImGui.SameLine();
      // Counts the WHOLE transcript during a round: a footer reporting the live stage's
      // 4 entries under a window showing 41 of them is the reset bug's last hiding place.
      var logEntryCount = VisibleEntries(run).OfType<LogEntry>().Count();
      ImGui.Text($"{logEntryCount} {(logEntryCount == 1 ? "entry" : "entries")}");

      // Progress + Timer display
      if (run is { TotalItems: > 0 })
      {
        ImGui.SameLine();
        ImGui.TextDisabled(" | ");
        ImGui.SameLine();
        ImGui.Text($"{run.ItemsProcessed}/{run.TotalItems}");
      }

      // ETA / final time display
      if (run is { IsComplete: true })
      {
        // Run finished — show final elapsed time
        ImGui.SameLine();
        ImGui.TextDisabled(" | ");
        ImGui.SameLine();

        var elapsed = run.RunStopwatch.Elapsed;
        var elapsedStr = elapsed.TotalMinutes >= 1
          ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s"
          : $"{elapsed.Seconds}s";

        ImGui.Text(elapsedStr);
      }
      else if (run is { TotalItems: > 0 } && run.RunStopwatch.IsRunning)
      {
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();

        if (run.Lifecycle.Eta(DateTime.UtcNow) is TimeSpan eta)
        {
          var etaStr = eta.TotalMinutes >= 1
            ? $"~{(int)eta.TotalMinutes}m {eta.Seconds:D2}s"
            : $"~{eta.Seconds}s";

          ImGui.TextDisabled($"ETA: {etaStr}");
        }
        else
        {
          ImGui.TextDisabled("Gathering data...");
        }
      }

      var gilBtnWidth = ImGui.CalcTextSize("Gil Dashboard").X + ImGui.GetStyle().FramePadding.X * 2;
      var copyBtnWidth = ImGui.CalcTextSize("Copy All").X + ImGui.GetStyle().FramePadding.X * 2;
      var spacing = ImGui.GetStyle().ItemSpacing.X;
      var padding = ImGui.GetStyle().WindowPadding.X;

      ImGui.SameLine(ImGui.GetWindowWidth() - gilBtnWidth - copyBtnWidth - spacing - padding);
      if (ImGui.Button("Gil Dashboard"))
        Plugin.GilDashboard.IsOpen = true;

      ImGui.SameLine();
      if (ImGui.Button("Copy All"))
      {
        var sb = new StringBuilder();
        // The round rail rides at the top of the copy exactly as it renders at the
        // top of the window (Drift, 07-26: "still no copy of the top text") - a pasted
        // log that opens with the stage checklist and any halt reads like the window
        // did, instead of starting mid-story.
        if (Plugin.Accountant.Conductor.RoundRail() is { } copyRail)
        {
          sb.AppendLine(StageRail.HeaderText(StageRail.RemainingMs(copyRail.Rows)));
          foreach (var row in copyRail.Rows)
            sb.AppendLine(StageRail.RowText(row));
          if (copyRail.Halt is { } copyHalt)
            sb.AppendLine(copyHalt.Message);
          sb.AppendLine();
        }
        foreach (var item in VisibleEntries(run))
        {
          switch (item)
          {
            case RunEntry runEntry:
              if (runEntry.EventType == RunEvent.Start || runEntry.EventType == RunEvent.End)
                sb.AppendLine($"--- {runEntry.Message} ---");
              else if (runEntry.EventType == RunEvent.Summary)
                sb.Append("  ").AppendLine(runEntry.Message);
              break;

            case RetainerHeader rh:
              sb.AppendLine($"[{rh.RetainerName}]");
              break;

            case LogEntry entry:
            {
              if (IsVoiceLine(entry.Outcome))
              {
                // The row as it reads on screen, and the evidence layer under it -
                // a pasted log is the one copy of the transcript that leaves the
                // game, so the hover must not be the half that stays behind.
                sb.Append("  ").AppendLine($"{entry.ItemName} — {entry.Message}");
                if (!string.IsNullOrWhiteSpace(entry.Hover))
                  foreach (var evidenceLine in entry.Hover.Split('\n'))
                    sb.Append("      ").AppendLine(evidenceLine);
                break;
              }
              // NoData and VendorSold are NOT here: both are voice lines
              // (see IsVoiceLine), so the branch above returns before this switch
              // ever sees one, and an arm for them was a prefix nothing could reach.
              var prefix = entry.Outcome switch
              {
                ItemOutcome.TurnedIn => "Turned in",
                ItemOutcome.Banned => "Banned",
                ItemOutcome.Desynthed => "Desynthed",
                ItemOutcome.Unlocked => "Unlocked",
                _ => "Entry"
              };
              // RunLogLines owns the stutter rule: the prefix already names the
              // act, so a message that only repeats it ("Desynthed: X — desynthed")
              // is dropped rather than said twice.
              sb.Append("  ").AppendLine(RunLogLines.CopyLine(prefix, entry.ItemName, entry.Message));
              break;
            }
          }
        }

        // Standing-listing summary
        if (run is { IsComplete: true } && run.StandingItems.Count > 0)
          sb.AppendLine().AppendLine($"{run.StandingItems.Count} {(run.StandingItems.Count == 1 ? "item needs" : "items need")} a ruling");

        ImGui.SetClipboardText(sb.ToString());
      }
    }
  }
}
