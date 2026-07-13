using System.Linq;
using System.Reflection;

namespace Scrooge;

/// <summary>
/// Git commit/branch and build time, stamped into the assembly by the StampBuildInfo
/// target in Scrooge.csproj. Answers "exactly what code is this DLL?" at runtime —
/// a "+dirty" suffix on the commit means uncommitted changes were in the build.
/// </summary>
internal static class BuildStamp
{
  public static readonly string Commit = Get("GitCommit");
  public static readonly string Branch = Get("GitBranch");
  public static readonly string BuiltAt = Get("BuildTime");

  /// <summary>One-line summary, e.g. "be0e0f7 on era/advisor, built 2026-07-11 19:05".</summary>
  public static readonly string Line = $"{Commit} on {Branch}, built {BuiltAt}";

  private static string Get(string key) =>
    typeof(BuildStamp).Assembly
      .GetCustomAttributes<AssemblyMetadataAttribute>()
      .FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } value
        ? value
        : "unknown";
}
