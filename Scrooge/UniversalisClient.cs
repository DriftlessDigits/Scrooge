using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Scrooge;

/// <summary>
/// One item's market stats from Universalis, home world scope. Null
/// LastUploadAt means Universalis has no data for the item (never uploaded /
/// unmarketable) — cached as "known nothing" so it isn't refetched every sweep.
/// Timestamps are unix seconds.
/// </summary>
internal readonly record struct UniversalisStat(
  uint ItemId,
  double NqVelocity,
  double HqVelocity,
  long? LastSaleAt,
  long? LastUploadAt);

/// <summary>
/// One item's DC-scope SALE history from Universalis — the labeled community
/// fallback lane. Unlike the velocity path, this deliberately reads sale
/// prices (as a lane of settled community sales) because it only ever fires
/// when the local lane is too thin to price at all. Foreign LISTINGS are never
/// read; only settled sales. Null LastUploadAt = Universalis has no data.
/// </summary>
internal readonly record struct UniversalisHistoryResult(
  uint ItemId,
  IReadOnlyList<LaneSale> Sales,
  long? LastUploadAt);

/// <summary>
/// The raw Universalis REST consumer (universalis.app, community-crowdsourced
/// market data). Design rules: consumer only (never uploads), batch endpoint
/// (up to 100 items per request), polite pacing owned by the caller. The
/// velocity path (<see cref="FetchAsync"/>) parses only per-quality sale
/// velocity, most recent sale time, and lastUploadTime — prices deliberately
/// not read. The DC-scope history path (<see cref="FetchHistoryAsync"/>) is
/// the one sanctioned price-reading exception: a community sale lane used only
/// when the local lane is too thin to price.
/// </summary>
internal static class UniversalisClient
{
  internal const int MaxBatch = 100;

  /// <summary>Sale entries to pull per item for the community lane — enough to clear MinHistorySamples with headroom.</summary>
  internal const int HistoryEntries = 20;

  private static readonly HttpClient Http = CreateClient();

  private static HttpClient CreateClient()
  {
    var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Scrooge-DalamudPlugin");
    return client;
  }

  /// <summary>
  /// Fetches stats for up to MaxBatch items on one world. Items Universalis
  /// has never seen come back with null LastUploadAt. Throws on network or
  /// parse failure — the caller owns retry/back-off policy.
  /// </summary>
  internal static async Task<List<UniversalisStat>> FetchAsync(
    uint worldId, IReadOnlyList<uint> itemIds, CancellationToken token)
  {
    // listings=0: prices are not consumed. entries=1: one most-recent sale
    // is enough for the recency signal.
    var url = $"https://universalis.app/api/v2/{worldId}/{string.Join(",", itemIds)}?listings=0&entries=1";

    using var response = await Http.GetAsync(url, token).ConfigureAwait(false);

    // A single unknown item 404s instead of listing it as unresolved.
    if (response.StatusCode == HttpStatusCode.NotFound && itemIds.Count == 1)
      return [new UniversalisStat(itemIds[0], 0, 0, null, null)];

    response.EnsureSuccessStatusCode();

    await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);

    var stats = new List<UniversalisStat>(itemIds.Count);
    var root = doc.RootElement;

    if (root.TryGetProperty("items", out var items))
    {
      // Multi-item shape: items keyed by id + unresolvedItems for unknowns.
      foreach (var prop in items.EnumerateObject())
        if (ParseItem(prop.Value) is { } stat)
          stats.Add(stat);

      if (root.TryGetProperty("unresolvedItems", out var unresolved)
          && unresolved.ValueKind == JsonValueKind.Array)
        foreach (var el in unresolved.EnumerateArray())
          if (el.TryGetUInt32(out var id))
            stats.Add(new UniversalisStat(id, 0, 0, null, null));
    }
    else if (ParseItem(root) is { } single)
    {
      // Single-item requests return the object directly.
      stats.Add(single);
    }

