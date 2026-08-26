using System;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Dalamud.Bindings.ImGui;

namespace QoLBar.Conditions;

public static class MiscConditionHelpers
{
    public const string TimespanRegex = @"^([0-9Xx]{1,2}:[0-9Xx]{2})\s*-\s*([0-9Xx]{1,2}:[0-9Xx]{2})$";

    private static (double, double, double, double) ParseTime(string str) => str.Length switch
    {
        4 => (0, char.GetNumericValue(str[0]), char.GetNumericValue(str[2]), char.GetNumericValue(str[3])),
        5 => (char.GetNumericValue(str[0]), char.GetNumericValue(str[1]), char.GetNumericValue(str[3]), char.GetNumericValue(str[4])),
        _ => (0, 0, 0, 0)
    };

    public static bool IsTimeBetween(string tStr, string minStr, string maxStr)
    {
        var t = ParseTime(tStr);
        var min = ParseTime(minStr);
        var max = ParseTime(maxStr);

        var minTime = 0.0;
        var maxTime = 0.0;
        var curTime = 0.0;
        if (min.Item1 >= 0 && max.Item1 >= 0)
        {
            minTime += min.Item1 * 1000;
            maxTime += max.Item1 * 1000;
            curTime += t.Item1 * 1000;
        }
        if (min.Item2 >= 0 && max.Item2 >= 0)
        {
            minTime += min.Item2 * 100;
            maxTime += max.Item2 * 100;
            curTime += t.Item2 * 100;
        }
        if (min.Item3 >= 0 && max.Item3 >= 0)
        {
            minTime += min.Item3 * 10;
            maxTime += max.Item3 * 10;
            curTime += t.Item3 * 10;
        }
        if (min.Item4 >= 0 && max.Item4 >= 0)
        {
            minTime += min.Item4;
            maxTime += max.Item4;
            curTime += t.Item4;
        }
        return (minTime < maxTime) ? (minTime <= curTime && curTime < maxTime) : (minTime <= curTime || curTime < maxTime);
    }

    public static void DrawTimespanInput(CndCfg cndCfg)
    {
        string timespan = cndCfg.Arg is string ? cndCfg.Arg : string.Empty;
        var reg = Regex.Match(timespan, TimespanRegex);
        if (ImGui.InputText("##Timespan", ref timespan, 16))
        {
            cndCfg.Arg = timespan;
            QoLBar.Config.Save();
        }
        if (!reg.Success)
            ImGui.GetWindowDrawList().AddRectFilled(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), 0x200000FF, 5f);

        if (!ImGui.IsItemHovered()) return;

        var regexInfo = "Failed regex!".Loc();
        if (reg.Success)
        {
            var min = ParseTime(reg.Groups[1].Value);
            var max = ParseTime(reg.Groups[2].Value);
            var use1 = min.Item1 >= 0 && max.Item1 >= 0;
            var use2 = min.Item2 >= 0 && max.Item2 >= 0;
            var use3 = min.Item3 >= 0 && max.Item3 >= 0;
            var use4 = min.Item4 >= 0 && max.Item4 >= 0;
            var minStr = $"{(use1 ? min.Item1.ToString() : "X")}{(use2 ? min.Item2.ToString() : "X")}:{(use3 ? min.Item3.ToString() : "X")}{(use4 ? min.Item4.ToString() : "X")}";
            var maxStr = $"{(use1 ? max.Item1.ToString() : "X")}{(use2 ? max.Item2.ToString() : "X")}:{(use3 ? max.Item3.ToString() : "X")}{(use4 ? max.Item4.ToString() : "X")}";
            regexInfo = "Minimum: ??\nMaximum: ??".Loc(minStr, maxStr) + " " + (minStr == maxStr ? "\nWarning: this will always be true!".Loc() : string.Empty);
        }

        ImGui.SetTooltip(("Timespan should be formatted as \"XX:XX-XX:XX\" (24h) and may contain \"X\" wildcards.\n" +
                         "I.e \"XX:30-XX:10\" will return true for times such as 01:30, 13:54, and 21:09.\n" +
                         "The minimum time is inclusive, but the maximum is not.\n\n").Loc() +
                         regexInfo);
    }

    public static unsafe void DrawAddonInput(CndCfg cndCfg)
    {
        string addon = cndCfg.Arg is string ? cndCfg.Arg : string.Empty;
        var focusedAddon = Game.GetFocusedAddon();
        var addonName = focusedAddon != null ? focusedAddon->NameString : string.Empty;
        if (ImGui.InputTextWithHint("##UIName", addonName, ref addon, 32))
        {
            cndCfg.Arg = addon;
            QoLBar.Config.Save();
        }

        if (!ImGui.IsItemHovered()) return;

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Right) && !string.IsNullOrEmpty(addonName))
        {
            cndCfg.Arg = addonName;
            QoLBar.Config.Save();
        }

        ImGui.SetTooltip(("See \"/xldata ai\" to find the names of various windows.\n" +
                         "Right click to set this to the currently focused UI addon's name.").Loc());
    }
}

