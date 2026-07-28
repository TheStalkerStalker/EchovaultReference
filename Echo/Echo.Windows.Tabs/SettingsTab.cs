using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Echo.Core;
using Echo.Wire;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace Echo.Windows.Tabs;

public sealed class SettingsTab
{
	private readonly PluginState _state;

	private readonly Func<EchoApiClient?> _client;

	private readonly IObjectTable _objectTable;

	private readonly IPluginLog _log;

	private readonly Action _saveSettings;

	private string _lodestoneId = "";

	private volatile string _verifyCode = "";

	private volatile string _verifyStatus = "";

	private volatile bool _verifyBusy;

	private volatile string _claimCode = "";

	private long _claimCodeExpiresAtTicks;

	private volatile string _claimStatus = "";

	private volatile bool _claimBusy;

	public SettingsTab(PluginState state, Func<EchoApiClient?> client, IObjectTable objectTable, IPluginLog log, Action saveSettings)
	{
		_state = state;
		_client = client;
		_objectTable = objectTable;
		_log = log;
		_saveSettings = saveSettings;
	}

	public void Draw()
	{
		PluginStateSnapshot snap = _state.Snapshot();
		ImGui.TextColored(in UiHelpers.Blue, "Capture");
		bool capture = snap.CaptureEnabled;
		if (ImGui.Checkbox("Enable capture", ref capture))
		{
			_state.SetCaptureEnabled(capture);
			_saveSettings();
		}
		bool social = snap.SocialCaptureEnabled;
		if (ImGui.Checkbox("Capture party/FC/friend lists", ref social))
		{
			_state.SetSocialCaptureEnabled(social);
			_saveSettings();
		}
		bool nameCache = snap.NameCacheCaptureEnabled;
		if (ImGui.Checkbox("Capture Party Finder & lookups", ref nameCache))
		{
			_state.SetNameCacheCaptureEnabled(nameCache);
			_saveSettings();
		}
		bool search = snap.SearchCaptureEnabled;
		if (ImGui.Checkbox("Capture Player Search results", ref search))
		{
			_state.SetSearchCaptureEnabled(search);
			_saveSettings();
		}
		bool autoSearch = snap.AutoSearchEnabled;
		if (ImGui.Checkbox("Auto-sweep Player Search (advanced)", ref autoSearch))
		{
			_state.SetAutoSearchEnabled(autoSearch);
			_saveSettings();
		}
		ImGui.SameLine();
		UiHelpers.HelpMarker("Off by default. When on, Echo runs Player Searches for you while you're idle - cycling jobs to find more characters across your world. Because it sends searches to the game server automatically, there's some account risk, so it stays opt-in. Turn it on if you're comfortable with that.");
		bool ctxLink = snap.ContextMenuLinkEnabled;
		if (ImGui.Checkbox("Show 'View on EchoVault' when right-clicking players", ref ctxLink))
		{
			_state.SetContextMenuLinkEnabled(ctxLink);
			_saveSettings();
		}
		ImGui.Separator();
		ImGui.TextColored(in UiHelpers.Blue, "Overlay");
		bool overlay = snap.OverlayEnabled;
		if (ImGui.Checkbox("Show session overlay", ref overlay))
		{
			_state.SetOverlayEnabled(overlay);
			_saveSettings();
		}
		bool clickThrough = snap.OverlayClickThrough;
		if (ImGui.Checkbox("Click-through", ref clickThrough))
		{
			_state.SetOverlayClickThrough(clickThrough);
			_saveSettings();
		}
		ImGui.SameLine();
		UiHelpers.HelpMarker("When on, the overlay ignores the mouse entirely - clicks pass through to the game. Turn off to drag it or right-click it.");
		bool locked = snap.OverlayLocked;
		if (ImGui.Checkbox("Lock overlay position", ref locked))
		{
			_state.SetOverlayLocked(locked);
			_saveSettings();
		}
		ImGui.Separator();
		IPlayerCharacter local = _objectTable.LocalPlayer;
		string who = ((local == null) ? "" : (" - " + local.Name.TextValue));
		ImU8String text = new ImU8String(7, 1);
		text.AppendLiteral("Account");
		text.AppendFormatted(who);
		ImGui.TextColored(in UiHelpers.Blue, text);
		if (snap.Registration == RegistrationStatus.Verified)
		{
			ImGui.TextColored(in UiHelpers.Green, "Verified - your uploads go live immediately.");
		}
		else
		{
			ImGui.TextUnformatted("Verify your character (optional)");
			ImGui.SameLine();
			UiHelpers.HelpMarker("Verification links your character's Lodestone profile to prove ownership. Verified uploads go live immediately instead of waiting for corroboration. Free-trial characters have no Lodestone profile and cannot verify. Your ID is the number in your Lodestone page URL: na.finalfantasyxiv.com/lodestone/character/<YOUR ID>/");
			ImGui.InputText("Lodestone character ID", ref _lodestoneId, 20);
			if (_verifyBusy)
			{
				ImGui.TextUnformatted("Working...");
			}
			else
			{
				if (ImGui.Button("1. Get verification code"))
				{
					StartVerifyAsync();
				}
				if (_verifyCode.Length > 0)
				{
					ImU8String text2 = new ImU8String(39, 1);
					text2.AppendLiteral("Code: ");
					text2.AppendFormatted(_verifyCode);
					text2.AppendLiteral("  (paste into your Lodestone bio)");
					ImGui.TextUnformatted(text2);
					if (ImGui.Button("2. I saved it - verify now"))
					{
						CompleteVerifyAsync();
					}
				}
			}
			if (_verifyStatus.Length > 0)
			{
				ImGui.TextUnformatted(_verifyStatus);
			}
		}
		ImGui.Separator();
		DrawClaimSection(snap);
	}

