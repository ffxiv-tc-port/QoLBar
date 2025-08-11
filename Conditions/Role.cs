using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace QoLBar.Conditions;

public class RoleCondition : ICondition, IDrawableCondition, IArgCondition, IConditionCategory
{
    public static readonly Dictionary<int, string> roleDictionary = new()
    {
        [1] = "Tank".Loc(),
        [2] = "Melee DPS".Loc(),
        [3] = "Ranged DPS".Loc(),
        [4] = "Healer".Loc(),
        [30] = "DoW".Loc(),
        [31] = "DoM".Loc(),
        [32] = "DoL".Loc(),
        [33] = "DoH".Loc()
    };

    public string ID => "r";
    public string ConditionName => "Role".Loc();
    public string CategoryName => "Role".Loc();
    public int DisplayPriority => 0;
    public bool Check(dynamic arg) => DalamudApi.ClientState.LocalPlayer is { } player
        && ((uint)arg < 30 ? player.ClassJob.ValueNullable?.Role : player.ClassJob.ValueNullable?.ClassJobCategory.RowId) == (uint)arg;
    public string GetTooltip(CndCfg cndCfg) => null;
    public string GetSelectableTooltip(CndCfg cndCfg) => null;
    public void Draw(CndCfg cndCfg)
    {
        roleDictionary.TryGetValue((int)cndCfg.Arg, out string s);
        if (!ImGui.BeginCombo("##Role", s)) return;

        foreach (var (id, name) in roleDictionary)
        {
            if (!ImGui.Selectable(name, id == cndCfg.Arg)) continue;

            cndCfg.Arg = id;
            QoLBar.Config.Save();
        }
        ImGui.EndCombo();
    }
    public dynamic GetDefaultArg(CndCfg cndCfg) => roleDictionary.FirstOrDefault(kv => Check(kv.Key)).Key;
}