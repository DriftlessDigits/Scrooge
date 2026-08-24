using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// WHAT A RENAME COSTS THE PLAYER'S CONFIG FILE (Rounds unit 1, 2026-08-10).
///
/// <para>The Rounds naming sweep opened with one question - "is renaming any of
/// this safe?" - and the spec's instinct was to check whether enums persist as
/// numbers. They do, and that half was never the danger. The danger is one level
/// up: Dalamud serializes the plugin config with TypeNameHandling.Objects, which
/// writes CLASS IDENTITIES into the file next to the data. Every config Drift has
/// carries <c>"$type": "Scrooge.SweepState, Scrooge"</c>, and a class rename makes
/// that string name nothing.</para>
///
/// <para>These tests are the receipt, and they are here rather than in a session
/// note because four more units of this campaign have renames left in them - unit
/// 5 re-seats the whole <c>Ledger*</c> family, and <c>GilGoalRecord</c> is sitting
/// in the same file with the same $type. The next person to rename a persisted
/// class should be told by a red test, not by Drift losing his settings.</para>
///
/// <para><b>The settings below are reconstructed from the artifact, not from
/// memory of Dalamud's source</b>: the live Scrooge.json writes $type without
/// Version/Culture/PublicKeyToken (TypeNameAssemblyFormatHandling.Simple), carries
/// $type on nested objects (TypeNameHandling.Objects), and is two-space indented.
/// If Dalamud ever changes that, these tests describe the OLD world and the file
/// on disk is the tiebreaker.</para>
/// </summary>
public class ConfigShapeTests
{
  private static readonly JsonSerializerSettings DalamudLike = new()
  {
    TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
    TypeNameHandling = TypeNameHandling.Objects,
  };

  private static string Write(object o) => JsonConvert.SerializeObject(o, Formatting.Indented, DalamudLike);
  private static T? Read<T>(string json) => JsonConvert.DeserializeObject<T>(json, DalamudLike);

  /// <summary>
  /// A $type tag in the format the config file uses (Simple assembly format: no
  /// Version/Culture/PublicKeyToken).
  ///
  /// <para>DERIVED, not written out, because of the test project's own shape: the
  /// production files are LINKED sources, so <c>RoundState</c> here lives in
  /// Scrooge.Tests while the file on disk says <c>, Scrooge</c>. Hard-coding the
  /// real tag would fail for a reason that has nothing to do with what is being
  /// tested - and pinning the test assembly's name would fossilize a detail nobody
  /// meant to freeze. The IDENTITY is the subject; the assembly it sits in is not.</para>
  /// </summary>
  private static string Tag(Type t) => $"{t.FullName}, {t.Assembly.GetName().Name}";

  /// <summary>A tag for a type that does not exist - a class rename, from the file's view.</summary>
  private static string DeadTag(string fullName)
    => $"{fullName}, {typeof(ConfigShapeTests).Assembly.GetName().Name}";

  // ========================================================================
  // Finding 1: enums are NUMBERS, so member names are free to change
  // ========================================================================

  [Fact]
  public void PersistedEnums_AreWrittenAsNumbers_NotNames()
  {
    var state = new RoundState
    {
      Active = true,
      Done = new List<RoundStage> { RoundStage.BellRun, RoundStage.TurnIn },
      HaltStage = RoundStage.Desynth,
    };

    var json = Write(state);

    // The numbers, not the words. This is the whole licence for the Rounds
    // vocabulary change - and for BellRun -> ListRun whenever that lands.
    Assert.Contains("1", json);
    Assert.Contains("4", json);
    Assert.DoesNotContain("\"BellRun\"", json);
    Assert.DoesNotContain("\"TurnIn\"", json);
    Assert.DoesNotContain("\"Desynth\"", json);
  }

  [Fact]
  public void ARenamedEnumMember_ReReadsItsPreviouslySavedValue()
  {
    // The proof the naming sweep actually needed. OldStage is the pre-Rounds
    // spelling; RenamedStage is what a member rename looks like on the wire -
    // same numbers, different words. A value saved under the old name comes back
    // as the new name, unchanged, with no migration code anywhere.
    var saved = JsonConvert.SerializeObject(
      new[] { OldStage.BellRun, OldStage.Desynth }, DalamudLike);

    var reread = JsonConvert.DeserializeObject<RenamedStage[]>(saved, DalamudLike)!;

    Assert.Equal(new[] { RenamedStage.ListRun, RenamedStage.Salvage }, reread);
  }

