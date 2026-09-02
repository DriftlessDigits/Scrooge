using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The routing brain's input aggregation layer. Assembles per-item evidence
/// from storage, game sheets, and player state — the rules engine and the
/// listing gate consume this instead of gathering their own inputs. The pure
/// data types live in RoutingModels.cs (shared with the test project).
/// </summary>
internal static class RoutingInputService
{
  /// <summary>
  /// Loads the batch context. Storage failures degrade to empty evidence
  /// (items read as Unknown downstream), never throw.
  /// </summary>
  internal static RoutingBatch BeginBatch()
  {
    Dictionary<(uint, bool), (int, long, int?)> sales = [];
    Dictionary<(uint, bool), long> melts = [];
    MeltPriorTable? meltPriors = null;

    try { sales = GilStorage.GetLastSalePrices(); }
    catch { /* storage unavailable — no sale evidence this batch */ }

    // The book's maturity, once per batch (the new-vs-experienced grade, ruled
    // 08-22). Two cheap COUNTs against indexed tables; unreadable counts leave
    // the book graded experienced - see RoutingBatch.BookIsNew.
    var bookIsNew = false;
    try
    {
      var (bankedSales, almanacAnswers) = GilStorage.EvidenceCounts();
      bookIsNew = BoardConfidence.IsNewBook(bankedSales, almanacAnswers);
      if (bookIsNew)
        Svc.Log.Debug($"[Routing] young book: {bankedSales} banked sales, {almanacAnswers} almanac answers - absence-based confidence withheld");
    }
    catch { /* storage unavailable - graded experienced */ }

    try
    {
      if (Plugin.DesynthYieldStore is { } store)
      {
        // Fill any missing mat weights BEFORE the rollup reads them, so a batch
        // that lands a price scores against it immediately.
        FillCommunityMatPrices(store);
        FillVendorMatPrices(store);

        // ONE read of the yield rollup feeds both the per-item value and the
        // banded prior — same rows, same mat prices, so the estimate and the
        // measurement can never be computed off different evidence.
        var summaries = store.ReadSourceSummary(0);
        melts = BuildMeltValues(summaries);
        meltPriors = BuildMeltPriors(summaries);
        Svc.Log.Debug($"[Routing] melt prior: {meltPriors.SolidBandCount} ilvl band(s) with enough melts to speak");
      }
    }
    catch { /* storage unavailable — no melt evidence this batch */ }

    var ventures = GameSafe.VentureTokenCount();
    if (ventures is int v)
      Svc.Log.Debug($"[Routing] venture stock: {v}");

    // Empirical seals-to-gil replaces the placeholder once venture returns
    // have enough data (fail-soft to the config rate).
    int? empirical = null;
    try { empirical = VentureReturns.EmpiricalSealToGilRate(); }
    catch { /* storage unavailable - placeholder rate */ }

    var cfg = Plugin.Configuration;
    return new RoutingBatch
    {
      BookIsNew = bookIsNew,
      LastSales = sales,
      MeltValues = melts,
      MeltPriors = meltPriors,
      VentureStock = ventures,
      SealToGilRate = empirical ?? cfg.SealToGilRate,
      SealRateEmpirical = empirical is not null,
      NowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
      Rules = new RoutingConfig
      {
        RoutingReviewBandPct = cfg.RoutingReviewBandPct,
        CommunityMinSamples = cfg.LaneMinHistorySamples,
        VentureBandFull = cfg.VentureBandFull,
        SealCurveFullBelow = cfg.SealCurveFullBelow,
        SealCurveZeroAbove = cfg.SealCurveZeroAbove,
        SkillupWorthYellow = cfg.SkillupWorthYellow,
        SkillupWorthRed = cfg.SkillupWorthRed,
        FloorMode = cfg.PriceFloorMode,
        MinimumListingPrice = cfg.MinimumListingPrice,
      },
    };
  }

