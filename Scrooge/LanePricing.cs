using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// Lane pricing - the pricing spine. "Listings are what people want; sales are
/// what people paid." The lane (recency-weighted clearing price from settled
/// sales) is the pricing model; the board is positioning only.
///
/// Pure decision core in the RoutingRules mold: no game reads, no storage, no
/// statics. Inputs arrive as snapshots, verdicts come back as records. Compiled
/// into Scrooge.Tests as a linked source - the calibration tables in
/// [[Scrooge - Lane Pricing - Design]] are the fixture data.
/// </summary>

/// <summary>One settled sale feeding a lane (MB history packet or community history).</summary>
internal readonly record struct LaneSale(long UnitPrice, long Timestamp, bool IsHq);

/// <summary>One live board listing at decision time.</summary>
internal readonly record struct LaneListing(long UnitPrice, bool IsOwn);

/// <summary>Where the lane's evidence came from. Community lanes are always labeled.</summary>
internal enum LaneSource { Local, Community }

internal enum LaneOutcome
{
  /// <summary>Undercut the cheapest in-lane listing; nothing suspicious on the board.</summary>
  InLane,
  /// <summary>Anchored in-lane; one or more above-ceiling walls ignored.</summary>
  WallIgnored,
  /// <summary>Anchored in-lane; one or more below-floor claims ignored (no walls).</summary>
  BaitIgnored,
  /// <summary>No in-lane competition (all walls, or empty board): list at the lane's upper edge.</summary>
  LaneOwned,
  /// <summary>All listings below the lane and velocity says the market clears: follow the race.</summary>
  RaceJoined,
  /// <summary>All listings below the lane: wait at the lane floor instead of chasing.</summary>
  RaceDeclined,
  /// <summary>Lane too thin and no community rescue: never act on a guess wearing numbers.</summary>
  HeldThinHistory,
}

/// <summary>A built lane: the recency-weighted clearing price and its confidence.</summary>
internal sealed record LaneModel
{
  public required double Median { get; init; }
  public required int SampleCount { get; init; }
  /// <summary>Weighted mean age of the evidence, in days - receipts carry this later.</summary>
  public required double WeightedAgeDays { get; init; }
  public required LaneSource Source { get; init; }
}

/// <summary>Lane knobs. Ceiling reuses UpwardRepriceMultiplier, promoted: "3x what it actually sells for = suspicious, in every direction."</summary>
internal sealed record LaneConfig
{
  public double FloorPct { get; init; } = 0.5;
  public double CeilingMult { get; init; } = 3.0;
  public int MinHistorySamples { get; init; } = 3;
  /// <summary>Recency half-life SEED (resolver v0). Seeded 30d from the 2026-07-13 sale-age query.</summary>
  public double HalfLifeDays { get; init; } = 30.0;
  /// <summary>Sales/day at or above which an all-below-lane race is joined instead of declined.</summary>
  public double RaceJoinMinVelocityPerDay { get; init; } = 0.1;
}

/// <summary>The verdict: an anchor and its named story, with the evidence spelled out.</summary>
internal sealed record LaneDecision
{
  public required LaneOutcome Outcome { get; init; }
  /// <summary>Null = held (keep-price-and-flag at the pinch, don't-auto-price in a Hawk run).</summary>
  public long? Anchor { get; init; }
  /// <summary>True = anchor is a board listing to undercut; false = an absolute lane price, list as-is.</summary>
  public bool AnchorIsListing { get; init; }
  public int WallsIgnored { get; init; }
  public int BaitIgnored { get; init; }
  public required string Evidence { get; init; }
}

internal static class LanePricing
{
  /// <summary>
  /// Build the lane from settled sales: recency-weighted median, sample count,
  /// weighted evidence age. Quality-aware - an HQ lane is built from HQ sales
  /// only. Long window with recency discounting, never a hard cutoff. Returns
  /// null when no quality-matching sales exist.
  /// </summary>
  public static LaneModel? BuildLane(IReadOnlyList<LaneSale> sales, bool isHq, LaneConfig cfg, long nowUnix, LaneSource source = LaneSource.Local)
  {
    var lane = new List<(double Price, double Weight, double AgeDays)>();
    foreach (var sale in sales)
    {
      if (sale.IsHq != isHq)
        continue;
      var ageDays = Math.Max(0, (nowUnix - sale.Timestamp) / 86400.0);
      var weight = Math.Pow(2, -ageDays / cfg.HalfLifeDays);
      lane.Add((sale.UnitPrice, weight, ageDays));
    }

    if (lane.Count == 0)
      return null;

    lane.Sort((a, b) => a.Price.CompareTo(b.Price));
    var totalWeight = lane.Sum(s => s.Weight);
    var median = lane[^1].Price;
    var cumulative = 0.0;
    foreach (var s in lane)
    {
      cumulative += s.Weight;
      if (cumulative >= totalWeight / 2)
      {
        median = s.Price;
        break;
      }
    }

    return new LaneModel
    {
      Median = median,
      SampleCount = lane.Count,
      WeightedAgeDays = lane.Sum(s => s.Weight * s.AgeDays) / totalWeight,
      Source = source,
    };
  }

