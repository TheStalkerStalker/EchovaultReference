using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Echo.Core;
using Echo.Wire;
using Lumina.Excel.Sheets;

namespace Echo.Windows.Tabs;

public sealed class CoverageTab
{
	private readonly Func<EchoApiClient?> _client;

	private readonly IObjectTable _objectTable;

	private readonly IClientState _clientState;

	private readonly IDataManager _dataManager;

	private readonly IPluginLog _log;

	private readonly CachedFetch<ScanTargetsResponse> _targets = new CachedFetch<ScanTargetsResponse>(TimeSpan.FromMinutes(15L));

	private volatile uint _worldId;

	public CoverageTab(Func<EchoApiClient?> client, IObjectTable objectTable, IClientState clientState, IDataManager dataManager, IPluginLog log)
	{
		_client = client;
		_objectTable = objectTable;
		_clientState = clientState;
		_dataManager = dataManager;
		_log = log;
	}

	public void Draw()
	{
		IPlayerCharacter local = _objectTable.LocalPlayer;
		if (local == null)
		{
			ImGui.TextDisabled("Log in to a character to see coverage.");
			return;
		}
		uint worldId = local.CurrentWorld.RowId;
		if (worldId != _worldId)
		{
			_worldId = worldId;
			_targets.Invalidate();
		}
		ScanTargetsResponse data = _targets.Get(FetchTargetsAsync);
		if ((object)data == null || data.WorldId != (int)worldId)
		{
			ImGui.TextDisabled(_targets.Busy ? "Checking..." : "No scan data yet.");
			return;
		}
		DrawHeader(data, worldId);
		if (data.TargetsRevision < 1)
		{
			return;
		}
		if (data.Targets.Count == 0)
		{
			ImGui.TextDisabled("No zone has enough scan data on this world yet. Anywhere you play builds it.");
			return;
		}
		ImGui.Spacing();
		ImGui.TextUnformatted("Best next stops");
		ImGui.SameLine();
		UiHelpers.HelpMarker("Where a visit helps the census most, this week. Counts are approximate and cover whole zones, never individual characters. Updates every 15 minutes.");
		int rank = 1;
		foreach (ScanSuggestion s in ScanSuggestions.Build(data, worldId))
		{
			if (!(s is WorldSuggestion w))
			{
				if (s is ZoneSuggestion z)
				{
					bool here = z.TerritoryId == _clientState.TerritoryType;
					string line = $"{rank}. {TerritoryName((ushort)z.TerritoryId) ?? $"Zone {z.TerritoryId}"} - ~{z.NewPlayers7d:N0} new chars/wk" + (here ? " (you are here)" : "");
					if (here)
					{
						ImGui.TextColored(in UiHelpers.Green, line);
					}
					else
					{
						ImGui.TextUnformatted(line);
					}
				}
			}
			else
			{
				double ratio = w.TheirPct / w.YourPct;
				ImU8String text = new ImU8String(21, 2);
				text.AppendFormatted(rank);
				text.AppendLiteral(". ");
				text.AppendFormatted(WorldName((uint)w.WorldId) ?? $"World {w.WorldId}");
				text.AppendLiteral(" - visit this world");
				ImGui.TextUnformatted(text);
				ImGui.SameLine();
				ImU8String text2 = new ImU8String(21, 1);
				text2.AppendFormatted(ratio, "0.0");
				text2.AppendLiteral("x your discovery rate");
				ImGui.TextColored(in UiHelpers.Green, text2);
			}
			rank++;
		}
		ImGui.Spacing();
		ImU8String label = new ImU8String(16, 2);
		label.AppendLiteral("All zones on ");
		label.AppendFormatted(WorldName(worldId) ?? "this world");
		label.AppendLiteral(" (");
		label.AppendFormatted(data.Targets.Count);
		label.AppendLiteral(")");
		if (!ImGui.CollapsingHeader(label) || !ImGui.BeginTable("##echoZones", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 220f)))
		{
			return;
		}
		ImGui.TableSetupColumn("Zone");
		ImGui.TableSetupColumn("New/wk");
		ImGui.TableHeadersRow();
		foreach (ScanTarget t in data.Targets.OrderByDescending((ScanTarget scanTarget) => scanTarget.NewPlayers7d))
		{
			ImGui.TableNextColumn();
			ImGui.TextUnformatted(TerritoryName((ushort)t.TerritoryId) ?? $"Zone {t.TerritoryId}");
			ImGui.TableNextColumn();
			ImU8String text3 = new ImU8String(1, 1);
			text3.AppendLiteral("~");
			text3.AppendFormatted(t.NewPlayers7d, "N0");
			ImGui.TextUnformatted(text3);
		}
		ImGui.EndTable();
	}

	private void DrawHeader(ScanTargetsResponse data, uint worldId)
	{
		WorldCompleteness? worldCompleteness = data.DcWorlds.FirstOrDefault((WorldCompleteness w) => w.WorldId == (int)worldId);
		WorldCompleteness thinnest = (from w in data.DcWorlds
			where w.WorldId != (int)worldId && w.NoveltyPct.HasValue
			orderby w.NoveltyPct.Value descending
			select w).FirstOrDefault();
		double? num = worldCompleteness?.NoveltyPct;
		object obj;
		if (num.HasValue)
		{
			double m = num.GetValueOrDefault();
			obj = $"{m:0.0}% discovery rate";
		}
		else
		{
			obj = "discovery rate unknown";
		}
		string mine = (string)obj;
		string line = (WorldName(worldId) ?? "This world") + " · " + mine;
		if ((object)thinnest != null)
		{
			line += $" - thinnest in your DC: {WorldName((uint)thinnest.WorldId)} {thinnest.NoveltyPct:0.0}%";
		}
		ImGui.TextDisabled(line);
	}

	private async Task<ScanTargetsResponse?> FetchTargetsAsync()
	{
		EchoApiClient client = _client();
		if (client == null)
		{
			return null;
		}
		uint worldId = _worldId;
		try
		{
			return await client.GetScanTargetsAsync(worldId, CancellationToken.None);
		}
		catch (Exception exception)
		{
			_log.Error(exception, "scan targets fetch failed");
			return null;
		}
	}

	private string? TerritoryName(ushort id)
	{
		if (id != 0)
		{
			return _dataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(id)?.PlaceName.ValueNullable?.Name.ExtractText();
		}
		return null;
	}

	private string? WorldName(uint id)
	{
		if (id != 0)
		{
			return _dataManager.GetExcelSheet<World>().GetRowOrDefault(id)?.Name.ExtractText();
		}
		return null;
	}
}
