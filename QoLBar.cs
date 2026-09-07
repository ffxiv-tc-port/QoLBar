using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Linq.Expressions;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

// Disclaimer: I have no idea what I'm doing.
namespace QoLBar;

public class QoLBar : IDalamudPlugin
{
    public static QoLBar Plugin { get; private set; }
    public static Configuration Config { get; private set; }

    public PluginUI ui;
    private bool pluginReady = false;

    public static TextureDictionary TextureDictionary => Config.UseHRIcons ? textureDictionaryHR : textureDictionaryLR;
    public static readonly TextureDictionary textureDictionaryLR = new(false, false);
    public static readonly TextureDictionary textureDictionaryHR = new(true, false);
    public static readonly TextureDictionary textureDictionaryGSLR = new(false, true);
    public static readonly TextureDictionary textureDictionaryGSHR = new(true, true);

    public const float DefaultFontSize = 17;
    public const float MaxFontSize = 64;
    public static IFontHandle Font { get; private set; }

    public QoLBar(IDalamudPluginInterface pluginInterface)
    {
        Plugin = this;
        Localization.Init(pluginInterface.AssemblyLocation.DirectoryName);
        DalamudApi.Initialize(this, pluginInterface);

        Config = (Configuration)DalamudApi.PluginInterface.GetPluginConfig() ?? new();
        Config.Initialize();
        Config.TryBackup(); // Backup on version change

        DalamudApi.Framework.Update += Update;

        ui = new PluginUI();
        DalamudApi.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        DalamudApi.PluginInterface.UiBuilder.Draw += Draw;
        SetupFont();

        CheckHideOptOuts();

        ReadyPlugin();
    }

    public void ReadyPlugin()
    {
        try
        {
            IPC.Initialize();

            var iconPath = Config.GetPluginIconPath();
            textureDictionaryLR.AddUserIcons(iconPath);
            textureDictionaryHR.AddUserIcons(iconPath);

            TextureDictionary.AddExtraTextures(textureDictionaryLR, textureDictionaryHR);
            TextureDictionary.AddExtraTextures(textureDictionaryGSLR, textureDictionaryGSHR);
            IconBrowserUI.BuildCache(false);

            Game.Initialize();
            ConditionManager.Initialize();

            pluginReady = true;
            IPC.InitializedProvider.SendMessage();
        }
        catch (Exception e)
        {
            DalamudApi.LogError($"Failed loading QoLBar!\n{e}");
        }
    }

    public void Reload()
    {
        Config = (Configuration)DalamudApi.PluginInterface.GetPluginConfig() ?? new();
        Config.Initialize();
        Config.UpdateVersion();
        Config.Save();
        ui.Reload();
        CheckHideOptOuts();
    }

    public void ToggleConfig() => ui.ToggleConfig();

    [Command("/qolbar")]
    [HelpMessage("Open the configuration menu.")]
    public void ToggleConfig(string command, string argument) => ToggleConfig();

    [Command("/qolicons")]
    [HelpMessage("Open the icon browser.")]
    public void ToggleIconBrowser(string command = null, string argument = null) => IconBrowserUI.ToggleIconBrowser();

    [Command("/qolvisible")]
    [HelpMessage("Hide or reveal a bar using its name or index. Usage: /qolvisible [on|off|toggle] <bar>")]
    private void OnQoLVisible(string command, string argument)
    {
        var reg = Regex.Match(argument, @"^(\w+) (.+)");
        if (reg.Success)
        {
            var subcommand = reg.Groups[1].Value.ToLower();
            var bar = reg.Groups[2].Value;
            var useID = int.TryParse(bar, out var id);
            switch (subcommand)
            {
                case "on":
                case "reveal":
                case "r":
                    if (useID)
                        ui.SetBarHidden(id - 1, false, false);
                    else
                        ui.SetBarHidden(bar, false, false);
                    break;
                case "off":
                case "hide":
                case "h":
                    if (useID)
                        ui.SetBarHidden(id - 1, false, true);
                    else
                        ui.SetBarHidden(bar, false, true);
                    break;
                case "toggle":
                case "t":
                    if (useID)
                        ui.SetBarHidden(id - 1, true);
                    else
                        ui.SetBarHidden(bar, true);
                    break;
                default:
                    PrintError("Invalid subcommand.".Loc());
                    break;
            }
        }
        else
            PrintError("Usage: /qolvisible [on|off|toggle] <bar>".Loc());
    }