  [Fact]
  public void APreRoundsDoneList_ReReadsUnderTheNewStageNames()
  {
    // The same fact, against the real type: the numbers the old build wrote for
    // its four stages still mean those four stages here.
    var preRounds = $$"""
    {
      "$type": "{{Tag(typeof(RoundState))}}",
      "Active": true,
      "Done": [ 0, 1, 3, 4 ],
      "HaltStage": null,
      "HaltMessage": null,
      "StartedAtUnix": 1700000000
    }
    """;

    var state = Read<RoundState>(preRounds)!;

    Assert.Equal(
      new[] { RoundStage.Pinch, RoundStage.BellRun, RoundStage.Desynth, RoundStage.TurnIn },
      state.Done);
  }

  // ========================================================================
  // Finding 2: a persisted CLASS rename is fatal - and how it is survived
  // ========================================================================

  [Fact]
  public void ARenamedPersistedClass_TakesTheWholeConfigDown_NotJustItsProperty()
  {
    // THE HAZARD, STATED AS A FAILING LOAD. The property still maps; only the
    // class it names was renamed. Newtonsoft resolves $type BEFORE it looks at
    // the value, cannot find the type, and throws out of the top-level read -
    // so the player loses every setting in the file, not one nested object.
    //
    // This is why RoundState's old name is still a live class in RoundPlan.cs.
    var oldFile = $$"""
    {
      "$type": "{{Tag(typeof(ConfigLike))}}",
      "Version": 7,
      "Held": {
        "$type": "{{DeadTag("Scrooge.GoneForever")}}",
        "Active": true
      }
    }
    """;

    var ex = Assert.ThrowsAny<JsonSerializationException>(() => Read<ConfigLike>(oldFile));
    Assert.Contains("Error resolving type", ex.Message);
  }

  [Fact]
  public void ARenamedPersistedClass_IsHarmlessOnceItsKeyStopsMappingToo()
  {
    // The escape hatch, and the reason "rename the property as well" is a real
    // option rather than a second bug: an unmapped property is skipped WHOLE -
    // its $type is never resolved, nothing throws, and the config loads with the
    // old blob simply dropped. Cheap, and it silently costs an in-flight round.
    var oldFile = $$"""
    {
      "$type": "{{Tag(typeof(ConfigLikeRenamed))}}",
      "Version": 7,
      "Held": {
        "$type": "{{DeadTag("Scrooge.GoneForever")}}",
        "Active": true
      }
    }
    """;

    var cfg = Read<ConfigLikeRenamed>(oldFile);

    Assert.NotNull(cfg);
    Assert.Equal(7, cfg!.Version);
    Assert.Null(cfg.Round);
  }

  [Fact]
  public void TheLegacySweepState_StillResolvesTheOldTypeName()
  {
    // The third option, and the one Rounds took: keep the old class, move the
    // data forward, drop the key. The blob below is the shape sitting in
    // Scrooge.json today, verbatim apart from its values.
    var onDisk = $$"""
    {
      "$type": "{{Tag(typeof(SweepState))}}",
      "Active": true,
      "Done": [ 0, 3 ],
      "HaltStage": null,
      "HaltMessage": null,
      "StartedAtUnix": 1700000000
    }
    """;

    // The type name in that tag is "Scrooge.SweepState" - the string sitting in
    // Drift's config file right now. If a later unit deletes the class, this line
    // is what fails.
    Assert.Contains("Scrooge.SweepState", Tag(typeof(SweepState)));

    var legacy = Read<SweepState>(onDisk)!;

    Assert.True(legacy.Active);
    Assert.Equal(new[] { RoundStage.Pinch, RoundStage.Desynth }, legacy.Done);
    Assert.Equal(1_700_000_000, legacy.StartedAtUnix);
  }

  // ========================================================================
  // The fold itself
  // ========================================================================

