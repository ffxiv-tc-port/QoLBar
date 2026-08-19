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

    public static UIModule* uiModule;

    public static bool IsGameTextInputActive => uiModule->GetRaptureAtkModule()->AtkModule.IsTextInputActive();
    public static bool IsMacroRunning => raptureShellModule->MacroCurrentLine >= 0;

    public static AgentInventoryContext* agentInventoryContext;

    public static AddonConfig* addonConfig;
    public static int CurrentHUDLayout => addonConfig->ActiveDataSet->CurrentHudLayout;

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
    public static RaptureShellModule* raptureShellModule;
    public static RaptureMacroModule* raptureMacroModule;

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
        var framework = Framework.Instance();
        if (framework == null)
            throw new InvalidOperationException("Game.Initialize: Framework.Instance() 回 null,遊戲尚未就緒。");

        uiModule = framework->GetUIModule();
        if (uiModule == null)
            throw new InvalidOperationException("Game.Initialize: UIModule 回 null,遊戲尚未就緒。");

        raptureShellModule = uiModule->GetRaptureShellModule();
        raptureMacroModule = uiModule->GetRaptureMacroModule();
        addonConfig = uiModule->GetAddonConfig();

        // TODO change back to static whenever support is added
        //SignatureHelper.Initialise(typeof(Game));
        DalamudApi.GameInteropProvider.InitializeFromAttributes(new Game());

        numCopiedMacroLinesPtr = DalamudApi.SigScanner.ScanText("48 8D 77 70 BF ?? 00 00 00") + 0x5;
        numExecutedMacroLinesPtr = DalamudApi.SigScanner.ScanText("41 83 F8 ?? 0F 8D ?? ?? ?? ?? 49 6B C8 68") + 0x3;
        agentInventoryContext = (AgentInventoryContext*)uiModule->GetAgentModule()->GetAgentByInternalId(AgentId.InventoryContext);
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
                                    if (macro < 100)
                                    {
                                        fixed (void* ptr = &raptureMacroModule->Individual[macro])
                                            ExecuteMacroHook.OriginalDisposeSafe(raptureShellModule, (nint)ptr);
                                    }
                                    else
                                    {
                                        fixed (void* ptr = &raptureMacroModule->Shared[macro - 100])
                                            ExecuteMacroHook.OriginalDisposeSafe(raptureShellModule, (nint)ptr);
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

                ProcessChatBox(uiModule, stringPtr, nint.Zero, 0);
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
            handler = GetCommandHandler(raptureShellModule, stringPtr, nint.Zero);
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

            ExecuteMacroHook.OriginalDisposeSafe(raptureShellModule, macroPtr);

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

        agentInventoryContext->UseItem(id);
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