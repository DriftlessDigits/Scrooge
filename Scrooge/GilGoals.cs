using System;
using System.Linq;
using ECommons.DalamudServices;

namespace Scrooge;

/// <summary>
/// Player-set gil goals: evaluation, celebration, and progress readouts.
/// Three independent buckets — a per-retainer bank target, a walking-around
/// player gil target, and a total-worth target. Crossings celebrate once per
/// target value; changing a target re-arms it (and quietly baselines goals
/// that are already met, so setting a target below current wealth doesn't
/// throw confetti for old money).
/// Evaluation runs after snapshot writes, never per-frame.
/// </summary>
internal static class GilGoals
{
  /// <summary>Progress for one goal bucket, for the dashboard readout.</summary>
  internal record GoalProgress(string Label, float Fraction, bool Achieved);

  /// <summary>
  /// Evaluates all active goals against the latest snapshot and celebrates
  /// new crossings (chat + goal history). Call after writing a gil snapshot.
  /// </summary>
  internal static void Evaluate()
  {
    var config = Plugin.Configuration;
    if (config.GoalPerRetainer <= 0 && config.GoalPlayerGil <= 0 && config.GoalTotalGil <= 0)
      return;

    var snap = GilStorage.GetLatestSnapshot();
    if (snap == null) return;

    var dirty = false;

    // --- Walking-around player gil ---
    if (config.GoalPlayerGil > 0 && snap.PlayerGil >= config.GoalPlayerGil
        && config.GoalPlayerCelebratedTarget != config.GoalPlayerGil)
    {
      config.GoalPlayerCelebratedTarget = config.GoalPlayerGil;
      Record(config, "player", config.GoalPlayerGil, $"{snap.PlayerGil:N0} gil on hand");
      Communicator.PrintGoalReached(
        $"Walking-around gil goal reached: {config.GoalPlayerGil:N0}",
        "The coin purse runneth over.");
      dirty = true;
    }

    // Retainer-dependent goals need retainer balances (bell snapshots).
    if (snap.RetainerGil.Count > 0)
    {
      // --- Total worth ---
      if (config.GoalTotalGil > 0 && snap.TotalGil >= config.GoalTotalGil
          && config.GoalTotalCelebratedTarget != config.GoalTotalGil)
      {
        config.GoalTotalCelebratedTarget = config.GoalTotalGil;
        Record(config, "total", config.GoalTotalGil, $"{snap.TotalGil:N0} gil total worth");
        Communicator.PrintGoalReached(
          $"Total worth goal reached: {config.GoalTotalGil:N0}",
          "Every coin counted twice, and the ledger sings.");
        dirty = true;
      }

      // --- Per-retainer bank ---
      if (config.GoalPerRetainer > 0)
      {
        var atTarget = snap.RetainerGil.Values.Count(g => g >= config.GoalPerRetainer);
        var total = snap.RetainerGil.Count;

        if (config.GoalPerRetainerBaselineTarget != config.GoalPerRetainer)
        {
          // Target was (re)set — baseline quietly against current state.
          config.GoalPerRetainerBaselineTarget = config.GoalPerRetainer;
          config.GoalPerRetainerCelebratedCount = atTarget;
          dirty = true;
        }
        else if (atTarget > config.GoalPerRetainerCelebratedCount)
        {
          config.GoalPerRetainerCelebratedCount = atTarget;
          Record(config, "retainer", config.GoalPerRetainer, $"{atTarget} of {total} retainers");
          Communicator.PrintGoalReached(
            $"Retainer bank: {atTarget} of {total} at {config.GoalPerRetainer:N0}",
            atTarget >= total
              ? "The whole vault gleams. Magnificent."
              : "Another vault sealed at the mark.");
          dirty = true;
        }
        else if (atTarget < config.GoalPerRetainerCelebratedCount)
        {
          // A retainer dipped below target (withdrawal) — lower the high-water
          // quietly so climbing back celebrates honestly.
          config.GoalPerRetainerCelebratedCount = atTarget;
          dirty = true;
        }
      }
    }

    if (dirty)
      config.Save();
  }

  /// <summary>
  /// Progress readouts for all active goals, for the dashboard money line.
  /// Empty when no goals are set.
  /// </summary>
  internal static System.Collections.Generic.List<GoalProgress> GetProgress(GilSnapshot snap)
  {
    var config = Plugin.Configuration;
    var progress = new System.Collections.Generic.List<GoalProgress>(3);

    if (config.GoalPerRetainer > 0 && snap.RetainerGil.Count > 0)
    {
      var atTarget = snap.RetainerGil.Values.Count(g => g >= config.GoalPerRetainer);
      var total = snap.RetainerGil.Count;
      // Fraction sums partial fill across retainers, so progress moves between crossings.
      var fraction = (float)(snap.RetainerGil.Values
        .Sum(g => Math.Min((double)g, config.GoalPerRetainer)) / ((double)config.GoalPerRetainer * total));
      progress.Add(new GoalProgress(
        $"Retainer bank: {atTarget}/{total} at {config.GoalPerRetainer:N0}", fraction, atTarget >= total));
    }

    if (config.GoalPlayerGil > 0)
    {
      var fraction = (float)Math.Min((double)snap.PlayerGil / config.GoalPlayerGil, 1.0);
      progress.Add(new GoalProgress(
        $"Walking gil: {snap.PlayerGil:N0} / {config.GoalPlayerGil:N0}", fraction, snap.PlayerGil >= config.GoalPlayerGil));
    }

    if (config.GoalTotalGil > 0)
    {
      var fraction = (float)Math.Min((double)snap.TotalGil / config.GoalTotalGil, 1.0);
      progress.Add(new GoalProgress(
        $"Total worth: {snap.TotalGil:N0} / {config.GoalTotalGil:N0}", fraction, snap.TotalGil >= config.GoalTotalGil));
    }

    return progress;
  }

  private static void Record(Configuration config, string kind, long target, string detail)
  {
    config.GoalHistory.Add(new GilGoalRecord
    {
      Kind = kind,
      Target = target,
      Detail = detail,
      AchievedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    });
    Svc.Log.Info($"[GilGoals] Goal reached ({kind}): target {target:N0} — {detail}");
  }
}