  [Fact]
  public void Fold_MovesAnInFlightPreRoundsRoundOntoTheNewKey()
  {
    var legacy = new SweepState
    {
      Active = true,
      Done = new List<RoundStage> { RoundStage.Pinch, RoundStage.Desynth },
      HaltStage = RoundStage.BellRun,
      HaltMessage = "Bell run halted - the run stopped.",
      StartedAtUnix = 1_700_000_000,
    };

    var (round, ceiling, _, migrated) = LegacyRoundConfig.Fold(null, legacy, 4, 6, true, null);

    Assert.True(migrated);
    Assert.NotNull(round);
    Assert.True(round!.Active);
    Assert.Equal(legacy.Done, round.Done);
    Assert.Equal(RoundStage.BellRun, round.HaltStage);
    Assert.Equal(legacy.HaltMessage, round.HaltMessage);
    Assert.Equal(1_700_000_000, round.StartedAtUnix);
    // The renamed scalar came with it - a player does not re-tune his ceiling
    // because we re-worded ours.
    Assert.Equal(6, ceiling);

    // And the restored place is the same place: the plan reads it identically.
    var plan = new RoundPlan();
    plan.Restore(round);
    Assert.True(plan.IsDone(RoundStage.Pinch));
    Assert.True(plan.IsDone(RoundStage.Desynth));
    Assert.Equal(RoundStage.BellRun, plan.HaltStage);
  }

  [Fact]
  public void Fold_ACleanRoundsConfig_MovesNothing()
  {
    // The steady state, every session after the first. Nothing to fold means no
    // write and no Save - a migration that reports "migrated" forever is a
    // migration that never ends.
    var (round, ceiling, _, migrated) = LegacyRoundConfig.Fold(null, null, 4, null, true, null);

    Assert.False(migrated);
    Assert.Null(round);
    Assert.Equal(4, ceiling);
  }

  [Fact]
  public void Fold_NeverClobbersALiveRoundWithAStaleLegacyBlob()
  {
    // Both keys present is the downgrade-then-upgrade case. The newer write is
    // the truthful one, so the new key stands - but the legacy key must still be
    // reported as migratable, or the caller never clears it and it folds forever.
    var live = new RoundState { Active = true, Done = new List<RoundStage> { RoundStage.Recon } };
    var stale = new SweepState { Active = true, Done = new List<RoundStage> { RoundStage.TurnIn } };

    var (round, _, _, migrated) = LegacyRoundConfig.Fold(live, stale, 4, null, true, null);

    Assert.True(migrated);
    Assert.Same(live, round);
    Assert.Equal(new[] { RoundStage.Recon }, round!.Done);
  }

  [Fact]
  public void Fold_AnAbsentLegacyCeiling_LeavesTheCurrentValueAlone()
  {
    // ABSENT and ZERO are different facts. A fresh install has never had the old
    // key, and a non-nullable legacy int would hand it a hard-coded 0 - which
    // floors to 1h on read and starts retiring live rounds an hour in.
    var (_, ceiling, _, migrated) = LegacyRoundConfig.Fold(null, null, 9, null, true, null);

    Assert.False(migrated);
    Assert.Equal(9, ceiling);
  }

  [Fact]
  public void Fold_IsIdempotent_OnceTheCallerHasClearedTheLegacyKeys()
  {
    var legacy = new SweepState { Active = true, Done = new List<RoundStage> { RoundStage.Pinch } };

    var first = LegacyRoundConfig.Fold(null, legacy, 4, 6, true, null);
    Assert.True(first.Migrated);

    // The caller writes the results back and nulls the legacy pair. The next
    // session finds nothing.
    var second = LegacyRoundConfig.Fold(first.Round, null, first.CeilingHours, null, first.LogEnabled, null);
    Assert.False(second.Migrated);
    Assert.Same(first.Round, second.Round);
    Assert.Equal(6, second.CeilingHours);
  }

  // ========================================================================
  // Unit 5: the Ledger name is reissued, and the log's key comes with it
  // ========================================================================

  [Fact]
  public void Fold_ADeliberatelyDisabledRunLog_SurvivesTheRename()
  {
    // EnablePinchRunLog -> EnableLedger. A player who turned the transcript OFF
    // must not find it back on because the window got a better name - the whole
    // ruling on the naming sweep is that config keys migrate SILENTLY.
    var (_, _, logEnabled, migrated) = LegacyRoundConfig.Fold(null, null, 4, null, true, false);

    Assert.True(migrated);
    Assert.False(logEnabled);
  }

  [Fact]
  public void Fold_AnAbsentLegacyLogKey_LeavesTheCurrentValueAlone()
  {
    // ABSENT and FALSE are different facts, exactly as for the ceiling. A
    // non-nullable legacy bool would read every fresh install's missing key as a
    // deliberate "off" and silently kill the transcript for new players.
    var (_, _, logEnabled, migrated) = LegacyRoundConfig.Fold(null, null, 4, null, true, null);

    Assert.False(migrated);
    Assert.True(logEnabled);
  }

