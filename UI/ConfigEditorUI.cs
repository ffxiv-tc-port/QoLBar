using System;
using System.Numerics;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility;
using static QoLBar.BarCfg;
using static QoLBar.ShCfg;

namespace QoLBar;

// I hate this file with a passion
public static class ConfigEditorUI
{
    private static int _inputPos = 0;
    private static unsafe int GetCursorPosCallback(ImGuiInputTextCallbackData* dataPtr)
    {
        var data = new ImGuiInputTextCallbackDataPtr(dataPtr);
        _inputPos = data.CursorPos;
        return 0;
    }

    private static void DrawInsertPrivateCharPopup(ref string input)
    {
        if (ImGui.BeginPopup($"Private Use Popup##{ImGui.GetCursorPos()}"))
        {
            var temp = input;
            static void InsertString(ref string str, string ins)
            {
                var bytes = Encoding.UTF8.GetBytes(str).ToList();
                _inputPos = Math.Min(_inputPos, bytes.Count);
                var newBytes = Encoding.UTF8.GetBytes(ins);
                for (int i = 0; i < newBytes.Length; i++)
                    bytes.Insert(_inputPos++, newBytes[i]);
                str = Encoding.UTF8.GetString(bytes.ToArray());
            }

            var bI = 0;
            void DrawButton(int i)
            {
                if (bI % 15 != 0)
                    ImGui.SameLine();

                var str = $"{(char)i}";
                ImGui.SetWindowFontScale(1.5f);
                if (ImGui.Button(str, new Vector2(36 * ImGuiHelpers.GlobalScale)))
                {
                    InsertString(ref temp, str);
                    QoLBar.Config.Save();
                }
                ImGui.SetWindowFontScale(1);
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.SetWindowFontScale(4);
                    ImGui.TextUnformatted(str);
                    ImGui.SetWindowFontScale(1);
                    ImGui.TextUnformatted($"{(Dalamud.Game.Text.SeIconChar)i}");
                    ImGui.EndTooltip();
                }

                bI++;
            }


            for (var i = 0xE020; i <= 0xE02B; i++)
                DrawButton(i);
            for (var i = 0xE031; i <= 0xE035; i++)
                DrawButton(i);
            for (var i = 0xE038; i <= 0xE044; i++)
                DrawButton(i);
            for (var i = 0xE048; i <= 0xE04E; i++)
                DrawButton(i);
            for (var i = 0xE050; i <= 0xE08A; i++)
                DrawButton(i);
            for (var i = 0xE08F; i <= 0xE0C6; i++)
                DrawButton(i);
            for (var i = 0xE0D0; i <= 0xE0DB; i++)
                DrawButton(i);

            input = temp;

            ImGui.EndPopup();
        }
    }

    private static void AddRightClickPrivateUsePopup(ref string input)
    {
        if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
            ImGui.OpenPopup($"Private Use Popup##{ImGui.GetCursorPos()}");

        DrawInsertPrivateCharPopup(ref input);
    }

    public static void AutoPasteIcon(ShCfg sh)
    {
        if (!IconBrowserUI.iconBrowserOpen || !IconBrowserUI.doPasteIcon) return;

        var split = sh.Name.Split(new[] { "##" }, 2, StringSplitOptions.None);
        var split2 = split[0].Split(new[] { "::" }, 2, StringSplitOptions.None);
        sh.Name = $"{split2[0]}::{IconBrowserUI.pasteIcon}" + (split.Length > 1 ? $"##{split[1]}" : "");
        QoLBar.Config.Save();
        IconBrowserUI.doPasteIcon = false;
    }

    public static void EditShortcutConfigBase(ShCfg sh, bool editing, bool hasIcon)
    {
        EditShortcutName(sh, editing);
        ImGuiEx.SetItemTooltip("在名稱開頭或結尾加上 ::x（x 為數字）以使用圖示，例如「::2914」。\n" +
                               "在名稱中任意位置使用 ## 可將其後的文字變成提示框，\n例如「名稱##這是提示框」。"
                               + (hasIcon ?
                                   "\n\n圖示可在「::」與其 ID 之間加上參數，例如「::f21」。\n" +
                                   "\t' f ' - 套用快捷列框架。\n" +
                                   "\t' n ' - 移除快捷列框架。\n" +
                                   "\t' l ' - 使用低解析度圖示。\n" +
                                   "\t' h ' - 若存在則使用高解析度圖示。\n" +
                                   "\t' g ' - 將圖示轉為灰階。\n" +
                                   "\t' r ' - 將圖示反轉。"
                                   : string.Empty));

        var _t = (int)sh.Type;
        ImGui.TextUnformatted("類型");
        ImGui.RadioButton("指令", ref _t, 0);
        ImGui.SameLine(ImGui.GetWindowWidth() / 3);
        ImGui.RadioButton("分類", ref _t, 1);
        ImGui.SameLine(ImGui.GetWindowWidth() / 3 * 2);
        ImGui.RadioButton("間隔", ref _t, 2);
        if (_t != (int)sh.Type)
        {
            sh.Type = (ShortcutType)_t;
            if (sh.Type == ShortcutType.Category)
                sh.SubList ??= new List<ShCfg>();

            if (editing)
                QoLBar.Config.Save();
        }

        if (sh.Type != ShortcutType.Spacer && (sh.Type != ShortcutType.Category || sh.Mode == ShortcutMode.Default))
        {
            var height = ImGui.GetFontSize() * Math.Min(sh.Command.Split('\n').Length + 1, 7) + ImGui.GetStyle().FramePadding.Y * 2; // ImGui issue #238: can't disable multiline scrollbar and it appears a whole line earlier than it should, so thats cool I guess

            unsafe
            {
                if (ImGui.InputTextMultiline("Command##Input", ref sh.Command, 65535, new Vector2(0, height), ImGuiInputTextFlags.CallbackAlways, GetCursorPosCallback) && editing)
                    QoLBar.Config.Save();
            }
            AddRightClickPrivateUsePopup(ref sh.Command);
            ImGuiEx.SetItemTooltip("你可以使用右鍵新增特殊遊戲符號，此外，\n" +
                                   "還有一些只能在快捷項目中使用的自訂指令。\n" +
                                   "\t' //m0 ' - 執行個人巨集 #0（最多到 //m99）。\n" +
                                   "\t' //m100 ' - 執行共用巨集 #0（最多到 //m199）。\n" +
                                   "\t' //m ' - 開始或結束自訂巨集。之後的行\n" +
                                   "會作為巨集執行而非快捷項目（可使用\n" +
                                   "/wait、/macrolock 等），直到再次使用 //m 為止，最多 30 行。\n" +
                                   "\t' //i <ID/名稱> ' - 使用道具，無法與 //m 一起使用。\n" +
                                   "\t' // <註解> ' - 新增註解。");
        }
    }

    public static unsafe bool EditShortcutName(ShCfg sh, bool editing)
    {
        var ret = ImGui.InputText("名稱", ref sh.Name, 256, ImGuiInputTextFlags.CallbackAlways, GetCursorPosCallback);
        AddRightClickPrivateUsePopup(ref sh.Name);

        if (ret && editing)
            QoLBar.Config.Save();

        return ret;
    }

    public static bool EditShortcutMode(ShortcutUI sh)
    {
        var _m = (int)sh.Config.Mode;
        ImGui.TextUnformatted("模式");
        ImGuiEx.SetItemTooltip("改變按下時的行為。\n" +
                               "注意：不建議用於包含子分類的分類。");

        ImGui.RadioButton("預設", ref _m, 0);
        ImGuiEx.SetItemTooltip("預設行為，分類必須設為此模式才能編輯其快捷項目！");

        ImGui.SameLine(ImGui.GetWindowWidth() / 3);
        ImGui.RadioButton("遞增", ref _m, 1);
        ImGuiEx.SetItemTooltip("每次按下時依序執行每一行／快捷項目。");

        ImGui.SameLine(ImGui.GetWindowWidth() / 3 * 2);
        ImGui.RadioButton("隨機", ref _m, 2);
        ImGuiEx.SetItemTooltip("按下時隨機執行一行／快捷項目。");

        if (_m != (int)sh.Config.Mode)
        {
            sh.Config.Mode = (ShortcutMode)_m;

            if (sh.Config.Mode == ShortcutMode.Random)
            {
                var c = Math.Max(1, (sh.Config.Type == ShortcutType.Category) ? sh.children.Count : sh.Config.Command.Split('\n').Length);
                sh.Config._i = (int)(QoLBar.FrameCount % c);
            }
            else
            {
                sh.Config._i = 0;
            }

            QoLBar.Config.Save();

            return true;
        }

        return false;
    }

    public static bool EditShortcutColor(ShortcutUI sh)
    {
        var color = ImGui.ColorConvertU32ToFloat4(sh.Config.Color);
        color.W += sh.Config.ColorAnimation / 255f; // Temporary
        if (ImGui.ColorEdit4("顏色", ref color, ImGuiColorEditFlags.NoDragDrop | ImGuiColorEditFlags.AlphaPreviewHalf))
        {
            sh.Config.Color = ImGui.ColorConvertFloat4ToU32(color);
            sh.Config.ColorAnimation = Math.Max((int)Math.Round(color.W * 255) - 255, 0);
            QoLBar.Config.Save();
            return true;
        }
        else
        {
            return false;
        }
    }

    public static void EditShortcutCategoryOptions(ShortcutUI sh)
    {
        if (ImGui.SliderInt("按鈕寬度", ref sh.Config.CategoryWidth, 0, 200))
            QoLBar.Config.Save();
        ImGuiEx.SetItemTooltip("設為 0 以使用文字寬度。");

        if (ImGui.SliderInt("欄數", ref sh.Config.CategoryColumns, 0, 12))
            QoLBar.Config.Save();
        ImGuiEx.SetItemTooltip("每行快捷項目的數量，超過後會另起一行。\n" +
                               "設為 0 表示不限制。");

        if (ImGui.DragFloat("縮放", ref sh.Config.CategoryScale, 0.002f, 0.7f, 2f, "%.2f"))
            QoLBar.Config.Save();

        if (ImGui.DragFloat("字型縮放", ref sh.Config.CategoryFontScale, 0.0018f, 0.5f, 1.0f, "%.2f"))
            QoLBar.Config.Save();

        var spacing = new Vector2(sh.Config.CategorySpacing[0], sh.Config.CategorySpacing[1]);
        if (ImGui.DragFloat2("間距", ref spacing, 0.12f, 0, 32, "%.f"))
        {
            sh.Config.CategorySpacing[0] = (int)spacing.X;
            sh.Config.CategorySpacing[1] = (int)spacing.Y;
            QoLBar.Config.Save();
        }

        if (ImGui.Checkbox("滑鼠移入時開啟", ref sh.Config.CategoryOnHover))
            QoLBar.Config.Save();
        ImGui.SameLine(ImGui.GetWindowWidth() / 2);
        if (ImGui.Checkbox("移出時關閉", ref sh.Config.CategoryHoverClose))
            QoLBar.Config.Save();

        if (ImGui.Checkbox("選取後保持開啟", ref sh.Config.CategoryStaysOpen))
            QoLBar.Config.Save();
        ImGuiEx.SetItemTooltip("按下分類內的快捷項目時保持分類開啟。\n若快捷項目與其他插件互動，可能無法正常運作。");
        ImGui.SameLine(ImGui.GetWindowWidth() / 2);
        if (ImGui.Checkbox("無背景", ref sh.Config.CategoryNoBackground))
            QoLBar.Config.Save();
    }

    public static void EditShortcutIconOptions(ShortcutUI sh)
    {
        if (ImGui.DragFloat("縮放", ref sh.Config.IconZoom, 0.005f, 1.0f, 5.0f, "%.2f"))
            QoLBar.Config.Save();

        var offset = new Vector2(sh.Config.IconOffset[0], sh.Config.IconOffset[1]);
        if (ImGui.DragFloat2("偏移", ref offset, 0.0005f, -0.5f, 0.5f, "%.3f"))
        {
            sh.Config.IconOffset[0] = offset.X;
            sh.Config.IconOffset[1] = offset.Y;
            QoLBar.Config.Save();
        }

        var r = (float)(sh.Config.IconRotation * 180 / Math.PI) % 360;
        if (ImGui.DragFloat("旋轉", ref r, 0.2f, -360, 360, "%.f"))
        {
            if (r < 0)
                r += 360;
            sh.Config.IconRotation = (float)(r / 180 * Math.PI);
            QoLBar.Config.Save();
        }

        static string formatName(Lumina.Excel.Sheets.Action a) => a.RowId switch
        {
            0 => "無",
            847 => "[847] 物品",
            _ => $"[{a.RowId}] {a.Name}"
        };
        if (ImGuiEx.ExcelSheetCombo<Lumina.Excel.Sheets.Action>("冷卻技能 ID", out var action, s => s.GetRowOrDefault(sh.Config.CooldownAction) is { } a ? formatName(a) : sh.Config.CooldownAction.ToString(),
            ImGuiComboFlags.None, (a, s) => (a.RowId == 0 || a is { CooldownGroup: > 0, ClassJobCategory.RowId: > 0 }) && formatName(a).Contains(s, StringComparison.CurrentCultureIgnoreCase),
            a => ImGui.Selectable(formatName(a), sh.Config.CooldownAction == a.RowId)))
        {
            sh.Config.CooldownAction = action.Value.RowId;

            if (sh.Config.CooldownAction == 0)
                sh.Config.CooldownStyle = 0;

            QoLBar.Config.Save();
        }

        if (sh.Config.CooldownAction > 0)
        {
            var save = ImGui.CheckboxFlags("##CooldownNumber", ref sh.Config.CooldownStyle, (int)ImGuiEx.IconSettings.CooldownStyle.Number);
            ImGuiEx.SetItemTooltip("數字");
            ImGui.SameLine();
            save |= ImGui.CheckboxFlags("##CooldownDisable", ref sh.Config.CooldownStyle, (int)ImGuiEx.IconSettings.CooldownStyle.Disable);
            ImGuiEx.SetItemTooltip("變暗（強制套用圖示框架）");
            ImGui.SameLine();
            save |= ImGui.CheckboxFlags("##CooldownDefault", ref sh.Config.CooldownStyle, (int)ImGuiEx.IconSettings.CooldownStyle.Cooldown);
            ImGuiEx.SetItemTooltip("預設旋轉圈（強制套用圖示框架）");
            ImGui.SameLine();
            save |= ImGui.CheckboxFlags("##CooldownGCD", ref sh.Config.CooldownStyle, (int)ImGuiEx.IconSettings.CooldownStyle.GCDCooldown);
            ImGuiEx.SetItemTooltip("橘色 GCD 旋轉圈");
            ImGui.SameLine();
            save |= ImGui.CheckboxFlags("冷卻樣式旗標##CooldownCharge", ref sh.Config.CooldownStyle, (int)ImGuiEx.IconSettings.CooldownStyle.ChargeCooldown);
            ImGuiEx.SetItemTooltip("充能旋轉圈");
            if (save)
                QoLBar.Config.Save();
        }
    }

    public static void EditBarGeneralOptions(BarUI bar)
    {
        if (ImGui.InputText("名稱", ref bar.Config.Name, 256))
            QoLBar.Config.Save();

        var _dock = (int)bar.Config.DockSide;
        if (ImGui.Combo("邊緣", ref _dock, (ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0
                ? "上\0右\0下\0左\0未固定"
                : "上\0右\0下\0左"))
        {
            bar.Config.DockSide = (BarDock)_dock;
            if (bar.Config.DockSide == BarDock.Undocked && bar.Config.Visibility == BarVisibility.Slide)
                bar.Config.Visibility = BarVisibility.Always;
            bar.Config.Position[0] = 0;
            bar.Config.Position[1] = 0;
            bar.Config.LockedPosition = false;
            QoLBar.Config.Save();
            bar.SetupPivot();
        }

        if (bar.IsDocked)
        {
            var topbottom = bar.Config.DockSide == BarDock.Top || bar.Config.DockSide == BarDock.Bottom;
            var _align = (int)bar.Config.Alignment;
            ImGui.Text("對齊");
            ImGui.RadioButton(topbottom ? "左" : "上", ref _align, 0);
            ImGui.SameLine(ImGui.GetWindowWidth() / 3);
            ImGui.RadioButton("置中", ref _align, 1);
            ImGui.SameLine(ImGui.GetWindowWidth() / 3 * 2);
            ImGui.RadioButton(topbottom ? "右" : "下", ref _align, 2);
            if (_align != (int)bar.Config.Alignment)
            {
                bar.Config.Alignment = (BarAlign)_align;
                QoLBar.Config.Save();
                bar.SetupPivot();
            }

            var _visibility = (int)bar.Config.Visibility;
            ImGui.Text("動畫");
            ImGui.RadioButton("滑動", ref _visibility, 0);
            ImGui.SameLine(ImGui.GetWindowWidth() / 3);
            ImGui.RadioButton("即時", ref _visibility, 1);
            ImGui.SameLine(ImGui.GetWindowWidth() / 3 * 2);
            ImGui.RadioButton("永遠顯示", ref _visibility, 2);
            if (_visibility != (int)bar.Config.Visibility)
            {
                bar.Config.Visibility = (BarVisibility)_visibility;
                QoLBar.Config.Save();
            }

            if ((bar.Config.Visibility != BarVisibility.Always) && ImGui.DragFloat("顯示區域縮放", ref bar.Config.RevealAreaScale, 0.01f, 0.0f, 1.0f, "%.2f"))
                QoLBar.Config.Save();
        }
        else
        {
            var _visibility = (int)bar.Config.Visibility;
            ImGui.Text("動畫");
            ImGui.RadioButton("即時", ref _visibility, 1);
            ImGui.SameLine(ImGui.GetWindowWidth() / 2);
            ImGui.RadioButton("永遠顯示", ref _visibility, 2);
            if (_visibility != (int)bar.Config.Visibility)
            {
                bar.Config.Visibility = (BarVisibility)_visibility;
                QoLBar.Config.Save();
            }
        }

        Keybind.KeybindInput(bar.Config);

        if (ImGui.Checkbox("編輯模式", ref bar.Config.Editing))
        {
            if (!bar.Config.Editing)
                Game.ExecuteCommand("/echo <se> 你可以右鍵點擊快捷列本身（黑色背景處）以重新開啟此設定選單！也可以使用 shift + 右鍵來新增快捷項目。");
            QoLBar.Config.Save();
        }
        ImGui.SameLine(ImGui.GetWindowWidth() / 2);
        if (ImGui.Checkbox("點擊穿透", ref bar.Config.ClickThrough))
            QoLBar.Config.Save();
        ImGuiEx.SetItemTooltip("警告：這將使你無法與此快捷列互動。\n" +
                               "若要再次編輯設定，你需要使用一般設定中\n" +
                               "快捷列名稱旁邊的「O」按鈕。");

        if (ImGui.Checkbox("鎖定位置", ref bar.Config.LockedPosition))
            QoLBar.Config.Save();
        if (bar.IsDocked && bar.Config.Visibility != BarVisibility.Always)
        {
            ImGui.SameLine(ImGui.GetWindowWidth() / 2);
            if (ImGui.Checkbox("提示", ref bar.Config.Hint))
                QoLBar.Config.Save();
            ImGuiEx.SetItemTooltip("防止快捷列休眠，會增加 CPU 負載。");
        }

        if (!bar.Config.LockedPosition)
        {
            var pos = bar.VectorPosition;
            var area = bar.UsableArea;
            var max = (area.X > area.Y) ? area.X : area.Y;
            if (ImGui.DragFloat2(bar.IsDocked ? "偏移" : "位置", ref pos, 1, -max, max, "%.f"))
            {
                bar.Config.Position[0] = Math.Min(pos.X / area.X, 1);
                bar.Config.Position[1] = Math.Min(pos.Y / area.Y, 1);
                QoLBar.Config.Save();
                if (bar.IsDocked)
                    bar.SetupPivot();
                else
                    bar._setPos = true;
            }
        }
    }

    public static void EditBarStyleOptions(BarUI bar)
    {
        if (ImGui.SliderInt("按鈕寬度", ref bar.Config.ButtonWidth, 0, 200))
            QoLBar.Config.Save();
        ImGuiEx.SetItemTooltip("設為 0 以使用文字寬度。");

        if (ImGui.SliderInt("欄數", ref bar.Config.Columns, 0, 12))
            QoLBar.Config.Save();
        ImGuiEx.SetItemTooltip("每行快捷項目的數量，超過後會另起一行。\n" +
                               "設為 0 表示不限制。");

        if (ImGui.DragFloat("縮放", ref bar.Config.Scale, 0.002f, 0.7f, 2.0f, "%.2f"))
            QoLBar.Config.Save();

        if (ImGui.DragFloat("字型縮放", ref bar.Config.FontScale, 0.0018f, 0.5f, 1.0f, "%.2f"))
            QoLBar.Config.Save();

        var spacing = new Vector2(bar.Config.Spacing[0], bar.Config.Spacing[1]);
        if (ImGui.DragFloat2("間距", ref spacing, 0.12f, 0, 32, "%.f"))
        {
            bar.Config.Spacing[0] = (int)spacing.X;
            bar.Config.Spacing[1] = (int)spacing.Y;
            QoLBar.Config.Save();
        }

        if (ImGui.Checkbox("無背景", ref bar.Config.NoBackground))
            QoLBar.Config.Save();
    }

    public static void DisplayRightClickDeleteMessage(string text = "右鍵點擊以刪除！") =>
        DalamudApi.ShowNotification($"\t\t\t{text}\t\t\t\n\n", NotificationType.Info);
}