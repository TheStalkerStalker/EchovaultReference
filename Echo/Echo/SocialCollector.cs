using System;
using System.Collections.Generic;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;
using Echo.Core;
using Echo.Wire;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace Echo;

internal sealed class SocialCollector(IFramework framework, IPartyList partyList, IObjectTable objectTable, IDataManager dataManager, CaptureEngine socialEngine, CaptureEngine searchEngine, Outbox outbox, PluginState state, IPluginLog log) : IDisposable
{
	private static readonly TimeSpan PartyInterval = TimeSpan.FromSeconds(15L);

	private static readonly TimeSpan RosterInterval = TimeSpan.FromSeconds(60L);

	private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(15L);

	private DateTime _lastParty = DateTime.MinValue;

	private DateTime _lastRoster = DateTime.MinValue;

	private DateTime _lastSearch = DateTime.MinValue;

	public void Enable()
	{
		framework.Update += OnUpdate;
	}

	public void Dispose()
	{
		framework.Update -= OnUpdate;
	}

	private string? WorldName(uint worldId)
	{
		if (worldId != 0)
		{
			return dataManager.GetExcelSheet<World>().GetRowOrDefault(worldId)?.Name.ExtractText();
		}
		return null;
	}

	private unsafe void OnUpdate(IFramework _)
	{
		try
		{
			PluginStateSnapshot snap = state.Snapshot();
			if (!snap.CaptureEnabled || !snap.ServerAllowsIngest || (!snap.SocialCaptureEnabled && !snap.SearchCaptureEnabled))
			{
				return;
			}
			IPlayerCharacter local = objectTable.LocalPlayer;
			if (local == null)
			{
				return;
			}
			ulong localContentId = ReadLocalContentId(local);
			DateTime now = DateTime.UtcNow;
			List<CapturedPlayer> captured = new List<CapturedPlayer>();
			List<CapturedPlayer> searched = new List<CapturedPlayer>();
			if (snap.SocialCaptureEnabled && now - _lastParty >= PartyInterval)
			{
				_lastParty = now;
				InfoProxyCrossRealm* crossRealm = InfoProxyCrossRealm.Instance();
				if (crossRealm != null && crossRealm->IsCrossRealm && crossRealm->GroupCount > 0)
				{
					Span<CrossRealmGroup> groups = crossRealm->CrossRealmGroups;
					Span<CrossRealmGroup> span = groups[..Math.Min(crossRealm->GroupCount, groups.Length)];
					for (int i = 0; i < span.Length; i++)
					{
						ref CrossRealmGroup reference = ref span[i];
						CrossRealmGroup crossRealmGroup = reference;
						Span<CrossRealmMember> members = crossRealmGroup.GroupMembers;
						Span<CrossRealmMember> span2 = members[..Math.Min(reference.GroupMemberCount, members.Length)];
						for (int j = 0; j < span2.Length; j++)
						{
							ref CrossRealmMember m = ref span2[j];
							ulong contentId = m.ContentId;
							CrossRealmMember crossRealmMember = m;
							CapturedPlayer mapped = CrossRealmCapture.TryMap(contentId, crossRealmMember.NameString, m.HomeWorld, m.CurrentWorld, m.ClassJobId, m.Level, (m.HomeWorld > 0) ? WorldName((uint)m.HomeWorld) : null);
							if ((object)mapped != null)
							{
								captured.Add(mapped);
							}
						}
					}
				}
				else
				{
					foreach (IPartyMember pm in partyList)
					{
						ulong contentId2 = pm.ContentId;
						uint worldId = pm.World.RowId;
						if (contentId2 != 0L && worldId != 0)
						{
							captured.Add(new CapturedPlayer(contentId2, pm.Name.TextValue, worldId, worldId, 0, 0f, 0f, 0f, (byte)pm.ClassJob.RowId, pm.Level, null, null, "social", 0uL, 0, 0, null, 0uL, 0uL, WorldName(worldId), 0, 0, 0));
						}
					}
				}
			}
			if (snap.SocialCaptureEnabled && now - _lastRoster >= RosterInterval)
			{
				_lastRoster = now;
				CollectProxy(InfoProxyId.FreeCompanyMember, captured);
				CollectProxy(InfoProxyId.FriendList, captured);
				CollectProxy(InfoProxyId.LinkshellMember, captured);
				CollectProxy(InfoProxyId.CrossWorldLinkshellMember, captured);
				CollectProxy(InfoProxyId.NoviceNetworkMember, captured);
				CollectProxy(InfoProxyId.NoviceNetworkMentor, captured);
			}
			if (snap.SearchCaptureEnabled && now - _lastSearch >= SearchInterval)
			{
				_lastSearch = now;
				CollectSearchProxy(searched);
			}
			if (captured.Count == 0 && searched.Count == 0)
			{
				return;
			}
			ReporterSelf reporter = state.Snapshot().Reporter;
			DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
			foreach (Sighting s in socialEngine.Process(captured, localContentId, nowUtc, reporter))
			{
				outbox.Append(JsonSerializer.Serialize(s, WireJson.Options));
			}
			int searchEmitted = 0;
			foreach (Sighting s2 in searchEngine.Process(searched, localContentId, nowUtc, reporter))
			{
				outbox.Append(JsonSerializer.Serialize(s2, WireJson.Options));
				searchEmitted++;
			}
			if (searched.Count > 0)
			{
				log.Verbose($"Echo search capture: read {searched.Count}, uploaded {searchEmitted} new");
			}
		}
		catch (Exception exception)
		{
			log.Verbose(exception, "Echo social collection failed");
		}
	}

