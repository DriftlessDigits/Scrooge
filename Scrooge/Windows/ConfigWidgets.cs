using System;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>
/// The config window's repeated furniture, drawn once instead of fifty times: the
/// '(?)' hint, the label-and-widget row, and the enum dropdown. Hand copies of these
/// drift - the hint copies had grown a tooltip nested inside a tooltip, which
/// <see cref="ImGui.SetTooltip(string)"/> never wanted.
/// </summary>
internal static class ConfigWidgets
{
  /// <summary>
  /// Enum option lists, built once per type. Every combo below rebuilt its own list
  /// every frame it drew; the names cannot change while the game is running.
  /// </summary>
  private static class EnumNames<T> where T : struct, Enum
  {
    internal static readonly string[] Raw = Enum.GetNames<T>();

    internal static readonly string[] Display = [.. Raw.Select(FormatEnumName)];
  }

  /// <summary>Converts PascalCase enum names to display-friendly format (e.g. "FixedAmount" → "Fixed Amount").</summary>
  /// <param name="name">The raw PascalCase enum name.</param>
  /// <returns>The name with spaces inserted before each capital letter (except the first).</returns>
  internal static string FormatEnumName(string name)
  {
    var result = new System.Text.StringBuilder();

    for (int i = 0; i < name.Length; i++)
    {
      if (i > 0 && char.IsUpper(name[i]))
        result.Append(' ');

      result.Append(name[i]);
    }

    return result.ToString();
  }

  /// <summary>The '(?)' marker that trails whatever was just drawn, explaining itself on hover.</summary>
  /// <param name="text">The hover text.</param>
  internal static void Hint(string text)
  {
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip(text);
  }

  /// <summary>
  /// A label, a stepper, and an optional unit word, grouped so a trailing
  /// <see cref="Hint"/> hangs off the whole row rather than the last thing in it.
  /// </summary>
  /// <param name="label">The text drawn before the widget.</param>
  /// <param name="id">The widget's ImGui id (the '##name' form keeps the label out of it).</param>
  /// <param name="value">The value being edited.</param>
  /// <param name="width">Widget width in pixels.</param>
  /// <param name="step">Amount the -/+ buttons move the value.</param>
  /// <param name="stepFast">Amount the -/+ buttons move the value when held with ctrl.</param>
  /// <param name="suffix">Unit word drawn after the widget, if any.</param>
  /// <returns>True when the value changed this frame.</returns>
  internal static bool LabeledInt(string label, string id, ref int value, float width,
                                  int step = 1, int stepFast = 100, string? suffix = null)
  {
    ImGui.BeginGroup();
    ImGui.Text(label);
    ImGui.SameLine();
    ImGui.SetNextItemWidth(width);
    var changed = ImGui.InputInt(id, ref value, step, stepFast);
    if (suffix != null)
    {
      ImGui.SameLine();
      ImGui.Text(suffix);
    }
    ImGui.EndGroup();
    return changed;
  }

  /// <summary><see cref="LabeledInt"/>'s shape with a slider in place of the stepper.</summary>
  /// <param name="label">The text drawn before the widget.</param>
  /// <param name="id">The widget's ImGui id.</param>
  /// <param name="value">The value being edited.</param>
  /// <param name="min">Lowest value the slider offers.</param>
  /// <param name="max">Highest value the slider offers.</param>
  /// <param name="width">Widget width in pixels.</param>
  /// <param name="format">printf format for the number drawn inside the slider.</param>
  /// <param name="suffix">Unit word drawn after the widget, if any.</param>
  /// <returns>True when the value changed this frame.</returns>
  internal static bool LabeledSlider(string label, string id, ref int value, int min, int max, float width,
                                     string format = "%d", string? suffix = null)
  {
    ImGui.BeginGroup();
    ImGui.Text(label);
    ImGui.SameLine();
    ImGui.SetNextItemWidth(width);
    var changed = ImGui.SliderInt(id, ref value, min, max, format);
    if (suffix != null)
    {
      ImGui.SameLine();
      ImGui.Text(suffix);
    }
    ImGui.EndGroup();
    return changed;
  }