  [Fact]
  public void Fold_TheLogKeyMigratesOnItsOwn_WithNoRoundInFlight()
  {
    // The commonest shape of this migration: no round, no ceiling override, just
    // the one boolean. It still has to report Migrated, or the caller never nulls
    // the legacy key and it folds forever.
    var (round, ceiling, logEnabled, migrated) =
      LegacyRoundConfig.Fold(null, null, 9, null, true, false);

    Assert.True(migrated);
    Assert.Null(round);
    Assert.Equal(9, ceiling);
    Assert.False(logEnabled);
  }

  // ========================================================================
  // Unit 5 addendum: the hinge's commit rides the persisted round state
  // ========================================================================

  [Fact]
  public void APreAddendumRoundState_ReadsAnEmptyCommit_NotACrash()
  {
    // The config on Drift's disk right now has no CommittedGateRows and no MeltRunIds.
    // Adding a property is safe where renaming the class is fatal - the $type still
    // resolves and the absent keys read as their initialisers - and the ADMISSION rule
    // is what makes the empty answer safe rather than merely legal: a Round restored
    // past its own hinge with no snapshot fails closed instead of guessing.
    var preAddendum = $$"""
    {
      "$type": "{{Tag(typeof(RoundState))}}",
      "Active": true,
      "Done": [ 0, 5, 6 ],
      "HaltStage": null,
      "HaltMessage": null,
      "StartedAtUnix": 1700000000,
      "RunId": 42
    }
    """;

    var state = Read<RoundState>(preAddendum)!;

    Assert.Equal(42, state.RunId);
    Assert.NotNull(state.CommittedGateRows);
    Assert.Empty(state.CommittedGateRows);
    Assert.NotNull(state.MeltRunIds);
    Assert.Empty(state.MeltRunIds);
    // Addendum 2's routed half reads the same way, for the same reason.
    Assert.NotNull(state.CommittedRoutedRows);
    Assert.Empty(state.CommittedRoutedRows);
    // And S4's melt and turn-in halves, which is the same fact a third and fourth time:
    // a Round mid-flight when the four-set commit shipped restores with no snapshot for
    // the two destroying verbs, and fails closed rather than melting on an old press.
    Assert.NotNull(state.CommittedMeltRows);
    Assert.Empty(state.CommittedMeltRows);
    Assert.NotNull(state.CommittedChurnRows);
    Assert.Empty(state.CommittedChurnRows);
  }

  [Fact]
  public void TheCommitSurvivesTheRoundTrip_WhichIsTheWholeReasonItIsPersisted()
  {
    var written = new RoundState
    {
      Active = true,
      Done = new List<RoundStage> { RoundStage.Pinch, RoundStage.Recon, RoundStage.Triage },
      StartedAtUnix = 1_700_000_000,
      RunId = 7,
      CommittedGateRows = new List<long> { 44100, 1_044_100 },
      CommittedRoutedRows = new List<long> { 30_000 },
      CommittedMeltRows = new List<long> { 1_030_000 },
      CommittedChurnRows = new List<long> { 12_345 },
      MeltRunIds = new List<long> { 3, 4 },
    };

    var reread = Read<RoundState>(Write(written))!;

    Assert.Equal(new[] { 44100L, 1_044_100L }, reread.CommittedGateRows);
    // The four sets are separate lists and stay separate: a merged one could not tell a
    // gate row that vanished from a routed row that did, and the melt and turn-in sets
    // answer different verbs over the same bags.
    Assert.Equal(new[] { 30_000L }, reread.CommittedRoutedRows);
    Assert.Equal(new[] { 1_030_000L }, reread.CommittedMeltRows);
    Assert.Equal(new[] { 12_345L }, reread.CommittedChurnRows);
    Assert.Equal(new[] { 3L, 4L }, reread.MeltRunIds);
    // And the hinge's own mark, which is what tells the bell to start holding at all.
    Assert.Contains(RoundStage.Triage, reread.Done);
  }

  [Fact]
  public void TheLegacyFold_ProducesARoundWithNoCommit_WhichFailsClosed()
  {
    // A pre-Rounds sweep folded forward has no snapshot and never had one. If its Done
    // list somehow carried the hinge, the bell must hold rather than admit - the fold
    // hands the admission rule a null, and null means "cannot vouch".
    var (round, _, _, _) = LegacyRoundConfig.Fold(
      null, new SweepState { Active = true, StartedAtUnix = 1_700_000_000 },
      4, null, true, null);

    Assert.NotNull(round);
    Assert.Empty(round!.CommittedGateRows);
    Assert.Empty(round.CommittedRoutedRows);
    Assert.Empty(round.CommittedMeltRows);
    Assert.Empty(round.CommittedChurnRows);
    Assert.Empty(round.MeltRunIds);
  }