    [Command("/performance")]
    [HelpMessage("Starts playing an instrument.")]
    public void OnPerformance(string command, string argument)
    {
        if (!byte.TryParse(argument, out var b)
            && DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Perform>().FirstOrDefault(r
                => argument?.Equals(r.Instrument.ExtractText(), StringComparison.CurrentCultureIgnoreCase) ?? false) is { RowId: > 0 } r)
            b = (byte)r.RowId;

        if (b == 0)
            PrintError("Invalid instrument.".Loc());
        else
            Game.StartPerformance(b);
    }

    public static bool HasPlugin(string name) => DalamudApi.PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == name);

    public static bool IsLoggedIn() => ConditionManager.CheckCondition("l");

    public static float RunTime => (float)DalamudApi.PluginInterface.LoadTimeDelta.TotalSeconds;
    public static long FrameCount => (long)DalamudApi.PluginInterface.UiBuilder.FrameCount;
    private void Update(IFramework framework)
    {
        if (!pluginReady) return;

        Config.DoTimedBackup();
        Game.ReadyCommand();
        Keybind.Run();
        Keybind.SetupHotkeys(ui.bars);
        ConditionManager.UpdateCache();
    }

    private void Draw()
    {
        if (_addUserIcons)
            AddUserIcons(ref _addUserIcons);

        if (!pluginReady) return;

        Config.DrawUpdateWindow();
        ui.Draw();
    }

    public static void SetupFont()
    {
        Font?.Dispose();
        Font = DalamudApi.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(buildToolkit =>
        {
            buildToolkit.OnPreBuild(tk =>
            {
                var config = new SafeFontConfig { SizePx = Math.Min(Math.Max(Config.FontSize, 1), MaxFontSize) };
                var font = tk.AddDalamudAssetFont(DalamudAsset.NotoSansScMedium, config);
                config.MergeFont = font;
                tk.AddGameSymbol(config);
                tk.SetFontScaleMode(font, FontScaleMode.UndoGlobalScale);
            });
        });
    }

    public void CheckHideOptOuts()
    {
        //pluginInterface.UiBuilder.DisableAutomaticUiHide = false;
        DalamudApi.PluginInterface.UiBuilder.DisableUserUiHide = Config.OptOutGameUIOffHide;
        DalamudApi.PluginInterface.UiBuilder.DisableCutsceneUiHide = Config.OptOutCutsceneHide;
        DalamudApi.PluginInterface.UiBuilder.DisableGposeUiHide = Config.OptOutGPoseHide;
    }

    public static Dictionary<int, string> GetUserIcons() => TextureDictionary.GetUserIcons();

    // TODO: .
    private bool _addUserIcons = false;
    private bool _iconsLR = false;
    private bool _iconsHR = false;
    private void AddUserIcons(ref bool b)
    {
        if (!_iconsLR && !_iconsHR)
        {
            _iconsLR = true;
            _iconsHR = true;
        }

        var iconPath = Config.GetPluginIconPath();

        if (_iconsLR)
            _iconsLR = !textureDictionaryLR.AddUserIcons(iconPath);

        if (_iconsHR)
            _iconsHR = !textureDictionaryHR.AddUserIcons(iconPath);

        if (!(b = _iconsLR || _iconsHR))
            IconBrowserUI.BuildCache(false);
    }

    public void AddUserIcons() => _addUserIcons = true;

    public static void CleanTextures(bool disposing)
    {
        if (disposing)
        {
            textureDictionaryLR.Dispose();
            textureDictionaryHR.Dispose();
            textureDictionaryGSLR.Dispose();
            textureDictionaryGSHR.Dispose();
        }
        else
        {
            textureDictionaryLR.TryEmpty();
            textureDictionaryHR.TryEmpty();
            textureDictionaryGSLR.TryEmpty();
            textureDictionaryGSHR.TryEmpty();
        }
    }

    /// <summary>還沒送出的聊天訊息。<b>順序就是呼叫順序</b>，一般訊息與錯誤訊息共用同一條佇列。</summary>
    private static readonly ConcurrentQueue<(string Message, bool IsError)> PendingChatLines = new();

    public static void PrintEcho(string message) => QueueForFramework($"[QoL Bar] {message}", false);
    public static void PrintError(string message) => QueueForFramework($"[QoL Bar] {message}", true);

    /// <summary>把一則已經組好的訊息排進佇列，並要求在 framework 執行緒上排乾。</summary>
    /// <remarks>
    /// 🔴🔴 <b>為什麼要排到 framework 執行緒才送出。</b>
    /// 本 pin 的 Dalamud <c>ChatGui.Print</c>／<c>PrintError</c> 只是把項目 <c>Enqueue</c> 進一個
    /// <b>沒有任何同步</b>的 <c>Queue&lt;XivChatEntry&gt;</c>，而 <c>UpdateQueue</c> 在 framework
    /// 執行緒上 <c>TryDequeue</c>。從別的執行緒呼叫 ⇒ 與 framework 執行緒並行改同一個
    /// <c>Queue</c>，<b>失敗形式不是「訊息晚一點出現」而是那個佇列本身壞掉</b>，
    /// 而且壞掉之後受害的是所有外掛的聊天輸出，不只這一個。
    /// <para>
    /// 🔴 本外掛絕大多數呼叫點都在 framework 執行緒上（聊天指令處理常式、ImGui 回呼、
    /// <c>Framework.Update</c>），<b>唯一的例外是 IPC 端點 <c>QoLBar.ImportBar</c></b>
    /// —— IPC 實作跑在<b>呼叫端外掛的執行緒</b>上，而它可達的聊天輸出有 7 個：
    /// <c>Importing.TryImport(printError: true)</c> 的三個匯入失敗訊息與三個
    /// 「已自動移除條件／快捷鍵」提示，加上 <c>PluginUI.AddBar → Configuration.Save</c>
    /// 失敗時的「Error saving config」。
    /// </para>
    /// <para>
    /// 📌 <c>IFramework.RunOnFrameworkThread</c> 在<b>已經是</b> framework 執行緒時就地同步執行
    /// （<c>Framework.cs:167</c>），所以指令與 UI 那些既有路徑的行為一個位元都沒變。
    /// </para>
    /// <para>
    /// 🔑 <b>為什麼還要自己排一個佇列</b>：Dalamud 的 <c>ThreadBoundTaskScheduler</c> 用
    /// <c>ConcurrentDictionary</c> 存待跑的工作、<c>Run()</c> 走訪它的 <c>Keys</c>
    /// ⇒ <b>不保證先進先出</b>。把每一次 <c>Print</c> 各自包成一個排程工作的話，
    /// <c>TryImport</c> 一次可能連印的三行「已自動移除…」順序會變成隨機的。
    /// 一般訊息與錯誤訊息刻意共用同一條佇列，這樣兩者之間的先後也維持原樣。
    /// </para>
    /// <para>
    /// 📌 訊息內容<b>在呼叫端的執行緒上就組好了</b>（<c>[QoL Bar]</c> 前綴與本地化字串都在
    /// 進佇列之前完成），排隊的只是「送出」這個動作，所以使用者看到的字一個都沒變。
    /// </para>
    /// </remarks>
    private static void QueueForFramework(string message, bool isError)
    {
        PendingChatLines.Enqueue((message, isError));
        _ = DalamudApi.Framework.RunOnFrameworkThread(static () =>
        {
            while (PendingChatLines.TryDequeue(out var line))
            {
                if (line.IsError)
                    DalamudApi.ChatGui.PrintError(line.Message);
                else
                    DalamudApi.ChatGui.Print(line.Message);
            }
        });
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;

        IPC.DisposedProvider.SendMessage();
        IPC.Dispose();

        Config.Save();
        Config.SaveTempConfig();

        DalamudApi.Framework.Update -= Update;
        DalamudApi.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        DalamudApi.PluginInterface.UiBuilder.Draw -= Draw;
        DalamudApi.Dispose();

        ui.Dispose();
        Game.Dispose();
        CleanTextures(true);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

public static class Extensions
{
    public static T2 GetDefaultValue<T, T2>(this T _, Expression<Func<T, T2>> expression)
    {
        if (((MemberExpression)expression.Body).Member.GetCustomAttribute(typeof(DefaultValueAttribute)) is DefaultValueAttribute attribute)
            return (T2)attribute.Value;
        else
            return default;
    }

    public static byte[] GetGrayscaleImageData(this Lumina.Data.Files.TexFile tex)
    {
        var rgba = tex.GetRgbaImageData();
        var pixels = rgba.Length / 4;
        var newData = new byte[rgba.Length];
        for (int i = 0; i < pixels; i++)
        {
            var pixel = i * 4;
            var alpha = rgba[pixel + 3];

            if (alpha > 0)
            {
                var avg = (byte)(0.2125f * rgba[pixel] + 0.7154f * rgba[pixel + 1] + 0.0721f * rgba[pixel + 2]);
                newData[pixel] = avg;
                newData[pixel + 1] = avg;
                newData[pixel + 2] = avg;
            }

            newData[pixel + 3] = alpha;
        }
        return newData;
    }

    public static object Cast(this Type Type, object data)
    {
        var DataParam = Expression.Parameter(typeof(object), "data");
        var Body = Expression.Block(Expression.Convert(Expression.Convert(DataParam, data.GetType()), Type));

        var Run = Expression.Lambda(Body, DataParam).Compile();
        var ret = Run.DynamicInvoke(data);
        return ret;
    }
}