[AttributeUsage(AttributeTargets.Class)]
public class MiscConditionAttribute : Attribute, IConditionCategory
{
    public string CategoryName => "Misc".Loc();
    public int DisplayPriority => 100;
}

[MiscCondition]
public class LoggedInCondition : ICondition
{
    public string ID => "l";
    public string ConditionName => "Is Logged In".Loc();
    public int DisplayPriority => 0;
    public bool Check(dynamic arg) => DalamudApi.ClientState.IsLoggedIn;
}

[MiscCondition]
public class CharacterCondition : ICondition, IDrawableCondition, IArgCondition, IOnImportCondition
{
    public string ID => "c";
    public string ConditionName => "Character ID".Loc();
    public int DisplayPriority => 0;
    public bool Check(dynamic arg) => (ulong)arg == DalamudApi.PlayerState.ContentId;
    public string GetTooltip(CndCfg cndCfg) => $"ID: {cndCfg.Arg}";
    public string GetSelectableTooltip(CndCfg cndCfg) => "Selecting this will assign the current character's ID to this condition.".Loc();
    public void Draw(CndCfg cndCfg)
    {
        if (cndCfg.Arg != 0)
        {
            if (ImGui.Button("Clear Data".Loc()))
                cndCfg.Arg = 0;
        }
        else
        {
            if (ImGui.Button("Assign Data".Loc()))
                cndCfg.Arg = GetDefaultArg(cndCfg);
        }

        ImGuiEx.SetItemTooltip("If this condition has no data when imported,\nit will automatically be assigned.".Loc());
    }
    public dynamic GetDefaultArg(CndCfg cndCfg) => DalamudApi.PlayerState.ContentId;
    public void OnImport(CndCfg cndCfg)
    {
        if (cndCfg.Arg == 0)
            cndCfg.Arg = GetDefaultArg(cndCfg);
    }
}

[MiscCondition]
public class TargetCondition : ICondition, IDrawableCondition, IArgCondition
{
    public string ID => "t";
    public string ConditionName => "Target Exists".Loc();
    public int DisplayPriority => 0;
    public bool Check(dynamic arg)
    {
        return (int)arg switch
        {
            0 => DalamudApi.TargetManager.Target != null,
            1 => DalamudApi.TargetManager.FocusTarget != null,
            2 => DalamudApi.TargetManager.SoftTarget != null,
            _ => false
        };
    }
    public string GetTooltip(CndCfg cndCfg) => null;
    public string GetSelectableTooltip(CndCfg cndCfg) => null;
    public void Draw(CndCfg cndCfg)
    {
        var _ = (int)cndCfg.Arg;
        if (ImGui.Combo("##TargetType", ref _, "Target".Loc() + "\0" + "Focus Target".Loc() + "\0" + "Soft Target".Loc() + "\0"))
            cndCfg.Arg = _;
    }
    public dynamic GetDefaultArg(CndCfg cndCfg) => 0;
}

[MiscCondition]
public class WeaponDrawnCondition : ICondition
{
    public string ID => "wd";
    public string ConditionName => "Weapon Drawn".Loc();
    public int DisplayPriority => 0;
    public bool Check(dynamic arg) => DalamudApi.ObjectTable.LocalPlayer is { } player && (player.StatusFlags & StatusFlags.WeaponOut) != 0;
}

[MiscCondition]
public class EorzeaTimespanCondition : ICondition, IDrawableCondition, IArgCondition
{
    public string ID => "et";
    public string ConditionName => "Eorzea Timespan".Loc();
    public int DisplayPriority => 0;
    private static bool CheckEorzeaTimeCondition(string arg)
    {
        var reg = Regex.Match(arg, MiscConditionHelpers.TimespanRegex);
        // Game.EorzeaTime 取不到（Framework 尚未就緒）時回 null ⇒ 條件不成立。
        // 讀不到時間就不讓時段條件成立，方向與其他條件的「讀不到 = false」一致。
        return reg.Success
            && Game.EorzeaTime is { } eorzeaTime
            && MiscConditionHelpers.IsTimeBetween(eorzeaTime.ToString("HH:mm"), reg.Groups[1].Value, reg.Groups[2].Value);
    }
    public bool Check(dynamic arg) => arg is string range && CheckEorzeaTimeCondition(range);
    public string GetTooltip(CndCfg cndCfg) => null;
    public string GetSelectableTooltip(CndCfg cndCfg) => null;
    public void Draw(CndCfg cndCfg) => MiscConditionHelpers.DrawTimespanInput(cndCfg);
    public dynamic GetDefaultArg(CndCfg cndCfg) => cndCfg.Arg is string ? cndCfg.Arg : string.Empty;
}

