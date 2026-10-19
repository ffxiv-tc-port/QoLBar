using System.Collections.Generic;
using System.Text;

namespace QoLBar;

// 點擊紀錄的純格式化：不碰 Dalamud／ImGui／原生，只在點擊或觸發的那一下被呼叫，不在每幀。
// 行格式由統計工具 plugin_ui_usage.py 解析，改格式要一起改：
//   [QoLBar][點擊] 方式=點擊 列=「…」 類別=「…」→「…」 按鈕=「…」 指令=/a ⏎ /b
//   [QoLBar][點擊] 方式=點擊 列=「…」 類別=（無） 按鈕=「…」 插件=「InternalName」 開=主介面
// 不變式：「」內的文字不含「」與換行（Clean 換掉），指令段永遠在行尾。
public static class ClickLog
{
    public enum Source { Click, Hotkey, Pie, Hover }

    public const string Prefix = "[QoLBar][點擊]";
    public const string CommandSeparator = " ⏎ ";
    public const string NoCategory = "（無）";

    public static string SourceText(Source source) => source switch
    {
        Source.Hotkey => "熱鍵",
        Source.Pie => "圓盤",
        Source.Hover => "懸停",
        _ => "點擊",
    };

    // 與 Game.QueueCommand 同樣以 \n 切、略過空行（不截 180 字）；回 null＝這一下沒有送出任何指令。
    public static string JoinSentCommands(string command)
    {
        if (string.IsNullOrEmpty(command)) return null;

        var parts = new List<string>();
        foreach (var line in command.Split('\n'))
        {
            if (!string.IsNullOrEmpty(line))
                parts.Add(line.Replace("\r", string.Empty));
        }
        return parts.Count == 0 ? null : string.Join(CommandSeparator, parts);
    }

    // 畫面上看到的名字：去掉「##」後的說明；純圖示按鈕沒有文字，改寫圖示編號。
    public static string ShortcutLabel(string rawName)
    {
        var name = rawName ?? string.Empty;
        var useIcon = ShortcutUI.ParseName(ref name, out var subName, out _, out var icon, out _);
        if (useIcon)
            return string.IsNullOrWhiteSpace(subName) ? $"（圖示 {icon}）" : subName.Trim();
        return string.IsNullOrWhiteSpace(name) ? "（無名）" : name.Trim();
    }

    public static string BarLabel(string name, int index) => string.IsNullOrWhiteSpace(name) ? $"#{index + 1}" : name.Trim();

    public static string FormatCommand(Source source, string bar, IReadOnlyList<string> categories, string button, string sentCommands) =>
        Head(source, bar, categories, button).Append(" 指令=").Append(sentCommands).ToString();

    public static string FormatPlugin(Source source, string bar, IReadOnlyList<string> categories, string button, string internalName, bool configOnly) =>
        Head(source, bar, categories, button)
            .Append(" 插件=「").Append(Clean(internalName)).Append('」')
            .Append(" 開=").Append(configOnly ? "設定" : "主介面")
            .ToString();

    private static StringBuilder Head(Source source, string bar, IReadOnlyList<string> categories, string button)
    {
        var sb = new StringBuilder(128);
        sb.Append(Prefix).Append(" 方式=").Append(SourceText(source));
        sb.Append(" 列=「").Append(Clean(bar)).Append('」');
        sb.Append(" 類別=");
        if (categories == null || categories.Count == 0)
            sb.Append(NoCategory);
        else
        {
            for (var i = 0; i < categories.Count; i++)
                sb.Append(i == 0 ? "「" : "→「").Append(Clean(categories[i])).Append('」');
        }
        return sb.Append(" 按鈕=「").Append(Clean(button)).Append('」');
    }

    // IPluginLog 把訊息當 Serilog 樣板解析：大括號加倍，指令裡的 {{ }} 才會原樣出現在 log。
    public static string EscapeTemplate(string line) => line.Replace("{", "{{").Replace("}", "}}");

    private static string Clean(string s) => string.IsNullOrEmpty(s)
        ? string.Empty
        : s.Replace('「', '『').Replace('」', '』').Replace('\r', ' ').Replace('\n', ' ');
}
