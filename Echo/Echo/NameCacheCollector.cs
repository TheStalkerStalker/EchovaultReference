using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Echo.Core;
using Echo.Wire;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace Echo;

internal sealed class NameCacheCollector(IFramework framework, IPartyFinderGui partyFinder, IContextMenu contextMenu, IObjectTable objectTable, CaptureEngine nameCacheEngine, Outbox outbox, PluginState state, IPluginLog log) : IDisposable
{
	private readonly ConcurrentQueue<CapturedPlayer> _queue = new ConcurrentQueue<CapturedPlayer>();

	public void Enable()
	{
		partyFinder.ReceiveListing += OnListing;
		contextMenu.OnMenuOpened += OnMenuOpened;
		framework.Update += OnUpdate;
	}

	public void Dispose()
	{
		partyFinder.ReceiveListing -= OnListing;
		contextMenu.OnMenuOpened -= OnMenuOpened;
		framework.Update -= OnUpdate;
	}

	private void OnListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
	{
		try
		{
			if (Enabled() && listing.ContentId != 0L)
			{
				_queue.Enqueue(new CapturedPlayer(listing.ContentId, listing.Name.TextValue, listing.HomeWorld.RowId, 0u, 0, 0f, 0f, 0f, 0, 0, null, null, "namecache", 0uL, 0, 0, null, 0uL, 0uL, null, 0, 0, listing.RawDuty));
			}
		}
		catch (Exception exception)
		{
			log.Verbose(exception, "Echo PF capture failed");
		}
	}

	private void OnMenuOpened(IMenuOpenedArgs args)
	{
		try
		{
			if (!(args.Target is MenuTargetDefault { TargetContentId: not 0uL } t) || t.TargetName.Length == 0)
			{
				return;
			}
			if (state.Snapshot().ContextMenuLinkEnabled)
			{
				string url = ProfileLink.For(t.TargetHomeWorld.RowId, t.TargetName);
				args.AddMenuItem(new MenuItem
				{
					Name = "View on EchoVault",
					OnClicked = delegate
					{
						Util.OpenLink(url);
					}
				});
			}
			if (Enabled())
			{
				_queue.Enqueue(new CapturedPlayer(t.TargetContentId, t.TargetName, t.TargetHomeWorld.RowId, 0u, 0, 0f, 0f, 0f, 0, 0, null, null, "namecache", 0uL, 0, 0, null, 0uL, 0uL, null, 0, 0, 0));
			}
		}
		catch (Exception exception)
		{
			log.Verbose(exception, "Echo context-menu capture failed");
		}
	}

	private bool Enabled()
	{
		PluginStateSnapshot snap = state.Snapshot();
		if (snap.CaptureEnabled && snap.NameCacheCaptureEnabled)
		{
			return snap.ServerAllowsIngest;
		}
		return false;
	}

	private void OnUpdate(IFramework _)
	{
		try
		{
			if (_queue.IsEmpty)
			{
				return;
			}
			IPlayerCharacter local = objectTable.LocalPlayer;
			if (local == null)
			{
				return;
			}
			ulong localContentId = ReadLocalContentId(local);
			List<CapturedPlayer> captured = new List<CapturedPlayer>();
			CapturedPlayer p;
			while (captured.Count < 50 && _queue.TryDequeue(out p))
			{
				captured.Add(p);
			}
			ReporterSelf reporter = state.Snapshot().Reporter;
			foreach (Sighting s in nameCacheEngine.Process(captured, localContentId, DateTimeOffset.UtcNow, reporter))
			{
				outbox.Append(JsonSerializer.Serialize(s, WireJson.Options));
			}
		}
		catch (Exception exception)
		{
			log.Verbose(exception, "Echo namecache drain failed");
		}
	}

	private unsafe static ulong ReadLocalContentId(IPlayerCharacter local)
	{
		return ((BattleChara*)local.Address)->ContentId;
	}
}