  /// <summary>
  /// The one decision function, both pricing paths. Safety references are
  /// lane-relative (absolute): currentListing is optional reprice context, not
  /// the baseline. (board, lane, velocity, config, currentListing?) in;
  /// (anchor, named outcome, evidence) out.
  /// </summary>
  public static LaneDecision Decide(IReadOnlyList<LaneListing> board, LaneModel? lane, double? velocityPerDay, LaneConfig cfg, long? currentPrice = null)
  {
    if (lane == null || lane.SampleCount < cfg.MinHistorySamples)
    {
      var n = lane?.SampleCount ?? 0;
      return new LaneDecision
      {
        Outcome = LaneOutcome.HeldThinHistory,
        Anchor = null,
        Evidence = $"lane has {n} sale{(n == 1 ? "" : "s")}, need {cfg.MinHistorySamples}",
      };
    }

    var floor = lane.Median * cfg.FloorPct;
    var ceiling = lane.Median * cfg.CeilingMult;
    var label = lane.Source == LaneSource.Community ? "community lane" : "lane";
    var median = FormatGil((long)Math.Round(lane.Median));

    long? cheapestInLane = null, cheapestWall = null, cheapestBait = null;
    int walls = 0, bait = 0;
    foreach (var listing in board)
    {
      if (listing.UnitPrice > ceiling)
      {
        walls++;
        cheapestWall = Math.Min(cheapestWall ?? long.MaxValue, listing.UnitPrice);
      }
      else if (listing.UnitPrice < floor)
      {
        bait++;
        cheapestBait = Math.Min(cheapestBait ?? long.MaxValue, listing.UnitPrice);
      }
      else
      {
        cheapestInLane = Math.Min(cheapestInLane ?? long.MaxValue, listing.UnitPrice);
      }
    }

    if (cheapestInLane != null)
    {
      var outcome = walls > 0 ? LaneOutcome.WallIgnored
                  : bait > 0 ? LaneOutcome.BaitIgnored
                  : LaneOutcome.InLane;
      var evidence = outcome switch
      {
        LaneOutcome.WallIgnored => $"{walls} listing{(walls == 1 ? "" : "s")} >= {cfg.CeilingMult:0.0}x {label} ({FormatGil(cheapestWall!.Value)} vs median {median})",
        LaneOutcome.BaitIgnored => $"{bait} claim{(bait == 1 ? "" : "s")} below {label} floor ({FormatGil(cheapestBait!.Value)} vs median {median})",
        _ => $"undercutting {FormatGil(cheapestInLane.Value)} in {label} (median {median}, n={lane.SampleCount})",
      };
      return new LaneDecision
      {
        Outcome = outcome,
        Anchor = cheapestInLane,
        AnchorIsListing = true,
        WallsIgnored = walls,
        BaitIgnored = bait,
        Evidence = evidence,
      };
    }

    if (bait > 0)
    {
      // Race to the bottom: every listing sits below the lane. Velocity
      // decides join vs wait; no velocity evidence fails toward holding value.
      if (velocityPerDay >= cfg.RaceJoinMinVelocityPerDay)
      {
        return new LaneDecision
        {
          Outcome = LaneOutcome.RaceJoined,
          Anchor = cheapestBait,
          AnchorIsListing = true,
          WallsIgnored = walls,
          BaitIgnored = 0,
          Evidence = $"{bait} below-{label} listing{(bait == 1 ? "" : "s")}, market clears {velocityPerDay:0.##}/day - joining at {FormatGil(cheapestBait!.Value)} (median {median})",
        };
      }
      return new LaneDecision
      {
        Outcome = LaneOutcome.RaceDeclined,
        Anchor = (long)Math.Round(floor),
        AnchorIsListing = false,
        WallsIgnored = walls,
        BaitIgnored = bait,
        Evidence = $"{bait} below-{label} listing{(bait == 1 ? "" : "s")} ({FormatGil(cheapestBait!.Value)} vs median {median}) - waiting at {label} floor",
      };
    }

    // No in-lane competition and nothing below: all walls, or an empty board.
    return new LaneDecision
    {
      Outcome = LaneOutcome.LaneOwned,
      Anchor = (long)Math.Round(ceiling),
      AnchorIsListing = false,
      WallsIgnored = walls,
      BaitIgnored = 0,
      Evidence = walls > 0
        ? $"no in-{label} competition, {walls} wall{(walls == 1 ? "" : "s")} ignored - listing at {label} edge (median {median})"
        : $"empty board - listing at {label} edge (median {median})",
    };
  }

  private static string FormatGil(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
