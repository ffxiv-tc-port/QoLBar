using System;
using System.Numerics;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using ImGuiNET;
using Dalamud.Interface.Utility;

namespace QoLBar;

public static class IconBrowserUI
{
    public static bool iconBrowserOpen = false;
    public static bool doPasteIcon = false;
    public static int pasteIcon = 0;

    private static bool _tabExists = false;
    private static int _i, _columns;
    private static string _name;
    private static float _iconSize;
    private static string _tooltip;
    private static bool _useLowQuality = false;
    private static List<(int, int)> _iconList;
    private static bool _displayOutsideMain = true;

    private const int iconMax = 350_000;

    // Null while the cache hasn't been built yet (or is being rebuilt from scratch), only ever
    // reassigned wholesale from the framework thread (see BuildCache), so plain reads of the
    // reference from the main thread are safe without extra locking.
    private static volatile HashSet<int> _iconExistsCache;
    private static volatile bool _isBuildingCache = false;
    public static bool IsBuildingCache => _isBuildingCache;
    private static readonly Dictionary<string, List<int>> _iconCache = new();

    public static void ToggleIconBrowser() => iconBrowserOpen = !iconBrowserOpen;

    public static void Draw()
    {
        if (!ImGuiEx.SetBoolOnGameFocus(ref _displayOutsideMain)) return;

        if (!iconBrowserOpen) { doPasteIcon = false; return; }

        var iconSize = 48 * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSizeConstraints(new Vector2((iconSize + ImGui.GetStyle().ItemSpacing.X) * 11 + ImGui.GetStyle().WindowPadding.X * 2 + 8), ImGuiHelpers.MainViewport.Size); // whyyyyyyyyyyyyyyyyyyyy
        ImGui.Begin("Icon Browser".Loc(), ref iconBrowserOpen);

        ImGuiEx.ShouldDrawInViewport(out _displayOutsideMain);

        if (ImGuiEx.AddHeaderIconButton("RebuildIconCache", TextureDictionary.FrameIconID + 105, 1.0f, Vector2.Zero, 0, 0xFFFFFFFF, "nhg"))
            BuildCache(true);
        ImGuiEx.SetItemTooltip((_isBuildingCache ? "Rebuilding icon cache...".Loc() : "Rebuild Icon Cache".Loc()));

        if (ImGui.BeginTabBar("Icon Tabs", ImGuiTabBarFlags.NoTooltip))
        {
            BeginIconList(" ★ ", iconSize);
            AddIcons(0, 100, "System".Loc());
            AddIcons(62_000, 62_600, "Classes/Jobs".Loc());
            AddIcons(62_800, 62_900, "Gearsets".Loc());
            AddIcons(66_000, 66_400, "Macros".Loc());
            AddIcons(90_000, 100_000, "FC Crests/Symbols".Loc());
            AddIcons(114_000, 114_100, "New Game+".Loc());
            AddIcons(230_850, 231_000, "Classes/Jobs (GPose)".Loc());
            AddIcons(TextureDictionary.FrameIconID, TextureDictionary.FrameIconID + 3000, "Extra".Loc());
            EndIconList();

            BeginIconList("Custom".Loc(), iconSize);
            ImGuiEx.SetItemTooltip(("Place images inside \"%AppData%\\XIVLauncher\\pluginConfigs\\QoLBar\\icons\"\n" +
                                   "to load them as usable icons, the file names must be in the format \"#.img\" (# > 0).\n" +
                                   "I.e. \"1.jpg\" \"2.png\" \"3.png\" \"732487.jpg\" and so on.").Loc());
            if (_tabExists)
            {
                if (ImGui.Button("Refresh Custom Icons".Loc()))
                    QoLBar.Plugin.AddUserIcons();
                ImGui.SameLine();
                if (ImGui.Button("Open Icon Folder".Loc()))
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = QoLBar.Config.GetPluginIconPath(),
                        UseShellExecute = true
                    });
            }
            foreach (var kv in QoLBar.GetUserIcons())
                AddIcons(kv.Key, kv.Key + 1);
            _tooltip = "";
            EndIconList();