[MiscCondition]
public class LocalTimespanCondition : ICondition, IDrawableCondition, IArgCondition
{
    public string ID => "lt";
    public string ConditionName => "Local Timespan".Loc();
    public int DisplayPriority => 0;
    private static bool CheckLocalTimeCondition(string arg)
    {
        var reg = Regex.Match(arg, MiscConditionHelpers.TimespanRegex);
        return reg.Success && MiscConditionHelpers.IsTimeBetween(DateTime.Now.ToString("HH:mm"), reg.Groups[1].Value, reg.Groups[2].Value);
    }
    public bool Check(dynamic arg) => arg is string range && CheckLocalTimeCondition(range);
    public string GetTooltip(CndCfg cndCfg) => null;
    public string GetSelectableTooltip(CndCfg cndCfg) => null;
    public void Draw(CndCfg cndCfg) => MiscConditionHelpers.DrawTimespanInput(cndCfg);
    public dynamic GetDefaultArg(CndCfg cndCfg) => cndCfg.Arg is string ? cndCfg.Arg : string.Empty;
}

[MiscCondition]
public class HUDLayoutCondition : ICondition, IDrawableCondition, IArgCondition
{
    public string ID => "hl";
    public string ConditionName => "Current HUD Layout".Loc();
    public int DisplayPriority => 0;
    public bool Check(dynamic arg) => (byte)arg == Game.CurrentHUDLayout;
    public string GetTooltip(CndCfg cndCfg) => null;
    public string GetSelectableTooltip(CndCfg cndCfg) => null;
    public void Draw(CndCfg cndCfg)
    {
        var _ = (int)cndCfg.Arg + 1;
        if (ImGui.SliderInt("##HUDLayout", ref _, 1, 4))
            cndCfg.Arg = _ - 1;
    }
    // 取不到目前配置時 CurrentHUDLayout 是 -1（哨兵值）。Check 拿到 -1 會落在
    // 「不成立」是對的，但這裡是要寫進使用者設定的預設值，夾回 0 才不會存進哨兵值。
    public dynamic GetDefaultArg(CndCfg cndCfg) => Math.Max(0, Game.CurrentHUDLayout);
}

[MiscCondition]
public class KeyHeldCondition : ICondition, IDrawableCondition
{
    public string ID => "k";
    public string ConditionName => "Key Held".Loc();
    public int DisplayPriority => 0;
    public bool Check(dynamic arg) => Keybind.IsHotkeyHeld((int)arg, false);
    public string GetTooltip(CndCfg cndCfg) => null;
    public string GetSelectableTooltip(CndCfg cndCfg) => null;
    public void Draw(CndCfg cndCfg)
    {
        var _ = (int)cndCfg.Arg;
        if (Keybind.InputHotkey("##KeyHeldCondition", ref _))
            cndCfg.Arg = _;
        ImGuiEx.SetItemTooltip("Press escape to clear the hotkey.".Loc());
    }
}

[MiscCondition]
public class PartyCondition : ICondition, IDrawableCondition, IArgCondition
{
    public string ID => "pt";
    public string ConditionName => "# Party Member Exists".Loc();
    public int DisplayPriority => 0;
    // PronounModule.Instance() 是手寫包裝（UIModule 為 null 時回 null），未登入／登出瞬間會是 null。
    public unsafe bool Check(dynamic arg)
    {
        var pronounModule = PronounModule.Instance();
        return pronounModule != null && pronounModule->ResolvePlaceholder($"<{arg}>", 0, 0) != null;
    }
    public string GetTooltip(CndCfg cndCfg) => "This will only return true if the party member exists in the current area.".Loc();
    public string GetSelectableTooltip(CndCfg cndCfg) => null;
    public void Draw(CndCfg cndCfg)
    {
        var _ = (int)cndCfg.Arg;
        if (ImGui.SliderInt("##MemberCount", ref _, 2, 8))
            cndCfg.Arg = _;
    }
    public dynamic GetDefaultArg(CndCfg cndCfg) => 2;
}

