using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Scrooge.Windows;

/// <summary>
/// The Ledger (M6 session 2): the one worklist surface. Evolved from the routing
/// window - it keeps the piles/reasons/overrides/Go/location-parity seed and widens
/// it into the action-named piles of the unified design (Review / List / Reprice /
/// Pull-and-Vendor / Melt / Churn / Watch, design Section 3). It ABSORBS the old
/// Triage inbox (design Section 7): held flags and this-run's skipped items become
/// the Review / Reprice / Pull-and-Vendor / Watch rows, keyed the same way as the
/// routing WorkItems, with their full action set preserved (Vend / Pull / Reprice /
/// Dismiss). There is no second surface: the TriageWindow is gone.
///
/// Bulk-ability IS the confidence threshold (design Section 4): each actionable pile
/// offers a one-click confirm that acts ONLY on its Unanimous-tier rows; Mixed needs
/// the row click; a Contradicted verdict is demoted to Review and is immune to bulk.
/// Every manual decision writes a teaching signal against its verdict class (V14
/// routing_overrides) - the write side is real even though the v0 read side is a
/// single override-count refinement.
/// </summary>
internal sealed class LedgerWindow : Window
{
  /// <summary>One routed inventory item (bag gear); Pile tracks player moves.</summary>
  internal sealed class RoutedItem
  {
    public uint ItemId { get; init; }
    public string Name { get; init; } = "";
    public bool IsHq { get; init; }
    public int Ilvl { get; init; }
    public int Quantity { get; init; }
    public InventoryType Container { get; init; }
    public int SlotIndex { get; init; }
    public int LastSalePrice { get; init; }
    /// <summary>The router's original call - immutable; overrides move Pile, not this.</summary>
    public RoutingVerdict Verdict { get; init; }
    /// <summary>Current routing exit. Starts at Verdict.Exit; the player may move it.</summary>
    public RoutingExit Pile { get; set; }
    /// <summary>Still in the Review pile (ambiguous, unresolved).</summary>
    public bool InReview { get; set; }
    public bool OverrideRecorded { get; set; }
    /// <summary>Evidence-refined confidence for this verdict (design Section 4). Bulk gates on Unanimous.</summary>
    public ConfidenceTier Confidence { get; set; } = ConfidenceTier.Mixed;
    /// <summary>
    /// The player ruled on this row - a move click now, or a persisted ruling
    /// re-applied from routing_overrides on refresh. Resolves Review (even a
    /// Contradicted demotion) and makes the row bulk-confirmable. A ruling holds
    /// while the router's verdict is unchanged; a new verdict re-asks.
    /// </summary>
    public bool PlayerResolved { get; set; }

    /// <summary>The Ledger pile this row is drawn in (confidence-demoted Contradicted -> Review).</summary>
    public LedgerPile ActivePile
      => LedgerPiles.Effective(LedgerPiles.ForRoutingExit(Pile, InReview), Confidence, PlayerResolved);
  }

  /// <summary>One inbox row: a live run item, or a held flag wearing a synthetic PricingItem.</summary>
  private sealed record InboxRow(PricingItem Item, TriageFlag? Flag)
  {
    public bool IsFresh => Flag == null;
  }

  // --- Bag-routing state ---
  private List<RoutedItem> _items = [];
  private int? _ventureStock;
  private int? _weeklyBurn;
  private int _uniVersion;
  private int _uniHistVersion;

  // --- Absorbed triage state (was TriageWindow) ---
  private List<PricingItem> _triageItems = [];
  private List<TriageFlag> _heldFlags = [];
  private readonly Dictionary<long, PricingItem> _flagItems = []; // flag id -> synthetic (stable identity)
  private Dictionary<PricingItem, TriageAction> _actions = [];

  // --- Confidence refinement (design Section 4) ---
  private Dictionary<string, int> _overrideCounts = new(StringComparer.Ordinal);
  private Dictionary<(uint, bool, string), string> _persistedRulings = new();
  private readonly HashSet<(uint, bool, string)> _signalsRecorded = [];

