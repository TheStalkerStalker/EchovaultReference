using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Echo.Core;

namespace Echo.Windows.Tabs;

public sealed class HealthTab
{
	private readonly PluginState _state;

	private readonly SessionTracker _session;

	private readonly HealthLog _health;

	private readonly string _pluginVersion;

	public HealthTab(PluginState state, SessionTracker session, HealthLog health, string pluginVersion)
	{
		_state = state;
		_session = session;
		_health = health;
		_pluginVersion = pluginVersion;
	}

	public void Draw()
	{
		PluginStateSnapshot snap = _state.Snapshot();
		SessionSnapshot session = _session.Snapshot();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		string summary;
		Vector4 color;
		if (!snap.AwaitingLogin)
		{
			string err = snap.LastError;
			if (err == null)
			{
				if (snap.SightingsDropped <= 0)
				{
					if (snap.ServerAllowsIngest)
					{
						if (!snap.ServerBusy)
						{
							if (!StatusHints.UploadsLookStalled(snap, now))
							{
								if (!snap.Standby)
								{
									Vector4 green = UiHelpers.Green;
									summary = "All systems normal";
									color = green;
								}
								else
								{
									Vector4 amber = UiHelpers.Amber;
									summary = "Standby: another game client is running Echo as this character";
									color = amber;
								}
							}
							else
							{
								Vector4 amber2 = UiHelpers.Amber;
								summary = "Uploads stalled - check echovault.gg/status";
								color = amber2;
							}
						}
						else
						{
							Vector4 amber3 = UiHelpers.Amber;
							summary = "Server busy - sightings queued, uploading as it catches up";
							color = amber3;
						}
					}
					else
					{
						Vector4 amber4 = UiHelpers.Amber;
						summary = "Uploads disabled by server (update Echo?)";
						color = amber4;
					}
				}
				else
				{
					Vector4 red = UiHelpers.Red;
					string text = $"{snap.SightingsDropped:N0} oldest sightings dropped this session (outbox full)";
					summary = text;
					color = red;
				}
			}
			else
			{
				Vector4 red2 = UiHelpers.Red;
				string text = "Upload problem: " + err;
				summary = text;
				color = red2;
			}
		}
		else
		{
			Vector4 dim = UiHelpers.Dim;
			summary = "Logged out - Echo is idle";
			color = dim;
		}
		ImU8String text2 = new ImU8String(2, 1);
		text2.AppendLiteral("● ");
		text2.AppendFormatted(summary);
		ImGui.TextColored(in color, text2);
		ImGui.Separator();
		ImGui.TextColored(in UiHelpers.Blue, "Upload pipeline");
		DateTimeOffset? lastUploadAt = snap.LastUploadAt;
		object obj;
		if (lastUploadAt.HasValue)
		{
			DateTimeOffset last = lastUploadAt.GetValueOrDefault();
			obj = $" ({(int)(now - last).TotalSeconds}s ago)";
		}
		else
		{
			obj = "";
		}
		string age = (string)obj;
		Row("Outbox", $"{snap.OutboxDepth} pending");
		lastUploadAt = snap.LastUploadAt;
		object value;
		if (lastUploadAt.HasValue)
		{
			DateTimeOffset l = lastUploadAt.GetValueOrDefault();
			value = $"{l.ToLocalTime():HH:mm:ss}{age}";
		}
		else
		{
			value = "never";
		}
		Row("Last upload", (string)value);
		Row("Session uploaded", $"{session.SightingsUploaded:N0} sightings");
		Row("Retry state", snap.ServerBusy ? "server busy, short retry" : ((snap.LastError == null) ? "none" : "backing off, will retry"));
		if (snap.SightingsDropped > 0)
		{
			Row("Dropped this session", $"{snap.SightingsDropped:N0} sightings (outbox full)");
		}
		ImGui.Separator();
		ImGui.TextColored(in UiHelpers.Blue, "Server");
		Row("Ingest", snap.ServerAllowsIngest ? "accepting" : "disabled");
		Row("Plugin version", _pluginVersion);
		ImGui.Separator();
		ImGui.TextColored(in UiHelpers.Blue, "Identity");
		Row("State", snap.AwaitingLogin ? "logged out" : (snap.Standby ? "standby" : "active"));
		Row("Tier", (snap.Registration == RegistrationStatus.Verified) ? "verified · uploads go live instantly" : snap.Registration.ToString().ToLowerInvariant());
		ImGui.Separator();
		ImGui.TextColored(in UiHelpers.Blue, "Recent problems");
		IReadOnlyList<HealthEntry> problems = _health.Entries();
		if (problems.Count == 0)
		{
			ImGui.TextDisabled("none");
		}
		foreach (HealthEntry p in problems)
		{
			string line = $"{p.At.ToLocalTime():HH:mm:ss} · {p.Description}" + (p.Recovered ? " - recovered" : "");
			if (p.Recovered)
			{
				ImGui.TextDisabled(line);
			}
			else
			{
				ImGui.TextColored(in UiHelpers.Amber, line);
			}
		}
		ImGui.Separator();
		if (ImGui.Button("Copy diagnostics"))
		{
			ImGui.SetClipboardText(DiagnosticsReport.Build(_pluginVersion, snap, session, problems, now));
		}
	}

	private static void Row(string key, string value)
	{
		ImGui.TextDisabled(key);
		ImGui.SameLine(170f);
		ImGui.TextUnformatted(value);
	}
}