  /// <summary>
  /// Tops up <c>community_mat_prices</c> for yield materials our own boards have
  /// never priced — the melt scale's missing weights (see <see cref="MeltYieldPrices"/>).
  ///
  /// <para>Deliberately NOT a new fetch path. It rides <see cref="UniversalisHistory"/>,
  /// the same DC-scope settled-sale queue the community lane already uses: a
  /// TryGet miss enqueues the item on that worker's debounce, its batch size, its
  /// one-in-flight rule and its back-off, and a hit hands back sales this pass. So
  /// the first batch that finds a hole queues it and scores on what it has; the
  /// next one scores with the weight in place.</para>
  ///
  /// <para>Elemental catalysts never reach here — the worklist excludes them, which
  /// is what makes their zero a ruling instead of a gap.</para>
  ///
  /// <para>A material Universalis has no sales for simply gets no row and keeps
  /// reading 0; a fabricated price would be worse than a known hole.</para>
  /// </summary>
  private static void FillCommunityMatPrices(DesynthYieldStore store)
  {
    var now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var wanted = store.MaterialsNeedingCommunityPrice(now);
    if (wanted.Count == 0) return;

    var landed = 0;
    foreach (var (itemId, isHq) in wanted)
    {
      // Miss queues the fetch and returns null — the standing lazy contract.
      if (UniversalisHistory.TryGet(itemId) is not { } sales) continue;

      // Median, not the last settle: one crazy sale must not become a
      // material's standing weight. Same derivation the list score reads, so the
      // two scales can never weigh one market differently.
      if (CommunityHistorySchema.Evidence(sales, isHq).Median is not long median || median <= 0) continue;

      store.UpsertCommunityPrice(itemId, isHq, median, now);
      landed++;
    }

    if (landed > 0)
      Svc.Log.Info($"[Melt] {landed} yield {(landed == 1 ? "material" : "materials")} priced from community sales — the melt scale weighs {(landed == 1 ? "it" : "them")} now");
  }

  /// <summary>
  /// Stamps <c>vendor_mat_prices</c> with each yield material's counter price — the
  /// melt scale's FLOOR RUNG (see <see cref="MeltYieldPrices.ApplyV28"/> for the
  /// receipt: Clear Demimateria III, 5,000 gil each, weighed at zero).
  ///
  /// <para><b>Nothing is fetched.</b> <c>Item.PriceLow</c> is Lumina sheet data —
  /// already in memory, never stale, never rate-limited. That is exactly why the fill
  /// lives here beside the community one rather than in the Universalis worker: the
  /// valuation is SQL and Lumina is Dalamud-bound, so this is the seam where the
  /// game's own numbers cross into the store.</para>
  ///
  /// <para>A PriceLow of 0 writes NOTHING. Two kinds of item read zero — elemental
  /// catalysts (Drift's deliberate zero, and excluded from the worklist besides) and
  /// current-tier gear SE has not yet given a sell value — and in both cases "the
  /// counter pays nothing" is already what an absent row means.</para>
  /// </summary>
  private static void FillVendorMatPrices(DesynthYieldStore store)
  {
    var wanted = store.MaterialsNeedingVendorPrice();
    if (wanted.Count == 0) return;

    var now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var landed = 0;
    foreach (var itemId in wanted)
    {
      if (!Svc.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var row)) continue;
      if (row.PriceLow == 0) continue;
      store.UpsertVendorPrice(itemId, row.PriceLow, now);
      landed++;
    }

