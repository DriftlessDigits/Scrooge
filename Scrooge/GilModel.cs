using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// A retainer sale captured from the RetainerHistory hook.
/// Contains the actual sale data, and the time it was captured.
/// </summary>
public record SaleRecord
{
  public uint ItemId { get; init; }
  public string ItemName { get; init; } = string.Empty;
  public string Category { get; init; } = string.Empty;     // ItemUICategory name
  public int UnitPrice { get; init; }                       // Price per unit, in gil.
  public int Quantity { get; init; }
  public long TotalGil => (long)UnitPrice * Quantity;
  public bool IsHQ { get; init; }
  public string RetainerName { get; init; } = string.Empty;
  public string BuyerName { get; init; } = string.Empty;
  public long SaleTimestamp { get; init; }                     // Unix seconds from game server
  public DateTime SaleTime => DateTimeOffset.FromUnixTimeSeconds(SaleTimestamp).LocalDateTime;

}

/// <summary>
/// A listing snapshot captured during a pinch run.
/// Tracks time-on-market via FirstSeenTimestamp.
/// </summary>
public record ListingRecord
{
  public uint ItemId { get; init; }
  public string ItemName { get; init; } = string.Empty;
  public string Category { get; init; } = string.Empty;     // ItemUICategory name
  public int UnitPrice { get; init; }                       // Price per unit, in gil.
  public int Quantity { get; init; }
  public bool IsHQ { get; init; }
  public string RetainerName { get; init; } = string.Empty;
  public int SlotIndex { get; init; }                      // retainer inventory slot (0–19), stable across repricing
  public long FirstSeenTimestamp { get; init; }            // Unix seconds, first time we saw this listing
  public long LastUpdatedTimestamp { get; init; }          // Unix seconds, last time price was set
}

/// <summary>
/// Gil balance snapshot taken at the end of a pinch run.
/// </summary>
public record GilSnapshot
{
  public long Timestamp { get; init; }               // Unix seconds
  public long PlayerGil { get; init; }
  public Dictionary<string, long> RetainerGil { get; init; } = new();
  public long TotalGil => PlayerGil + RetainerGil.Values.Sum();
}

/// <summary>
/// Market snapshot captured at the end of a pinch run.
/// Tracks what's listed — separate from actual gil wealth.
/// </summary>
public record MarketSnapshot
{
  public long Timestamp { get; init; }               // Unix seconds
  public int ItemCount { get; init; }                // Total # of listings across all retainers
  public long TotalListingValue { get; init; }       // Sum of UnitPrice * Quantity for all listings
  public double AverageListingAgeDays { get; init; } // Average days on market across all listings
}

/// <summary>
/// A quote for the ConfigWindow header, selected from the SQLite quotes table.
/// </summary>
public record QuoteRecord
{
  public int Id { get; init; }
  public string Text { get; init; } = string.Empty;
  public string Author {  get; init; } = string.Empty;
}