	private unsafe void CollectProxy(InfoProxyId id, List<CapturedPlayer> captured)
	{
		InfoModule* module = InfoModule.Instance();
		if (module == null)
		{
			return;
		}
		InfoProxyCommonList* proxy = (InfoProxyCommonList*)module->GetInfoProxyById(id);
		if (proxy == null)
		{
			return;
		}
		ReadOnlySpan<InfoProxyCommonList.CharacterData> charDataSpan = proxy->CharDataSpan;
		for (int i = 0; i < charDataSpan.Length; i++)
		{
			ref readonly InfoProxyCommonList.CharacterData data = ref charDataSpan[i];
			ulong contentId = data.ContentId;
			uint worldId = data.HomeWorld;
			if (contentId != 0L && worldId != 0)
			{
				captured.Add(new CapturedPlayer(contentId, data.NameString, worldId, worldId, 0, 0f, 0f, 0f, data.Job, 0, string.IsNullOrEmpty(data.FCTagString) ? null : data.FCTagString, null, "social", data.AccountId, 0, 0, null, 0uL, 0uL, WorldName(worldId), 0, 0, 0));
			}
		}
	}

	private unsafe void CollectSearchProxy(List<CapturedPlayer> searched)
	{
		InfoModule* module = InfoModule.Instance();
		if (module == null)
		{
			return;
		}
		InfoProxyCommonList* proxy = (InfoProxyCommonList*)module->GetInfoProxyById(InfoProxyId.PlayerSearch);
		if (proxy == null)
		{
			return;
		}
		ReadOnlySpan<InfoProxyCommonList.CharacterData> charDataSpan = proxy->CharDataSpan;
		for (int i = 0; i < charDataSpan.Length; i++)
		{
			ref readonly InfoProxyCommonList.CharacterData data = ref charDataSpan[i];
			CapturedPlayer mapped = SearchCapture.TryMap(data.ContentId, data.NameString, data.HomeWorld, data.CurrentWorld, data.Location, data.Job, (byte)data.GrandCompany, data.FCTagString, (data.HomeWorld > 0) ? WorldName(data.HomeWorld) : null);
			if ((object)mapped != null)
			{
				searched.Add(mapped);
			}
		}
	}

	private unsafe static ulong ReadLocalContentId(IPlayerCharacter local)
	{
		return ((BattleChara*)local.Address)->ContentId;
	}
}
