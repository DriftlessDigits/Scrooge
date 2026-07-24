using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The pinch vendor rider's row selection (WALK unit 3). The contract: only
/// Unanimous Pull &amp; Vendor rows ride (the pile's own BulkSet gate), grouped by
/// the retainer whose pinch visit executes them; an empty or all-Mixed set leaves
/// the rider silent.
/// </summary>
public class VendorRiderTests
{
  /// <summary>A stand-in Pull &amp; Vendor row - identity plus the retainer it sits on.</summary>
  private sealed record Row(uint Id, string Retainer);

  private static (Row, ConfidenceTier) At(uint id, string retainer, ConfidenceTier tier)
    => (new Row(id, retainer), tier);

  [Fact]
  public void Riders_UnanimousOnly()
  {
    var candidates = new[]
    {
      At(1, "Bender", ConfidenceTier.Unanimous),
      At(2, "Bender", ConfidenceTier.Mixed),
      At(3, "Bender", ConfidenceTier.Contradicted),
      At(4, "Bender", ConfidenceTier.Unanimous),
    };

    var riders = VendorRider.Riders(candidates);

    Assert.Equal(new uint[] { 1, 4 }, riders.Select(r => r.Id).OrderBy(x => x));
  }

  [Fact]
  public void Riders_EmptyCandidates_Silent()
  {
    var riders = VendorRider.Riders(System.Array.Empty<(Row, ConfidenceTier)>());
    Assert.Empty(riders);
  }

  [Fact]
  public void Riders_AllMixed_Silent()
  {
    var candidates = new[]
    {
      At(1, "Bender", ConfidenceTier.Mixed),
      At(2, "Fry", ConfidenceTier.Contradicted),
    };

    Assert.Empty(VendorRider.Riders(candidates));
  }

  [Fact]
  public void ByRetainer_GroupsRidersByRetainer()
  {
    var candidates = new[]
    {
      At(1, "Bender", ConfidenceTier.Unanimous),
      At(2, "Fry", ConfidenceTier.Unanimous),
      At(3, "Bender", ConfidenceTier.Unanimous),
      At(4, "Fry", ConfidenceTier.Mixed),        // demoted - stays in the pile
    };

    var byRetainer = VendorRider.ByRetainer(candidates, r => r.Retainer);

    Assert.Equal(2, byRetainer.Count);
    Assert.Equal(new uint[] { 1, 3 }, byRetainer["Bender"].Select(r => r.Id).OrderBy(x => x));
    Assert.Equal(new uint[] { 2 }, byRetainer["Fry"].Select(r => r.Id));
  }

  [Fact]
  public void ByRetainer_NoUnanimousRows_EmptyMap()
  {
    var candidates = new[]
    {
      At(1, "Bender", ConfidenceTier.Mixed),
      At(2, "Fry", ConfidenceTier.Contradicted),
    };

    Assert.Empty(VendorRider.ByRetainer(candidates, r => r.Retainer));
  }

  [Fact]
  public void ByRetainer_RetainerWithOnlyMixedRows_Absent()
  {
    var candidates = new[]
    {
      At(1, "Bender", ConfidenceTier.Unanimous),
      At(2, "Fry", ConfidenceTier.Mixed),
    };

    var byRetainer = VendorRider.ByRetainer(candidates, r => r.Retainer);

    Assert.True(byRetainer.ContainsKey("Bender"));
    Assert.False(byRetainer.ContainsKey("Fry"));
  }

  [Fact]
  public void ForRetainer_FiltersToOneRetainersRiders()
  {
    var candidates = new[]
    {
      At(1, "Bender", ConfidenceTier.Unanimous),
      At(2, "Fry", ConfidenceTier.Unanimous),
      At(3, "Bender", ConfidenceTier.Unanimous),
    };

    var benders = VendorRider.ForRetainer(candidates, r => r.Retainer, "Bender");

    Assert.Equal(new uint[] { 1, 3 }, benders.Select(r => r.Id).OrderBy(x => x));
  }
}
