using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Echo.Windows;

public sealed class MainWindow : Window
{
	private readonly IReadOnlyList<(MainTab Tab, string Label, Action Draw)> _tabs;

	private MainTab? _pendingTab;

	public MainWindow(IReadOnlyList<(MainTab Tab, string Label, Action Draw)> tabs)
		: base("Echo v0.8.1###EchoSettings")
	{
		_tabs = tabs;
		base.Size = new Vector2(560f, 440f);
		base.SizeCondition = ImGuiCond.FirstUseEver;
	}

	public void OpenTab(MainTab tab)
	{
		_pendingTab = tab;
		base.IsOpen = true;
	}

	public override void Draw()
	{
		if (!ImGui.BeginTabBar("##echoTabs"))
		{
			return;
		}
		foreach (var tab2 in _tabs)
		{
			MainTab tab = tab2.Tab;
			string label = tab2.Label;
			Action draw = tab2.Draw;
			ImGuiTabItemFlags flags = ((_pendingTab == tab) ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
			if (ImGui.BeginTabItem(label, flags))
			{
				if (_pendingTab == tab)
				{
					_pendingTab = null;
				}
				draw();
				ImGui.EndTabItem();
			}
		}
		ImGui.EndTabBar();
	}
}
