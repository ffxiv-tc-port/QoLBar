using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using QoLBar.Structures;

namespace QoLBar;

public unsafe class Game
{
    private const int maxCommandLength = 180; // 180 is the max per line for macros, 500 is the max you can actually type into the chat, however it is still possible to inject more

    private static bool commandReady = true;
    private static bool macroMode = false;
    private static float chatQueueTimer = 0;
    private static readonly Queue<string> commandQueue = new();
    private static readonly Queue<string> macroQueue = new();
    private static readonly Queue<string> chatQueue = new();
    private static uint retryItem = 0;

    [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern int GetWindowThreadProcessId(nint handle, out int processId);
    public static bool IsGameFocused
    {
        get
        {
            var activatedHandle = GetForegroundWindow();
            if (activatedHandle == nint.Zero)
                return false;

            var procId = Environment.ProcessId;
            _ = GetWindowThreadProcessId(activatedHandle, out var activeProcId);

            return activeProcId == procId;
        }
    }

    // 🔴 Framework.Instance() 與下面 EventFramework 同理,是 [StaticAddress(..., isPointer: true)]
    //    —— 讀「指標的位址」再解參考一層,登入前那個槽就是 0,回的是 null 不是擲例外。
    //    裸解參考 null 原生指標是攔不到的 AVE(try/catch 無效)。
    //    📌 回 null 而不是退回 UnixEpoch:退回紀元會變成「00:00」,可能剛好落進使用者設定的
    //    時段條件裡而靜默成立,那比「條件不成立」難察覺得多。
    public static DateTimeOffset? EorzeaTime
    {
        get
        {
            var framework = Framework.Instance();
            return framework == null
                ? null
                : DateTimeOffset.FromUnixTimeSeconds(framework->ClientTime.EorzeaTime);
        }
    }

    // 兩層都會是 null,而且都是常態不是異常:
    //   EventFramework.Instance() 是 [StaticAddress(..., isPointer: true)] —— 讀「指標的位址」,登入前那個槽就是 0。
    //   GetInstanceContentDirector() 不在副本裡時回 null —— CS 自己的 GetInstanceContentDirector<T>() 就先判過空才用。
    // 解參考 null 原生指標是攔不到的 AVE(try/catch 無效),而這支被 ExplorerModeCondition.Check 每次判定條件時呼叫。
    // 讀不到一律回 false(＝不在探索模式),對「不在副本裡」來說本來就是正確答案。
    public static bool IsInExplorerMode
    {
        get
        {
            var eventFramework = EventFramework.Instance();
            if (eventFramework == null)
                return false;
            var director = eventFramework->GetInstanceContentDirector();
            return director != null && (director->ContentFlags & 1) != 0;
        }
    }

    // 🔴 這幾個取得器（含下面的 raptureShellModule／raptureMacroModule）刻意是每次存取重查的屬性，不跨幀保存指標：
    //    登出回標題畫面時整棵 UIModule 樹會被拆掉，留著的舊指標之後任何一次解參考都是攔不到的 AccessViolationException。
    //    取法用 ClientStructs 自帶的 Instance()（上一層為 null 就回 null），重查成本只是幾個 vtable 跳轉。
    // ⚠️ 它們拿得到 null，所以本檔每一個消費點都要判空。
    public static UIModule* uiModule => UIModule.Instance();
    public static AgentInventoryContext* agentInventoryContext => AgentInventoryContext.Instance();
    public static AddonConfig* addonConfig => AddonConfig.Instance();

    /// <summary>遊戲自己的文字輸入框是不是正在輸入中。取不到 UI 模組（標題畫面／讀取畫面）
    /// 時回 false —— 那個狀態下遊戲裡根本沒有輸入框，回 false 與實情一致。</summary>
    public static bool IsGameTextInputActive
    {
        get
        {
            var ui = uiModule;
            if (ui == null) return false;
            var atkModule = ui->GetRaptureAtkModule();
            return atkModule != null && atkModule->AtkModule.IsTextInputActive();
        }
    }

    public static bool IsMacroRunning
    {
        get
        {
            var shell = raptureShellModule;
            return shell != null && shell->MacroCurrentLine >= 0;
        }
    }

    /// <summary>「HUD 配置編號取不到」的哨兵值。0 是合法的第 1 組配置，取不到時回 0
    /// 會讓「目前 HUD 配置＝1」這個條件在標題畫面誤判為成立，所以用 -1。</summary>
    public const int UnknownHUDLayout = -1;

    /// <summary>目前的 HUD 配置編號（0..3）。取不到時回 <see cref="UnknownHUDLayout"/>。</summary>
    public static int CurrentHUDLayout
    {
        get
        {
            var cfg = addonConfig;
            if (cfg == null) return UnknownHUDLayout;
            // ActiveDataSet 是欄位 +0x58 的指標，設定檔還沒載入完成時是 null。
            var data = cfg->ActiveDataSet;
            return data == null ? UnknownHUDLayout : data->CurrentHudLayout;
        }
    }

    // 契約：本檔每一個 [Signature] 成員與下面兩個巨集行數位址都可能沒解到（null／0），
    // 插件照常載入，只停掉用到它的那一項；每個呼叫點、解參點都要先判。

    // Command Execution
    public delegate void ProcessChatBoxDelegate(UIModule* uiModule, nint message, nint unused, byte a4);
    [Signature("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B F2 48 8B F9 45 84 C9", Fallibility = Fallibility.Fallible)]
    public static ProcessChatBoxDelegate ProcessChatBox;

    public delegate int GetCommandHandlerDelegate(RaptureShellModule* raptureShellModule, nint message, nint unused);
    [Signature("E8 ?? ?? ?? ?? 66 89 06 66 85 C0", Fallibility = Fallibility.Fallible)]
    public static GetCommandHandlerDelegate GetCommandHandler;

    // Macro Execution
    public delegate void ExecuteMacroDelegate(RaptureShellModule* raptureShellModule, nint macro);
    [Signature("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 8D 4E ?? 49 8B D6", Fallibility = Fallibility.Fallible)]
    public static Hook<ExecuteMacroDelegate>? ExecuteMacroHook;
    // 與上面的 uiModule 等同理：每次存取重查，不跨幀保存指標。
    public static RaptureShellModule* raptureShellModule => RaptureShellModule.Instance();
    public static RaptureMacroModule* raptureMacroModule => RaptureMacroModule.Instance();

    // 兩個位址要嘛都解到、要嘛都是 0（Initialize 保證）：只改寫其中一個會讓遊戲複製與執行的行數不一致。
    public static bool ExtendedMacroLinesAvailable => numCopiedMacroLinesPtr != nint.Zero && numExecutedMacroLinesPtr != nint.Zero;

    private const string copiedLinesSig = "48 8D 77 70 BF ?? 00 00 00";
    private const string executedLinesSig = "41 83 F8 ?? 0F 8D ?? ?? ?? ?? 49 6B C8 68";
    // 核對時讀到的原值；Dispose 寫回它們。只在兩個位址都通過核對時才有意義。
    private static byte copiedLinesOriginal;
    private static byte executedLinesOriginal;
    // 核對不過（命中不唯一、讀不到、原值不符）時由 Initialize 設；提示旗標由 framework 執行緒登入後印一次就清。
    private static bool macroLinesCheckFailed;
    private static volatile bool macroLinesCheckNoticePending;

    public static nint numCopiedMacroLinesPtr = nint.Zero;
    public static byte NumCopiedMacroLines
    {
        get => numCopiedMacroLinesPtr != nint.Zero ? *(byte*)numCopiedMacroLinesPtr : (byte)ExtendedMacro.GameLines;
        set
        {
            if (numCopiedMacroLinesPtr != nint.Zero)
                SafeMemory.WriteBytes(numCopiedMacroLinesPtr, new[] {value});
        }
    }

    public static nint numExecutedMacroLinesPtr = nint.Zero;
    public static byte NumExecutedMacroLines
    {
        get => numExecutedMacroLinesPtr != nint.Zero ? *(byte*)numExecutedMacroLinesPtr : (byte)ExtendedMacro.GameLines;
        set
        {
            if (numExecutedMacroLinesPtr != nint.Zero)
                SafeMemory.WriteBytes(numExecutedMacroLinesPtr, new[] {value});
        }
    }

    // Misc
    private const int aetherCompassID = 2_001_886;
    private static Dictionary<uint, string> usables;
    [Signature("48 8D 0D ?? ?? ?? ?? 4C 8B C0 8B D7", ScanType = ScanType.StaticAddress, Fallibility = Fallibility.Fallible)]
    private static nint performanceStruct;
    [Signature("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 45 33 C0", Fallibility = Fallibility.Fallible)]
    private static delegate* unmanaged<nint, byte, void> startPerformance;

    public static void Initialize()
    {
        // 🔴 Framework.Instance()（isPointer:true）與 GetUIModule() 都可能回 null。這裡是插件載入時的一次性就緒檢查，
        //    失敗語意＝「這個插件沒有辦法運作」，所以擲一個訊息明確的受管理例外，而不是對 null 裸解參考
        //    （攔不到的 AVE，整個遊戲閃退且堆疊指不到這裡）。呼叫端已經包在 try/catch 裡，會記錄
        //    "Failed loading QoLBar!" 並讓 pluginReady 維持 false。這裡不把任何指標存起來。
        var framework = Framework.Instance();
        if (framework == null)
            throw new InvalidOperationException("Game.Initialize: Framework.Instance() 回 null,遊戲尚未就緒。");

        if (framework->GetUIModule() == null)
            throw new InvalidOperationException("Game.Initialize: UIModule 回 null,遊戲尚未就緒。");

        // TODO change back to static whenever support is added
        //SignatureHelper.Initialise(typeof(Game));
        DalamudApi.GameInteropProvider.InitializeFromAttributes(new Game());

        ResolveMacroLineSites();

        LogUnresolved();

        usables = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().Where(i => i.ItemAction.RowId > 0).ToDictionary(i => i.RowId, i => i.Name.ToString().ToLower())
            .Concat(DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.EventItem>().Where(i => i.Action.RowId > 0).ToDictionary(i => i.RowId, i => i.Name.ToString().ToLower()))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        usables[aetherCompassID] = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.EventItem>().GetRowOrDefault(aetherCompassID)?.Name.ToString().ToLower();

        ExecuteMacroHook?.Enable();
    }

    // 改的是遊戲碼裡的立即數：特徵碼改版後若漂到別的函式，寫進去的 15～30 就成了那裡的常數（可能是迴圈上界＝越界寫入）。
    // 所以兩條都要恰好命中一處、原值等於遊戲的巨集行數才設位址；任一不過兩個都不設＝整項停用、一個位元組都不寫。
    private static void ResolveMacroLineSites()
    {
        var expected = (byte)ExtendedMacro.GameLines;
        var copiedHits = DalamudApi.SigScanner.ScanAllText(copiedLinesSig);
        var executedHits = DalamudApi.SigScanner.ScanAllText(executedLinesSig);
        var copied = MacroLinesPatchCheck.Verify(copiedHits, 0x5, expected, ReadGameByte, out var copiedSite, out var copiedValue);
        var executed = MacroLinesPatchCheck.Verify(executedHits, 0x3, expected, ReadGameByte, out var executedSite, out var executedValue);

        if (copied == MacroLinesPatchCheck.Result.Ok && executed == MacroLinesPatchCheck.Result.Ok)
        {
            copiedLinesOriginal = copiedValue;
            executedLinesOriginal = executedValue;
            numCopiedMacroLinesPtr = copiedSite;
            numExecutedMacroLinesPtr = executedSite;
            return;
        }

        // 只是沒命中＝既有的「特徵碼對不上」，交給 LogUnresolved 的 Warning 與使用時的提示。
        if (copied is MacroLinesPatchCheck.Result.Ok or MacroLinesPatchCheck.Result.NotFound
            && executed is MacroLinesPatchCheck.Result.Ok or MacroLinesPatchCheck.Result.NotFound)
            return;

        macroLinesCheckFailed = true;
        macroLinesCheckNoticePending = true;
        DalamudApi.LogError("[改版防護] 巨集行數上限的遊戲碼核對不過，不改動遊戲碼、超過 15 行的巨集停用（15 行以內照常）："
            + $"複製行數 {DescribeCheck(copied, copiedHits, 0x5, copiedValue, expected)}；"
            + $"執行行數 {DescribeCheck(executed, executedHits, 0x3, executedValue, expected)}。");
    }

    private static string DescribeCheck(MacroLinesPatchCheck.Result result, nint[] hits, int offset, byte value, byte expected)
    {
        string Rva() => $"+0x{(long)(hits[0] + offset - DalamudApi.SigScanner.Module.BaseAddress):X}";
        return result switch
        {
            MacroLinesPatchCheck.Result.Ok => $"通過（{Rva()}＝{value}）",
            MacroLinesPatchCheck.Result.NotFound => "特徵碼沒有命中",
            MacroLinesPatchCheck.Result.Ambiguous => $"特徵碼命中 {hits.Length} 處（應為 1 處）",
            MacroLinesPatchCheck.Result.Unreadable => $"讀不到 {Rva()}",
            MacroLinesPatchCheck.Result.Mismatch => $"{Rva()} 讀到 {value}（應為 {expected}）",
            _ => result.ToString(),
        };
    }

    private static byte? ReadGameByte(nint address) => SafeMemory.Read<byte>(address, out var value) ? value : null;

    /// <summary>framework 執行緒每幀呼叫。核對不過的提示等登入後才印（登入前印進聊天可能看不到），只印一次。</summary>
    public static void ShowPendingNotices()
    {
        if (!macroLinesCheckNoticePending || !DalamudApi.ClientState.IsLoggedIn) return;
        macroLinesCheckNoticePending = false;
        QoLBar.PrintError("The game code for the macro line limit does not match what QoL Bar expects, so it was left untouched and macros using more than 15 lines are disabled until QoL Bar is updated for this game version.".Loc());
    }

    // 載入時每項印一次，寫給看 log 的人：少了哪個遊戲函式、停掉哪一項。
    private static void LogUnresolved()
    {
        if (ProcessChatBox == null)
            DalamudApi.LogWarning("[改版防護] 找不到「送出指令」的遊戲函式（特徵碼對不上）：快捷列按鈕與快捷鍵的指令送不出去（按下時會在聊天視窗提示）；列的顯示、//m 巨集、//i 道具不受影響。");
        if (GetCommandHandler == null)
            DalamudApi.LogWarning("[改版防護] 找不到「查詢指令種類」的遊戲函式（特徵碼對不上）：聊天發言類指令不再排隊間隔送出，超過 15 行的巨集停用；其餘指令照常。");
        if (ExecuteMacroHook == null)
            DalamudApi.LogWarning("[改版防護] 找不到「執行巨集」的遊戲函式（特徵碼對不上）：//m 巨集停用（按下時會在聊天視窗提示）。");
        if (!ExtendedMacroLinesAvailable && !macroLinesCheckFailed)
            DalamudApi.LogWarning("[改版防護] 找不到巨集行數上限的兩個位址（特徵碼對不上）：超過 15 行的巨集停用，15 行以內照常。");
        if (startPerformance == null || performanceStruct == nint.Zero)
            DalamudApi.LogWarning("[改版防護] 找不到「開始演奏」的遊戲函式或資料位址（特徵碼對不上）：/performance 停用（使用時會在聊天視窗提示）。");
    }

    public static void ExecuteMacroDetour(RaptureShellModule* raptureShellModule, nint macro)
    {
        NumCopiedMacroLines = (byte)ExtendedMacro.GameLines;
        NumExecutedMacroLines = (byte)ExtendedMacro.GameLines;
        ExecuteMacroHook!.OriginalDisposeSafe(raptureShellModule, macro);
    }

    public static void ReadyCommand()
    {
        if (chatQueueTimer > 0 && (chatQueueTimer -= Dalamud.Bindings.ImGui.ImGui.GetIO().DeltaTime) <= 0 && chatQueue.Count > 0)
            ExecuteCommand(chatQueue.Dequeue(), true);

        if (retryItem > 0)
        {
            commandReady = false;
            UseItem(retryItem); // Gross bandaid to "fix" failed items
            retryItem = 0;
        }
        else
        {
            commandReady = true;
            RunCommandQueue();
        }

        if (!commandReady) return;

        macroMode = false;

        // If the user forgot to close off the macro with "//m" then try to execute it now
        if (macroQueue.Count > 0)
            CreateAndExecuteMacro();
    }

    public static void QueueCommand(string command)
    {
        foreach (var c in command.Split('\n'))
        {
            if (!string.IsNullOrEmpty(c))
                commandQueue.Enqueue(c[..Math.Min(c.Length, maxCommandLength)]);
        }
    }

    private static void RunCommandQueue()
    {
        while (commandQueue.Count > 0 && commandReady)
        {
            commandReady = false;
            var command = commandQueue.Dequeue();

            if (command.StartsWith("//"))
            {
                command = command[2..].ToLower();
                switch (command[0])
                {
                    case 'm': // Execute Macro
                        try
                        {
                            if (ExecuteMacroHook == null)
                            {
                                QoLBar.PrintError("Macro execution is unavailable on this game client.".Loc());
                            }
                            else if (int.TryParse(command[1..], out var macro))
                            {
                                if (macro is >= 0 and < 200)
                                {
                                    // 兩個模組每次重查，取不到就走既有的 catch
                                    //（會印「Failed running macro」），不要對 null 取位址。
                                    var macroModule = raptureMacroModule;
                                    var shell = raptureShellModule;
                                    if (macroModule == null || shell == null)
                                        throw new InvalidOperationException("RaptureMacroModule/RaptureShellModule is unavailable.");

                                    if (macro < 100)
                                    {
                                        fixed (void* ptr = &macroModule->Individual[macro])
                                            ExecuteMacroHook.OriginalDisposeSafe(shell, (nint)ptr);
                                    }
                                    else
                                    {
                                        fixed (void* ptr = &macroModule->Shared[macro - 100])
                                            ExecuteMacroHook.OriginalDisposeSafe(shell, (nint)ptr);
                                    }
                                }
                                else
                                {
                                    QoLBar.PrintError("Invalid macro. Usage: \"//m0\" for individual macro #0, \"//m100\" for shared macro #0, valid up to 199.".Loc());
                                }
                            }
                            else
                            {
                                if (macroMode)
                                {
                                    macroMode = false;
                                    CreateAndExecuteMacro();
                                }
                                else
                                {
                                    macroMode = true;
                                    commandReady = true;
                                }
                            }
                        }
                        catch { QoLBar.PrintError("Failed running macro".Loc()); }
                        break;
                    case 'i': // Item
                        if (!macroMode)
                        {
                            if (uint.TryParse(command[2..], out var id))
                                UseItem(id);
                            else
                                UseItem(command[2..]);
                        }
                        else
                        {
                            QoLBar.PrintError("Macros do not support item usage.".Loc());
                        }
                        break;
                    case ' ': // Comment
                        commandReady = true;
                        break;
                }
            }
            else
            {
                if (macroMode)
                {
                    if (macroQueue.Count < ExtendedMacro.numLines)
                    {
                        macroQueue.Enqueue(command + "\0");
                        commandReady = true;
                    }
                    else
                        QoLBar.PrintError("Failed to add command to macro, capacity reached. Please close off the macro with another \"//m\" if you didn't intend to do this.".Loc());
                }
                else
                    ExecuteCommand(command, IsChatSendCommand(command));
            }
        }
    }

    private static long lastCommandUnavailableNotice = -commandUnavailableNoticeInterval;
    private const long commandUnavailableNoticeInterval = 5000;

    public static void ExecuteCommand(string command, bool chat = false)
    {
        if (ProcessChatBox == null)
        {
            // 一顆按鈕可以有很多行指令，提示每 5 秒最多一次。
            var now = Environment.TickCount64;
            if (now - lastCommandUnavailableNotice >= commandUnavailableNoticeInterval)
            {
                lastCommandUnavailableNotice = now;
                QoLBar.PrintError("Commands cannot be sent until QoL Bar is updated for this game version.".Loc());
            }
            return;
        }

        var stringPtr = nint.Zero;

        try
        {
            stringPtr = Marshal.AllocHGlobal(sizeof(Utf8String));
            using var str = new TempUtf8String((Utf8String*)stringPtr, command);

            if (!chat || chatQueueTimer <= 0)
            {
                if (chat)
                    chatQueueTimer = 1f / 6f;

                // 取不到 UI 模組就走既有的 catch（會印「Failed injecting command」），
                // 不要把 null 當 this 指標交給原生函式。
                var ui = uiModule;
                if (ui == null)
                    throw new InvalidOperationException("UIModule is unavailable.");

                ProcessChatBox(ui, stringPtr, nint.Zero, 0);
            }
            else
                chatQueue.Enqueue(command);
        }
        catch { QoLBar.PrintError("Failed injecting command".Loc()); }

        Marshal.FreeHGlobal(stringPtr);
    }

    public static bool IsChatSendCommand(string command)
    {
        var split = command.IndexOf(' ');
        if (split < 1) return split == 0 || !command.StartsWith("/");

        // 查不到指令種類時回 false（不排隊間隔送出）；超過 15 行的巨集另外在 CreateAndExecuteMacro 擋。
        if (GetCommandHandler == null) return false;

        var handler = 0;
        var stringPtr = nint.Zero;

        try
        {
            stringPtr = Marshal.AllocHGlobal(sizeof(Utf8String));
            using var str = new TempUtf8String((Utf8String*)stringPtr, command.Substring(0, split));
            // 取不到就讓 handler 維持 0，下面的 switch 會落在 _ => false，
            // 也就是「不是聊天發言指令」—— 這是保守的預設方向。
            var shell = raptureShellModule;
            if (shell != null)
                handler = GetCommandHandler(shell, stringPtr, nint.Zero);
        }
        catch { }

        Marshal.FreeHGlobal(stringPtr);

        // TODO probably swap to using the TextCommand.csv and checking 2nd to last column since it appears to be flags of some sort (all of the chat senders including echo are 1021, say is 1023)
        return handler switch
        {
            8 or (>= 13 and <= 20) or (>= 91 and <= 119 and not 116) => true,
            _ => false,
        };
    }

    private static void CreateAndExecuteMacro()
    {
        var macroPtr = nint.Zero;

        try
        {
            if (ExecuteMacroHook == null)
                throw new InvalidOperationException("Macro execution is unavailable on this game client.");

            var count = (byte)Math.Max(ExtendedMacro.GameLines, macroQueue.Count);
            // 刻意不放行：行數位址沒解到時遊戲只認 15 行；分不出聊天指令時無法確認這個巨集安全。
            if (count > ExtendedMacro.GameLines && (!ExtendedMacroLinesAvailable || GetCommandHandler == null))
            {
                QoLBar.PrintError("Macros using more than 15 lines are unavailable until QoL Bar is updated for this game version.".Loc());
                throw new InvalidOperationException();
            }

            if (count > ExtendedMacro.GameLines && macroQueue.Any(IsChatSendCommand))
            {
                QoLBar.PrintError("Macros using more than 15 lines do not support chat message commands!".Loc());
                throw new InvalidOperationException();
            }

            macroPtr = Marshal.AllocHGlobal(ExtendedMacro.size);
            using var macro = new ExtendedMacro(macroPtr, string.Empty, macroQueue.ToArray());

            NumCopiedMacroLines = count;
            NumExecutedMacroLines = count;

            var shell = raptureShellModule;
            if (shell == null)
                throw new InvalidOperationException("RaptureShellModule is unavailable.");

            ExecuteMacroHook.OriginalDisposeSafe(shell, macroPtr);

            NumCopiedMacroLines = (byte)ExtendedMacro.GameLines;
        }
        catch { QoLBar.PrintError("Failed injecting macro".Loc()); }

        Marshal.FreeHGlobal(macroPtr);
        macroQueue.Clear();
    }

    public static AtkUnitBase* GetAddonStructByName(string name, int index) => (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName(name, index).Address;

    public static AtkUnitBase* GetFocusedAddon()
    {
        // AtkStage.Instance() 是 [StaticAddress(..., isPointer: true)]：特徵碼沒解析到才會擲例外，
        // 解析到但遊戲還沒建好 AtkStage 時回傳的是 null 指標。RaptureAtkUnitManager 則是純指標欄位。
        // 兩者任一為 null 時裸解參考產生的是 try/catch 攔不到的 AccessViolationException。
        var stage = AtkStage.Instance();
        if (stage == null) return null;

        var unitManager = stage->RaptureAtkUnitManager;
        if (unitManager == null) return null;

        var entries = unitManager->AtkUnitManager.FocusedUnitsList.Entries;
        int count = unitManager->AtkUnitManager.FocusedUnitsList.Count;
        // Count 是 ushort 而 Entries 固定長 256，理論上不該超出；超出時取不到就回 null（fail-closed），
        // 不要讓 UI 繪製路徑丟 IndexOutOfRangeException。
        if (count <= 0 || count > entries.Length) return null;

        return entries[count - 1].Value;
    }

    public static void UseItem(uint id)
    {
        if (id == 0 || !usables.ContainsKey(id is >= 1_000_000 and < 2_000_000 ? id - 1_000_000 : id)) return;

        // Aether Compass support
        if (id == aetherCompassID)
        {
            ActionManager.Instance()->UseAction(ActionType.Action, 26988);
            return;
        }

        // Dumb fix for dumb bug
        if (retryItem == 0 && id < 2_000_000)
        {
            var actionID = ActionManager.GetSpellIdForAction(ActionType.Item, id);
            if (actionID == 0)
            {
                retryItem = id;
                return;
            }
        }

        // 未登入時代理人不存在。與本函式開頭那個「不是可用道具就直接返回」同一個語意：
        // 安靜返回（那個狀態下按下道具按鈕本來就不該有動作）。
        var agent = agentInventoryContext;
        if (agent == null) return;

        agent->UseItem(id);
    }

    public static void UseItem(string name)
    {
        if (usables == null || string.IsNullOrWhiteSpace(name)) return;

        var newName = name.Replace("\uE03C", ""); // Remove HQ Symbol
        var useHQ = newName != name;
        newName = newName.ToLower().Trim(' ');

        try { UseItem(usables.First(i => i.Value == newName).Key + (uint)(useHQ ? 1_000_000 : 0)); }
        catch { }
    }

    public static float GetRecastTime(ActionType actionType, uint actionID)
    {
        var recast = ActionManager.Instance()->GetRecastTime(actionType, actionID);
        if (recast == 0) return -1;
        return recast;
    }

    public static float GetRecastTime(byte actionType, uint actionID) => GetRecastTime((ActionType)actionType, actionID);

    public static float GetRecastTimeElapsed(ActionType actionType, uint actionID) => ActionManager.Instance()->GetRecastTimeElapsed(actionType, actionID);

    public static float GetRecastTimeElapsed(byte actionType, uint actionID) => GetRecastTimeElapsed((ActionType)actionType, actionID);

    public static void StartPerformance(byte instrument)
    {
        // performanceStruct 是交給遊戲函式當 this 的位址，0 不可以送進去。
        if (startPerformance == null || performanceStruct == nint.Zero)
        {
            QoLBar.PrintError("Performance cannot be started until QoL Bar is updated for this game version.".Loc());
            return;
        }

        startPerformance(performanceStruct, instrument);
    }

    public static void Dispose()
    {
        ExecuteMacroHook?.Dispose();
        // 寫回核對時讀到的原值；沒通過核對時兩個位址都是 0，setter 不寫。
        NumCopiedMacroLines = copiedLinesOriginal;
        NumExecutedMacroLines = executedLinesOriginal;
    }
}