  public LedgerWindow()
    : base("Scrooge - Ledger###LedgerWindow", ImGuiWindowFlags.None)
  {
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new Vector2(560, 320),
      MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };
    Size = new Vector2(720, 560);
    SizeCondition = ImGuiCond.FirstUseEver;
    IsOpen = false;
  }

  public override void OnOpen()
  {
    Refresh();
    RefreshHeldFlags();
  }

  // ==========================================================================
  // Intake
  // ==========================================================================

  /// <summary>
  /// Scans the player's bags (gear only), gathers evidence once, runs every item
  /// through the rules engine into routing exits, and scores each verdict's
  /// confidence. No-op on the bag scan when the routing brain is off - the Ledger
  /// still shows the absorbed triage rows.
  /// </summary>
  public void Refresh()
  {
    _items = [];
    _overrideCounts = LoadOverrideCounts();
    _persistedRulings = LoadPersistedRulings();
    if (!Plugin.Configuration.EnableRoutingBrain) return;

    _uniVersion = UniversalisStats.Version;
    _uniHistVersion = UniversalisHistory.Version;
    var batch = RoutingInputService.BeginBatch();
    _ventureStock = batch.VentureStock;
    _weeklyBurn = batch.WeeklyVentureBurn;
    var gearsetIds = DesynthInventoryScanner.SnapshotGearsetItemIds();
    var itemSheet = Svc.Data.GetExcelSheet<Item>();

    unsafe
    {
      var im = InventoryManager.Instance();
      if (im == null) return;

      var containers = new[]
      {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
      };

      foreach (var containerType in containers)
      {
        var container = im->GetInventoryContainer(containerType);
        if (container == null) continue;

        for (int i = 0; i < container->Size; i++)
        {
          var slot = container->GetInventorySlot(i);
          if (slot == null || slot->ItemId == 0) continue;

          var itemId = slot->ItemId;
          if (!itemSheet.TryGetRow(itemId, out var row)) continue;
          if (row.EquipSlotCategory.RowId == 0) continue; // gear only

          var isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;

          var gearsetKey = isHq ? itemId + 1_000_000u : itemId;
          var protections = new ItemProtections(
            InGearset: gearsetIds.Contains(gearsetKey),
            Spiritbond100: slot->SpiritbondOrCollectability >= 10000,
            HasMateria: HasAnyMateria(slot));

          if (RoutingInputService.Collect(batch, itemId, isHq, protections) is not { } inputs)
            continue;

          var verdict = RoutingRules.Evaluate(inputs, batch);
          var item = new RoutedItem
          {
            ItemId = itemId,
            Name = inputs.Name,
            IsHq = isHq,
            Ilvl = inputs.Ilvl,
            Quantity = (int)slot->Quantity,
            Container = containerType,
            SlotIndex = i,
            LastSalePrice = inputs.LastSale?.Price ?? 0,
            Verdict = verdict,
            Pile = verdict.Exit,
            InReview = verdict.IsReview,
            Confidence = ScoreRouted(inputs, verdict, batch.Rules),
          };
          ApplyPersistedRuling(item);
          _items.Add(item);
        }
      }
    }

    _items = _items.OrderByDescending(i => i.Ilvl).ThenBy(i => i.Name).ToList();
  }

  /// <summary>
  /// Re-applies the player's most recent persisted ruling to a freshly routed row.
  /// A ruling holds only while it answers the SAME question - keyed on the router's
  /// current verdict, so a changed verdict (new evidence) misses and re-asks. This
  /// is what stops the Ledger asking the same question every reload.
  /// </summary>
  private void ApplyPersistedRuling(RoutedItem item)
  {
    var routerVerdict = item.Verdict.IsReview ? "Review" : item.Verdict.Exit.ToString();
    if (!_persistedRulings.TryGetValue((item.ItemId, item.IsHq, routerVerdict), out var ruled))
      return;
    if (!Enum.TryParse<RoutingExit>(ruled, out var exit))
      return;
    item.Pile = exit;
    item.InReview = false;
    item.PlayerResolved = true;
  }

  private static Dictionary<(uint, bool, string), string> LoadPersistedRulings()
  {
    try { return GilStorage.GetLatestRoutingRulings(); }
    catch { return new Dictionary<(uint, bool, string), string>(); }
  }

  private static Dictionary<string, int> LoadOverrideCounts()
  {
    try { return GilStorage.GetRoutingOverrideCounts(); }
    catch { return new Dictionary<string, int>(StringComparer.Ordinal); }
  }

  /// <summary>
  /// Confidence for a bag-gear verdict. The evidence axes come from the same inputs
  /// the rules engine judged; the override history for this verdict class refines the
  /// tier. NOTE (Fable QA): the bag path has no full lane model, so lane n / spread
  /// are approximated from the community sample count and velocity - the signal is
  /// strongest on the triage (listed) rows that carry real sale history.
  /// </summary>
  private ConfidenceTier ScoreRouted(RoutingItemInputs inputs, RoutingVerdict verdict, RoutingConfig cfg)
  {
    var lean = verdict.IsReview ? VerdictLean.Neutral : verdict.Exit switch
    {
      RoutingExit.List => VerdictLean.OnMarket,
      RoutingExit.Vendor or RoutingExit.Gc or RoutingExit.Desynth => VerdictLean.OffMarket,
      _ => VerdictLean.Neutral,
    };
    var recentSales = inputs.CommunitySampleCount > 0 ? inputs.CommunitySampleCount
      : inputs.LastSale is not null ? 1 : 0;
    var evidence = new LedgerConfidence.Evidence(
      Lean: lean,
      LaneSampleCount: Math.Max(inputs.CommunitySampleCount, inputs.LastSale is not null ? 1 : 0),
      LaneSpread: 0.0,
      VelocityPerDay: inputs.MarketVelocity,
      RecentSalesCount: recentSales,
      EvidenceAgeDays: inputs.MarketLastSaleDays ?? 0,
      LocalCommunityAccord: Accord.Unknown,
      MinSamples: cfg.CommunityMinSamples,
      StaleDays: 14);
    var overrides = _overrideCounts.GetValueOrDefault(verdict.Exit.ToString());
    return LedgerConfidence.Tier(evidence, overrides);
  }

  /// <summary>Confidence for an absorbed triage (listed) row - the Alexander Miniature path.</summary>
  private ConfidenceTier ScoreTriage(PricingItem item, LedgerPile natural)
  {
    var lean = natural switch
    {
      LedgerPile.Reprice or LedgerPile.List => VerdictLean.OnMarket,
      LedgerPile.PullAndVendor or LedgerPile.Melt or LedgerPile.Churn => VerdictLean.OffMarket,
      _ => VerdictLean.Neutral,
    };
    var evidence = new LedgerConfidence.Evidence(
      Lean: lean,
      LaneSampleCount: item.HistorySaleCount,
      LaneSpread: 0.0,
      VelocityPerDay: null,
      RecentSalesCount: item.HistorySaleCount, // sales in the past 14 days
      EvidenceAgeDays: 0,                       // the current listing is live evidence
      LocalCommunityAccord: Accord.Unknown,
      MinSamples: Plugin.Configuration.LaneMinHistorySamples,
      StaleDays: 14);
    var overrides = _overrideCounts.GetValueOrDefault(natural.ToString());
    return LedgerConfidence.Tier(evidence, overrides);
  }

  private static unsafe bool HasAnyMateria(InventoryItem* slot)
  {
    for (int m = 0; m < 5; m++)
      if (slot->Materia[m] != 0) return true;
    return false;
  }

  /// <summary>Loads open persistent flags (V12). Called on window open and after flag mutations.</summary>
  private void RefreshHeldFlags()
  {
    try { _heldFlags = GilStorage.GetOpenTriageFlags(); }
    catch { _heldFlags = []; }
  }

  /// <summary>The unified triage row list: fresh run items, then held flags not already shown as a live row.</summary>
  private List<InboxRow> BuildTriageRows()
  {
    var rows = new List<InboxRow>();
    foreach (var item in _triageItems)
      rows.Add(new InboxRow(item, null));
    foreach (var flag in _heldFlags)
    {
      if (_triageItems.Any(t => t.ItemId == flag.ItemId && t.IsHq == flag.IsHq && t.RetainerName == flag.RetainerName))
        continue;
      rows.Add(new InboxRow(SyntheticItem(flag), flag));
    }
    return rows;
  }

  private PricingItem SyntheticItem(TriageFlag flag)
  {
    if (_flagItems.TryGetValue(flag.Id, out var cached)) return cached;

    var item = new PricingItem
    {
      SlotIndex = flag.SlotIndex,
      ItemId = flag.ItemId,
      IsHq = flag.IsHq,
      ItemName = GilTracker.GetItemName(flag.ItemId),
      RetainerName = flag.RetainerName,
      Quantity = 0,
      CurrentListingPrice = flag.OldPrice > 0 ? flag.OldPrice : null,
      MbPrice = flag.FlaggedPrice > 0 ? flag.FlaggedPrice : null,
      VendorPrice = VendorPriceOf(flag.ItemId),
      Result = flag.Reason switch
      {
        "upward_held" or "outlier_warn" => PricingResult.UpwardHeld,
        "lane_held" => PricingResult.LaneHeld,
        "cap_blocked" => PricingResult.CapBlocked,
        _ => PricingResult.NoData,
      },
    };
    _flagItems[flag.Id] = item;
    return item;
  }

  private static int VendorPriceOf(uint itemId)
  {
    try
    {
      return (int)Svc.Data.GetExcelSheet<Item>().GetRow(itemId).PriceLow;
    }
    catch { return 0; }
  }

  // ==========================================================================
  // Draw
  // ==========================================================================

  public override void Draw()
  {
    // A batch killed by a task timeout leaves the orchestrator latched - recover.
    Plugin.TriageOrchestrator.RecoverIfStalled();

    var brainOn = Plugin.Configuration.EnableRoutingBrain;

    // Universalis answers land async - re-run the bag piles when data arrives so
    // "no evidence" verdicts settle, but never while the player is mid-decision.
    if (brainOn)
    {
      var uniLanded = UniversalisStats.Version != _uniVersion
                      || UniversalisHistory.Version != _uniHistVersion;
      var playerTouched = _items.Any(i =>
        i.OverrideRecorded || i.Pile != i.Verdict.Exit || i.InReview != i.Verdict.IsReview)
        || _actions.Count > 0;
      if (uniLanded && !playerTouched && !Plugin.GcTurnIn.IsRunning)
        Refresh();
    }

    var triageRows = BuildTriageRows();

    if (_items.Count == 0 && triageRows.Count == 0)
    {
      ImGui.TextDisabled(brainOn
        ? "Nothing on the ledger - no routable gear in your bags and no open flags."
        : "The routing brain is off (config's Routing tab). No open flags to review.");
      if (ImGui.Button("Refresh")) { Refresh(); RefreshHeldFlags(); }
      return;
    }

    DrawHeader(triageRows);
    ImGui.Separator();

    DrawReviewPile(triageRows);
    DrawListPile();
    DrawRepricePile(triageRows);
    DrawPullAndVendorPile(triageRows);
    DrawMeltPile();
    DrawChurnPile();
    DrawWatchPile(triageRows);
  }

  private void DrawHeader(List<InboxRow> triageRows)
  {
    ImGui.Text("the ledger speaks in last-pinch tense");
    ImGui.SameLine();
    if (_ventureStock is int stock)
    {
      // Projection beside the stock: where the tokens WILL be in a week at the
      // measured burn - the operand the saturation tilt actually reads.
      ImGui.TextDisabled(_weeklyBurn is int wb && wb > 0
        ? $"- {stock:N0} venture tokens (~{stock - wb:N0} in 7d at your burn)"
        : $"- {stock:N0} venture tokens");
    }

    var uniPending = UniversalisStats.PendingCount;
    if (uniPending > 0)
    {
      ImGui.SameLine();
      ImGui.TextDisabled($"- Universalis: checking {uniPending}...");
    }

    if (ImGui.Button("Refresh")) { Refresh(); RefreshHeldFlags(); }
    ImGui.SameLine();

    // The triage batch Go (absorbed): executes whatever actions are staged on the
    // listed rows. Staged by the per-pile bulk confirms or by row clicks.
    if (Plugin.TriageOrchestrator.IsRunning)
    {
      if (ImGui.Button("Cancel batch")) Plugin.TriageOrchestrator.Abort();
      ImGui.SameLine();
      ImGui.TextDisabled("Batch in progress...");
    }
    else
    {
      var staged = _actions.Count;
      ImGui.BeginDisabled(staged == 0);
      if (ImGui.Button($"Go ({DescribeActions()})"))
        ExecuteTriageBatch();
      ImGui.EndDisabled();
    }

    // The GC churn readout / Turn In button (location-gated). The RUNNING branch is
    // gated on the run, not the pile - the standard run readout (RunHostRender).
    if (Plugin.GcTurnIn.IsRunning)
    {
      ImGui.SameLine();
      if (ImGui.Button("Cancel turn-in")) Plugin.GcTurnIn.Abort();
      ImGui.SameLine();
      RunHostRender.Progress(Plugin.GcTurnIn.Run, "Turning in");
    }
  }

  private string DescribeActions()
  {
    var vend = _actions.Count(a => a.Value == TriageAction.Vendor);
    var pull = _actions.Count(a => a.Value == TriageAction.Pull);
    var reprice = _actions.Count(a => a.Value == TriageAction.Reprice);
    var parts = new List<string>();
    if (vend > 0) parts.Add($"{vend} vendor");
    if (pull > 0) parts.Add($"{pull} pull");
    if (reprice > 0) parts.Add($"{reprice} reprice");
    return parts.Count > 0 ? string.Join(", ", parts) : "nothing staged";
  }

  // ---- Review ----

  private void DrawReviewPile(List<InboxRow> triageRows)
  {
    var bagReview = _items.Where(i => i.ActivePile == LedgerPile.Review).ToList();
    var listedReview = triageRows.Where(r => EffectiveTriagePile(r.Item) == LedgerPile.Review).ToList();
    if (bagReview.Count == 0 && listedReview.Count == 0) return;

    if (!PileHeader("Review", "Review", bagReview.Count + listedReview.Count, ScroogeColors.Stale,
        "Too close to call, no evidence, or the market contradicts the verdict - your eyes, row by row. Never bulk."))
      return;

    foreach (var item in bagReview)
      DrawRoutedRow(item, showMoves: true);
    foreach (var row in listedReview)
      DrawTriageRow(row, LedgerPile.Review);
  }

  // ---- List ----

  private void DrawListPile()
  {
    var rows = _items.Where(i => i.ActivePile == LedgerPile.List).ToList();
    if (rows.Count == 0) return;

    var atBell = AtRetainerBell();
    if (!PileHeader($"List - at a retainer bell{LocationTag(atBell)}", "List", rows.Count, ScroogeColors.Earned,
        "Earns real gil on the market board. The Hawk run lists it - summon your retainers."))
      return;

    BellConfirmButton(atBell, "L");
    foreach (var item in rows)
      DrawRoutedRow(item, showMoves: true);
  }

  // ---- Reprice ----

  private void DrawRepricePile(List<InboxRow> triageRows)
  {
    var rows = triageRows.Where(r => EffectiveTriagePile(r.Item) == LedgerPile.Reprice).ToList();
    if (rows.Count == 0) return;

    if (!PileHeader("Reprice - at the listing's retainer", "Reprice", rows.Count, ScroogeColors.Warning,
        "A standing listing needs its price fixed (cap / undercut / upward-held). Reprices with the guards bypassed."))
      return;

    BulkConfirmTriage(rows, TriageAction.Reprice, "reprice");
    foreach (var row in rows)
      DrawTriageRow(row, LedgerPile.Reprice);
  }

  // ---- Pull-and-Vendor ----

  private void DrawPullAndVendorPile(List<InboxRow> triageRows)
  {
    var bagVendor = _items.Where(i => i.ActivePile == LedgerPile.PullAndVendor).ToList();
    var listed = triageRows.Where(r => EffectiveTriagePile(r.Item) == LedgerPile.PullAndVendor).ToList();
    if (bagVendor.Count == 0 && listed.Count == 0) return;

    var atBell = AtRetainerBell();
    if (!PileHeader($"Pull & Vendor - at a retainer bell{LocationTag(atBell)}", "PullVendor",
        bagVendor.Count + listed.Count, ScroogeColors.Muted,
        "No better exit in evidence: pull the listing and/or vendor-sell. Executed at a retainer."))
      return;

    // Bag gear rides the shared bell run; listed rows -> triage vendor batch.
    if (bagVendor.Count > 0)
      BellConfirmButton(atBell, "V");
    if (listed.Count > 0)
      BulkConfirmTriage(listed, TriageAction.Vendor, "vendor");

    foreach (var item in bagVendor)
      DrawRoutedRow(item, showMoves: true);
    foreach (var row in listed)
      DrawTriageRow(row, LedgerPile.PullAndVendor);
  }

  // ---- Melt ----

  private void DrawMeltPile()
  {
    var rows = _items.Where(i => i.ActivePile == LedgerPile.Melt).ToList();
    if (rows.Count == 0) return;

    if (!PileHeader("Melt - at the desynthesis window", "Melt", rows.Count, ScroogeColors.Amber,
        "Skillup value or yields beat the alternatives. Run from the desynth window (it pre-selects this pile)."))
      return;

    ImGui.TextDisabled("Open the desynthesis window - it marks and selects this pile for you.");
    foreach (var item in rows)
      DrawRoutedRow(item, showMoves: true);
  }

  // ---- Churn ----

  private void DrawChurnPile()
  {
    var rows = _items.Where(i => i.ActivePile == LedgerPile.Churn).ToList();
    if (rows.Count == 0) return;

    var atGc = GcTurnInOrchestrator.AtExpertDelivery();
    if (!PileHeader($"Churn - at your GC's Expert Delivery{LocationTag(atGc)}", "Churn", rows.Count, ScroogeColors.Warning,
        "Seals beat gil (or venture stock demands it). Expert Delivery at your GC."))
      return;

    if (!Plugin.GcTurnIn.IsRunning)
    {
      var unanimous = rows.Where(i => LedgerConfidence.IsBulkEligible(i.Confidence)).ToList();
      ImGui.BeginDisabled(!atGc || unanimous.Count == 0);
      if (ImGui.Button($"Turn In ({unanimous.Count} unanimous)"))
        ExecuteChurn(unanimous);
      ImGui.EndDisabled();
      if (!atGc && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        ImGui.SetTooltip("Open your GC personnel officer's Expert Delivery window.");
    }
    foreach (var item in rows)
      DrawRoutedRow(item, showMoves: true);
  }

  // ---- Watch (ruling 3: always visible, collapsed count summary) ----

  private void DrawWatchPile(List<InboxRow> triageRows)
  {
    // Categorize: thin = lane_held listed rows; races/bait = lane outcomes (grown in
    // session 3's listed-rows slice); other = protected holds + observed bans + bag Watch.
    var counts = new Dictionary<WatchCategory, int>();
    void Bump(WatchCategory c) => counts[c] = counts.GetValueOrDefault(c) + 1;

    var watchListed = triageRows.Where(r => EffectiveTriagePile(r.Item) == LedgerPile.Watch).ToList();
    foreach (var r in watchListed)
      Bump(r.Item.Result == PricingResult.LaneHeld ? WatchCategory.Thin : WatchCategory.Other);

    var bagWatch = _items.Where(i => i.ActivePile == LedgerPile.Watch).ToList();
    foreach (var _ in bagWatch) Bump(WatchCategory.Other);

    var summary = LedgerConfidence.WatchSummary(counts);

    // Always visible; expands to detail rows on demand (ruling 3: full rows break out
    // only when volume makes inline unwieldy - the collapsing header is that seam).
    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
    var open = ImGui.CollapsingHeader($"Watch - {summary}###pileWatch");
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("Correctly placed, leave it: thin lanes, floor-waiting races, ignored bait, protected holds. Always shown, never crowds the actionable piles.");
    if (!open) return;

    foreach (var item in bagWatch)
      ImGui.TextDisabled($"  {Format.Hq(item.Name, item.IsHq)} - {item.Verdict.Reason}");
    foreach (var r in watchListed)
      ImGui.TextDisabled($"  {Format.Hq(r.Item.ItemName, r.Item.IsHq)} @ {r.Item.RetainerName} - {(r.IsFresh ? BuildReason(r.Item) : r.Flag!.Detail)}");
  }

  // ==========================================================================
  // Row renderers
  // ==========================================================================

  /// <summary>
  /// Collapsing pile header with a colored title + count. Returns whether it is open.
  /// The <paramref name="id"/> is STABLE (never carries the dynamic location tag), so
  /// walking to a retainer does not reset the header's expand state.
  /// </summary>
  private static bool PileHeader(string title, string id, int count, Vector4 color, string hint)
  {
    ImGui.PushStyleColor(ImGuiCol.Text, color);
    var open = ImGui.CollapsingHeader($"{title}: {count}###pile{id}", ImGuiTreeNodeFlags.DefaultOpen);
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(hint);
    return open;
  }

  private static string LocationTag(bool here) => here ? "  [you are here]" : "";

  private void DrawRoutedRow(RoutedItem item, bool showMoves)
  {
    TierBadge(item.Confidence);
    ImGui.SameLine();
    ImGui.Text(Format.Hq(item.Name, item.IsHq));
    ImGui.SameLine();
    ImGui.TextDisabled($"ilvl {item.Ilvl}");
    ImGui.SameLine();
    ImGui.TextWrapped($"- {item.Verdict.Reason}");
    if (item.Verdict.RunnerUpReason.Length > 0 && ImGui.IsItemHovered())
      ImGui.SetTooltip($"Runner-up ({item.Verdict.RunnerUp}): {item.Verdict.RunnerUpReason}");

    if (showMoves)
      DrawMoveButtons(item);
  }

  private void DrawTriageRow(InboxRow row, LedgerPile pile)
  {
    var item = row.Item;
    var tier = ScoreTriage(item, LedgerPiles.ForTriage(item.Result));
    TierBadge(tier);
    ImGui.SameLine();
    var color = row.IsFresh ? ScroogeColors.Amber : ScroogeColors.Warning;
    ImGui.PushStyleColor(ImGuiCol.Text, color);
    ImGui.Text(Format.Hq(item.ItemName, item.IsHq));
    ImGui.PopStyleColor();
    ImGui.SameLine();
    ImGui.TextDisabled($"@ {item.RetainerName}");
    if (!row.IsFresh)
    {
      ImGui.SameLine();
      var ageDays = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - row.Flag!.CreatedAt) / 86400;
      ImGui.TextDisabled(ageDays < 1 ? "(today)" : $"({ageDays}d)");
    }
    ImGui.SameLine();
    ImGui.TextWrapped($"- {(row.IsFresh ? BuildReason(item) : row.Flag!.Detail)}");

    if (Plugin.TriageOrchestrator.IsRunning) return;

    // Action set - preserved from the triage inbox (Vend / Pull / Reprice / Dismiss).
    _actions.TryGetValue(item, out var current);
    var canReprice = item.Result is PricingResult.CapBlocked or PricingResult.UndercutTooDeep or PricingResult.UpwardHeld;
    var key = $"{item.ItemId}_{item.IsHq}_{item.RetainerName}";

    DrawActionToggle(item, key, TriageAction.Vendor, "Vend", current, pile);
    ImGui.SameLine(0, 2);
    DrawActionToggle(item, key, TriageAction.Pull, "Pull", current, pile);
    if (canReprice)
    {
      ImGui.SameLine(0, 2);
      DrawActionToggle(item, key, TriageAction.Reprice, "Reprc", current, pile);
    }
    ImGui.SameLine(0, 2);
    if (ImGui.SmallButton($"Dismiss##{key}"))
      DismissRow(row);
  }

  private static void TierBadge(ConfidenceTier tier)
  {
    var (glyph, color, label) = tier switch
    {
      ConfidenceTier.Unanimous => ("*", ScroogeColors.Earned, "Unanimous - evidence agrees; bulk-confirmable"),
      ConfidenceTier.Contradicted => ("!", ScroogeColors.Spent, "Contradicted - the market disagrees; in Review, immune to bulk"),
      _ => ("~", ScroogeColors.Amber, "Mixed - thin or partial evidence; needs your row click"),
    };
    ImGui.PushStyleColor(ImGuiCol.Text, color);
    ImGui.Text(glyph);
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
  }

  // ==========================================================================
  // Bulk confirm (design Section 4 - only Unanimous rows enumerate)
  // ==========================================================================

  /// <summary>
  /// ONE confirm for everything ruled at the bell: List and Pull-and-Vendor bag
  /// rows execute through the same Hawk run (it lists and vendors per item), so
  /// one trip to the bell is one click - the pile split is presentation, not two
  /// jobs. Drawn in both piles; either instance fires the whole set. Only rows
  /// the confidence gate cleared (Unanimous or player-ruled) ride.
  /// </summary>
  private void BellConfirmButton(bool atBell, string idSuffix)
  {
    var listRows = _items.Where(i => i.ActivePile == LedgerPile.List).ToList();
    var vendorRows = _items.Where(i => i.ActivePile == LedgerPile.PullAndVendor).ToList();
    var listSet = LedgerConfidence.BulkSet(listRows.Select(r => (r, r.Confidence, r.PlayerResolved)));
    var vendSet = LedgerConfidence.BulkSet(vendorRows.Select(r => (r, r.Confidence, r.PlayerResolved)));
    var total = listSet.Count + vendSet.Count;

    ImGui.BeginDisabled(!atBell || total == 0);
    if (ImGui.Button($"Confirm bell run ({listSet.Count} list + {vendSet.Count} vendor)##bellrun{idSuffix}"))
    {
      var all = listSet.Concat(vendSet).ToList();
      foreach (var item in all)
        RecordRoutedSignal(item, item.Pile); // a bulk confirm is mass agreement
      RunHawkForRouted(all);
    }
    ImGui.EndDisabled();
    if (!atBell && total > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
      ImGui.SetTooltip("Summon your retainers (any bell) to run these.");

    var pending = (listRows.Count - listSet.Count) + (vendorRows.Count - vendSet.Count);
    if (pending > 0)
    {
      ImGui.SameLine();
      ImGui.TextDisabled($"{pending} need a row click");
    }
  }

  private void BulkConfirmTriage(List<InboxRow> rows, TriageAction action, string verb)
  {
    var eligible = rows
      .Where(r => LedgerConfidence.IsBulkEligible(ScoreTriage(r.Item, LedgerPiles.ForTriage(r.Item.Result))))
      .ToList();
    ImGui.BeginDisabled(eligible.Count == 0 || Plugin.TriageOrchestrator.IsRunning);
    if (ImGui.Button($"Confirm all {verb} ({eligible.Count} unanimous)##bulkt{verb}{action}"))
    {
      foreach (var r in eligible)
      {
        _actions[r.Item] = action;
        RecordTriageSignal(r.Item, LedgerPiles.ForTriage(r.Item.Result), action);
      }
      ExecuteTriageBatch();
    }
    ImGui.EndDisabled();
    var mixed = rows.Count - eligible.Count;
    if (mixed > 0)
    {
      ImGui.SameLine();
      ImGui.TextDisabled($"{mixed} need a row click");
    }
  }

  // ==========================================================================
  // Actions + teaching signals
  // ==========================================================================

  private void DrawMoveButtons(RoutedItem item)
  {
    DrawMoveButton(item, RoutingExit.List, "List");
    ImGui.SameLine(0, 2);
    DrawMoveButton(item, RoutingExit.Desynth, "Melt");
    ImGui.SameLine(0, 2);
    DrawMoveButton(item, RoutingExit.Gc, "GC");
    ImGui.SameLine(0, 2);
    DrawMoveButton(item, RoutingExit.Vendor, "Vend");
  }

  private void DrawMoveButton(RoutedItem item, RoutingExit target, string label)
  {
    // Green means DECIDED - by evidence (Unanimous) or by the player - never just
    // "where the row happens to sit". An undecided row (Mixed, or demoted into
    // Review) shows no green and every button stays live, so the click IS the
    // ruling whether it confirms the router or moves the row.
    var settled = item.PlayerResolved || LedgerConfidence.IsBulkEligible(item.Confidence);
    var isCurrent = settled && item.ActivePile != LedgerPile.Review && item.Pile == target;
    if (isCurrent)
      ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.45f, 0.25f, 1f));

    if (ImGui.SmallButton($"{label}##move{item.Container}_{item.SlotIndex}_{target}") && !isCurrent)
    {
      RecordRoutedSignal(item, target); // confirmation or disagreement - both teach
      item.Pile = target;
      item.InReview = false;
      item.PlayerResolved = true;
    }

    if (isCurrent)
      ImGui.PopStyleColor();
  }

  /// <summary>
  /// Records a teaching signal for a bag verdict (design Section 4). Confirmations
  /// (player == router) and overrides (player != router) both write to V14
  /// routing_overrides; the confidence read side counts only the disagreements.
  /// Once per (item, target) per session so a held view does not spam the table.
  /// </summary>
  private void RecordRoutedSignal(RoutedItem item, RoutingExit playerExit)
  {
    var routerVerdict = item.Verdict.IsReview ? "Review" : item.Verdict.Exit.ToString();
    var key = (item.ItemId, item.IsHq, $"{routerVerdict}->{playerExit}");
    if (!_signalsRecorded.Add(key)) return;
    item.OverrideRecorded = true;
    try
    {
      GilStorage.InsertRoutingOverride(item.ItemId, item.IsHq, item.Ilvl,
        routerVerdict, item.Verdict.Reason, playerExit.ToString());
    }
    catch { /* storage unavailable - the move still applies, just unrecorded */ }
  }

  /// <summary>Records a teaching signal for an absorbed triage row - the verdict class is its natural pile.</summary>
  private void RecordTriageSignal(PricingItem item, LedgerPile natural, TriageAction action)
  {
    var routerVerdict = natural.ToString();
    var key = (item.ItemId, item.IsHq, $"{routerVerdict}->{action}");
    if (!_signalsRecorded.Add(key)) return;
    try
    {
      GilStorage.InsertRoutingOverride(item.ItemId, item.IsHq, 0,
        routerVerdict, item.Result.ToString(), action.ToString());
    }
    catch { /* storage unavailable */ }
  }

  private void DrawActionToggle(PricingItem item, string key, TriageAction action, string label,
    TriageAction current, LedgerPile pile)
  {
    var isActive = current == action;
    if (isActive)
    {
      var highlight = action switch
      {
        TriageAction.Vendor => new Vector4(0.8f, 0.2f, 0.2f, 1f),
        TriageAction.Pull => new Vector4(0.2f, 0.5f, 0.8f, 1f),
        TriageAction.Reprice => new Vector4(0.7f, 0.6f, 0.1f, 1f),
        _ => new Vector4(0.4f, 0.4f, 0.4f, 1f),
      };
      ImGui.PushStyleColor(ImGuiCol.Button, highlight);
    }

    if (ImGui.SmallButton($"{label}##{key}_{action}"))
    {
      if (isActive)
        _actions.Remove(item);
      else
      {
        _actions[item] = action;
        RecordTriageSignal(item, pile, action);
      }
    }

    if (isActive)
      ImGui.PopStyleColor();
  }

  private void DismissRow(InboxRow row)
  {
    if (row.IsFresh)
    {
      _triageItems.Remove(row.Item);
      _actions.Remove(row.Item);
    }
    else
    {
      try { GilStorage.SetTriageFlagStatus(row.Flag!.Id, "dismissed"); } catch { /* storage unavailable */ }
      if (_flagItems.TryGetValue(row.Flag!.Id, out var stale)) _actions.Remove(stale);
      _flagItems.Remove(row.Flag!.Id);
      RefreshHeldFlags();
    }
    RecordTriageSignal(row.Item, EffectiveTriagePile(row.Item), TriageAction.None);
  }

  private LedgerPile EffectiveTriagePile(PricingItem item)
  {
    var natural = LedgerPiles.ForTriage(item.Result);
    return LedgerPiles.Effective(natural, ScoreTriage(item, natural));
  }

  // ==========================================================================
  // Executors (existing paths, now confidence-gated)
  // ==========================================================================

  /// <summary>Hands a specific set of bag rows to the Hawk run (List selected, Vendor flagged).</summary>
  private void RunHawkForRouted(List<RoutedItem> rows)
  {
    var hawkItems = new List<HawkWindow.HawkItem>();
    foreach (var item in rows.Where(i => i.Pile is RoutingExit.List or RoutingExit.Vendor))
    {
      hawkItems.Add(new HawkWindow.HawkItem
      {
        ItemId = item.ItemId,
        Name = item.Name,
        Quantity = item.Quantity,
        IsHq = item.IsHq,
        Selected = item.Pile == RoutingExit.List,
        IsAlwaysVendor = item.Pile == RoutingExit.Vendor,
        Container = item.Container,
        SlotIndex = item.SlotIndex,
        LastSalePrice = item.LastSalePrice,
      });
    }
    if (hawkItems.Count == 0)
    {
      Svc.Chat.PrintError("[Scrooge] None of the confirmed rows are List/Vendor exits - nothing for the Hawk run.");
      return;
    }
    Plugin.AutoPinch.NavigateAndStartHawkRun(hawkItems);
    IsOpen = false;
  }

  /// <summary>Hands a specific set of Churn rows to the GC turn-in orchestrator.</summary>
  private void ExecuteChurn(List<RoutedItem> rows)
  {
    var churnItems = rows
      .Select(i => new GcTurnInOrchestrator.GcTurnInItem(
        i.ItemId, i.IsHq, i.Name, GcSeals.For(i.ItemId) ?? 0))
      .Where(i => i.SealReward > 0)
      .OrderByDescending(i => i.SealReward)
      .ToList();
    var skipped = rows.Count - churnItems.Count;
    if (skipped > 0)
      Svc.Chat.Print($"[Scrooge] Turn-in: skipped {skipped} {(skipped == 1 ? "item" : "items")} with no resolvable seal value - {(skipped == 1 ? "it stays" : "they stay")} in your bags.");
    if (churnItems.Count == 0) return;
    foreach (var item in rows)
      RecordRoutedSignal(item, RoutingExit.Gc);
    Plugin.GcTurnIn.StartRun(churnItems);
  }

  private void ExecuteTriageBatch()
  {
    if (_actions.Count == 0) return;
    var batch = new Dictionary<PricingItem, TriageAction>(_actions);
    if (!Plugin.TriageOrchestrator.QueueTriageBatch(batch))
      return;
    // Rows are retired per-item on completion (RemoveItem callback), never here.
    _actions.Clear();
  }

  // ==========================================================================
  // Triage row lifecycle (called by the orchestrator + the run log)
  // ==========================================================================

  /// <summary>Stores this run's triage items so they survive after CurrentRun clears. Called by the run log.</summary>
  internal void SetRun(RunData run)
  {
    _triageItems = run.TriageItems;
    _actions.Clear();
  }

  /// <summary>
  /// Retires a row on COMPLETION of its action (vendored/pulled/repriced) or when the
  /// orchestrator skipped it as no-longer-listed. Closes any matching held flags.
  /// Called by TriageOrchestrator - the ONLY place actions resolve rows.
  /// </summary>
  internal void RemoveItem(PricingItem item)
  {
    _triageItems.Remove(item);
    _actions.Remove(item);
    foreach (var flag in _heldFlags.Where(f => f.ItemId == item.ItemId && f.IsHq == item.IsHq && f.RetainerName == item.RetainerName))
      try { GilStorage.SetTriageFlagStatus(flag.Id, "actioned"); } catch { /* storage unavailable */ }
    RefreshHeldFlags();
  }

  // ==========================================================================
  // Desynth preview hooks (location-session parity, unchanged contract)
  // ==========================================================================

  internal int MeltPileCount
    => Plugin.Configuration.EnableRoutingBrain
      ? _items.Count(i => i.ActivePile == LedgerPile.Melt) : 0;

  internal HashSet<(uint ItemId, bool IsHq)> MeltPileVariants()
  {
    if (!Plugin.Configuration.EnableRoutingBrain) return [];
    return _items.Where(i => i.ActivePile == LedgerPile.Melt)
      .Select(i => (i.ItemId, i.IsHq)).ToHashSet();
  }

  // ==========================================================================
  // Reason strings (absorbed from the triage inbox)
  // ==========================================================================

  private static string BuildReason(PricingItem item)
  {
    return item.Result switch
    {
      PricingResult.BelowMinimum =>
        $"Below Minimum (MB/ea at {item.MbPrice:N0} gil < {Plugin.Configuration.MinimumListingPrice:N0} gil min)",
      PricingResult.BelowFloor =>
        $"Below Floor (MB/ea at {item.MbPrice:N0} gil < {item.VendorPrice:N0} gil vendor)",
      PricingResult.CapBlocked =>
        $"Cap ({item.CurrentListingPrice:N0} -> {item.MbPrice:N0}, {item.PriceChangePercent:F0}%)",
      PricingResult.UndercutTooDeep =>
        $"Undercut Too Deep ({item.PriceChangePercent:F0}%)",
      PricingResult.UpwardHeld =>
        $"Upward Held (market {item.MbPrice:N0} exceeds {Plugin.Configuration.UpwardRepriceMultiplier:0.#}x own-sales sanity; listed {item.CurrentListingPrice:N0})",
      PricingResult.LaneHeld =>
        item.Lane?.Evidence is { } ev ? $"Held (thin history) - {ev}" : "Held (thin history)",
      PricingResult.NoData => "No Data (no listings)",
      _ => "Unknown"
    };
  }

  /// <summary>
  /// True anywhere Hawk can start from: the bell roster (NavigateAndStartHawkRun
  /// hops to a retainer's sell view itself) or already inside a sell view.
  /// </summary>
  private static unsafe bool AtRetainerBell()
    => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out _)
    || GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _);
}