  /// <summary><see cref="LabeledSlider"/> for the knobs that measure in fractions.</summary>
  /// <param name="label">The text drawn before the widget.</param>
  /// <param name="id">The widget's ImGui id.</param>
  /// <param name="value">The value being edited.</param>
  /// <param name="min">Lowest value the slider offers.</param>
  /// <param name="max">Highest value the slider offers.</param>
  /// <param name="width">Widget width in pixels.</param>
  /// <param name="format">printf format for the number drawn inside the slider.</param>
  /// <param name="suffix">Unit word drawn after the widget, if any.</param>
  /// <returns>True when the value changed this frame.</returns>
  internal static bool LabeledFloat(string label, string id, ref float value, float min, float max, float width,
                                    string format = "%.3f", string? suffix = null)
  {
    ImGui.BeginGroup();
    ImGui.Text(label);
    ImGui.SameLine();
    ImGui.SetNextItemWidth(width);
    var changed = ImGui.SliderFloat(id, ref value, min, max, format);
    if (suffix != null)
    {
      ImGui.SameLine();
      ImGui.Text(suffix);
    }
    ImGui.EndGroup();
    return changed;
  }

  /// <summary>
  /// A dropdown over an enum's live NAMES, so a retired member simply stops appearing
  /// and the surviving ordinals stay frozen for the configs that store them. Width is
  /// the caller's (set it with SetNextItemWidth before the call), as is the Save.
  /// </summary>
  /// <typeparam name="T">The enum being chosen from.</typeparam>
  /// <param name="label">The combo's ImGui id.</param>
  /// <param name="value">The current selection, replaced when the player picks another.</param>
  /// <param name="formatNames">
  /// True to space out PascalCase names for display; false for enums whose names are
  /// already the thing the player reads (key names, where spacing them would be noise).
  /// </param>
  /// <returns>True when the selection changed this frame.</returns>
  internal static bool EnumCombo<T>(string label, ref T value, bool formatNames = true) where T : struct, Enum
  {
    var names = EnumNames<T>.Raw;
    var display = formatNames ? EnumNames<T>.Display : names;
    var index = Array.IndexOf(names, value.ToString());

    if (!ImGui.Combo(label, ref index, display, display.Length))
      return false;

    value = Enum.Parse<T>(names[index]);
    return true;
  }

  /// <summary>
  /// THE CURATED KEY LIST for the hotkey combos (3.1 sweep): the raw VirtualKey
  /// enum is ~190 members deep in mouse buttons, IME codes and OEM keys nobody
  /// binds a pinch to. Modifiers lead (the natural hold-keys), then the keys a
  /// hand actually reaches: letters, digits, F-keys, space.
  /// </summary>
  private static readonly Dalamud.Game.ClientState.Keys.VirtualKey[] HotkeyChoices = BuildHotkeyChoices();

  private static Dalamud.Game.ClientState.Keys.VirtualKey[] BuildHotkeyChoices()
  {
    var keys = new System.Collections.Generic.List<Dalamud.Game.ClientState.Keys.VirtualKey>
    {
      Dalamud.Game.ClientState.Keys.VirtualKey.SHIFT,
      Dalamud.Game.ClientState.Keys.VirtualKey.CONTROL,
      Dalamud.Game.ClientState.Keys.VirtualKey.MENU,
      Dalamud.Game.ClientState.Keys.VirtualKey.SPACE,
    };
    for (var k = Dalamud.Game.ClientState.Keys.VirtualKey.A; k <= Dalamud.Game.ClientState.Keys.VirtualKey.Z; k++)
      keys.Add(k);
    for (var k = Dalamud.Game.ClientState.Keys.VirtualKey.KEY_0; k <= Dalamud.Game.ClientState.Keys.VirtualKey.KEY_9; k++)
      keys.Add(k);
    for (var k = Dalamud.Game.ClientState.Keys.VirtualKey.F1; k <= Dalamud.Game.ClientState.Keys.VirtualKey.F12; k++)
      keys.Add(k);
    return [.. keys];
  }

  /// <summary>"MENU" means nothing at a keyboard; "KEY_3" is house style for nobody.</summary>
  private static string KeyDisplay(Dalamud.Game.ClientState.Keys.VirtualKey key) => key switch
  {
    Dalamud.Game.ClientState.Keys.VirtualKey.MENU => "ALT",
    Dalamud.Game.ClientState.Keys.VirtualKey.SPACE => "SPACE",
    var k => k.ToString().Replace("KEY_", ""),
  };

  /// <summary>
  /// A dropdown over the curated hotkey list. A stored value OUTSIDE the list is
  /// prepended rather than hidden - curating the choices must never eat a saved
  /// binding (the config would still hold it; the combo would just lie).
  /// </summary>
  internal static bool KeyCombo(string label, ref Dalamud.Game.ClientState.Keys.VirtualKey value)
  {
    var choices = HotkeyChoices;
    var index = Array.IndexOf(choices, value);
    if (index < 0)
    {
      choices = [value, .. HotkeyChoices];
      index = 0;
    }
    var display = Array.ConvertAll(choices, KeyDisplay);

    if (!ImGui.Combo(label, ref index, display, display.Length))
      return false;

    value = choices[index];
    return true;
  }
}
