using System;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE PULL HAS TWO EDGES AND THE GAME PICKS THE ORDER (2026-09-01).
///
/// <para>Live receipt, two Materiel Containers two minutes apart: the first banked
/// because the bag poll armed 6ms before the chat line; the second was LOST because
/// the chat line beat the poll by 13ms and the disarmed handler dropped it. Capture
/// was a coin flip on frame-vs-chat ordering. These pins say: either order pairs,
/// and only a pairing inside the window banks.</para>
/// </summary>
public class CofferPullMatchTests
{
  private static readonly DateTime T0 = new(2026, 9, 1, 19, 13, 11, DateTimeKind.Utc);
  private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
  private const uint Box30 = 36635;
  private const uint Box40 = 36636;

  [Fact]
  public void ArmThenObtain_Pairs()
  {
    // The order that always worked: poll first, chat 6ms later.
    var m = new CofferPullMatch(Window);
    Assert.Null(m.Arm(Box40, T0));
    Assert.Equal(new CofferPull(Box40, 20533, 1, false),
      m.Obtain(20533, 1, false, T0.AddMilliseconds(6)));
  }

  [Fact]
  public void ObtainThenArm_Pairs()
  {
    // The lost minecart: chat 13ms BEFORE the poll. The arm looks back.
    var m = new CofferPullMatch(Window);
    Assert.Null(m.Obtain(777, 1, false, T0));
    Assert.Equal(new CofferPull(Box30, 777, 1, false),
      m.Arm(Box30, T0.AddMilliseconds(13)));
  }

  [Fact]
  public void AnObtainWithNoArmInsideTheWindow_NeverBanks()
  {
    // Somebody else's loot: no watched count fell around it. Expires unpaired.
    var m = new CofferPullMatch(Window);
    Assert.Null(m.Obtain(777, 1, false, T0));
    Assert.Null(m.Arm(Box30, T0 + Window + TimeSpan.FromMilliseconds(1)));
  }

  [Fact]
  public void AnArmWithNoObtainInsideTheWindow_NeverBanks()
  {
    var m = new CofferPullMatch(Window);
    Assert.Null(m.Arm(Box30, T0));
    Assert.Null(m.Obtain(777, 1, false, T0 + Window + TimeSpan.FromMilliseconds(1)));
  }

  [Fact]
  public void APairConsumesBothEdges()
  {
    // One box, one reward. After the pair, a second arm does not re-bank the consumed
    // line, and a second obtain line does not ride the consumed arm.
    var m = new CofferPullMatch(Window);
    m.Arm(Box40, T0);
    Assert.NotNull(m.Obtain(20533, 1, false, T0.AddMilliseconds(6)));
    Assert.Null(m.Arm(Box30, T0.AddMilliseconds(20)));

    var n = new CofferPullMatch(Window);
    n.Obtain(20533, 1, false, T0);
    Assert.NotNull(n.Arm(Box40, T0.AddMilliseconds(13)));
    Assert.Null(n.Obtain(999, 1, false, T0.AddMilliseconds(20)));
  }

  [Fact]
  public void TheNewerObtainLineIsTheOneHeld()
  {
    // Two unrelated obtain lines then an arm: the arm pairs with the latest, not the first.
    var m = new CofferPullMatch(Window);
    m.Obtain(111, 1, false, T0);
    m.Obtain(222, 3, true, T0.AddSeconds(1));
    Assert.Equal(new CofferPull(Box30, 222, 3, true), m.Arm(Box30, T0.AddSeconds(1.5)));
  }
}
