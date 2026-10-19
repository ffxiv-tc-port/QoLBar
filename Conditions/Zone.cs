using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace QoLBar.Conditions;

public class ZoneCondition : ICondition, IDrawableCondition, IArgCondition, IConditionCategory
{
    public string ID => "z";
    public string ConditionName => "Zone".Loc();
    public string CategoryName => "Zone".Loc();
    public int DisplayPriority => 0;
    public bool Check(dynamic arg) => DalamudApi.ClientState.TerritoryType == (ushort)arg;
    public string GetTooltip(CndCfg cndCfg) => null;
    public string GetSelectableTooltip(CndCfg cndCfg) => null;
    // 表在執行中不變，標籤按列記住；只在 UI 執行緒讀寫。
    private static readonly Dictionary<uint, string> names = new();

    public void Draw(CndCfg cndCfg)
    {
        static string formatName(Lumina.Excel.Sheets.TerritoryType t)
        {
            if (!names.TryGetValue(t.RowId, out var name))
                names[t.RowId] = name = $"[{t.RowId}] {t.PlaceName.ValueNullable?.Name}";
            return name;
        }
        // 先用截斷後的列號篩掉其他列，dynamic 比較（每次都裝箱）只在可能相等的那一列做；結果與逐列比較相同。
        var argRow = (uint)cndCfg.Arg;
        if (!ImGuiEx.ExcelSheetCombo<Lumina.Excel.Sheets.TerritoryType>("##Zone", out var territory, s => s.GetRowOrDefault((uint)cndCfg.Arg) is { } row ? formatName(row) : string.Empty,
            ImGuiComboFlags.None, (t, s) => formatName(t).Contains(s, StringComparison.CurrentCultureIgnoreCase),
            t => ImGui.Selectable(formatName(t), t.RowId == argRow && (bool)(cndCfg.Arg == t.RowId)))) return;
        cndCfg.Arg = territory.Value.RowId;
        QoLBar.Config.Save();
    }
    // This list is completely and utterly awful so help people out a little bit
    public dynamic GetDefaultArg(CndCfg cndCfg) => DalamudApi.ClientState.TerritoryType;
}