            BeginIconList("Misc".Loc(), iconSize);
            AddIcons(60_000, 61_000, "UI".Loc());
            AddIcons(61_200, 61_250, "Markers".Loc());
            AddIcons(61_290, 61_390, "Markers 2".Loc());
            AddIcons(61_390, 62_000, "UI 2".Loc());
            AddIcons(62_600, 62_620, "HQ FC Banners".Loc());
            AddIcons(63_900, 64_000, "Map Markers".Loc());
            AddIcons(64_500, 64_550, "Stamps".Loc());
            AddIcons(65_000, 65_900, "Currencies".Loc());
            AddIcons(180_000, 180_060, "Chocobo Racing".Loc());
            AddIcons(230_000, 230_850, "GPose".Loc());
            AddIcons(231_000, 240_000, "GPose 2".Loc());
            EndIconList();

            BeginIconList("Misc 2".Loc(), iconSize);
            AddIcons(62_900, 63_200, "Achievements/Hunting Log".Loc());
            AddIcons(63_875, 63_900, "Cosmic Exploration".Loc());
            AddIcons(65_900, 66_000, "Fishing".Loc());
            AddIcons(66_400, 66_500, "Tags".Loc());
            AddIcons(67_000, 68_000, "Fashion Log".Loc());
            AddIcons(70_120, 70_200, "Animals".Loc());
            AddIcons(70_500, 70_960, "Cosmic Exploration 2".Loc());
            AddIcons(70_960, 71_450, "Quests".Loc());
            AddIcons(72_000, 72_500, "BLU UI".Loc());
            AddIcons(72_500, 72_620, "Bozja UI".Loc());
            AddIcons(76_000, 76_200, "Mahjong".Loc());
            AddIcons(80_000, 80_200, "Quest Log".Loc());
            AddIcons(80_730, 81_000, "Relic Log".Loc());
            AddIcons(82_000, 82_100, "Misc UI".Loc());
            AddIcons(82_270, 82_325, "Occult Crescent UI".Loc());
            AddIcons(83_000, 84_000, "FC Ranks".Loc());
            AddIcons(180_060, 180_100, "UI Text".Loc());
            AddIcons(240_000, 241_000, "Strategy Board".Loc());
            EndIconList();

            BeginIconList("Actions".Loc(), iconSize);
            AddIcons(100, 4_000, "Classes/Jobs".Loc());
            AddIcons(5_100, 8_000, "Traits".Loc());
            AddIcons(8_000, 9_000, "Fashion".Loc());
            AddIcons(9_000, 10_000, "PvP".Loc());
            AddIcons(19_600, 19_800, "Event".Loc());
            AddIcons(19_800, 20_000, "Mount".Loc());
            AddIcons(61_250, 61_290, "Duties/Trials".Loc());
            AddIcons(64_200, 64_325, "FC".Loc());
            AddIcons(64_550, 64_600, "Occult Crescent".Loc());
            AddIcons(64_600, 64_800, "Eureka".Loc());
            AddIcons(64_800, 65_000, "NPC".Loc());
            AddIcons(70_000, 70_120, "Chocobo Racing".Loc());
            AddIcons(82_200, 82_270, "Occult Crescent 2".Loc());
            AddIcons(246_000, 250_000, "Emotes".Loc());
            EndIconList();

            BeginIconList("Mounts & Minions".Loc(), iconSize);
            AddIcons(4_000, 4_400, "Mounts".Loc());
            AddIcons(4_400, 5_100, "Minions".Loc());
            AddIcons(59_000, 59_400, "Mounts... again?".Loc());
            AddIcons(59_400, 60_000, "Minion Items".Loc());
            AddIcons(68_000, 68_400, "Mounts Log".Loc());
            AddIcons(68_400, 69_000, "Minions Log".Loc());
            EndIconList();

            BeginIconList("Items".Loc(), iconSize);
            AddIcons(20_000, 30_000, "General".Loc());
            AddIcons(50_000, 54_000, "Housing".Loc());
            AddIcons(58_000, 59_000, "Fashion".Loc());
            EndIconList();

            BeginIconList("Equipment".Loc(), iconSize);
            AddIcons(30_000, 50_000, "Equipment".Loc());
            AddIcons(54_000, 54_225, "Belts".Loc());
            AddIcons(54_225, 54_400, "Flowers".Loc());
            AddIcons(54_400, 58_000, "Special Equipment".Loc());
            AddIcons(200_000, 210_000, "Glasses".Loc());
            EndIconList();

            BeginIconList("Aesthetics".Loc(), iconSize);
            AddIcons(130_000, 142_000);
            AddIcons(250_000, 251_001);
            EndIconList();

            BeginIconList("Statuses".Loc(), iconSize);
            AddIcons(210_000, 230_000);
            EndIconList();

