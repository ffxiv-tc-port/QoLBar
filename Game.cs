using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
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

    // 🔴 下面這五個原本是 static 欄位，由 Initialize() 在外掛載入時解析一次就存著，
    //    之後跨幀、跨換區、跨登出重登都沿用同一個值，沒有任何一條路徑會重查或歸零 ——
    //    那正是艦隊紅線「絕不跨幀保存原生指標」講的形狀。登出回標題畫面時整棵 UIModule
    //    樹會被拆掉，留在欄位裡的舊指標之後任何一次解參考都是 AccessViolationException
    //    （corrupted-state exception，try/catch 與 HookSafety.ExecuteSafe 都攔不到）。
    // ⇒ 全部改成每次存取重查的屬性。取法直接用 ClientStructs 自帶的 Instance()，它們逐字
    //    就是「上一層為 null 就回 null」的判空鏈（UIModule.Instance() → Framework.Instance()
    //    （[StaticAddress(..., isPointer: true)]，可能為 null）→ GetUIModule()）。
    //    重查成本是幾個 vtable 跳轉：GetRaptureShellModule(9) / GetRaptureMacroModule(12) /
    //    GetAddonConfig(19) / GetAgentModule(37) 全是 [VirtualFunction]，不掃特徵碼。
    // ⚠️ 呼叫端的寫法完全不用改（名稱與型別都保持原樣），但它們現在拿得到 null，
    //    所以本檔每一個消費點都補上了判空。
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

    // Command Execution
    public delegate void ProcessChatBoxDelegate(UIModule* uiModule, nint message, nint unused, byte a4);
    [Signature("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B F2 48 8B F9 45 84 C9")]
    public static ProcessChatBoxDelegate ProcessChatBox;

    public delegate int GetCommandHandlerDelegate(RaptureShellModule* raptureShellModule, nint message, nint unused);
    [Signature("E8 ?? ?? ?? ?? 66 89 06 66 85 C0")]
    public static GetCommandHandlerDelegate GetCommandHandler;

    // Macro Execution
    public delegate void ExecuteMacroDelegate(RaptureShellModule* raptureShellModule, nint macro);
    [Signature("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 8D 4E ?? 49 8B D6", Fallibility = Fallibility.Fallible)]
    public static Hook<ExecuteMacroDelegate>? ExecuteMacroHook;
    // 同上：改成每次存取重查，不再是 Initialize() 存下來的跨幀指標。
    public static RaptureShellModule* raptureShellModule => RaptureShellModule.Instance();
    public static RaptureMacroModule* raptureMacroModule => RaptureMacroModule.Instance();

    public static nint numCopiedMacroLinesPtr = nint.Zero;
    public static byte NumCopiedMacroLines
    {
        get => *(byte*)numCopiedMacroLinesPtr;
        set
        {
            if (numCopiedMacroLinesPtr != nint.Zero)
                SafeMemory.WriteBytes(numCopiedMacroLinesPtr, new[] {value});
        }
    }

    public static nint numExecutedMacroLinesPtr = nint.Zero;
    public static byte NumExecutedMacroLines
    {
        get => *(byte*)numExecutedMacroLinesPtr;
        set
        {
            if (numExecutedMacroLinesPtr != nint.Zero)
                SafeMemory.WriteBytes(numExecutedMacroLinesPtr, new[] {value});
        }
    }

    // Misc
    private const int aetherCompassID = 2_001_886;
    private static Dictionary<uint, string> usables;
    [Signature("48 8D 0D ?? ?? ?? ?? 4C 8B C0 8B D7", ScanType = ScanType.StaticAddress)]
    private static nint performanceStruct;
    [Signature("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 45 33 C0", Fallibility = Fallibility.Fallible)]
    private static delegate* unmanaged<nint, byte, void> startPerformance;

    public static void Initialize()
    {
        // 🔴 Framework.Instance()（isPointer:true）與 GetUIModule() 都可能回 null。
        //    這裡是外掛載入時的一次性初始化,失敗語意＝「這個外掛沒有辦法運作」,
        //    所以擲一個訊息明確的受管理例外,而不是讓下面三行對 null 裸解參考
        //    ——後者是攔不到的 AVE,會整個遊戲閃退且堆疊指不到這裡。
        //    呼叫端 QoLBar.cs:70 已經包在 try/catch 裡,會記錄 "Failed loading QoLBar!"
        //    並讓 pluginReady 維持 false,這是既有的失敗路徑,不需要新增處理。
        //    ⚠️ 這段現在只是「載入時的就緒檢查」，不再把任何指標存起來 ——
        //       上面那五個取得器每次存取都會自己重查。
        var framework = Framework.Instance();
        if (framework == null)
            throw new InvalidOperationException("Game.Initialize: Framework.Instance() 回 null,遊戲尚未就緒。");

        if (framework->GetUIModule() == null)
            throw new InvalidOperationException("Game.Initialize: UIModule 回 null,遊戲尚未就緒。");

        // TODO change back to static whenever support is added
        //SignatureHelper.Initialise(typeof(Game));
        DalamudApi.GameInteropProvider.InitializeFromAttributes(new Game());

        numCopiedMacroLinesPtr = DalamudApi.SigScanner.ScanText("48 8D 77 70 BF ?? 00 00 00") + 0x5;
        numExecutedMacroLinesPtr = DalamudApi.SigScanner.ScanText("41 83 F8 ?? 0F 8D ?? ?? ?? ?? 49 6B C8 68") + 0x3;
        usables = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().Where(i => i.ItemAction.RowId > 0).ToDictionary(i => i.RowId, i => i.Name.ToString().ToLower())
            .Concat(DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.EventItem>().Where(i => i.Action.RowId > 0).ToDictionary(i => i.RowId, i => i.Name.ToString().ToLower()))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        usables[aetherCompassID] = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.EventItem>().GetRowOrDefault(aetherCompassID)?.Name.ToString().ToLower();

        ExecuteMacroHook?.Enable();
    }

    public static void ExecuteMacroDetour(RaptureShellModule* raptureShellModule, nint macro)
    {
        NumCopiedMacroLines = Macro.numLines;
        NumExecutedMacroLines = Macro.numLines;
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
                                    // 兩個模組都改成重查，取不到就走既有的 catch
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

    public static void ExecuteCommand(string command, bool chat = false)
    {
        var stringPtr = nint.Zero;

        try
        {
            stringPtr = Marshal.AllocHGlobal(UTF8String.size);
            using var str = new UTF8String(stringPtr, command);
            Marshal.StructureToPtr(str, stringPtr, false);

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

        var handler = 0;
        var stringPtr = nint.Zero;

        try
        {
            stringPtr = Marshal.AllocHGlobal(UTF8String.size);
            using var str = new UTF8String(stringPtr, command.Substring(0, split));
            Marshal.StructureToPtr(str, stringPtr, false);
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

            var count = (byte)Math.Max(Macro.numLines, macroQueue.Count);
            if (count > Macro.numLines && macroQueue.Any(IsChatSendCommand))
            {
                QoLBar.PrintError("Macros using more than 15 lines do not support chat message commands!".Loc());
                throw new InvalidOperationException();
            }

            macroPtr = Marshal.AllocHGlobal(ExtendedMacro.size);
            using var macro = new ExtendedMacro(macroPtr, string.Empty, macroQueue.ToArray());
            Marshal.StructureToPtr(macro, macroPtr, false);

            NumCopiedMacroLines = count;
            NumExecutedMacroLines = count;

            var shell = raptureShellModule;
            if (shell == null)
                throw new InvalidOperationException("RaptureShellModule is unavailable.");

            ExecuteMacroHook.OriginalDisposeSafe(shell, macroPtr);

            NumCopiedMacroLines = Macro.numLines;
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
        if (startPerformance != null)
            startPerformance(performanceStruct, instrument);
    }

    public static void Dispose()
    {
        ExecuteMacroHook?.Dispose();
        NumCopiedMacroLines = 15;
        NumExecutedMacroLines = 15;
    }
}