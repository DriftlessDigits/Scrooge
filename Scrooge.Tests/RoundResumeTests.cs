using System;
using System.Collections.Generic;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The Look half's checkpoint and the sentence a held Round says when the player
/// comes back to it (Rounds unit 4).
///
/// <para>The ruling these receipts defend is a negative one: NO staleness threshold,
/// no fork, no offer logic (08-10). The line states an age and two counts and stops -
/// every test here that pins a string is pinning the absence of a decision.</para>
/// </summary>
public class RoundResumeTests
{
  private static readonly DateTimeOffset LookDone =
    new(2026, 8, 10, 22, 10, 0, TimeSpan.Zero);

  private static Func<RoundStage, bool> Set(params RoundStage[] stages)
  {
    var set = new HashSet<RoundStage>(stages);
    return set.Contains;
  }

  // ==========================================================================
  // The checkpoint
  // ==========================================================================

  [Fact]
  public void LookComplete_WhenBothLookStagesAreDone()
  {
    Assert.True(RoundResume.LookComplete(
      Set(RoundStage.Pinch, RoundStage.Recon), Set(RoundStage.BellRun)));
  }

  /// <summary>
  /// THE CASE THAT MADE THE MARKS UNUSABLE: an empty stage is skipped silently and
  /// never marked done, so "both Look stages are marked" would never fire on the
  /// commonest good night of all - a fresh board and a fresh cache. The checkpoint
  /// asks the cursor's own question instead.
  /// </summary>
  [Fact]
  public void LookComplete_WhenTheLookHadNothingToDo()
  {
    Assert.True(RoundResume.LookComplete(Set(), Set(RoundStage.BellRun)));
  }

  [Fact]
  public void LookComplete_False_WhileEitherHalfStillHasWork()
  {
    Assert.False(RoundResume.LookComplete(
      Set(RoundStage.Pinch), Set(RoundStage.Recon, RoundStage.BellRun)));
    Assert.False(RoundResume.LookComplete(
      Set(RoundStage.Recon), Set(RoundStage.Pinch)));
  }

  /// <summary>
  /// The waist is where it is: an act stage having work says nothing about whether
  /// the reads are done, and a checkpoint that waited on the whole round would never
  /// fire at the hinge - which is the only place it is wanted.
  /// </summary>
  [Fact]
  public void LookComplete_IgnoresTheActHalfEntirely()
  {
    Assert.True(RoundResume.LookComplete(
      Set(), Set(RoundStage.Desynth, RoundStage.BellRun, RoundStage.TurnIn, RoundStage.Triage)));
  }

  // ==========================================================================
  // The age label
  // ==========================================================================

  [Fact]
  public void Ago_ClimbsTheSameCoarseLadderTheCachedPostNoteUses()
  {
    Assert.Equal("just now", RoundResume.Ago(30));
    Assert.Equal("12 minutes ago", RoundResume.Ago(12 * 60));
    Assert.Equal("2 hours ago", RoundResume.Ago(2 * 3600));
    Assert.Equal("3 days ago", RoundResume.Ago(3 * 86400));
  }

  /// <summary>
  /// The two labels describe one staleness from two seats. A player reading "2h old"
  /// on a cached post and "just now" on the Look it came from would be reading a
  /// contradiction, so the rungs are the same rungs.
  /// </summary>
  [Fact]
  public void Ago_AgreesWithCachedPostNoteAtEveryBoundary()
  {
    foreach (var seconds in new long[] { 0, 59, 60, 3599, 3600, 172799, 172800 })
      Assert.Equal(
        CachedPostNote.Age(0, seconds).Replace(" old", " ago"),
        RoundResume.Ago(seconds));
  }

  // ==========================================================================
  // The line
  // ==========================================================================

  [Fact]
  public void Line_SaysTheTimeTheAgeAndBothCounts()
  {
    var line = RoundResume.Line(LookDone, LookDone.AddHours(2), 34, 2);

    Assert.Equal(
      "Look done 22:10 (2 hours ago) - 34 decisions cached, 2 rulings owed - resume the Act half.",
      line);
  }

  [Fact]
  public void Line_CountsReadAsEnglishAtOne()
  {
    var line = RoundResume.Line(LookDone, LookDone.AddMinutes(5), 1, 1);

    Assert.Contains("1 decision cached", line);
    Assert.Contains("1 ruling owed", line);
    Assert.DoesNotContain("decisions", line);
    Assert.DoesNotContain("rulings", line);
  }

  /// <summary>
  /// A Look with nothing banked and nothing owed still says so plainly. There is no
  /// branch here that hides a zero, because the zero is information: it means the
  /// Look bought nothing, which is exactly when a player wants a Re-Look.
  /// </summary>
  [Fact]
  public void Line_StatesZeroesRatherThanHidingThem()
  {
    var line = RoundResume.Line(LookDone, LookDone.AddDays(1), 0, 0);

    Assert.Contains("0 decisions cached", line);
    Assert.Contains("0 rulings owed", line);
  }

  /// <summary>
  /// A clock that went backwards (a resync, a restored round stamped in the future)
  /// reads as "just now", never as a negative age. The label is a trust cue; a
  /// negative one would read as a bug and cost the whole sentence its credit.
  /// </summary>
  [Fact]
  public void Line_ClampsAClockThatWentBackwards()
  {
    var line = RoundResume.Line(LookDone, LookDone.AddHours(-3), 5, 0);

    Assert.Contains("(just now)", line);
  }

  /// <summary>
  /// The ruling, pinned: the sentence never forks. However old the Look is, it says
  /// the same thing in the same shape and offers no opinion about whether that is
  /// too old - Drift is the threshold, and the Re-Look verb is unconditional beside it.
  /// </summary>
  [Fact]
  public void Line_NeverForksOnAge()
  {
    foreach (var hours in new[] { 0.01, 1, 6, 30, 200 })
    {
      var line = RoundResume.Line(LookDone, LookDone.AddHours(hours), 3, 1);
      Assert.EndsWith("- 3 decisions cached, 1 ruling owed - resume the Act half.", line);
      Assert.StartsWith("Look done 22:10 (", line);
    }
  }

  /// <summary>
  /// WHICH COMPLETIONS MAY DATE THE LOOK (review ruling S3). The checkpoint hangs off
  /// a completed Look-half run now, not off every persisted transition - so this
  /// predicate is the whole of "was that a read". A melt landing an hour later must
  /// never be the thing that stamps the time the boards were read.
  /// </summary>
  [Fact]
  public void IsLookStage_OnlyTheTwoStagesThatSpendNothing()
  {
    Assert.True(RoundResume.IsLookStage(RoundStage.Pinch));
    Assert.True(RoundResume.IsLookStage(RoundStage.Recon));
    Assert.False(RoundResume.IsLookStage(RoundStage.Triage));
    Assert.False(RoundResume.IsLookStage(RoundStage.Desynth));
    Assert.False(RoundResume.IsLookStage(RoundStage.BellRun));
    Assert.False(RoundResume.IsLookStage(RoundStage.TurnIn));
  }
}
