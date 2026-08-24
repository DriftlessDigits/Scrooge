using System;
using Scrooge.Windows;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE DASHBOARD'S REFRESH CADENCE (stability sweep, 2026-08-16).
///
/// <para>Four Gil Dashboard tabs ran raw SQL on every ImGui frame - sixty queries a
/// second for a table nobody was watching change - while four siblings hand-rolled
/// the same value/stamp/interval trio in four dialects. TabCache is that idiom with
/// one spelling, and it is deliberately Dalamud-free so the cadence itself can be
/// pinned here rather than eyeballed in a running game.</para>
///
/// <para>The rules worth pinning are the two clocks: the TTL is the world moving on
/// its own, and Invalidate is the player's own hand - which must never wait out a
/// stale window.</para>
/// </summary>
public class TabCacheTests
{
  private sealed class FakeClock
  {
    internal DateTime Now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
    internal void Advance(TimeSpan by) => Now += by;
  }

  [Fact]
  public void TheQueryDoesNotRun_UntilSomebodyAsks()
  {
    var loads = 0;
    var clock = new FakeClock();
    _ = new TabCache<int>(() => ++loads, TabCache.Tab, () => clock.Now);

    Assert.Equal(0, loads);
  }

  [Fact]
  public void RepeatedFrames_ShareOneQuery()
  {
    var loads = 0;
    var clock = new FakeClock();
    var cache = new TabCache<int>(() => ++loads, TabCache.Tab, () => clock.Now);

    for (var frame = 0; frame < 100; frame++)
      Assert.Equal(1, cache.Get());

    Assert.Equal(1, loads);
  }

  [Fact]
  public void PastTheTtl_TheAnswerIsAskedAgain()
  {
    var loads = 0;
    var clock = new FakeClock();
    var cache = new TabCache<int>(() => ++loads, TabCache.Tab, () => clock.Now);

    Assert.Equal(1, cache.Get());
    clock.Advance(TabCache.Tab);          // exactly at the edge is still fresh
    Assert.Equal(1, cache.Get());
    clock.Advance(TimeSpan.FromTicks(1)); // past it is not
    Assert.Equal(2, cache.Get());
  }

  [Fact]
  public void Invalidate_DoesNotWaitOutTheWindow()
  {
    var loads = 0;
    var clock = new FakeClock();
    var cache = new TabCache<int>(() => ++loads, TabCache.Tab, () => clock.Now);

    Assert.Equal(1, cache.Get());
    cache.Invalidate(); // the player turned the page
    Assert.Equal(2, cache.Get());
  }

  [Fact]
  public void AnOnDemandCache_NeverExpiresOnItsOwn()
  {
    var loads = 0;
    var clock = new FakeClock();
    var cache = new TabCache<int>(() => ++loads, TabCache.OnDemand, () => clock.Now);

    Assert.Equal(1, cache.Get());
    clock.Advance(TimeSpan.FromDays(3650));
    Assert.Equal(1, cache.Get());

    cache.Invalidate();
    Assert.Equal(2, cache.Get());
  }

  [Fact]
  public void ANullAnswerIsAnAnswer_NotAMissingOne()
  {
    // The earliest-transaction cache holds long? and a fresh ledger has none. A cache
    // that treated null as "not loaded" would re-run that scan every single frame -
    // exactly the bill this class exists to stop paying.
    var loads = 0;
    var clock = new FakeClock();
    var cache = new TabCache<long?>(() => { loads++; return null; }, TabCache.Tab, () => clock.Now);

    Assert.Null(cache.Get());
    Assert.Null(cache.Get());
    Assert.Equal(1, loads);
  }

  [Fact]
  public void LoadedAt_ReportsTheClockTheAnswerWasTakenAt()
  {
    var clock = new FakeClock();
    var cache = new TabCache<int>(() => 1, TabCache.Tab, () => clock.Now);

    Assert.False(cache.HasValue);
    Assert.Equal(DateTime.MinValue, cache.LoadedAt);

    var at = clock.Now;
    cache.Get();

    Assert.True(cache.HasValue);
    Assert.Equal(at, cache.LoadedAt);
  }
}