    if (landed > 0)
      Svc.Log.Info($"[Melt] {landed} yield {(landed == 1 ? "material" : "materials")} floored at the vendor counter — gil the melt realizes whether or not anyone lists {(landed == 1 ? "it" : "them")}");
  }

  /// <summary>
  /// Per-quality melt values from the desynth ledger, keyed by source item.
  /// Aggregation, so it lives here (the gate consumed it, but never owned it).
  /// </summary>
  internal static Dictionary<(uint ItemId, bool IsHq), long> BuildMeltValues(List<DesynthSourceSummary> summaries)
  {
    var melts = new Dictionary<(uint, bool), long>();
    foreach (var s in summaries)
      if (s.Attempts > 0)
        melts[(s.SourceItemId, s.SourceIsHq)] = s.YieldValue / s.Attempts;
    return melts;
  }

  /// <summary>
  /// The ilvl-banded melt prior, built ONCE per batch from the same rollup. The
  /// only thing the shell adds is the source item's ilvl, which lives in the game
  /// sheet and not in the yields table; the banding and the evidence floor are
  /// <see cref="MeltPriorTable"/>'s, and are pure.
  ///
  /// <para>An item the sheet no longer has a row for (or that carries no gear
  /// level) contributes nothing rather than landing in the bottom band - see the
  /// note there.</para>
  /// </summary>
  internal static MeltPriorTable BuildMeltPriors(List<DesynthSourceSummary> summaries)
  {
    var sheet = Svc.Data.GetExcelSheet<Item>();
    var observations = new List<MeltObservation>(summaries.Count);
    foreach (var s in summaries)
    {
      if (s.Attempts <= 0) continue;
      if (!sheet.TryGetRow(s.SourceItemId, out var row)) continue;
      // Gear only: fish yields are trash (ruled 2026-08-28) and must not
      // drag down the band average gear is scored against.
      if (row.EquipSlotCategory.RowId == 0) continue;
      observations.Add(new MeltObservation((int)row.LevelItem.RowId, s.Attempts, s.YieldValue));
    }
    return MeltPriorTable.Build(observations);
  }

  /// <summary>
  /// Assembles the inputs for one item variant. Returns null when the item
  /// has no sheet row (event items, tokens) — nothing to route. Protections
  /// are slot-level facts only the caller's scan can see; omit for contexts
  /// (like the Hawk listing gate) that don't route protected items anyway.
  /// </summary>
  internal static RoutingItemInputs? Collect(RoutingBatch batch, uint itemId, bool isHq,
    ItemProtections protections = default)
  {
    if (!Svc.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
      return null;

    var isEquipment = item.EquipSlotCategory.RowId != 0;
    var fullId = isHq ? itemId + 1_000_000u : itemId;

    // Desynth skill state — the sheet's Desynth flag is the real
    // desynthesizable test; repair class alone is a leaky proxy (finding
    // #18: current-patch raid gear carries a repair class but Desynth=0,
    // so the router piled it on an exit the game refuses). The flag is
    // patch data - when SE lifts the restriction the next scan just
    // starts routing these to desynth again, no code change.
    DesynthSkillupColor? color = null;
    var isDesynthable = item.Desynth != 0;
    var repairClass = (byte)item.ClassJobRepair.RowId;
    // Any desynthable with a repair class grades — fish carry CUL and grant
    // skill the same as gear (the old isEquipment gate structurally zeroed
    // skillup worth out of every fish's melt score).
    if (repairClass != 0 && isDesynthable)
      color = DesynthSkillup.Classify(
        GameSafe.GetDesynthLevel(repairClass),
        (int)item.LevelItem.RowId,
        GameSafe.MaxDesynthLevel());

    // Universalis almanac — marketable items only (untradable gear has no
    // market to ask about). A cache miss queues an async fetch; this batch
    // evaluates on local evidence and the UI re-runs when the answer lands.
    var isMarketable = item.ItemSearchCategory.RowId != 0;
    (double Velocity, int? LastSaleDaysAgo)? market = null;
    if (isMarketable)
      market = UniversalisStats.TryGet(itemId, isHq);

    // DC-scope settled sales (community history) — the almanac cross-check's
    // evidence, and the whole of the LIST score's community case. Same
    // miss-queues-a-fetch lifecycle as velocity above, so a window round
    // self-warms the cache and re-runs when answers land; since V29 the cache
    // comes back warm after a reload instead of forfeiting to melt.
    long? communityMedian = null;
    var communityCount = 0;
    if (isMarketable && UniversalisHistory.TryGet(itemId) is { } dcSales)
      (communityMedian, communityCount) = CommunityHistorySchema.Evidence(dcSales, isHq);

    // THE SETTLED TAPE (F6, ruled 08-22): this world's banked sale ring, read
    // through the lane's own builder - recency-weighted, no hard cutoff. One
    // query against the ring index per marketable bag item; storage down means
    // no witness, exactly like every other evidence leg here.
    long? tapeMedian = null;
    var tapeCount = 0;
    double? tapeAgeDays = null;
    if (isMarketable)
      try
      {
        var ring = GilStorage.ReadSaleHistory(itemId);
        if (ring.Count > 0)
        {
          var laneCfg = new LaneConfig
          {
            HalfLifeDays = LaneHalfLife.Resolve(itemId),
            MinHistorySamples = Plugin.Configuration.LaneMinHistorySamples,
          };
          var laneSales = new List<LaneSale>(ring.Count);
          foreach (var r in ring) laneSales.Add(new LaneSale(r.UnitPrice, r.SaleTime, r.IsHq));
          if (LanePricing.BuildLane(laneSales, isHq, laneCfg,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()) is { } tapeLane)
          {
            tapeMedian = (long)Math.Round(tapeLane.Median);
            tapeCount = tapeLane.SampleCount;
            tapeAgeDays = tapeLane.WeightedAgeDays;
          }
        }
      }
      catch { /* storage unavailable - the tape stays silent */ }

    // THE LOOK'S TESTIMONY (ruled 08-23): a fresh banked recon decision with a
    // price. One PK hit against decision_cache, same cost class as the tape read
    // above; the freshness rule is the hawk's own (ONE definition of fresh). A
    // held Look (null price) stays silent - "we looked, no price would carry" is
    // not a witness for List.
    long? lookAsk = null;
    double? lookAgeHours = null;
    if (isMarketable)
      try
      {
        if (GilStorage.GetDecisionCache(itemId, isHq) is { DecidedPrice: long banked } look
            && ReconFreshness.IsFresh(look.BankedAt, batch.NowUnix, Plugin.Configuration.ReconFreshHours))
        {
          lookAsk = banked;
          lookAgeHours = batch.NowUnix > 0 ? (batch.NowUnix - look.BankedAt) / 3600.0 : null;
        }
      }
      catch { /* storage unavailable - the Look stays silent */ }

    return new RoutingItemInputs
    {
      ItemId = itemId,
      IsHq = isHq,
      Name = item.Name.ToString(),
      Ilvl = (int)item.LevelItem.RowId,
      IsEquipment = isEquipment,
      IsMarketable = isMarketable,
      IsDesynthable = isDesynthable,
      VendorPrice = (int)item.PriceLow,
      LastSale = batch.LastSales.TryGetValue((itemId, isHq), out var sale) ? sale : null,
      // Age against the batch's one clock (SF-B2) - computed here so the rules
      // stay pure and every item in the batch judges staleness from the same instant.
      LastSaleAgeDays = batch.LastSales.TryGetValue((itemId, isHq), out var aged) && batch.NowUnix > 0
        ? (int)((batch.NowUnix - aged.Timestamp) / 86400L)
        : null,
      MeltValuePerAttempt = batch.MeltValues.TryGetValue((itemId, isHq), out var melt) ? melt : null,
      // The prior rides along for EVERY desynthable item; the rules engine is the
      // one place that decides it only speaks where item history is silent, so the
      // precedence lives at one seam instead of two.
      // Equipment only: the band table is gear knowledge, and the rules
      // engine refuses a fish-borrowed prior anyway - don't attach one.
      MeltBandPrior = isEquipment && isDesynthable ? batch.MeltPriors?.For((int)item.LevelItem.RowId) : null,
      SealValue = GcSeals.For(itemId),
      MarketVelocity = market?.Velocity,
      MarketLastSaleDays = market?.LastSaleDaysAgo,
      LocalTapeMedian = tapeMedian,
      LocalTapeSampleCount = tapeCount,
      LocalTapeAgeDays = tapeAgeDays,
      LookAsk = lookAsk,
      LookAgeHours = lookAgeHours,
      CommunityMedian = communityMedian,
      CommunitySampleCount = communityCount,
      DesynthColor = color,
      DesynthSkillupEligible = color is { } c && DesynthSkillup.IsSkillupEligible(c),
      IsBanned = Plugin.Configuration.BannedItemIds.Contains(fullId),
      IsAlwaysVendor = Plugin.Configuration.AlwaysVendorItemIds.Contains(fullId),
      IsProtected = protections.Any,
      ProtectionReason = protections.Describe(),
    };
  }
}
