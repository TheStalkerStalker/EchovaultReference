using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Echo.Core;
using Echo.Windows;
using Echo.Windows.Tabs;
using Echo.Wire;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace Echo;

public sealed class Plugin : IDalamudPlugin, IDisposable
{
	private const string Command = "/echovault";

	private const string ServerBaseUrl = "https://echovault.gg";

	internal const string PluginVersion = "0.8.1";

	private readonly ICommandManager _commands;

	private readonly WindowSystem _windows = new WindowSystem("Echo");

	private readonly MainWindow _mainWindow;

	private readonly OverlayWindow _overlayWindow;

	private readonly HttpClient _http;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private readonly IDalamudPluginInterface _pluginInterface;

	private readonly IChatGui _chat;

	private readonly IPluginLog _log;

	private EchoApiClient? _client;

	private readonly IFramework _framework;

	private readonly IObjectTable _objectTable;

	private readonly IClientState _clientState;

	private readonly IPartyList _partyList;

	private readonly IDataManager _dataManager;

	private readonly IPartyFinderGui _partyFinderGui;

	private readonly IContextMenu _contextMenu;

	private readonly ICondition _condition;

	private readonly string _configDir;

	private readonly SettingsStore _settingsStore;

	private readonly IdentitySupervisor _supervisor;

	private InstanceLock? _instanceLock;

	private CancellationTokenSource? _sessionCts;

	private volatile bool _disposed;

	private ObjectTableSweeper? _sweeper;

	private SocialCollector? _social;

	private NameCacheCollector? _nameCache;

	private SearchSweeper? _searchSweeper;

	private Task? _drainTask;

	private long _lastDroppedLogged;

	private DateTimeOffset _lastDroppedLogAt = DateTimeOffset.MinValue;

	internal PluginState State { get; } = new PluginState();

	internal SessionTracker Session { get; } = new SessionTracker();

	internal HealthLog Health { get; } = new HealthLog();

	internal CaptureEngine Engine { get; private set; } = new CaptureEngine();

	internal CaptureEngine SocialEngine { get; private set; } = new CaptureEngine(TimeSpan.FromMinutes(15L));

	internal CaptureEngine SearchEngine { get; private set; } = new CaptureEngine();

	internal CaptureEngine NameCacheEngine { get; private set; } = new CaptureEngine();

	internal Outbox? Outbox { get; private set; }

