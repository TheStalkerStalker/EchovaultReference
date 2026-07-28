using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Echo.Core;
using Echo.Wire;

namespace Echo.Windows.Tabs;

public sealed class ProgressTab
{
	private readonly CachedFetch<ScannerStatsResponse> _stats;

	private readonly Func<EchoApiClient?> _client;

	private readonly IPluginLog _log;

	public ProgressTab(CachedFetch<ScannerStatsResponse> stats, Func<EchoApiClient?> client, IPluginLog log)
	{
		_stats = stats;
		_client = client;
		_log = log;
	}

	public void Draw()
	{
		ImGui.TextUnformatted("Your progression");
		ImGui.SameLine();
		UiHelpers.HelpMarker("Your private contribution to the census. Only you can see these numbers - they are never shown on the website or to other contributors. Updates every 15 minutes.");
		ScannerStatsResponse stats = _stats.Get(FetchStatsAsync);
		if ((object)stats == null)
		{
			ImGui.TextDisabled(_stats.Busy ? "Checking..." : "No stats yet - they appear once uploads flow.");
			return;
		}
		ImGui.Spacing();
		Track("Lifetime sightings", ScannerTrack.Sightings, stats.LifetimeSightings);
		Track("Characters first added by you", ScannerTrack.Characters, stats.CharactersContributed);
		Track("Territories covered", ScannerTrack.Territories, stats.TerritoriesCovered);
		ImGui.Separator();
		string obj = $"This week {stats.WeekSightings:N0}";
		ScannerBestWeek bw = stats.BestWeek;
		ImGui.TextUnformatted(obj + (((object)bw != null) ? $" · best week {bw.Count:N0}" : ""));
		int? percentileBand = stats.PercentileBand;
		if (percentileBand.HasValue)
		{
			int band = percentileBand.GetValueOrDefault();
			if (band <= 75)
			{
				ImU8String text = new ImU8String(46, 1);
				text.AppendLiteral("You're in the top ");
				text.AppendFormatted(band);
				text.AppendLiteral("% of contributors this week.");
				ImGui.TextColored(in UiHelpers.Green, text);
			}
		}
		ImU8String text2 = new ImU8String(21, 1);
		text2.AppendLiteral("Characters observed: ");
		text2.AppendFormatted(stats.CharactersObserved, "N0");
		ImGui.TextUnformatted(text2);
	}

	private static void Track(string label, ScannerTrack track, long value)
	{
		MilestoneProgress p = ScannerMilestones.Progress(track, value);
		ImGui.TextUnformatted(label);
		ImGui.SameLine();
		ImU8String text = new ImU8String(0, 1);
		text.AppendFormatted(value, "N0");
		ImGui.TextDisabled(text);
		string overlay = (((object)p.Next == null) ? (p.Reached.Name + " · max tier") : $"{p.Reached?.Name ?? "Unranked"} → {p.Next.Name} · {p.Next.Threshold:N0}");
		ImGui.ProgressBar(p.Fraction, new Vector2(-1f, 17f), overlay);
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
