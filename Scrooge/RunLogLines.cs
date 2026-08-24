using System;

namespace Scrooge;

/// <summary>
/// The run log transcript's line grammar, for the copy-out. Pure - the log entry
/// types live in the ImGui window and cannot link into the test project, so what
/// crosses over is the string rule alone.
/// </summary>
internal static class RunLogLines
{
  /// <summary>
  /// One legacy-outcome transcript line: "<c>Desynthed: Aetherial Ring - HQ</c>".
  ///
  /// <para>The stutter this exists to kill: the outcome prefix and the entry's own
  /// message both named the act, so a melted item copied out as "Desynthed:
  /// Aetherial Ring - desynthed". The message is the QUALIFIER, so when it merely
  /// repeats the prefix it is dropped, and when it leads with it, only the lead is.
  /// Every other outcome is untouched - their messages say something the prefix
  /// does not.</para>
  /// </summary>
  internal static string CopyLine(string prefix, string itemName, string? message)
  {
    var qualifier = (message ?? "").Trim();
    if (qualifier.Equals(prefix, StringComparison.OrdinalIgnoreCase))
      qualifier = "";
    else if (qualifier.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
      qualifier = qualifier[prefix.Length..].Trim();

    return qualifier.Length > 0
      ? $"{prefix}: {itemName} — {qualifier}"
      : $"{prefix}: {itemName}";
  }
}