[MiscCondition]
public class PetCondition : ICondition
{
    public string ID => "pe";
    public string ConditionName => "Pet Exists".Loc();
    public int DisplayPriority => 0;
    public unsafe bool Check(dynamic arg)
    {
        var pronounModule = PronounModule.Instance();
        return pronounModule != null && pronounModule->ResolvePlaceholder("<pet>", 0, 0) != null;
    }
}

[MiscCondition]
public class ChocoboCondition : ICondition
{
    public string ID => "ce";
    public string ConditionName => "Chocobo Exists".Loc();
    public int DisplayPriority => 0;
    public unsafe bool Check(dynamic arg)
    {
        var pronounModule = PronounModule.Instance();
        return pronounModule != null && pronounModule->ResolvePlaceholder("<c>", 0, 0) != null;
    }
}

[MiscCondition]
public class SanctuaryCondition : ICondition, IDrawableCondition
{
    public string ID => "is";
    public string ConditionName => "In Sanctuary".Loc();
    public int DisplayPriority => 0;
    public unsafe bool Check(dynamic arg) => FFXIVClientStructs.FFXIV.Client.Game.UI.TerritoryInfo.Instance()->InSanctuary;
    public string GetTooltip(CndCfg cndCfg) => null;
    public string GetSelectableTooltip(CndCfg cndCfg) => "This refers to areas that accumulate rested experience.".Loc();
    public void Draw(CndCfg cndCfg) { }
}

[MiscCondition]
public class ExplorerModeCondition : ICondition
{
    public string ID => "em";
    public string ConditionName => "In Explorer Mode".Loc();
    public int DisplayPriority => 0;
    public bool Check(dynamic arg) => Game.IsInExplorerMode;
}

[MiscCondition]
public class AddonExistsCondition : ICondition, IDrawableCondition, IArgCondition
{
    public string ID => "ae";
    public string ConditionName => "Addon Exists".Loc();
    public int DisplayPriority => 100;
    public unsafe bool Check(dynamic arg) => arg is string addon && Game.GetAddonStructByName(addon, 1) != null;
    public string GetTooltip(CndCfg cndCfg) => null;
    public string GetSelectableTooltip(CndCfg cndCfg) => "Advanced condition.".Loc();
    public void Draw(CndCfg cndCfg) => MiscConditionHelpers.DrawAddonInput(cndCfg);
    public dynamic GetDefaultArg(CndCfg cndCfg) => cndCfg.Arg is string ? cndCfg.Arg : string.Empty;
}

[MiscCondition]
public class AddonVisibleCondition : ICondition, IDrawableCondition, IArgCondition
{
    public string ID => "av";
    public string ConditionName => "Addon Visible".Loc();
    public int DisplayPriority => 101;
    public unsafe bool Check(dynamic arg) => arg is string addon && Game.GetAddonStructByName(addon, 1) is var atkBase && atkBase != null && atkBase->IsVisible;
    public string GetTooltip(CndCfg cndCfg) => null;
    public string GetSelectableTooltip(CndCfg cndCfg) => "Advanced condition.".Loc();
    public void Draw(CndCfg cndCfg) => MiscConditionHelpers.DrawAddonInput(cndCfg);
    public dynamic GetDefaultArg(CndCfg cndCfg) => cndCfg.Arg is string ? cndCfg.Arg : string.Empty;
}

[MiscCondition]
public class PluginCondition : ICondition, IDrawableCondition, IArgCondition
{
    public string ID => "p";
    public string ConditionName => "Plugin Enabled".Loc();
    public int DisplayPriority => 102;
    public bool Check(dynamic arg) => arg is string plugin && QoLBar.HasPlugin(plugin);
    public string GetTooltip(CndCfg cndCfg) => null;
    public string GetSelectableTooltip(CndCfg cndCfg) => null;

    public void Draw(CndCfg cndCfg)
    {
        if (ImGui.BeginCombo("##PluginsList", cndCfg.Arg is string ? cndCfg.Arg : string.Empty))
        {
            var i = 0;
            foreach (var plugin in DalamudApi.PluginInterface.InstalledPlugins)
            {
                var name = plugin.InternalName;
                if (!ImGui.Selectable($"{name}##{i++}", cndCfg.Arg == name)) continue;

                cndCfg.Arg = name;
                QoLBar.Config.Save();
            }

            ImGui.EndCombo();
        }

        if (cndCfg.Arg is not "QoLBar") return;

        ImGui.PushFont(UiBuilder.IconFont);
        ImGuiEx.SetItemTooltip(FontAwesomeIcon.Poo.ToIconString());
        ImGui.PopFont();
    }
    public dynamic GetDefaultArg(CndCfg cndCfg) => cndCfg.Arg is string ? cndCfg.Arg : string.Empty;
}