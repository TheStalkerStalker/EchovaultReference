using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Echo.Windows;

internal static class UiHelpers
{
	public static readonly Vector4 Green = new Vector4(0.4f, 0.9f, 0.4f, 1f);

	public static readonly Vector4 Amber = new Vector4(1f, 0.7f, 0.2f, 1f);

	public static readonly Vector4 Red = new Vector4(1f, 0.3f, 0.3f, 1f);

	public static readonly Vector4 Blue = new Vector4(0.47f, 0.72f, 1f, 1f);

	public static readonly Vector4 Dim = new Vector4(0.55f, 0.55f, 0.59f, 1f);

	public static void HelpMarker(string text)
	{
		ImGui.TextDisabled("(?)");
		if (ImGui.IsItemHovered())
		{
			ImGui.BeginTooltip();
			ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24f);
			ImGui.TextUnformatted(text);
			ImGui.PopTextWrapPos();
			ImGui.EndTooltip();
		}
	}

	public static string FormatUptime(TimeSpan up)
	{
		if (up.TotalHours >= 1.0)
		{
			return $"{(int)up.TotalHours}h {up.Minutes:D2}m";
		}
		return $"{up.Minutes}m";
	}
}
