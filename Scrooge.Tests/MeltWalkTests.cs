using Scrooge;
using Xunit;

namespace Scrooge.Tests;

// ============================================================================
// The melt run's category walk (Phase B of the Rattan Sofa arc, 2026-08-30).
// The desynthesis window shows ONE category at a time; melt is BAGS-ONLY, and
// bag items appear under exactly two of the eight filter categories -
// Equipment/Items and Housing. The walk visits the bag categories the run
// has not seen, in a fixed order, and never the armoury four.
// ============================================================================
public class MeltWalkTests
{
  [Fact]
  public void FromEquipment_VisitsExactlyHousing()
  {
    var visit = MeltWalk.CategoriesToVisit(MeltWalk.Equipment);
    Assert.Equal(new[] { MeltWalk.Housing }, visit);
  }

  [Fact]
  public void FromHousing_VisitsExactlyEquipment()
  {
    var visit = MeltWalk.CategoriesToVisit(MeltWalk.Housing);
    Assert.Equal(new[] { MeltWalk.Equipment }, visit);
  }

  [Fact]
  public void FromAnArmouryCategory_VisitsBothBagCategories()
  {
    // A run started with the filter on an armoury view has seen NEITHER bag
    // category; the walk owes both, Equipment first (the default view - the
    // common home of the pile).
    var visit = MeltWalk.CategoriesToVisit(3);
    Assert.Equal(new[] { MeltWalk.Equipment, MeltWalk.Housing }, visit);
  }

  [Fact]
  public void CategoryLabels_SpeakTheGamesOwnNames()
  {
    Assert.Equal("Equipment/Items", MeltWalk.CategoryLabel(MeltWalk.Equipment));
    Assert.Equal("Housing", MeltWalk.CategoryLabel(MeltWalk.Housing));
  }
}
