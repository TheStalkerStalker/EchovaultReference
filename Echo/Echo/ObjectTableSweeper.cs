using System;
using System.Collections.Generic;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Echo.Core;
using Echo.Wire;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace Echo;

internal sealed class ObjectTableSweeper(IFramework framework, IObjectTable objectTable, IClientState clientState, CaptureEngine captureEngine, Outbox outbox, PluginState state, IPluginLog log) : IDisposable
{
	private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5L);

	private static readonly TimeSpan SpawnDiffInterval = TimeSpan.FromSeconds(1L);

	private DateTime _lastSweep = DateTime.MinValue;

	private DateTime _lastDiff = DateTime.MinValue;

	private readonly HashSet<ulong> _present = new HashSet<ulong>();

	public void Enable()
	{
		framework.Update += OnUpdate;
	}

	public void Dispose()
	{
		framework.Update -= OnUpdate;
	}

	private unsafe void OnUpdate(IFramework _)
	{
		try
		{
			DateTime now = DateTime.UtcNow;
			bool doSweep = now - _lastSweep >= SweepInterval;
			bool doDiff = now - _lastDiff >= SpawnDiffInterval;
			if (!doSweep && !doDiff)
			{
				return;
			}
			PluginStateSnapshot snap = state.Snapshot();
			if (!snap.CaptureEnabled || !snap.ServerAllowsIngest)
			{
				return;
			}
			IPlayerCharacter local = objectTable.LocalPlayer;
			if (local == null)
			{
				return;
			}
			ushort territory = (ushort)clientState.TerritoryType;
			ReporterSelf reporter = new ReporterSelf(territory, local.Position.X, local.Position.Y, local.Position.Z);
			state.SetReporter(reporter);
			BattleChara* localChara = (BattleChara*)local.Address;
			ulong localContentId = localChara->ContentId;
			HashSet<ulong> seen = new HashSet<ulong>();
			List<CapturedPlayer> sweepCaptured = (doSweep ? new List<CapturedPlayer>() : null);
			List<CapturedPlayer> spawnCaptured = (doDiff ? new List<CapturedPlayer>() : null);
			foreach (IBattleChara playerObject in objectTable.PlayerObjects)
			{
				if (!(playerObject is IPlayerCharacter pc))
				{
					continue;
				}
				BattleChara* chara = (BattleChara*)pc.Address;
				ulong contentId = chara->ContentId;
				seen.Add(contentId);
				if (contentId != localContentId)
				{
					if (doDiff && !_present.Contains(contentId))
					{
						spawnCaptured.Add(Capture(pc, chara, territory, "spawn"));
					}
					if (doSweep)
					{
						sweepCaptured.Add(Capture(pc, chara, territory, "sweep"));
					}
				}
			}
			if (doDiff)
			{
				_lastDiff = now;
				_present.Clear();
				_present.UnionWith(seen);
				Emit(captureEngine, spawnCaptured, localContentId, reporter);
			}
			if (doSweep)
			{
				_lastSweep = now;
				Emit(captureEngine, sweepCaptured, localContentId, reporter);
				state.SetOutboxDepth(outbox.Count());
			}
		}
		catch (Exception exception)
		{
			log.Verbose(exception, "Echo sweep failed");
		}
	}

	private void Emit(CaptureEngine engine, List<CapturedPlayer> captured, ulong localContentId, ReporterSelf reporter)
	{
		if (captured.Count == 0)
		{
			return;
		}
		foreach (Sighting sighting in engine.Process(captured, localContentId, DateTimeOffset.UtcNow, reporter))
		{
			outbox.Append(JsonSerializer.Serialize(sighting, WireJson.Options));
		}
	}

	private unsafe static CapturedPlayer Capture(IPlayerCharacter pc, BattleChara* chara, ushort territory, string source)
	{
		List<EquipSlot> equipment = new List<EquipSlot>(10);
		Span<EquipmentModelId> equipmentModelIds = chara->DrawData.EquipmentModelIds;
		for (int i = 0; i < equipmentModelIds.Length; i++)
		{
			ref EquipmentModelId slot = ref equipmentModelIds[i];
			equipment.Add(new EquipSlot(slot.Id, slot.Variant, slot.Stain0, slot.Stain1));
		}
		ulong contentId = chara->ContentId;
		string textValue = pc.Name.TextValue;
		uint rowId = pc.HomeWorld.RowId;
		uint rowId2 = pc.CurrentWorld.RowId;
		float x = pc.Position.X;
		float y = pc.Position.Y;
		float z = pc.Position.Z;
		byte jobId = (byte)pc.ClassJob.RowId;
		byte level = pc.Level;
		string tag = pc.CompanyTag.TextValue;
		return new CapturedPlayer(contentId, textValue, rowId, rowId2, territory, x, y, z, jobId, level, (tag != null && tag.Length > 0) ? tag : null, (pc.Customize.Length > 0) ? pc.Customize.ToArray() : null, source, chara->AccountId, chara->TitleId, chara->Battalion, equipment, chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId.Value, chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.OffHand).ModelId.Value, pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty, (ushort)(pc.CurrentMount?.RowId ?? 0), (ushort)pc.OnlineStatus.RowId, 0);
	}
}