    return stats;
  }

  /// <summary>
  /// Fetches DC-scope SALE history for up to MaxBatch items. <paramref name="scope"/>
  /// is a Universalis world/DC/region name (data-center name for the community
  /// fallback, e.g. "Aether"). Items Universalis has never seen come back with
  /// null LastUploadAt and no sales. Throws on network/parse failure — the
  /// caller owns retry/back-off.
  /// </summary>
  internal static async Task<List<UniversalisHistoryResult>> FetchHistoryAsync(
    string scope, IReadOnlyList<uint> itemIds, CancellationToken token)
  {
    var url = $"https://universalis.app/api/v2/history/{scope}/{string.Join(",", itemIds)}?entriesToReturn={HistoryEntries}";

    using var response = await Http.GetAsync(url, token).ConfigureAwait(false);

    // A single unknown item 404s instead of listing it as unresolved.
    if (response.StatusCode == HttpStatusCode.NotFound && itemIds.Count == 1)
      return [new UniversalisHistoryResult(itemIds[0], Array.Empty<LaneSale>(), null)];

    response.EnsureSuccessStatusCode();

    await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);

    var results = new List<UniversalisHistoryResult>(itemIds.Count);
    var root = doc.RootElement;

    if (root.TryGetProperty("items", out var items))
    {
      foreach (var prop in items.EnumerateObject())
        if (ParseHistory(prop.Value) is { } r)
          results.Add(r);

      if (root.TryGetProperty("unresolvedItems", out var unresolved)
          && unresolved.ValueKind == JsonValueKind.Array)
        foreach (var el in unresolved.EnumerateArray())
          if (el.TryGetUInt32(out var id))
            results.Add(new UniversalisHistoryResult(id, Array.Empty<LaneSale>(), null));
    }
    else if (ParseHistory(root) is { } single)
    {
      results.Add(single);
    }

    return results;
  }

  private static UniversalisHistoryResult? ParseHistory(JsonElement item)
  {
    if (!item.TryGetProperty("itemID", out var idEl) || !idEl.TryGetUInt32(out var id))
      return null;

    // lastUploadTime arrives in milliseconds; 0 = never uploaded.
    long? lastUpload = item.TryGetProperty("lastUploadTime", out var upEl)
      && upEl.TryGetInt64(out var uploadMs) && uploadMs > 0
      ? uploadMs / 1000 : null;

    var sales = new List<LaneSale>();
    if (item.TryGetProperty("entries", out var entries)
        && entries.ValueKind == JsonValueKind.Array)
      foreach (var entry in entries.EnumerateArray())
      {
        // History-entry timestamps are unix SECONDS (unlike lastUploadTime).
        var price = entry.TryGetProperty("pricePerUnit", out var pEl) && pEl.TryGetInt64(out var p) ? p : 0;
        var ts = entry.TryGetProperty("timestamp", out var tEl) && tEl.TryGetInt64(out var t) ? t : 0;
        var hq = entry.TryGetProperty("hq", out var hEl) && hEl.ValueKind == JsonValueKind.True;
        if (price > 0 && ts > 0)
          sales.Add(new LaneSale(price, ts, hq));
      }

    return new UniversalisHistoryResult(id, sales, lastUpload);
  }

  private static UniversalisStat? ParseItem(JsonElement item)
  {
    if (!item.TryGetProperty("itemID", out var idEl) || !idEl.TryGetUInt32(out var id))
      return null;

    var nq = item.TryGetProperty("nqSaleVelocity", out var nqEl) && nqEl.ValueKind == JsonValueKind.Number
      ? nqEl.GetDouble() : 0;
    var hq = item.TryGetProperty("hqSaleVelocity", out var hqEl) && hqEl.ValueKind == JsonValueKind.Number
      ? hqEl.GetDouble() : 0;

    // lastUploadTime arrives in milliseconds; 0 = never uploaded.
    long? lastUpload = item.TryGetProperty("lastUploadTime", out var upEl)
      && upEl.TryGetInt64(out var uploadMs) && uploadMs > 0
      ? uploadMs / 1000 : null;

    // Most recent sale by ANYONE on this world (entries=1 → at most one row).
    long? lastSale = null;
    if (item.TryGetProperty("recentHistory", out var history)
        && history.ValueKind == JsonValueKind.Array)
      foreach (var sale in history.EnumerateArray())
        if (sale.TryGetProperty("timestamp", out var tsEl) && tsEl.TryGetInt64(out var ts))
          lastSale = lastSale is long cur ? Math.Max(cur, ts) : ts;

    return new UniversalisStat(id, nq, hq, lastSale, lastUpload);
  }
}