  // ========================================================================
  // The doctrine sweep (2026-08-15): the retired Percentage undercut mode
  // ========================================================================

  [Fact]
  public void AStoredPercentageMode_FoldsToFixedAmount()
  {
    // THE FOLD, stated as the config file states it: a 1 in the UndercutMode key.
    // Percentage priced off a share of a stranger's ask, which is a prediction, and
    // the doctrine sweep retired it. A config carrying the old number must land on a
    // live mode - never on a hole where a mode used to be.
    Assert.Equal(
      UndercutMode.FixedAmount,
      LegacyUndercutMode.Fold((UndercutMode)LegacyUndercutMode.RetiredPercentage));
  }

  [Fact]
  public void TheSurvivingModes_KeepTheirStoredNumbers()
  {
    // The ordinals are the contract, not the names. Deleting a member from the middle
    // of an enum renumbers everything after it, and a player who chose Humanized would
    // silently be moved to Clean Numbers - so the survivors carry explicit values and
    // the retired 1 stays a hole forever.
    Assert.Equal(0, (int)UndercutMode.FixedAmount);
    Assert.Equal(2, (int)UndercutMode.GentlemansMatch);
    Assert.Equal(3, (int)UndercutMode.CleanNumbers);
    Assert.Equal(4, (int)UndercutMode.Humanized);
  }

  [Theory]
  [InlineData(UndercutMode.FixedAmount)]
  [InlineData(UndercutMode.GentlemansMatch)]
  [InlineData(UndercutMode.CleanNumbers)]
  [InlineData(UndercutMode.Humanized)]
  public void ALiveMode_PassesThroughTheFoldUntouched(UndercutMode stored)
  {
    Assert.Equal(stored, LegacyUndercutMode.Fold(stored));
  }

  [Fact]
  public void AnUnknownModeNumber_AlsoFoldsRatherThanRunning()
  {
    // Hand-edited configs and downgrade-then-upgrade both produce numbers no build
    // ever wrote. Same answer: the default the plugin ships with.
    Assert.Equal(UndercutMode.FixedAmount, LegacyUndercutMode.Fold((UndercutMode)99));
  }

  [Fact]
  public void TheSerializerHandsBackTheRetiredNumberUnchanged_WhichIsWhyTheFoldExists()
  {
    // The fact the guard is built on, pinned against the real serializer. Newtonsoft
    // does not validate an enum int: a retired value deserializes to itself, silently,
    // and nothing throws. So reading the key is not enough - the value has to be
    // folded on the way in, which is what the Configuration setter does.
    var stored = $$"""
    {
      "$type": "{{Tag(typeof(ModeHolder))}}",
      "UndercutMode": {{LegacyUndercutMode.RetiredPercentage}}
    }
    """;

    var raw = Read<ModeHolder>(stored)!;

    Assert.Equal(LegacyUndercutMode.RetiredPercentage, (int)raw.UndercutMode);
    Assert.False(Enum.IsDefined(raw.UndercutMode));
    Assert.Equal(UndercutMode.FixedAmount, LegacyUndercutMode.Fold(raw.UndercutMode));
  }

  // ---- fixtures -----------------------------------------------------------

  /// <summary>
  /// An unguarded stand-in for the config root's mode key. Deliberately WITHOUT the
  /// fold in its setter: its whole job is to show what the serializer produces when
  /// nothing catches the value, which is the reason the real setter catches it.
  /// </summary>
  public sealed class ModeHolder
  {
    public UndercutMode UndercutMode { get; set; }
  }


  /// <summary>The pre-Rounds stage numbering, as the config file recorded it.</summary>
  private enum OldStage { Pinch = 0, BellRun = 1, Desynth = 3, TurnIn = 4 }

  /// <summary>The same numbers, re-worded. What a member rename looks like on the wire.</summary>
  private enum RenamedStage { Pinch = 0, ListRun = 1, Salvage = 3, TurnIn = 4 }

  /// <summary>A config root whose held-place property still MAPS to a dead class.</summary>
  public sealed class ConfigLike
  {
    public int Version { get; set; }
    public RoundState? Held { get; set; }
  }

  /// <summary>The same root with the property renamed too - the old key maps to nothing.</summary>
  public sealed class ConfigLikeRenamed
  {
    public int Version { get; set; }
    public RoundState? Round { get; set; }
  }
}
