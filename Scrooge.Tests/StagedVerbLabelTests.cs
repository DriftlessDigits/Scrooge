using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE VERB WORDS, PINNED (08-22). <see cref="StandingReAsk.Verb"/>'s contract is "a
/// staged verb in the words its own button uses" - so the pane's button label and this
/// method are one string with two spellings, and the re-ask note a player reads is the
/// half nobody would notice going stale.
///
/// <para>The board's no-abbreviation rule caught "Reprc": an abbreviation only we can
/// expand, sitting on a button next to three words that are not abbreviated. The pin is
/// here so the rename cannot half-happen.</para>
/// </summary>
public class StagedVerbLabelTests
{
  [Fact]
  public void Reprice_IsSpelledOut_NotAbbreviated()
    => Assert.Equal("Reprice", StandingReAsk.Verb(StandingAction.Reprice));

  [Theory]
  [InlineData(StandingAction.Vendor, "Vend")]
  [InlineData(StandingAction.Pull, "Pull")]
  [InlineData(StandingAction.Reprice, "Reprice")]
  [InlineData(StandingAction.Melt, "Melt")]
  [InlineData(StandingAction.Gc, "GC")]
  [InlineData(StandingAction.None, "nothing")]
  public void EveryStagedVerbHasItsButtonWord(StandingAction action, string expected)
    => Assert.Equal(expected, StandingReAsk.Verb(action));

  [Fact]
  public void NoVerbWordIsATruncation()
  {
    // "Vend" and "GC" are the real short forms of their verbs, not clipped spellings;
    // a word ending mid-syllable is the failure this guards against.
    foreach (var action in new[]
    {
      StandingAction.Vendor, StandingAction.Pull, StandingAction.Reprice,
      StandingAction.Melt, StandingAction.Gc,
    })
      Assert.DoesNotContain("Reprc", StandingReAsk.Verb(action));
  }
}
