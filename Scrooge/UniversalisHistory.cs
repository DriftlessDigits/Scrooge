using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Scrooge;

/// <summary>
/// The community sale-history fallback: Universalis DC-scope settled sales,
/// consulted only when the LOCAL lane is too thin to price. Mirrors the
/// <see cref="UniversalisStats"/> lifecycle discipline — sync TryGet on the
/// framework thread, a miss queues a debounced background fetch, one request
/// in flight, results land on the framework thread, TTL + back-off + disposed
/// guards — but stays IN-MEMORY: a within-session rescue gated by the trust
/// window doesn't need to survive restarts, and this avoids an unverifiable
/// SQLite migration.
///
/// Design guarantees: consumer only (never uploads), foreign LISTINGS never
/// read (settled sales only), stale-by-trust-window = silent (TryGet returns
/// null and the item falls through to hold). The lane math is untouched:
/// BuildLane simply labels a lane built from these sales LaneSource.Community.
/// </summary>
internal static class UniversalisHistory
{
  private static readonly object Lock = new();
  private static readonly Dictionary<uint, CacheRow> Cache = [];
  private static readonly HashSet<uint> Queue = [];
  private static readonly HashSet<uint> InFlight = [];

  private readonly record struct CacheRow(IReadOnlyList<LaneSale> Sales, long? LastUploadAt, long FetchedAt);

  private static string? _scope;   // current data-center name
  private static long _backoffUntil;
  private static CancellationTokenSource? _cts;
  private static Task? _worker;

  /// <summary>Seconds between fetch rounds — lets a pinch sweep accumulate one batch.</summary>
  private const double DebounceSeconds = 1.5;
  /// <summary>Back-off after a failed fetch — no hammering an unreachable API.</summary>
  private const int BackoffSeconds = 300;

  internal static void Initialize() => _cts = new CancellationTokenSource();

  internal static void Dispose()
  {
    _cts?.Cancel();
    _cts?.Dispose();
    _cts = null;
  }

  /// <summary>
  /// Community settled sales for one item, or null when there is no usable
  /// answer (disabled, DC unknown, not cached yet, TTL expired, or the data is
  /// older than the trust window — stale = unknown). A miss queues a fetch as a
  /// side effect so the next pinch pass finds it warm. Framework thread only.
  /// Returns ALL qualities; BuildLane filters to the quality being priced.
  /// </summary>
  internal static IReadOnlyList<LaneSale>? TryGet(uint itemId)
  {
    var cfg = Plugin.Configuration;
    if (!cfg.EnableUniversalis || _cts is null)
      return null;

    if (DataCenterName() is not string dc)
      return null;

    EnsureScope(dc);

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    lock (Lock)
    {
      if (!Cache.TryGetValue(itemId, out var row)
          || now - row.FetchedAt > (long)cfg.UniversalisCacheTtlHours * 3600)
      {
        Enqueue(itemId, now);
        return null;
      }

      // Trust gate: data older than the trust window is treated as NO data.
      // The row stays cached (no refetch churn) until its TTL expires.
      if (row.LastUploadAt is not long uploaded
          || now - uploaded > (long)cfg.UniversalisTrustDays * 86400)
        return null;

      return row.Sales.Count > 0 ? row.Sales : null;
    }
  }

  /// <summary>Home data-center name (Universalis scope), or null when unavailable.</summary>
  private static string? DataCenterName()
  {
    try
    {
      if (!ECommons.GameHelpers.Player.Available)
        return null;
      var world = ECommons.GameHelpers.Player.Object.HomeWorld.RowId;
      if (world == 0)
        return null;
      var name = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.World>()
        .GetRow(world).DataCenter.ValueNullable?.Name.ToString();
      return string.IsNullOrWhiteSpace(name) ? null : name;
    }
    catch (Exception ex)
    {
      Svc.Log.Debug($"[UniversalisHistory] DC resolve failed: {ex.Message}");
      return null;
    }
  }

  /// <summary>Resets the cache on a DC change (alts can live on another DC). Framework thread.</summary>
  private static void EnsureScope(string dc)
  {
    lock (Lock)
    {
      if (_scope == dc)
        return;
      _scope = dc;
      Cache.Clear();
      Queue.Clear();
    }
  }

  /// <summary>Queues one item for fetch. Caller holds Lock.</summary>
  private static void Enqueue(uint itemId, long now)
  {
    if (now < _backoffUntil || InFlight.Contains(itemId))
      return;

    Queue.Add(itemId);

    if (_worker is null or { IsCompleted: true })
      _worker = Task.Run(() => WorkAsync(_cts!.Token));
  }

  /// <summary>
  /// Drains the queue in polite batches: debounce, one request in flight,
  /// back-off on failure. Results are marshaled to the framework thread.
  /// </summary>
  private static async Task WorkAsync(CancellationToken token)
  {
    while (!token.IsCancellationRequested)
    {
      await Task.Delay(TimeSpan.FromSeconds(DebounceSeconds), token).ConfigureAwait(false);

      string scope;
      List<uint> ids;
      lock (Lock)
      {
        if (Queue.Count == 0)
          return;
        if (_scope is null)
          return;
        scope = _scope;
        ids = Queue.Take(UniversalisClient.MaxBatch).ToList();
        foreach (var id in ids)
        {
          Queue.Remove(id);
          InFlight.Add(id);
        }
      }

      List<UniversalisHistoryResult>? results = null;
      try
      {
        results = await UniversalisClient.FetchHistoryAsync(scope, ids, token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return;
      }
      catch (Exception ex)
      {
        lock (Lock)
          _backoffUntil = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + BackoffSeconds;
        Svc.Log.Debug($"[UniversalisHistory] fetch failed ({ids.Count} items), backing off {BackoffSeconds}s: {ex.Message}");
      }

      if (results is not null)
      {
        try { await Svc.Framework.RunOnFrameworkThread(() => Land(scope, ids, results)).ConfigureAwait(false); }
        catch (Exception ex) { Svc.Log.Warning($"[UniversalisHistory] failed to store fetched history: {ex.Message}"); }
      }

      lock (Lock)
        foreach (var id in ids)
          InFlight.Remove(id);
    }
  }

  /// <summary>
  /// Lands one fetch round into the memory cache. Requested ids missing from
  /// the response are cached as "known nothing" so they don't re-queue every
  /// sweep. Framework thread.
  /// </summary>
  private static void Land(string scope, List<uint> requested, List<UniversalisHistoryResult> results)
  {
    // Disposed mid-flight (plugin unload runs on the framework thread): drop
    // the batch rather than write during teardown.
    if (_cts is null)
      return;

    var byId = results.ToDictionary(r => r.ItemId);
    foreach (var id in requested)
      if (!byId.ContainsKey(id))
        byId[id] = new UniversalisHistoryResult(id, Array.Empty<LaneSale>(), null);

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    lock (Lock)
    {
      // A DC change while the fetch was in flight — drop rather than poison.
      if (_scope != scope)
        return;
      foreach (var r in byId.Values)
        Cache[r.ItemId] = new CacheRow(r.Sales, r.LastUploadAt, now);
    }

    Svc.Log.Debug($"[UniversalisHistory] {byId.Count} items landed for DC {scope}");
  }
}