            BeginIconList("Garbage".Loc(), iconSize, true);
            AddIcons(61_000, 61_100, "Splash Logos".Loc());
            AddIcons(62_620, 62_800, "World Map".Loc());
            AddIcons(63_200, 63_875, "Zone Maps".Loc());
            AddIcons(66_500, 67_000, "Gardening Log".Loc());
            AddIcons(69_000, 70_000, "Mount/Minion Footprints".Loc());
            AddIcons(70_200, 70_500, "DoH/DoL Logs".Loc());
            AddIcons(71_450, 71_500, "Credits".Loc());
            AddIcons(78_000, 80_000, "Fishing Log".Loc());
            AddIcons(80_200, 80_730, "Notebooks".Loc());
            AddIcons(81_000, 82_000, "Notebooks 2".Loc());
            AddIcons(82_100, 82_200, "Housing".Loc());
            AddIcons(84_000, 85_000, "Hunts".Loc());
            AddIcons(85_000, 87_000, "Large UI".Loc());
            AddIcons(150_000, 170_000, "Tutorials".Loc());
            AddIcons(190_000, 200_000, "Adventurer Plates".Loc());
            AddIcons(241_000, 241_200, "Cosmic Exploration Zones".Loc());
            EndIconList();

            BeginIconList("Spoilers".Loc(), iconSize, true);
            AddIcons(87_000, 90_000, "Triple Triad".Loc()); // Out of order because people might want to use these
            AddIcons(72_620, 76_000, "Duty Support".Loc());
            AddIcons(120_000, 130_000, "Popup Texts".Loc());
            AddIcons(142_000, 150_000, "Japanese Popup Texts".Loc());
            AddIcons(181_000, 181_500, "Boss Titles".Loc());
            EndIconList();

            BeginIconList("Spoilers 2".Loc(), iconSize, true);
            AddIcons(71_500, 72_000, "Credits".Loc());
            AddIcons(100_000, 114_000, "Quest Images".Loc());
            AddIcons(114_100, 120_000, "New Game+".Loc());
            EndIconList();

            BeginIconList("Unsorted Future Icons".Loc(), iconSize);
            AddIcons(10_000, 19_600);
            AddIcons(61_100, 61_200);
            AddIcons(64_000, 64_200);
            AddIcons(64_325, 64_500);
            AddIcons(76_200, 78_000);
            AddIcons(82_325, 83_000);
            //AddIcons(170_000, 180_000); // Still empty icons
            AddIcons(181_500, 190_000);
            AddIcons(241_200, 246_000);
            AddIcons(251_001, iconMax);
            EndIconList();

