using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE POSTURE TAG'S GRAMMAR (V45). These are contract pins, not behaviour tests:
/// the string is banked on every decision receipt and parsed by a reader that does
/// not exist yet, so the keys, their order and their number formatting are the
/// interface. A rename or a reorder here silently splits a tally in the 4.0
/// undercut report card, exactly the way a renamed doubt-branch key would.
/// </summary>
public class UndercutPostureTests
{
  private static string Compose(
    UndercutMode mode = UndercutMode.FixedAmount,
    int amount = 1,
    bool undercutSelf = false,
    float maxUndercutPct = 100f,
    bool increaseCapEnabled = false,
    float maxIncreasePct = 50f,
    float ceilingMult = 3f)
    => UndercutPosture.Compose(mode, amount, undercutSelf, maxUndercutPct,
        increaseCapEnabled, maxIncreasePct, ceilingMult);

  [Fact]
  public void TheShippedDefaults_ComposeToTheDocumentedShape()
  {
    Assert.Equal("mode=FixedAmount;amt=1;self=0;maxcut=100;inccap=0;incpct=50;ceil=3",
      Compose());
  }

  [Fact]
  public void EveryPeggedKnob_IsPresent_InTheFrozenOrder()
  {
    var tag = Compose();
    var keys = new[]
    {
      UndercutPosture.ModeKey, UndercutPosture.AmountKey, UndercutPosture.SelfKey,
      UndercutPosture.MaxCutKey, UndercutPosture.IncreaseCapKey,
      UndercutPosture.IncreasePctKey, UndercutPosture.CeilingKey,
    };

    var at = -1;
    foreach (var key in keys)
    {
      var found = tag.IndexOf(key + "=", System.StringComparison.Ordinal);
      Assert.True(found > at, $"{key} is missing or out of order");
      at = found;
    }
  }

  [Fact]
  public void UndercutSelf_IsRecoverable_WhichIsTheWholePointOfTheColumn()
  {
    Assert.Contains("self=0", Compose(undercutSelf: false));
    Assert.Contains("self=1", Compose(undercutSelf: true));
  }

  [Fact]
  public void TheClimbCapsArmingAndItsPercentage_AreTwoSeparateFacts()
  {
    // A disarmed cap set to 50 and an armed one set to 50 produce opposite prices;
    // folding the flag into the number would lose the knob the player has set.
    Assert.Contains("inccap=0;incpct=50", Compose(increaseCapEnabled: false, maxIncreasePct: 50f));
    Assert.Contains("inccap=1;incpct=50", Compose(increaseCapEnabled: true, maxIncreasePct: 50f));
  }

  [Fact]
  public void TheRetiredPercentageMode_BanksAsWhatItIsHonoured() // the LegacyUndercutMode fold
  {
    // A config from the wild carrying the retired ordinal 1 runs as FixedAmount, so
    // the receipt says FixedAmount. Banking the raw name would record a mode the
    // arithmetic can never have used.
    var tag = Compose(mode: (UndercutMode)LegacyUndercutMode.RetiredPercentage);
    Assert.Contains("mode=FixedAmount", tag);
  }

  [Fact]
  public void Percentages_BankAsPercent_WithTrailingZeroesTrimmed()
  {
    // 100.0f and 100f are one stance; a tag that spelled them differently would read
    // as a stance that moved.
    Assert.Equal(Compose(maxUndercutPct: 100f), Compose(maxUndercutPct: 100.0f));
    Assert.Contains("maxcut=12.5", Compose(maxUndercutPct: 12.5f));
    Assert.Contains("ceil=2.5", Compose(ceilingMult: 2.5f));
  }

  [Fact]
  public void TheTagIsOneLine_NoSeparatorCollidesWithTheGrammar()
  {
    var tag = Compose(mode: UndercutMode.Humanized, amount: 7, undercutSelf: true,
      maxUndercutPct: 33.3f, increaseCapEnabled: true, maxIncreasePct: 12f, ceilingMult: 4f);

    Assert.DoesNotContain("\n", tag);
    Assert.Equal(7, tag.Split(';').Length);
    foreach (var field in tag.Split(';'))
      Assert.Equal(2, field.Split('=').Length);
  }
}
