using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Echo.Core;
using Echo.Wire;

namespace Echo.Windows.Tabs;

public sealed class DashboardTab
{
	private readonly PluginState _state;

	private readonly SessionTracker _session;

	private readonly CachedFetch<ScannerStatsResponse> _stats;

	private readonly Func<EchoApiClient?> _client;

	private readonly IObjectTable _objectTable;

	private readonly IPluginLog _log;

	private readonly Action _openSettings;

	private readonly Action _saveSettings;

	public DashboardTab(PluginState state, SessionTracker session, CachedFetch<ScannerStatsResponse> stats, Func<EchoApiClient?> client, IObjectTable objectTable, IPluginLog log, Action openSettings, Action saveSettings)
	{
		_state = state;
		_session = session;
		_stats = stats;
		_client = client;
		_objectTable = objectTable;
		_log = log;
		_openSettings = openSettings;
		_saveSettings = saveSettings;
	}

	public void Draw()
	{
		PluginStateSnapshot snap = _state.Snapshot();
		SessionSnapshot session = _session.Snapshot();
		IPlayerCharacter local = _objectTable.LocalPlayer;
		DrawStatusLine(snap, session, local?.Name.TextValue, local?.CurrentWorld.ValueNullable?.Name.ExtractText());
		if (snap.AwaitingLogin || snap.Standby)
		{
			return;
		}
		ScannerStatsResponse stats = _stats.Get(FetchStatsAsync);
		ImGui.Spacing();
		string value2;
		object sub2;
		if (ImGui.BeginTable("##echoTiles", 2, ImGuiTableFlags.SizingStretchSame))
		{
			ImGui.TableNextColumn();
			string value = $"{session.SightingsUploaded:N0}";
			double? sightingsPerMinute = session.SightingsPerMinute;
			object sub;
			if (sightingsPerMinute.HasValue)
			{
				double r = sightingsPerMinute.GetValueOrDefault();
				sub = $"{r:0.0} / min";
			}
			else
			{
				sub = "rate after 1 min";
			}
			Tile("Session sightings", value, (string)sub);
			ImGui.TableNextColumn();
			Tile("Zones visited", $"{session.ZonesVisited:N0}", "this session");
			ImGui.TableNextColumn();
			if (snap.Registration == RegistrationStatus.Verified)
			{
				value2 = (((object)stats == null) ? "-" : $"{stats.WeekSightings:N0}");
				int? num = stats?.PercentileBand;
				if (num.HasValue)
				{
					int band = num.GetValueOrDefault();
					if (band <= 75)
					{
						sub2 = $"top {band}% of scanners";
						goto IL_0231;
					}
				}
				sub2 = "";
				goto IL_0231;
			}
			NudgeTile();
			goto IL_023e;
		}
		goto IL_0243;
		IL_0243:
		ImGui.Spacing();
		DateTimeOffset? lastUploadAt = snap.LastUploadAt;
		object obj;
		if (lastUploadAt.HasValue)
		{
			DateTimeOffset last = lastUploadAt.GetValueOrDefault();
			obj = $" · last upload {last.ToLocalTime():HH:mm:ss}";
		}
		else
		{
			obj = "";
		}
		string lastUp = (string)obj;
		ImU8String text = new ImU8String(15, 2);
		text.AppendLiteral("Outbox ");
		text.AppendFormatted(snap.OutboxDepth);
		text.AppendLiteral(" pending");
		text.AppendFormatted(lastUp);
		ImGui.TextDisabled(text);
		DrawAlertLine(snap);
		ImGui.Separator();
		if (local != null)
		{
			if (ImGui.Button("Open my EchoVault profile"))
			{
				Util.OpenLink(ProfileLink.For(local.HomeWorld.RowId, local.Name.TextValue));
			}
			ImGui.SameLine();
		}
		if (ImGui.Button(snap.OverlayEnabled ? "Unpin session overlay" : "Pin session overlay"))
		{
			_state.SetOverlayEnabled(!snap.OverlayEnabled);
			_saveSettings();
		}
		return;
		IL_0231:
		Tile("This week", value2, (string)sub2);
		goto IL_023e;
		IL_023e:
		ImGui.EndTable();
		goto IL_0243;
	}

	private void DrawStatusLine(PluginStateSnapshot snap, SessionSnapshot session, string? name, string? world)
	{
		if (snap.AwaitingLogin)
		{
			ImGui.TextColored(in UiHelpers.Dim, "● Logged out");
			ImGui.TextDisabled("Log in to a character to start Echo in this window.");
		}
		else if (snap.Standby)
		{
			ImGui.TextColored(in UiHelpers.Amber, "● Standby");
			ImGui.TextDisabled("Another game client is running Echo as this character. This one takes over if it closes.");
		}
		else if (!snap.CaptureEnabled)
		{
			ImU8String text = new ImU8String(19, 2);
			text.AppendLiteral("● Capture off - ");
			text.AppendFormatted(name);
			text.AppendLiteral(" · ");
			text.AppendFormatted(world);
			ImGui.TextColored(in UiHelpers.Dim, text);
		}
		else
		{
			ImGui.TextColored(in UiHelpers.Green, "● Scanning");
			ImGui.SameLine();
			ImU8String text2 = new ImU8String(11, 3);
			text2.AppendLiteral("- ");
			text2.AppendFormatted(name);
			text2.AppendLiteral(" · ");
			text2.AppendFormatted(world);
			text2.AppendLiteral(" · up ");
			text2.AppendFormatted(UiHelpers.FormatUptime(session.Uptime));
			ImGui.TextUnformatted(text2);
		}
	}

	private void DrawAlertLine(PluginStateSnapshot snap)
	{
		if (snap.SightingsDropped > 0)
		{
			ImU8String text = new ImU8String(56, 1);
			text.AppendFormatted(snap.SightingsDropped, "N0");
			text.AppendLiteral(" oldest sightings dropped (outbox full) - see Health tab");
			ImGui.TextColored(in UiHelpers.Red, text);
		}
		string alert = ((snap.LastError != null) ? "Upload problem" : ((!snap.ServerAllowsIngest) ? "Uploads disabled by server" : (snap.ServerBusy ? "Server busy, uploads queued" : (StatusHints.UploadsLookStalled(snap, DateTimeOffset.UtcNow) ? "Uploads stalled" : null))));
		if (alert != null)
		{
			ImU8String text2 = new ImU8String(17, 1);
			text2.AppendFormatted(alert);
			text2.AppendLiteral(" - see Health tab");
			ImGui.TextColored(in UiHelpers.Amber, text2);
		}
	}

	private void NudgeTile()
	{
		ImGui.TextDisabled("SETUP");
		if (ImGui.Button("Verify to go live instantly → Settings"))
		{
			_openSettings();
		}
		ImGui.TextDisabled("uploads wait for corroboration until then");
	}

	private static void Tile(string caption, string value, string sub)
	{
		ImGui.TextDisabled(caption.ToUpperInvariant());
		ImGui.SetWindowFontScale(1.55f);
		ImGui.TextUnformatted(value);
		ImGui.SetWindowFontScale(1f);
		if (sub.Length > 0)
		{
			ImGui.TextDisabled(sub);
		}
		ImGui.Spacing();
	}

	private async Task<ScannerStatsResponse?> FetchStatsAsync()
	{
		EchoApiClient client = _client();
		if (client == null)
		{
			return null;
		}
		try
		{
			return await client.GetScannerStatsAsync(CancellationToken.None);
		}
		catch (Exception exception)
		{
			_log.Warning(exception, "scanner stats fetch failed");
			return null;
		}
	}
}
