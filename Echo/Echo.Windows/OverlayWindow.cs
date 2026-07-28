using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Echo.Core;

namespace Echo.Windows;

public sealed class OverlayWindow : Window
{
	private readonly PluginState _state;

	private readonly SessionTracker _session;

	private readonly Action _openMain;

	public OverlayWindow(PluginState state, SessionTracker session, Action openMain)
		: base("###EchoOverlay")
	{
		_state = state;
		_session = session;
		_openMain = openMain;
		base.Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing;
		base.BgAlpha = 0.72f;
		base.RespectCloseHotkey = false;
	}

	public override void PreDraw()
	{
		PluginStateSnapshot pluginStateSnapshot = _state.Snapshot();
		ImGuiWindowFlags f = base.Flags;
		f = (pluginStateSnapshot.OverlayClickThrough ? (f | ImGuiWindowFlags.NoInputs) : (f & ~ImGuiWindowFlags.NoInputs));
		f = (pluginStateSnapshot.OverlayLocked ? (f | ImGuiWindowFlags.NoMove) : (f & ~ImGuiWindowFlags.NoMove));
		base.Flags = f;
	}

	public override void Draw()
	{
		PluginStateSnapshot snap = _state.Snapshot();
		SessionSnapshot session = _session.Snapshot();
		ImGui.TextColored((snap.LastError == null && snap.ServerAllowsIngest && !StatusHints.UploadsLookStalled(snap, DateTimeOffset.UtcNow)) ? UiHelpers.Green : UiHelpers.Amber, "●");
		ImGui.SameLine();
		ImU8String text = new ImU8String(0, 1);
		text.AppendFormatted(session.SightingsUploaded, "N0");
		ImGui.TextUnformatted(text);
		double? sightingsPerMinute = session.SightingsPerMinute;
		if (sightingsPerMinute.HasValue)
		{
			double rate = sightingsPerMinute.GetValueOrDefault();
			ImGui.SameLine();
			ImU8String text2 = new ImU8String(6, 1);
			text2.AppendLiteral("· ");
			text2.AppendFormatted(rate, "0.0");
			text2.AppendLiteral("/min");
			ImGui.TextDisabled(text2);
		}
		if (session.NewCharacters > 0)
		{
			ImU8String text3 = new ImU8String(16, 1);
			text3.AppendLiteral("✦ ");
			text3.AppendFormatted(session.NewCharacters, "N0");
			text3.AppendLiteral(" new to census");
			ImGui.TextColored(in UiHelpers.Blue, text3);
		}
		ImU8String text4 = new ImU8String(9, 2);
		text4.AppendFormatted(session.ZonesVisited);
		text4.AppendLiteral(" zones · ");
		text4.AppendFormatted(UiHelpers.FormatUptime(session.Uptime));
		ImGui.TextDisabled(text4);
		if (!snap.OverlayClickThrough && ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
		{
			_openMain();
		}
	}
}