            ImGui.EndTabBar();
        }
        ImGui.End();

        if (iconBrowserOpen) return;
        QoLBar.CleanTextures(false);
    }

    private static bool BeginIconList(string name, float iconSize, bool useLowQuality = false)
    {
        _tooltip = "Contains:".Loc();
        if (ImGui.BeginTabItem(name))
        {
            _name = name;
            _tabExists = true;
            _i = 0;
            _columns = (int)((ImGui.GetContentRegionAvail().X - ImGui.GetStyle().WindowPadding.X) / (iconSize + ImGui.GetStyle().ItemSpacing.X)); // WHYYYYYYYYYYYYYYYYYYYYY
            _iconSize = iconSize;
            _iconList = new List<(int, int)>();

            if (useLowQuality)
                _useLowQuality = true;
        }
        else
        {
            _tabExists = false;
        }

        return _tabExists;
    }

    private static void EndIconList()
    {
        if (_tabExists)
        {
            if (!string.IsNullOrEmpty(_tooltip))
                ImGuiEx.SetItemTooltip(_tooltip);
            BuildTabCache();
            DrawIconList();
            ImGui.EndTabItem();
        }
        else if (!string.IsNullOrEmpty(_tooltip))
        {
            ImGuiEx.SetItemTooltip(_tooltip);
        }
    }

    private static void AddIcons(int start, int end, string desc = "")
    {
        _tooltip += $"\n\t{start} -> {end - 1}{(!string.IsNullOrEmpty(desc) ? ("   " + desc) : "")}";
        if (_tabExists)
            _iconList.Add((start, end));
    }

    private static void DrawIconList()
    {
        if (_columns <= 0) return;

        ImGui.BeginChild($"{_name}##IconList");

        var cache = _iconCache[_name];

        ImGuiListClipperPtr clipper;
        unsafe { clipper = new(ImGuiNative.ImGuiListClipper_ImGuiListClipper()); }
        clipper.Begin((cache.Count - 1) / _columns + 1, _iconSize + ImGui.GetStyle().ItemSpacing.Y);

        var iconSize = new Vector2(_iconSize);
        var settings = new ImGuiEx.IconSettings { size = iconSize };
        while (clipper.Step())
        {
            for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                var start = row * _columns;
                var end = Math.Min(start + _columns, cache.Count);
                for (int i = start; i < end; i++)
                {
                    var icon = cache[i];
                    ShortcutUI.DrawIcon(icon, settings, _useLowQuality ? "ln" : "n");
                    if (ImGui.IsItemClicked())
                    {
                        doPasteIcon = true;
                        pasteIcon = icon;
                        ImGui.SetClipboardText($"::{icon}");
                    }

                    if (ImGui.IsItemHovered())
                    {
                        var tex = QoLBar.TextureDictionary[icon];
                        if (!ImGui.IsMouseDown(ImGuiMouseButton.Right))
                            ImGui.SetTooltip($"{icon}");
                        else if (tex != null && tex.ImGuiHandle != nint.Zero)
                        {
                            ImGui.BeginTooltip();
                            ImGui.Image(tex.ImGuiHandle, new Vector2(700 * ImGuiHelpers.GlobalScale));
                            ImGui.EndTooltip();
                        }
                    }
                    if (_i % _columns != _columns - 1)
                        ImGui.SameLine();
                    _i++;
                }
            }
        }

        clipper.Destroy();

        ImGui.EndChild();
    }

    private static void BuildTabCache()
    {
        if (_iconCache.ContainsKey(_name)) return;
        DalamudApi.LogInfo($"Building Icon Browser cache for tab \"{_name}\"");

        var cache = _iconCache[_name] = new();
        // Snapshot the reference in case a background rebuild reassigns _iconExistsCache while
        // we're iterating (see BuildCache). While it's still null (nothing built yet), treat
        // existence as "unknown" and optimistically include everything so the browser isn't
        // just empty during the initial scan; BuildCache clears _iconCache once real data is in,
        // so this tab gets rebuilt with accurate results afterwards.
        var existsCache = _iconExistsCache;
        foreach (var (start, end) in _iconList)
        {
            for (int icon = start; icon < end; icon++)
            {
                if (existsCache == null || existsCache.Contains(icon))
                    cache.Add(icon);
            }
        }

        DalamudApi.LogInfo($"Done building tab cache! {cache.Count} icons found.");
    }

    private static void AddUserAndOverrideIcons()
    {
        foreach (var kv in QoLBar.textureDictionaryLR.GetUserIcons())
            _iconExistsCache.Add(kv.Key);

        foreach (var kv in QoLBar.textureDictionaryLR.GetTextureOverrides())
            _iconExistsCache.Add(kv.Key);
    }

    public static void BuildCache(bool rebuild)
    {
        if (_isBuildingCache) return; // A scan is already in progress, don't start another

        DalamudApi.LogInfo("Building Icon Browser cache");

        _iconCache.Clear();

        var loaded = !rebuild ? QoLBar.Config.LoadIconCache() : null;
        if (loaded is { Count: > 0 })
        {
            _iconExistsCache = loaded;
            AddUserAndOverrideIcons();
            DalamudApi.LogInfo($"Done building cache! {_iconExistsCache.Count} icons found.");
            return;
        }

        // The on-disk cache is missing/empty (fresh install) or a rebuild was explicitly
        // requested. This involves up to ~350,000 IconExists() calls (each up to 2x
        // DataManager.FileExists), so run it on a background thread instead of blocking the
        // main thread. _iconExistsCache is left null while this runs (see BuildTabCache for how
        // that's handled), and reassigned wholesale from the framework thread once done.
        _isBuildingCache = true;
        _iconExistsCache = null;

        Task.Run(() =>
        {
            var newCache = new HashSet<int>();
            for (int i = 0; i < iconMax; i++)
            {
                if (TextureDictionary.IconExists((uint)i))
                    newCache.Add(i);
            }

            newCache.Remove(125052); // Remove broken image (TextureFormat R8G8B8X8 is not supported for image conversion)

            QoLBar.Config.SaveIconCache(newCache);

            DalamudApi.Framework.RunOnFrameworkThread(() =>
            {
                _iconExistsCache = newCache;
                AddUserAndOverrideIcons();
                _iconCache.Clear(); // Tab caches built while _iconExistsCache was null were only optimistic guesses, rebuild them now
                _isBuildingCache = false;
                DalamudApi.LogInfo($"Done building cache! {_iconExistsCache.Count} icons found.");
            });
        });
    }
}