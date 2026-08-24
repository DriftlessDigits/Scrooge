using System;

namespace Scrooge.Windows;

/// <summary>
/// A dashboard tab's answer, held between frames.
///
/// <para>ImGui redraws every tab body every frame, which means a query written
/// straight into a Draw method runs sixty times a second against SQLite for a
/// table nobody is watching change. Four tabs already knew that and hand-rolled
/// the same three fields - a value, a stamp, and an interval - once each, in
/// their own dialect; four more never got the memo and paid the full per-frame
/// bill. This is the idiom with one spelling.</para>
///
/// <para><b>Two clocks, deliberately.</b> The TTL is the answer's shelf life
/// against a world that moves on its own (sales land, listings expire).
/// <see cref="Invalidate"/> is the answer's death by the player's own hand - a
/// page turn, a filter change - and it fires immediately regardless of the TTL,
/// because "I just clicked next page" must never wait out a stale window.</para>
///
/// <para>Deliberately Dalamud-free and ImGui-free: it is a value, a clock and a
/// closure, which is what makes the cadence itself testable without a game
/// running. The clock is injectable for exactly that reason.</para>
/// </summary>
internal sealed class TabCache<T>
{
  private readonly Func<T> _load;
  private readonly TimeSpan _ttl;
  private readonly Func<DateTime> _clock;

  private T _value = default!;
  private bool _loaded;
  private DateTime _loadedAt = DateTime.MinValue;

  /// <param name="load">The query. Runs on the caller's thread, on first Get and on every expiry.</param>
  /// <param name="ttl">How long an answer stays honest. Use <see cref="TabCache.OnDemand"/> for caches only the player invalidates.</param>
  /// <param name="clock">The now the TTL is measured against. Defaults to UTC wall clock; tests pass their own.</param>
  internal TabCache(Func<T> load, TimeSpan ttl, Func<DateTime>? clock = null)
  {
    _load = load;
    _ttl = ttl;
    _clock = clock ?? (() => DateTime.UtcNow);
  }

  /// <summary>When the held value was loaded. <see cref="DateTime.MinValue"/> before the first load.</summary>
  internal DateTime LoadedAt => _loadedAt;

  /// <summary>True when a value is held (whether or not it is still fresh).</summary>
  internal bool HasValue => _loaded;

  /// <summary>The answer — the held one while it is fresh, a new one the moment it isn't.</summary>
  internal T Get()
  {
    var now = _clock();
    if (!_loaded || now - _loadedAt > _ttl)
    {
      _value = _load();
      _loaded = true;
      _loadedAt = now;
    }
    return _value;
  }

  /// <summary>Drops the held answer. The next <see cref="Get"/> re-queries.</summary>
  internal void Invalidate()
  {
    _loaded = false;
    _value = default!;
  }
}

/// <summary>The shared cadences, so the numbers live in one place rather than eight.</summary>
internal static class TabCache
{
  /// <summary>The headline strip's cadence — it sits above every tab and reads a single row.</summary>
  internal static readonly TimeSpan Headline = TimeSpan.FromSeconds(5);

  /// <summary>The tab cadence: slow enough that a stationary player stops paying, fast enough that a sale shows up while he watches.</summary>
  internal static readonly TimeSpan Tab = TimeSpan.FromSeconds(30);

  /// <summary>
  /// No shelf life — the answer stands until the player invalidates it. For the
  /// paged and filtered tabs, where "stale" is a thing the player causes, not
  /// something the clock decides.
  /// </summary>
  internal static readonly TimeSpan OnDemand = TimeSpan.MaxValue;
}
