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
/// The raw Universalis REST consumer (universalis.app, community-crowdsourced
/// market data). Design rules: consumer only (never uploads), batch endpoint
/// (up to 100 items per request), polite pacing owned by the caller. Parses
/// only what the advisor consumes: per-quality sale velocity, most recent
/// sale time, and lastUploadTime for the trust gate. Prices are deliberately
/// not read — pricing stays in-game.
/// </summary>
internal static class UniversalisClient
{
  internal const int MaxBatch = 100;

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