	private void DrawClaimSection(PluginStateSnapshot snap)
	{
		ImGui.TextUnformatted("Claim this character on echovault.gg");
		ImGui.SameLine();
		UiHelpers.HelpMarker("Minting a code claims this character on echovault.gg so you can manage its privacy settings. Enter the code at echovault.gg/me within 10 minutes. Works for free-trial characters too. Minting a new code replaces any earlier one.");
		if (snap.Registration == RegistrationStatus.Verified)
		{
			ImGui.TextUnformatted("Lodestone-verified characters get full ownership on the site automatically.");
		}
		if (_claimBusy)
		{
			ImGui.TextUnformatted("Working...");
		}
		else if (ImGui.Button("Get claim code"))
		{
			StartClaimAsync();
		}
		DateTimeOffset expiresAt = new DateTimeOffset(Interlocked.Read(in _claimCodeExpiresAtTicks), TimeSpan.Zero);
		if (_claimCode.Length > 0 && expiresAt > DateTimeOffset.UtcNow)
		{
			string code = _claimCode;
			ImGui.SetNextItemWidth(120f);
			ImGui.InputText("##echoClaimCode", ref code, 16, ImGuiInputTextFlags.ReadOnly);
			ImGui.SameLine();
			if (ImGui.Button("Copy"))
			{
				ImGui.SetClipboardText(_claimCode);
			}
			TimeSpan left = expiresAt - DateTimeOffset.UtcNow;
			ImU8String text = new ImU8String(42, 1);
			text.AppendLiteral("Enter it at echovault.gg/me - expires in ");
			text.AppendFormatted(left, "m\\:ss");
			text.AppendLiteral(".");
			ImGui.TextUnformatted(text);
		}
		if (_claimStatus.Length > 0)
		{
			ImGui.TextUnformatted(_claimStatus);
		}
	}