	public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework, IObjectTable objectTable, IClientState clientState, IPartyList partyList, IDataManager dataManager, IPartyFinderGui partyFinderGui, IContextMenu contextMenu, ICondition condition, IChatGui chatGui, IPluginLog log)
	{
		_pluginInterface = pluginInterface;
		_commands = commands;
		_chat = chatGui;
		_log = log;
		_framework = framework;
		_objectTable = objectTable;
		_clientState = clientState;
		_partyList = partyList;
		_dataManager = dataManager;
		_partyFinderGui = partyFinderGui;
		_contextMenu = contextMenu;
		_condition = condition;
		_configDir = pluginInterface.GetPluginConfigDirectory();
		_http = new HttpClient
		{
			BaseAddress = new Uri("https://echovault.gg")
		};
		_settingsStore = new SettingsStore(Path.Combine(_configDir, "settings.json"));
		PersistedSettings persisted = _settingsStore.Load();
		State.SetCaptureEnabled(persisted.CaptureEnabled);
		State.SetSocialCaptureEnabled(persisted.SocialCaptureEnabled);
		State.SetNameCacheCaptureEnabled(persisted.NameCacheCaptureEnabled);
		State.SetSearchCaptureEnabled(persisted.SearchCaptureEnabled);
		State.SetContextMenuLinkEnabled(persisted.ContextMenuLinkEnabled);
		State.SetAutoSearchEnabled(persisted.AutoSearchEnabled);
		State.SetOverlayEnabled(persisted.OverlayEnabled);
		State.SetOverlayClickThrough(persisted.OverlayClickThrough);
		State.SetOverlayLocked(persisted.OverlayLocked);
		CachedFetch<ScannerStatsResponse> statsCache = new CachedFetch<ScannerStatsResponse>(TimeSpan.FromMinutes(15L));
		DashboardTab dashboardTab = new DashboardTab(State, Session, statsCache, () => _client, objectTable, log, delegate
		{
			_mainWindow.OpenTab(MainTab.Settings);
		}, SaveSettings);
		ProgressTab progressTab = new ProgressTab(statsCache, () => _client, log);
		CoverageTab coverageTab = new CoverageTab(() => _client, objectTable, clientState, dataManager, log);
		HealthTab healthTab = new HealthTab(State, Session, Health, "0.8.1");
		SettingsTab settingsTab = new SettingsTab(State, () => _client, objectTable, log, SaveSettings);
		_mainWindow = new MainWindow(new List<(MainTab, string, Action)>
		{
			(MainTab.Dashboard, "Dashboard", dashboardTab.Draw),
			(MainTab.Progress, "Progress", progressTab.Draw),
			(MainTab.Coverage, "Coverage", coverageTab.Draw),
			(MainTab.Health, "Health", healthTab.Draw),
			(MainTab.Settings, "Settings", settingsTab.Draw)
		});
		_windows.AddWindow(_mainWindow);
		_overlayWindow = new OverlayWindow(State, Session, delegate
		{
			_mainWindow.IsOpen = true;
		});
		_windows.AddWindow(_overlayWindow);
		pluginInterface.UiBuilder.Draw += _windows.Draw;
		pluginInterface.UiBuilder.OpenConfigUi += OpenSettingsTab;
		_commands.AddHandler("/echovault", new CommandInfo(OnCommand)
		{
			HelpMessage = "Open the Echo settings window. \"/echovault link\" mints a site claim code. \"/echovault stats\" shows your private contribution stats."
		});
		_supervisor = new IdentitySupervisor(ReadLocalContentIdOrZero, TryActivate, Deactivate, () => DateTimeOffset.UtcNow);
		_framework.Update += OnSupervisorTick;
		log.Information("Echo loaded.");
	}

	private void OnSupervisorTick(IFramework framework)
	{
		if (_disposed)
		{
			return;
		}
		_supervisor.Tick();
		Session.NoteTerritory((ushort)_clientState.TerritoryType);
		if (_supervisor.Phase == IdentityPhase.LoggedOut)
		{
			PluginStateSnapshot pluginStateSnapshot = State.Snapshot();
			if (pluginStateSnapshot.Standby)
			{
				State.SetStandby(v: false);
			}
			if (!pluginStateSnapshot.AwaitingLogin)
			{
				State.SetAwaitingLogin(v: true);
			}
		}
		PluginStateSnapshot tickSnap = State.Snapshot();
		_overlayWindow.IsOpen = tickSnap.OverlayEnabled && !tickSnap.AwaitingLogin && !tickSnap.Standby;
		long dropped = tickSnap.SightingsDropped;
		if (dropped > _lastDroppedLogged && DateTimeOffset.UtcNow - _lastDroppedLogAt > TimeSpan.FromMinutes(5L))
		{
			_log.Warning("Echo outbox full: {Dropped} oldest sightings dropped this session.", dropped);
			_lastDroppedLogged = dropped;
			_lastDroppedLogAt = DateTimeOffset.UtcNow;
		}
	}

	private ulong ReadLocalContentIdOrZero()
	{
		IPlayerCharacter local = _objectTable.LocalPlayer;
		if (local != null)
		{
			return ReadContentId(local);
		}
		return 0uL;
	}

	private bool TryActivate(ulong contentId)
	{
		InstancePaths paths = new InstancePaths(_configDir, contentId);
		InstanceLock acquired = InstanceLock.TryAcquire(paths.LockPath);
		if (acquired == null)
		{
			State.SetStandby(v: true);
			State.SetAwaitingLogin(v: false);
			_log.Warning("Echo standby: another process is already running Echo as this character.");
			return false;
		}
		try
		{
			LegacyIdentityMigrator.TryAdopt(_configDir, paths);
			_instanceLock = acquired;
			KeyStore keys = new KeyStore(paths.KeysPath, new DpapiKeyProtector());
			EchoApiClient client = (_client = new EchoApiClient(_http, keys, delegate(string msg)
			{
				_log.Information("[Echo] {Message}", msg);
			}));
			PluginState state = State;
			StoredCredentials stored = keys.Load();
			RegistrationStatus registration;
			if ((object)stored != null)
			{
				bool flag;
				switch (stored.Tier)
				{
				case "standard":
				case "trusted":
				case "verified":
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				registration = ((!flag) ? RegistrationStatus.Registered : RegistrationStatus.Verified);
			}
			else
			{
				registration = RegistrationStatus.Unregistered;
			}
			state.SetRegistration(registration);
			State.SetStandby(v: false);
			State.SetAwaitingLogin(v: false);
			Activate(paths.OutboxPath, client);
			_log.Information("Echo active for this character's identity.");
			return true;
		}
		catch (Exception exception)
		{
			_sweeper?.Dispose();
			_sweeper = null;
			_social?.Dispose();
			_social = null;
			_nameCache?.Dispose();
			_nameCache = null;
			_searchSweeper?.Dispose();
			_searchSweeper = null;
			Outbox = null;
			_client = null;
			_sessionCts?.Cancel();
			_sessionCts?.Dispose();
			_sessionCts = null;
			_instanceLock = null;
			acquired.Dispose();
			_log.Error(exception, "Echo activation failed; retrying on the normal interval.");
			return false;
		}
	}

	private void Activate(string outboxPath, EchoApiClient client)
	{
		State.ResetSessionUploaded();
		Session.Start();
		Health.Reset();
		CancellationTokenSource sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
		_sessionCts = sessionCts;
		Engine = new CaptureEngine();
		SocialEngine = new CaptureEngine(TimeSpan.FromMinutes(15L));
		SearchEngine = new CaptureEngine();
		NameCacheEngine = new CaptureEngine();
		Outbox outbox = (Outbox = new Outbox(outboxPath, 52428800L));
		DrainLoop drain = new DrainLoop(outbox, client, new BackoffPolicy(), State, null, Health, Session);
		_sweeper = new ObjectTableSweeper(_framework, _objectTable, _clientState, Engine, outbox, State, _log);
		_sweeper.Enable();
		_social = new SocialCollector(_framework, _partyList, _objectTable, _dataManager, SocialEngine, SearchEngine, outbox, State, _log);
		_social.Enable();
		_nameCache = new NameCacheCollector(_framework, _partyFinderGui, _contextMenu, _objectTable, NameCacheEngine, outbox, State, _log);
		_nameCache.Enable();
		_searchSweeper = new SearchSweeper(_framework, _objectTable, _condition, State, _log);
		_searchSweeper.Enable();
		drain.OnConfig = delegate(ConfigResponse config)
		{
			TimeSpan cadence = TimeSpan.FromSeconds(config.CaptureCadenceSeconds);
			TimeSpan floor = TimeSpan.FromSeconds(config.MinEmitIntervalSeconds);
			Engine.SetCadence(cadence);
			Engine.SetFloor(floor);
			SocialEngine.SetCadence(TimeSpan.FromSeconds(config.SocialCadenceSeconds));
			SocialEngine.SetFloor(floor);
			SearchEngine.SetCadence(cadence);
			SearchEngine.SetFloor(floor);
			NameCacheEngine.SetCadence(cadence);
			NameCacheEngine.SetFloor(floor);
		};
		_drainTask = Task.Run(async delegate
		{
			await drain.RunAsync("0.8.1", sessionCts.Token);
		}, sessionCts.Token);
	}

	private void Deactivate()
	{
		_sweeper?.Dispose();
		_sweeper = null;
		_social?.Dispose();
		_social = null;
		_nameCache?.Dispose();
		_nameCache = null;
		_searchSweeper?.Dispose();
		_searchSweeper = null;
		Outbox = null;
		_client = null;
		State.SetAwaitingLogin(v: true);
		State.SetStandby(v: false);
		State.SetRegistration(RegistrationStatus.Unregistered);
		State.SetOutboxDepth(0);
		State.SetError(null);
		State.SetServerBusy(v: false);
		State.ResetSessionUploaded();
		Session.Stop();
		CancellationTokenSource cts = _sessionCts;
		Task drain = _drainTask;
		InstanceLock handle = _instanceLock;
		_sessionCts = null;
		_drainTask = null;
		_instanceLock = null;
		cts?.Cancel();
		Task.Run(async delegate
		{
			try
			{
				if (drain != null)
				{
					await drain;
				}
			}
			catch (Exception)
			{
			}
			finally
			{
				handle?.Dispose();
				cts?.Dispose();
			}
		});
	}

	private void ToggleWindow()
	{
		_mainWindow.IsOpen = !_mainWindow.IsOpen;
	}

	private void OpenSettingsTab()
	{
		_mainWindow.OpenTab(MainTab.Settings);
	}

	private void SaveSettings()
	{
		PluginStateSnapshot s = State.Snapshot();
		_settingsStore.Save(new PersistedSettings(s.CaptureEnabled, s.SocialCaptureEnabled, s.NameCacheCaptureEnabled, s.SearchCaptureEnabled, s.ContextMenuLinkEnabled, s.AutoSearchEnabled, s.OverlayEnabled, s.OverlayClickThrough, s.OverlayLocked));
	}

	private void OnCommand(string command, string args)
	{
		if (string.Equals(args.Trim(), "link", StringComparison.OrdinalIgnoreCase))
		{
			LinkAsync();
		}
		else if (string.Equals(args.Trim(), "stats", StringComparison.OrdinalIgnoreCase))
		{
			StatsAsync();
		}
		else if (args.StartsWith("appeal", StringComparison.OrdinalIgnoreCase))
		{
			string note = ((args.Length > 6) ? args.Substring(6).Trim() : null);
			AppealAsync(string.IsNullOrWhiteSpace(note) ? null : note);
		}
		else
		{
			ToggleWindow();
		}
	}

	private unsafe static ulong ReadContentId(IPlayerCharacter local)
	{
		return ((BattleChara*)local.Address)->ContentId;
	}

	private async Task LinkAsync()
	{
		try
		{
			IPlayerCharacter local = _objectTable.LocalPlayer;
			if (local == null)
			{
				_chat.Print("Echo: log in to a character first.");
				return;
			}
			ulong contentId = ReadContentId(local);
			string name = local.Name.TextValue;
			uint world = local.HomeWorld.RowId;
			if (contentId == 0L)
			{
				_chat.Print("Echo: could not read the character id - try again in a moment.");
				return;
			}
			EchoApiClient client = _client;
			if (client == null)
			{
				_chat.Print("Echo: not active in this window yet - try again in a moment.");
				return;
			}
			LinkStartResult result = await client.LinkStartAsync(new LinkStartRequest(2, contentId, name, world), _cts.Token);
			IChatGui chat = _chat;
			LinkStartResponse resp = result.Response;
			chat.Print(((object)resp != null) ? ("Echo: claim code " + resp.Code + " - enter it at echovault.gg/me within 10 minutes. The /echovault window shows it with a copy button.") : ("Echo: " + LinkClaimMessages.Describe(result.Error)));
		}
		catch (Exception)
		{
			try
			{
				_chat.Print("Echo: link failed - unexpected error.");
			}
			catch
			{
			}
		}
	}

	private async Task AppealAsync(string? note)
	{
		try
		{
			EchoApiClient client = _client;
			if (client == null)
			{
				_chat.Print("Echo: log in to a character first.");
				return;
			}
			bool ok = await client.AppealAsync(note, _cts.Token);
			_chat.Print(ok ? "Echo: appeal submitted. It will be reviewed by the operator." : "Echo: could not reach the server. Try again later.");
		}
		catch (Exception)
		{
			try
			{
				_chat.Print("Echo: could not reach the server. Try again later.");
			}
			catch
			{
			}
		}
	}

	private async Task StatsAsync()
	{
		try
		{
			PluginStateSnapshot snap = State.Snapshot();
			EchoApiClient client = _client;
			if (client == null)
			{
				_chat.Print("Echo: log in to a character first.");
				return;
			}
			ScannerStatsResponse stats = await client.GetScannerStatsAsync(_cts.Token);
			if ((object)stats == null)
			{
				_chat.Print("Echo: could not reach the server. Try again later.");
				return;
			}
			IChatGui chat = _chat;
			string obj = $"Echo: lifetime {stats.LifetimeSightings:N0} sightings, {stats.WeekSightings:N0} this week";
			ScannerBestWeek bw = stats.BestWeek;
			chat.Print(obj + (((object)bw != null) ? $" (best week {bw.Count:N0})" : "") + ".");
			_chat.Print($"Echo: {stats.CharactersObserved:N0} characters observed, {stats.CharactersContributed:N0} first added by you, {stats.TerritoriesCovered:N0} territories covered.");
			int? percentileBand = stats.PercentileBand;
			if (percentileBand.HasValue)
			{
				int band = percentileBand.GetValueOrDefault();
				if (band <= 75)
				{
					_chat.Print($"Echo: you're in the top {band}% of contributors this week.");
				}
			}
			if (!snap.Standby)
			{
				_chat.Print($"Echo: this session: {snap.SessionSightingsUploaded:N0} sightings uploaded.");
			}
		}
		catch (Exception)
		{
			try
			{
				_chat.Print("Echo: could not reach the server. Try again later.");
			}
			catch
			{
			}
		}
	}

	public void Dispose()
	{
		_disposed = true;
		_framework.Update -= OnSupervisorTick;
		_commands.RemoveHandler("/echovault");
		_pluginInterface.UiBuilder.Draw -= _windows.Draw;
		_pluginInterface.UiBuilder.OpenConfigUi -= OpenSettingsTab;
		_windows.RemoveAllWindows();
		_sweeper?.Dispose();
		_social?.Dispose();
		_nameCache?.Dispose();
		_searchSweeper?.Dispose();
		_cts.Cancel();
		try
		{
			_drainTask?.Wait(TimeSpan.FromSeconds(5L));
		}
		catch (AggregateException)
		{
		}
		_http.Dispose();
		_cts.Dispose();
		_sessionCts?.Dispose();
		_instanceLock?.Dispose();
	}
}
