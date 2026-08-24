using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE ROUND'S ONE TRANSCRIPT (2026-07-26): "each stage seems to reset the run log".
/// The carry is the hold/release/append decision; the window's rendering is ImGui and
/// untested here by convention.
/// </summary>
public class RunLogCarryTests
{
  private static List<string> Run(params string[] lines) => lines.ToList();

  [Fact]
  public void OutsideARound_NothingAccumulates()
  {
    // The standalone path is unchanged and costs nothing: a run's log is its own.
    var carry = new RunLogCarry<string>();
    Assert.False(carry.Holding);
    carry.Adopt(Run("melt 1", "melt 2"));
    Assert.Empty(carry.Entries);
  }

  [Fact]
  public void HoldingAcrossStages_AppendsInOrder()
  {
    var carry = new RunLogCarry<string>();
    carry.Hold();
    carry.Adopt(Run("pinch start", "pinch done"));
    carry.Adopt(Run("melt start", "melt done"));

    Assert.Equal(new[] { "pinch start", "pinch done", "melt start", "melt done" }, carry.Entries);
  }

  [Fact]
  public void HoldStartsFromEmpty()
  {
    // A new round is a new book, even if the last one's pages were never released.
    var carry = new RunLogCarry<string>();
    carry.Hold();
    carry.Adopt(Run("old round"));
    carry.Hold();
    Assert.Empty(carry.Entries);
    Assert.True(carry.Holding);
  }

  [Fact]
  public void ReleaseEndsTheHoldAndLEAVESTheBookStanding()
  {
    // Review ruling S11: "don't overcomplicate: delete the wipe". A finished round's
    // transcript is the thing the player opens the Ledger to read, and this method
    // used to empty it at the exact moment he pressed done.
    var carry = new RunLogCarry<string>();
    carry.Hold();
    carry.Adopt(Run("bell"));
    carry.Release();

    Assert.False(carry.Holding);
    Assert.Equal(new[] { "bell" }, carry.Entries);

    // Released means released: nothing further is ADOPTED into a book whose round
    // is over. What supersedes it is the next thing written - a new round's Hold, or
    // the log window's own clear at the next standalone run's start.
    carry.Adopt(Run("a standalone run"));
    Assert.Equal(new[] { "bell" }, carry.Entries);
  }

  [Fact]
  public void TheNextRoundSupersedesTheLastRoundsStandingBook()
  {
    // The other half of S11: the book stands UNTIL something replaces it, and a new
    // round's start is one of the two things that does.
    var carry = new RunLogCarry<string>();
    carry.Hold();
    carry.Adopt(Run("last round's bell"));
    carry.Release();
    carry.Hold();

    Assert.Empty(carry.Entries);
    Assert.True(carry.Holding);
  }

  [Fact]
  public void ClearWipesButKeepsHolding()
  {
    // The Clear button says Clear. It empties the transcript without ending the round -
    // the stages still to come go on writing one book.
    var carry = new RunLogCarry<string>();
    carry.Hold();
    carry.Adopt(Run("melt"));
    carry.Clear();

    Assert.True(carry.Holding);
    Assert.Empty(carry.Entries);

    carry.Adopt(Run("bell"));
    Assert.Equal(new[] { "bell" }, carry.Entries);
  }

  [Fact]
  public void AnEmptyRunAddsNothing()
  {
    var carry = new RunLogCarry<string>();
    carry.Hold();
    carry.Adopt(new List<string>());
    Assert.Empty(carry.Entries);
  }

  [Fact]
  public void GrowthIsBounded_OldestLinesGoFirst()
  {
    // A round held across many resumes must not be an unbounded list. The cap trims the
    // top of the transcript, which is the part nobody scrolls back to.
    var carry = new RunLogCarry<string>();
    carry.Hold();

    for (var batch = 0; batch < 6; batch++)
      carry.Adopt(Enumerable.Range(0, 1000).Select(i => $"{batch}:{i}").ToList());

    Assert.Equal(RunLogCarry<string>.MaxEntries, carry.Entries.Count);
    Assert.Equal("5:999", carry.Entries[^1]);           // the newest line survives
    Assert.DoesNotContain("0:0", carry.Entries);        // the oldest was dropped
  }

  // ==========================================================================
  // The write-through and the rehydrate (Rounds unit 4): the transcript stops
  // dying with the session
  // ==========================================================================

  [Fact]
  public void WriteThrough_BanksEveryAdoptedChapter()
  {
    var banked = new List<List<string>>();
    var carry = new RunLogCarry<string> { WriteThrough = e => banked.Add(e.ToList()) };
    carry.Hold();

    carry.Adopt(Run("pinch start", "pinch done"));
    carry.Adopt(Run("recon 1", "recon 2"));

    // Chapter by chapter, in order, exactly as adopted - the batch IS the stage, and
    // the banked stage column depends on that staying true.
    Assert.Equal(2, banked.Count);
    Assert.Equal(new[] { "pinch start", "pinch done" }, banked[0]);
    Assert.Equal(new[] { "recon 1", "recon 2" }, banked[1]);
  }

  [Fact]
  public void WriteThrough_IsSilentOutsideARoundAndOnEmptyChapters()
  {
    var banked = new List<List<string>>();
    var carry = new RunLogCarry<string> { WriteThrough = e => banked.Add(e.ToList()) };

    carry.Adopt(Run("a standalone run"));   // not holding
    carry.Hold();
    carry.Adopt(new List<string>());        // nothing to bank

    Assert.Empty(banked);
  }

  /// <summary>
  /// THE ROUND TRIP A RELOAD MAKES: bank the chapters, then rehydrate a fresh carry
  /// from them and go on writing into the same book.
  /// </summary>
  [Fact]
  public void Rehydrate_RestoresTheTranscriptAndKeepsHolding()
  {
    var banked = new List<string>();
    var live = new RunLogCarry<string> { WriteThrough = e => banked.AddRange(e) };
    live.Hold();
    live.Adopt(Run("pinch done"));
    live.Adopt(Run("recon done"));

    var restored = new RunLogCarry<string>();
    restored.Rehydrate(banked);

    Assert.True(restored.Holding);
    Assert.Equal(new[] { "pinch done", "recon done" }, restored.Entries);

    restored.Adopt(Run("bell done"));
    Assert.Equal(new[] { "pinch done", "recon done", "bell done" }, restored.Entries);
  }

  /// <summary>
  /// A rehydrate must NOT write through. These lines came FROM the bank; handing them
  /// back would append the whole transcript to itself on every reload, and a round
  /// held across three restarts would read its own first stage four times.
  /// </summary>
  [Fact]
  public void Rehydrate_DoesNotBankWhatItJustRead()
  {
    var banked = new List<List<string>>();
    var carry = new RunLogCarry<string> { WriteThrough = e => banked.Add(e.ToList()) };

    carry.Rehydrate(Run("pinch done", "recon done"));

    Assert.Empty(banked);
  }

  [Fact]
  public void Rehydrate_CapsAnOverFullBank()
  {
    var carry = new RunLogCarry<string>();
    carry.Rehydrate(Enumerable.Range(0, RunLogCap.MaxEntries + 10).Select(i => $"{i}").ToList());

    Assert.Equal(RunLogCap.MaxEntries, carry.Entries.Count);
    Assert.Equal("10", carry.Entries[0]);
  }

  /// <summary>The two copies are capped by ONE const, so they can never diverge.</summary>
  [Fact]
  public void TheCapHasOneDefinition()
  {
    Assert.Equal(RunLogCap.MaxEntries, RunLogCarry<string>.MaxEntries);
  }
}