	private async Task StartClaimAsync()
	{
		EchoApiClient client = _client();
		if (client == null)
		{
			_claimStatus = "Echo is not active in this window yet - try again in a moment.";
			return;
		}
		_claimBusy = true;
		try
		{
			IPlayerCharacter local = _objectTable.LocalPlayer;
			if (local == null)
			{
				_claimStatus = "Log in to a character first.";
				return;
			}
			ulong contentId = ReadContentId(local);
			string name = local.Name.TextValue;
			uint world = local.HomeWorld.RowId;
			if (contentId == 0L)
			{
				_claimStatus = "Could not read the character id - try again in a moment.";
				return;
			}
			LinkStartResult result = await client.LinkStartAsync(new LinkStartRequest(2, contentId, name, world), CancellationToken.None);
			LinkStartResponse resp = result.Response;
			if ((object)resp != null)
			{
				_claimCode = resp.Code;
				Interlocked.Exchange(ref _claimCodeExpiresAtTicks, resp.ExpiresAt.UtcTicks);
				_claimStatus = "";
			}
			else
			{
				_claimCode = "";
				_claimStatus = Capitalize(LinkClaimMessages.Describe(result.Error));
			}
		}
		catch (Exception exception)
		{
			_log.Error(exception, "claim code mint failed");
			_claimStatus = "Error getting a claim code.";
		}
		finally
		{
			_claimBusy = false;
		}
	}

	private static string Capitalize(string s)
	{
		if (s.Length <= 0)
		{
			return s;
		}
		return char.ToUpperInvariant(s[0]) + s.Substring(1);
	}

	private unsafe static ulong ReadContentId(IPlayerCharacter local)
	{
		return ((BattleChara*)local.Address)->ContentId;
	}

	private async Task StartVerifyAsync()
	{
		EchoApiClient client = _client();
		if (client == null)
		{
			_verifyStatus = "Echo is not active in this window yet - try again in a moment.";
			return;
		}
		_verifyBusy = true;
		try
		{
			IPlayerCharacter local = _objectTable.LocalPlayer;
			if (local == null)
			{
				_verifyStatus = "Log in to a character first.";
				return;
			}
			ulong localContentId = ReadContentId(local);
			if (localContentId == 0L)
			{
				_verifyStatus = "Log in to a character first.";
				return;
			}
			string name = local.Name.TextValue;
			string homeWorldName = local.HomeWorld.Value.Name.ExtractText();
			string lodestoneId = _lodestoneId.Trim();
			if (lodestoneId.Length == 0 || !lodestoneId.All(char.IsAsciiDigit))
			{
				_verifyStatus = "Enter your numeric Lodestone character ID first (the number in your Lodestone profile URL). Digits only.";
				return;
			}
			VerifyStartResult result = await client.VerifyStartAsync(new VerifyStartRequest(2, lodestoneId, name, homeWorldName, localContentId), CancellationToken.None);
			VerifyStartResponse response = result.Response;
			if ((object)response == null)
			{
				_verifyStatus = VerifyMessages.DescribeStartError(result.Error);
				return;
			}
			_verifyCode = response.Code;
			_verifyStatus = "";
		}
		catch (Exception exception)
		{
			_log.Error(exception, "verify start failed");
			_verifyStatus = "Error starting verification.";
		}
		finally
		{
			_verifyBusy = false;
		}
	}

	private async Task CompleteVerifyAsync()
	{
		EchoApiClient client = _client();
		if (client == null)
		{
			_verifyStatus = "Echo is not active in this window yet - try again in a moment.";
			return;
		}
		_verifyBusy = true;
		try
		{
			VerifyCompleteResponse response = await client.VerifyCompleteAsync(CancellationToken.None);
			if ((object)response != null && response.Verified)
			{
				_state.SetRegistration(RegistrationStatus.Verified);
				_verifyStatus = "Verified! Your uploads go live immediately now.";
				_verifyCode = "";
			}
			else
			{
				_verifyStatus = "Not verified: " + VerifyMessages.DescribeReason(response?.Reason);
			}
		}
		catch (Exception exception)
		{
			_log.Error(exception, "verify complete failed");
			_verifyStatus = "Error completing verification.";
		}
		finally
		{
			_verifyBusy = false;
		}
	}
}
