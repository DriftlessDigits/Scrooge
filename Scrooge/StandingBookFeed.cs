using System;
using System.Collections.Generic;
using ECommons.DalamudServices;

namespace Scrooge;

/// <summary>
/// The write-side feed for <see cref="StandingBook"/> - the shell half. Executors
/// call the record methods as they place, pull, and reprice listings; the rows
/// buffer in memory and FLUSH ON RUN COMPLETION, because "a finished run is a flow
/// event" is the whole shape of unit 6: the run is the unit of truth, not the
/// individual click.
///
/// <para>An ABORTED run flushes too, and must. It listed real items before it
/// died - every retainer full, a cancel, a watchdog - and those listings are on
/// the board whether the run finished or not. Dropping the buffer on abort would
/// reintroduce exactly the blind spot this exists to close.</para>
///
/// <para>Everything here is best-effort: the standing book is a READOUT, and a
/// storage hiccup may make the header hedge, but it must never take down the run
/// that was doing the real work.</para>
///
/// <para><b>Best-effort is not silent.</b> The first cut of this was quiet on
/// failure, and on 07-25 that quiet cost a whole day of the book: a bug two layers
/// down committed every flush as an empty transaction, and the only symptom
/// anywhere was a Listed header that looked plausible. Swallowing the run is the
/// contract; swallowing the FACT is how that shipped. Every flush now says what it
/// banked, a short bank warns, and an exception is logged with its stack.</para>
/// </summary>
internal static class StandingBookFeed
{
  private static readonly List<OwnWrite> _pending = [];

  private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

  /// <summary>A Hawk run put this listing on the board at this ask.</summary>
  internal static void Listed(uint itemId, bool isHq, string retainer, long unitPrice, int quantity)
    => Add(new OwnWrite(Now, WriteKind.Listed, itemId, isHq, retainer, unitPrice, 0, quantity, "hawk"));

  /// <summary>
  /// A listing left the board by our own hand - the pinch's vendor rider or a
  /// triage pull/vendor. <paramref name="unitPrice"/> is the ask it was standing
  /// at, which is what leaves the book (never the vendor price we then got for it -
  /// that is a gil event, not a board event).
  /// </summary>
  internal static void Removed(uint itemId, bool isHq, string retainer, long unitPrice, int quantity, string source)
    => Add(new OwnWrite(Now, WriteKind.Removed, itemId, isHq, retainer, unitPrice, 0, quantity, source));

  /// <summary>
  /// A standing listing's ask MOVED. Only the delta enters the book - the listing
  /// itself was already standing and already counted in the baseline.
  ///
  /// <para>The pinch's own reprices deliberately do NOT come through here: a pinch
  /// re-snapshots each sell list and then updates the listings table in place as it
  /// prices, so the baseline it just wrote already carries them. Only reprices that
  /// happen AFTER the baseline - the round's reprice stage, and manual reprices -
  /// are a delta the book has to layer on.</para>
  /// </summary>
  internal static void Repriced(uint itemId, bool isHq, string retainer, long newPrice, long priorPrice, int quantity)
  {
    if (newPrice == priorPrice || priorPrice <= 0) return; // no move, or no honest operand to compare
    Add(new OwnWrite(Now, WriteKind.Repriced, itemId, isHq, retainer, newPrice, priorPrice, quantity, "triage"));
  }

  private static void Add(OwnWrite write)
  {
    lock (_pending) _pending.Add(write);
  }

  /// <summary>
  /// A run ended - bank what it wrote. Complete or aborted alike (see the class
  /// note). Non-write-side runs simply have an empty buffer and cost nothing.
  /// </summary>
  internal static void OnRunCompleted(RunCompletion completion)
  {
    List<OwnWrite> batch;
    lock (_pending)
    {
      if (_pending.Count == 0) return;
      batch = [.. _pending];
      _pending.Clear();
    }

    try
    {
      var banked = GilStorage.InsertOwnWrites(batch);
      if (banked == batch.Count)
        Svc.Log.Debug($"[Book] Banked {banked} own write(s) from a {completion.Kind} run");
      else
        // The 07-25 shape exactly: rows handed over, none (or not all) landed, and
        // nothing threw. The book will read short until the next pinch re-reads the
        // board, so SAY SO rather than letting the header quietly lie.
        Svc.Log.Warning($"[Book] Banked only {banked} of {batch.Count} own write(s) from a {completion.Kind} run - the board's value will read short until the next full scan");
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, $"[Book] Could not bank {batch.Count} own write(s) from a {completion.Kind} run");
    }
  }

  /// <summary>
  /// The board's live book-kept value, and whether it is currently saying anything
  /// a plain listings read would not. Storage failure degrades to the plain
  /// baseline - the header then reads as ground truth, which is the honest
  /// fallback: with no writes readable we have nothing to add to the last scan.
  /// </summary>
  internal static (long Value, bool BookKept, long PutUpToday) Read(long baselineGil, long lastFullScanAt)
  {
    try
    {
      var writes = GilStorage.GetOwnWritesSince(lastFullScanAt);
      var sales = GilStorage.GetRetainerSalesGilSince(lastFullScanAt);
      // "Today" is local midnight - the day Drift is having, not a UTC boundary.
      var midnight = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
      // Put-up spans the DAY, which can reach back past the last scan, so it reads
      // its own window rather than reusing the book's.
      var today = GilStorage.GetOwnWritesSince(Math.Min(midnight, lastFullScanAt));
      return (StandingBook.Value(baselineGil, writes, sales),
              StandingBook.IsBookKept(writes.Count, sales),
              StandingBook.PutUp(today, midnight));
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, "[Book] Standing-book read failed - falling back to the plain listings total");
      return (baselineGil, false, 0);
    }
  }

  /// <summary>
  /// The listing COUNT standing right now, book-kept like <see cref="Read"/> -
  /// baseline plus our own placings/pullings since the last scan, minus what sold.
  /// Storage failure degrades to the plain baseline: with no writes readable there
  /// is nothing to layer on the last scan's count.
  /// </summary>
  internal static int ListedCountNow(int baselineCount, long lastFullScanAt)
  {
    try
    {
      return StandingBook.Count(baselineCount,
        GilStorage.GetOwnWritesSince(lastFullScanAt),
        GilStorage.GetRetainerSalesCountSince(lastFullScanAt));
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, "[Book] Listed-count read failed - falling back to the last scan's count");
      return baselineCount;
    }
  }

  /// <summary>Drops anything unbanked. Plugin dispose only.</summary>
  internal static void Clear()
  {
    lock (_pending) _pending.Clear();
  }
}
