using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Scrooge;

/// <summary>
/// The Universalis almanac: home-world sale velocity + sale recency for the
/// routing brain. Design rules (all five locked): advisor-only (never sets a
/// price), on-demand + long-TTL cache, home world scope, stale = unknown,
/// consumer only.
///
/// Consumers read synchronously via TryGet — a miss queues the item and a
/// debounced background worker batch-fetches politely (one request in flight,
/// back-off on failure). Results land in SQLite (verdicts survive restarts)
/// and the memory cache on the framework thread, then Version ticks so open
/// windows can re-evaluate. Offline or unavailable = TryGet returns null and
/// the plugin behaves exactly as before — Universalis only ever upgrades
/// verdicts from "unknown" to "known".
/// </summary>
internal static class UniversalisStats
{
  private static readonly object Lock = new();
  private static readonly Dictionary<uint, (UniversalisStat Stat, long FetchedAt)> Cache = [];
  private static readonly HashSet<uint> Queue = [];
  private static readonly HashSet<uint> InFlight = [];

  private static uint _worldId;
  private static bool _loaded;
  private static long _backoffUntil;
  private static CancellationTokenSource? _cts;
  private static Task? _worker;

  /// <summary>Seconds between fetch rounds — lets a UI sweep accumulate one batch.</summary>
  private const double DebounceSeconds = 1.5;
  /// <summary>Back-off after a failed fetch — no hammering an unreachable API.</summary>
  private const int BackoffSeconds = 300;

  /// <summary>Bumped when fetched data lands — open windows re-evaluate on change.</summary>
  internal static int Version { get; private set; }

  /// <summary>Items queued or in flight — the UI's "checking N items" count.</summary>
  internal static int PendingCount
  {
    get { lock (Lock) return Queue.Count + InFlight.Count; }
  }

  internal static void Initialize() => _cts = new CancellationTokenSource();

  internal static void Dispose()
  {
    _cts?.Cancel();
    _cts?.Dispose();
    _cts = null;
  }

  /// <summary>
  /// Trusted stats for one item variant, or null when there is no usable
  /// answer (disabled, world unknown, not cached yet, TTL expired, or data
  /// older than the trust window — stale = unknown). Misses queue a fetch
  /// as a side effect. Framework/draw thread only.
  /// </summary>
  internal static (double Velocity, int? LastSaleDaysAgo)? TryGet(uint itemId, bool isHq)
  {
    var cfg = Plugin.Configuration;
    if (!cfg.EnableUniversalis || _cts is null)
      return null;

    if (HomeWorldId() is not uint world)
      return null;

    EnsureWorldLoaded(world);

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
      if (row.Stat.LastUploadAt is not long uploaded
          || now - uploaded > (long)cfg.UniversalisTrustDays * 86400)
        return null;

      var velocity = isHq ? row.Stat.HqVelocity : row.Stat.NqVelocity;
      int? saleDays = row.Stat.LastSaleAt is long sale ? (int)((now - sale) / 86400) : null;
      return (velocity, saleDays);
    }
  }

  /// <summary>Home world id, or null when the player isn't loaded yet.</summary>
  private static uint? HomeWorldId()
  {
    if (!ECommons.GameHelpers.Player.Available)
      return null;
    var world = ECommons.GameHelpers.Player.Object.HomeWorld.RowId;
    return world != 0 ? world : null;
  }

  /// <summary>
  /// Loads the SQLite cache for the world on first use (or world change —
  /// alts can live elsewhere). Framework thread only.
  /// </summary>
  private static void EnsureWorldLoaded(uint world)
  {
    if (_loaded && _worldId == world)
      return;

    Dictionary<uint, (UniversalisStat, long)> rows = [];
    try { rows = GilStorage.GetUniversalisStats(world); }
    catch { /* storage unavailable — start with an empty cache */ }

    lock (Lock)
    {
      _worldId = world;
      _loaded = true;
      Cache.Clear();
      Queue.Clear();
      foreach (var (id, row) in rows)
        Cache[id] = row;
    }
  }

  /// <summary>Queues one item for fetch. Caller holds Lock.</summary>
  private static void Enqueue(uint itemId, long now)
  {
    if (now < _backoffUntil || InFlight.Contains(itemId) || !Queue.Add(itemId))
      return;

    if (_worker is null or { IsCompleted: true })
      _worker = Task.Run(() => WorkAsync(_cts!.Token));
  }

  /// <summary>
  /// Drains the queue in polite batches: debounce, one request in flight,
  /// back-off on failure. Results are marshaled to the framework thread —
  /// the shared SQLite connection is not thread-safe.
  /// </summary>
  private static async Task WorkAsync(CancellationToken token)
  {
    while (!token.IsCancellationRequested)
    {
      await Task.Delay(TimeSpan.FromSeconds(DebounceSeconds), token).ConfigureAwait(false);

      uint world;
      List<uint> ids;
      lock (Lock)
      {
        if (Queue.Count == 0)
          return;
        world = _worldId;
        ids = Queue.Take(UniversalisClient.MaxBatch).ToList();
        foreach (var id in ids)
        {
          Queue.Remove(id);
          InFlight.Add(id);
        }
      }

      List<UniversalisStat>? stats = null;
      try
      {
        stats = await UniversalisClient.FetchAsync(world, ids, token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return;
      }
      catch (Exception ex)
      {
        _backoffUntil = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + BackoffSeconds;
        Svc.Log.Debug($"[Universalis] fetch failed ({ids.Count} items), backing off {BackoffSeconds}s: {ex.Message}");
      }

      if (stats is not null)
      {
        try { await Svc.Framework.RunOnFrameworkThread(() => Land(world, ids, stats)).ConfigureAwait(false); }
        catch (Exception ex) { Svc.Log.Warning($"[Universalis] failed to store fetched stats: {ex.Message}"); }
      }

      lock (Lock)
        foreach (var id in ids)
          InFlight.Remove(id);
    }
  }

  /// <summary>
  /// Lands one fetch round: SQLite upsert + memory cache + Version tick.
  /// Requested ids missing from the response are cached as "known nothing"
  /// so they don't re-queue every sweep. Framework thread.
  /// </summary>
  private static void Land(uint world, List<uint> requested, List<UniversalisStat> stats)
  {
    var byId = stats.ToDictionary(s => s.ItemId);
    foreach (var id in requested)
      if (!byId.ContainsKey(id))
        byId[id] = new UniversalisStat(id, 0, 0, null, null);

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    try { GilStorage.UpsertUniversalisStats(world, byId.Values.ToList(), now); }
    catch (Exception ex) { Svc.Log.Warning($"[Universalis] cache persist failed (memory only this session): {ex.Message}"); }

    lock (Lock)
    {
      // A world change while the fetch was in flight — results are for the
      // old world; drop them rather than poison the new cache.
      if (_worldId != world)
        return;
      foreach (var stat in byId.Values)
        Cache[stat.ItemId] = (stat, now);
    }

    Version++;
    Svc.Log.Debug($"[Universalis] {byId.Count} items landed for world {world}");
  }
}
