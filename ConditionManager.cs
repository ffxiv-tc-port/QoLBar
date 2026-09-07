using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace QoLBar;

public interface IDisplayPriority
{
    public int DisplayPriority { get; }
}

public interface ICondition : IDisplayPriority
{
    public string ID { get; }
    public string ConditionName { get; }
    public bool Check(dynamic arg);
}

public interface IDrawableCondition
{
    public string GetTooltip(CndCfg cndCfg);
    public string GetSelectableTooltip(CndCfg cndCfg);
    public void Draw(CndCfg cndCfg);
}

public interface IArgCondition
{
    public dynamic GetDefaultArg(CndCfg cndCfg);
}

public interface IOnImportCondition
{
    public void OnImport(CndCfg cndCfg);
}

public interface IConditionCategory : IDisplayPriority
{
    public string CategoryName { get; }
}

public interface IConditionSetPreset
{
    public string Name { get; }
    public CndSetCfg Generate();
}

/// <summary>
/// 條件組評估結果的<b>不可變快照</b>，由 framework 執行緒每幀發布、任何執行緒都可以安全讀。
/// </summary>
/// <remarks>
/// 🔴🔴 為什麼需要它：IPC 端點 <c>QoLBar.CheckConditionSet</c> 跑在<b>呼叫端外掛的執行緒</b>上，
/// 而原本的實作直接呼叫 <see cref="ConditionManager.CheckConditionSet(int)"/>，那會
/// ①寫 <c>conditionCache</c>／<c>conditionSetCache</c>／<c>debugSteps</c>／<c>lockedSets</c>
///   四個裸容器，而 framework 執行緒每幀也在改同一批（<c>QoLBar.Update</c> 的
///   <c>Keybind.Run</c>、<c>Keybind.SetupHotkeys</c>、<c>UpdateCache</c>，以及繪製路徑的
///   <c>BarUI.IsVisible</c>／<c>PieUI</c>／<c>ConditionSetUI</c>）
///   ⇒ 失敗形式不是「拿到舊值」而是<b>字典本身壞掉</b>；
/// ②在呼叫端的執行緒上執行 <c>ICondition.Check</c>，而那裡面有一堆<b>原生遊戲記憶體讀取</b>
///   （<c>PronounModule.Instance()-&gt;ResolvePlaceholder</c>、<c>TerritoryInfo.Instance()-&gt;InSanctuary</c>、
///   <c>Game.GetAddonStructByName</c>、<c>ObjectTable.LocalPlayer</c>、<c>TargetManager</c>…）
///   ⇒ 從遊戲主執行緒以外的地方解參那些結構，失敗形式是 AccessViolation，
///   而 <c>CheckUnaryCondition</c> 的 <c>try/catch</c> <b>攔不到</b>（AVE 在 .NET Core 是
///   corrupted-state exception）；
/// ③讀 <c>QoLBar.Config.CndSetCfgs</c> 這個 <c>List&lt;T&gt;</c> 的 <c>Count</c> 與索引，
///   而它會被 <c>SwapConditionSet</c>／<c>RemoveConditionSet</c>／設定視窗在 framework 執行緒上增刪。
/// <para>
/// 📌 快照<b>只複製已經算好的結果</b>，不觸發任何評估 —— 發布程序對
/// <see cref="ConditionManager.CheckConditionSet(CndSetCfg)"/> 用到的每一個狀態都是唯讀，
/// 所以繪製與 framework 路徑的行為<b>逐位元不變</b>。
/// </para>
/// </remarks>
public sealed class ConditionSetSnapshot
{
    /// <summary>該格條件組從來沒被評估過（沒有人參照它，也沒在設定視窗打開過）。</summary>
    public const byte Unknown = 0;
    public const byte KnownFalse = 1;
    public const byte KnownTrue = 2;

    public static readonly ConditionSetSnapshot Empty = new(Array.Empty<byte>(), Array.Empty<string>());

    // 🔴 這兩個陣列建構之後絕不再寫入 —— 「不可變」是靠紀律，不是靠型別。
    private readonly byte[] states;
    private readonly string[] names;

    public ConditionSetSnapshot(byte[] states, string[] names)
    {
        this.states = states;
        this.names = names;
    }

    public int Count => states.Length;

    /// <summary>取第 <paramref name="i"/> 組的評估結果；沒有評估過或索引越界就回 <see langword="false"/>。</summary>
    public bool TryGet(int i, out bool value)
    {
        value = false;
        if (i < 0 || i >= states.Length) return false;
        var s = states[i];
        if (s == Unknown) return false;
        value = s == KnownTrue;
        return true;
    }

    /// <summary>條件組名稱的副本（給 IPC 端點回答用，呼叫端拿到的是自己的一份）。</summary>
    public string[] CopyNames() => (string[])names.Clone();

    /// <summary>內容是否與 framework 執行緒剛算好的暫存區完全相同（相同就不必換一份新的）。</summary>
    public bool Matches(byte[] otherStates, string[] otherNames)
    {
        if (states.Length != otherStates.Length || names.Length != otherNames.Length) return false;
        for (var i = 0; i < states.Length; i++)
            if (states[i] != otherStates[i]) return false;
        for (var i = 0; i < names.Length; i++)
            if (!string.Equals(names[i], otherNames[i], StringComparison.Ordinal)) return false;
        return true;
    }
}

public static class ConditionManager
{
    public enum BinaryOperator
    {
        AND,
        OR,
        EQUALS,
        XOR
    }

    private static readonly Dictionary<string, ICondition> conditions = new();
    private static readonly Dictionary<ICondition, IConditionCategory> categoryMap = new();
    private static readonly Dictionary<(ICondition, dynamic), bool> conditionCache = new();
    private static readonly Dictionary<CndSetCfg, (bool prev, float time)> conditionSetCache = new();
    private static readonly Dictionary<CndSetCfg, List<bool>> debugSteps = new();
    private static readonly HashSet<CndSetCfg> lockedSets = new();
    private static float lastConditionCache = 0;

    #region 給 IPC 端點讀的快照（framework 執行緒發布）

    // 🔴 下面這兩個暫存區只有 framework 執行緒碰（PublishSnapshot 內），不對外曝光。
    private static byte[] snapshotStates = Array.Empty<byte>();
    private static string[] snapshotNames = Array.Empty<string>();

    // 已發布的快照。寫入只在 framework 執行緒、讀取可能在任何執行緒 ⇒ 走 Volatile。
    private static ConditionSetSnapshot publishedSnapshot = ConditionSetSnapshot.Empty;

    private const long SnapshotMissLogIntervalMs = 10_000;
    private static long lastSnapshotMissLog = -1;

    // 「最近有人透過 IPC 問過第 i 組」→ 上次被問的 Environment.TickCount64。
    // 🔴 寫入端是 IPC 呼叫端的執行緒、讀取與清除端是 framework 執行緒 ⇒ 必須是 ConcurrentDictionary。
    // 上限只是防呆：呼叫端若一直丟不存在的索引，這張表也不會無限長；超過上限就不再收新的鍵
    // （ContainsKey/Count/索引指派三步不是原子的，所以實際筆數可能短暫略高於上限，無害）。
    private static readonly ConcurrentDictionary<int, long> ipcWarmRequests = new();
    private const long IpcWarmTtlMs = 5_000;
    private const int MaxIpcWarmEntries = 64;

    // 快照裡的一格若比這個秒數更舊就當成 Unknown。
    // 🔑 這個數字必須遠大於 CheckConditionSet 自己的 0.1 秒求值 TTL：被任何列參照的條件組
    // 每幀都會經由 BarUI.IsVisible 走一次 CheckConditionSet（0.1 秒內是快取命中），
    // 所以它們的 time 至少每 0.1 秒更新一次，永遠不會因為這道閘門變成 Unknown。
    // 它擋的是另一種情況：預熱停止之後，那一格會停在最後算出來的值 —— 沒有這道閘門的話
    // 那個值會一直凍在那裡，消費端幾分鐘後再問一次會拿到一個很舊卻看起來正常的答案，
    // 而且完全沒有 log。有了它，預熱停止約一秒後那一格回到 Unknown ⇒ 回 false ＋ Information。
    private const float SnapshotStaleSeconds = 1.0f;

    /// <summary>目前已發布的快照。<b>任何執行緒都可以讀。</b></summary>
    public static ConditionSetSnapshot Snapshot => Volatile.Read(ref publishedSnapshot);

    /// <summary>
    /// framework 執行緒每幀在 <see cref="UpdateCache"/> 之後呼叫，把「這一幀已經算好的」
    /// 條件組結果與名稱發布成一份不可變快照。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意只複製 <c>conditionSetCache</c> 裡現成的值，不主動評估任何條件組。</b>
    /// 主動評估會讓「沒有任何列參照的條件組」也每幀跑一遍 <c>ICondition.Check</c>
    /// （裡面有原生讀取與 <c>ResolvePlaceholder</c> 呼叫），那是白花的每幀成本，
    /// 而且會改變既有的求值時機。代價是<b>從來沒被評估過的條件組在快照裡是 Unknown</b>，
    /// IPC 端點對它回 <see langword="false"/> —— 見 <see cref="CheckConditionSetForIpc"/>。
    /// <para>
    /// 📌 內容沒變就不換物件，所以常見情況<b>零配置</b>；換的時候才複製一份新陣列。
    /// </para>
    /// </remarks>
    public static void PublishSnapshot()
    {
        var sets = QoLBar.Config.CndSetCfgs;
        var n = sets.Count;

        // 先把「最近被 IPC 問過」的條件組在這條（安全的）執行緒上算一次，結果會落進
        // conditionSetCache，下面建快照時就抄得到。
        WarmIpcRequestedSets(sets, n);

        if (snapshotStates.Length != n)
        {
            snapshotStates = new byte[n];
            snapshotNames = new string[n];
        }

        var now = QoLBar.RunTime;
        for (var i = 0; i < n; i++)
        {
            var set = sets[i];
            snapshotNames[i] = set.Name;
            snapshotStates[i] = conditionSetCache.TryGetValue(set, out var c) && now - c.time <= SnapshotStaleSeconds
                ? (c.prev ? ConditionSetSnapshot.KnownTrue : ConditionSetSnapshot.KnownFalse)
                : ConditionSetSnapshot.Unknown;
        }

        var current = Volatile.Read(ref publishedSnapshot);
        if (current.Matches(snapshotStates, snapshotNames)) return;

        Volatile.Write(ref publishedSnapshot,
            new ConditionSetSnapshot((byte[])snapshotStates.Clone(), (string[])snapshotNames.Clone()));
    }

    /// <summary>
    /// 在 framework 執行緒上評估「最近 <see cref="IpcWarmTtlMs"/> 毫秒內被 IPC 問過」的條件組。
    /// </summary>
    /// <remarks>
    /// 🔑 這是為了讓「沒有任何列參照、只有別的外掛在問」的條件組也能拿到真答案：
    /// 第一次查詢仍然 miss（回 <see langword="false"/> ＋ 一則 Information），但它會登記需求，
    /// 下一幀這裡就會把它算出來，第二次查詢起就是真答案。
    /// <para>
    /// 🔴 求值刻意放在<b>這條執行緒</b>：<c>ICondition.Check</c> 裡有原生指標解參
    /// （<c>PronounModule.Instance()-&gt;</c>、<c>TerritoryInfo.Instance()-&gt;</c>、<c>AtkStage</c>）。
    /// </para>
    /// <para>
    /// 📌 對「被列參照的條件組」零額外成本：<see cref="CheckConditionSet(CndSetCfg)"/> 自己有
    /// 0.1 秒的求值 TTL，這一幀繪製路徑已經算過的話這裡只是一次字典命中。
    /// </para>
    /// <para>
    /// 📌 沒有另設每幀上限：能進到這張表的鍵最多 <see cref="MaxIpcWarmEntries"/> 個，
    /// 而且逾時就被清掉。
    /// </para>
    /// </remarks>
    private static void WarmIpcRequestedSets(List<CndSetCfg> sets, int n)
    {
        if (ipcWarmRequests.IsEmpty) return;

        var now = Environment.TickCount64;
        foreach (var kv in ipcWarmRequests)
        {
            if (now - kv.Value >= IpcWarmTtlMs)
            {
                ipcWarmRequests.TryRemove(kv.Key, out _);
                continue;
            }

            var i = kv.Key;
            if (i < 0 || i >= n) continue;

            CheckConditionSet(sets[i]);
        }
    }

    /// <summary>IPC 端點 <c>QoLBar.CheckConditionSet</c> 的實作。<b>跑在呼叫端外掛的執行緒上。</b></summary>
    /// <remarks>
    /// 🔴 這裡<b>只讀快照</b>：不評估、不寫任何共用容器、不碰 <c>QoLBar.Config.CndSetCfgs</c>、
    /// 不呼叫任何 <c>ICondition.Check</c>（那裡面有原生記憶體讀取）。端點的簽章與同步語意不變
    /// —— 一樣是「立刻回答」。
    /// <para>
    /// ⚠️ <b>行為差異</b>：快照裡沒有這一組（＝這一幀之前從來沒有人評估過它，通常表示沒有任何
    /// 列參照它、設定視窗也沒打開過）時回 <see langword="false"/>，與「索引不存在」同值。
    /// 舊實作在這種情況會當場算一次並回真正的答案。走到這條會寫一則節流過的 Information。
    /// </para>
    /// </remarks>
    public static bool CheckConditionSetForIpc(int i)
    {
        // 登記「有人在問這一組」。framework 執行緒下一幀會把它算出來（見 WarmIpcRequestedSets），
        // 所以只有第一次查詢會 miss。這裡刻意不做任何求值。
        RegisterIpcWarmRequest(i);

        var snapshot = Snapshot;
        if (snapshot.TryGet(i, out var value)) return value;

        LogSnapshotMiss(i, snapshot.Count);
        return false;
    }

    // 跑在 IPC 呼叫端的執行緒上。只碰 ConcurrentDictionary，不碰任何裸容器、不求值。
    private static void RegisterIpcWarmRequest(int i)
    {
        if (i < 0) return;
        if (ipcWarmRequests.Count >= MaxIpcWarmEntries && !ipcWarmRequests.ContainsKey(i)) return;
        ipcWarmRequests[i] = Environment.TickCount64;
    }

    /// <summary>IPC 端點 <c>QoLBar.GetConditionSets</c> 的實作。<b>跑在呼叫端外掛的執行緒上。</b></summary>
    /// <remarks>
    /// 🔴 舊實作是 <c>QoLBar.Config.CndSetCfgs.Select(s =&gt; s.Name).ToArray()</c> ——
    /// 從別的執行緒<b>走訪一個 framework 執行緒正在增刪的 <c>List&lt;T&gt;</c></b>
    /// （<c>SwapConditionSet</c> 的 RemoveAt/Insert、<c>RemoveConditionSet</c> 的 RemoveAt、
    /// 設定視窗的 Add）⇒ 好一點是 <c>InvalidOperationException</c>，壞一點是讀到撕裂的內部陣列。
    /// 改成從同一份快照回答。
    /// </remarks>
    public static string[] GetConditionSetNamesForIpc() => Snapshot.CopyNames();

    // 節流：EzThrottler 不是執行緒安全的、而且是全外掛共用（本外掛沒用 ECommons，但同一條理由成立），
    // 所以自己用 CAS 做。第一次一定放行；之後每 10 秒最多一則。
    private static void LogSnapshotMiss(int i, int count)
    {
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref lastSnapshotMissLog);
        if (last >= 0 && now - last < SnapshotMissLogIntervalMs) return;
        if (Interlocked.CompareExchange(ref lastSnapshotMissLog, now, last) != last) return;

        DalamudApi.LogInfo($"[QoL Bar] IPC CheckConditionSet({i}) answered false from the published snapshot "
                           + $"(snapshot has {count} set(s), but no evaluated result for that index). "
                           + "A condition set that no bar references is never evaluated, so it stays unknown. "
                           + "Please report this if you expected a real answer.");
    }

    #endregion

    public static List<(IConditionCategory category, List<ICondition> conditions)> ConditionCategories { get; private set; } = new();
    public static List<IConditionSetPreset> Presets { get; } = new();

    public static void Initialize()
    {
        foreach (var t in Assembly.GetExecutingAssembly().GetTypes().Where(t => t.IsAssignableTo(typeof(IConditionCategory)) && !t.IsInterface))
        {
            var category = (IConditionCategory)Activator.CreateInstance(t);
            if (category == null) continue;

            var list = new List<ICondition>();
            ConditionCategories.Add((category, list));
            if (!t.IsAssignableTo(typeof(ICondition))) continue;

            list.Add((ICondition)category);
        }

        foreach (var t in Assembly.GetExecutingAssembly().GetTypes().Where(t => t.IsAssignableTo(typeof(ICondition)) && !t.IsInterface))
        {
            var condition = (ICondition)Activator.CreateInstance(t);
            if (condition == null) continue;

            conditions.Add(condition.ID, condition);

            var categoryType = t.GetCustomAttributes().FirstOrDefault(attr => attr.GetType().IsAssignableTo(typeof(IConditionCategory)))?.GetType();
            if (categoryType == null)
            {
                if (t.IsAssignableTo(typeof(IConditionCategory)))
                    categoryMap.Add(condition, (IConditionCategory)condition);
                continue;
            }

            var (category, list) = ConditionCategories.FirstOrDefault(tuple => tuple.category.GetType() == categoryType);
            if (category == null) continue;

            list.Add(condition);
            categoryMap.Add(condition, category);
        }

        ConditionCategories = ConditionCategories.OrderBy(t => t.category.DisplayPriority).ToList();
        for (int i = 0; i < ConditionCategories.Count; i++)
        {
            var (category, list) = ConditionCategories[i];
            ConditionCategories[i] = (category, list.OrderBy(c => c.DisplayPriority).ToList());
        }

        foreach (var t in Assembly.GetExecutingAssembly().GetTypes().Where(t => t.IsAssignableTo(typeof(IConditionSetPreset)) && !t.IsInterface))
        {
            var preset = (IConditionSetPreset)Activator.CreateInstance(t);
            if (preset == null) continue;
            Presets.Add(preset);
        }
    }

    public static ICondition GetCondition(string id) => conditions.TryGetValue(id, out var condition) ? condition : null;

    public static IConditionCategory GetConditionCategory(ICondition condition) => categoryMap[condition];

    public static IConditionCategory GetConditionCategory(string id) => GetConditionCategory(GetCondition(id));

    public static bool CheckCondition(string id, dynamic arg = null, bool negate = false)
    {
        var condition = GetCondition(id);
        return condition != null && (!negate ? CheckCondition(condition, arg, null) : !CheckCondition(condition, arg, null));
    }

    private static bool CheckCondition(ICondition condition, dynamic arg, string setName)
    {
        if (conditionCache.TryGetValue((condition, arg), out bool cache)) // ReSharper / Rider hates this being a var for some reason
            return cache;

        try
        {
            cache = condition.Check(arg);
        }
        catch (Exception e)
        {
            // 舊版是裸 catch 什麼都不留 ⇒ 條件永遠算成 false 而完全查不到原因。
            // 回傳值與快取行為一個位元都沒變，只是多留一條線索。
            LogConditionError(condition, setName, e);
            cache = false;
        }

        conditionCache[(condition, arg)] = cache;
        return cache;
    }

    private static bool CheckUnaryCondition(bool negate, ICondition condition, dynamic arg, string setName)
    {
        try
        {
            return !negate ? condition.Check(arg) : !condition.Check(arg);
        }
        catch (Exception e)
        {
            // 同上：條件擲例外時仍然算成 false（行為不變），但至少留下一條可回報的線索。
            LogConditionError(condition, setName, e);
            return false;
        }
    }

    private static bool CheckBinaryCondition(bool prev, BinaryOperator op, bool negate, ICondition condition, dynamic arg, string setName)
    {
        return op switch
        {
            BinaryOperator.AND => prev && CheckUnaryCondition(negate, condition, arg, setName),
            BinaryOperator.OR => prev || CheckUnaryCondition(negate, condition, arg, setName),
            BinaryOperator.EQUALS => prev == CheckUnaryCondition(negate, condition, arg, setName),
            BinaryOperator.XOR => prev ^ CheckUnaryCondition(negate, condition, arg, setName),
            _ => prev
        };
    }

    public static bool CheckConditionSet(int i) => i >= 0 && i < QoLBar.Config.CndSetCfgs.Count && CheckConditionSet(QoLBar.Config.CndSetCfgs[i]);

    public static bool CheckConditionSet(CndSetCfg set)
    {
        if (lockedSets.Contains(set))
            return conditionSetCache.TryGetValue(set, out var c) && c.prev;

        if (conditionSetCache.TryGetValue(set, out var cache) && QoLBar.RunTime <= cache.time + (QoLBar.Config.NoConditionCache ? 0 : 0.1f))
            return cache.prev;

        lockedSets.Add(set);

        var first = true;
        var prev = true;
        var steps = new List<bool>();
        foreach (var cnd in set.Conditions)
        {
            var condition = GetCondition(cnd.ID);
            if (condition == null) continue;

            if (first)
            {
                prev = CheckUnaryCondition(cnd.Negate, condition, cnd.Arg, set.Name);
                first = false;
            }
            else
            {
                prev = CheckBinaryCondition(prev, cnd.Operator, cnd.Negate, condition, cnd.Arg, set.Name);
            }

            steps.Add(prev);
        }

        lockedSets.Remove(set);

        conditionSetCache[set] = (prev, QoLBar.RunTime);
        debugSteps[set] = steps;
        return prev;
    }

    #region 條件擲例外時的節流診斷

    // 🔴 為什麼要有這一段：CheckCondition／CheckUnaryCondition 原本是裸 catch，把任何例外
    // 都吞成 false 而且一個字都不留。ICondition.Check 裡有原生指標解參與 FFXIVClientStructs
    // 的產生式 Instance()（位址解不出來時擲 InvalidOperationException，訊息裡逐字帶著特徵碼），
    // 所以「條件組莫名其妙一直不成立」在台服是真的會發生、而且完全查不到原因。
    // ⚠️ 回傳值與快取行為完全不變，這裡只多寫一則 log。

    // 鍵＝(條件型別 ID, 條件組名)。上限只是防呆（使用者反覆改名可能長出新鍵），滿了就整個清掉。
    private static readonly Dictionary<(string conditionId, string setName), long> lastConditionErrorLog = new();
    private static readonly object conditionErrorLogLock = new();
    private const long ConditionErrorLogIntervalMs = 60_000;
    private const int MaxConditionErrorLogKeys = 256;

    /// <summary>條件擲例外時寫一則節流過的 Information。回傳值與行為完全不受影響。</summary>
    /// <remarks>
    /// 🔴 <b>log 呼叫在鎖外面。</b> 這段跑在 framework 執行緒上（含 <c>WarmIpcRequestedSets</c> 的
    /// 預熱路徑），鎖內只做時間比對與記錄，決定要不要寫之後<b>先放掉鎖</b>再呼叫 <c>LogInfo</c> ——
    /// 鎖內做 I/O 是全艦隊反覆踩到的形狀。
    /// </remarks>
    private static void LogConditionError(ICondition condition, string setName, Exception e)
    {
        var id = condition?.ID ?? "?";
        if (!ShouldLogConditionError(id, setName)) return;

        DalamudApi.LogInfo($"[QoL Bar] Condition '{id}' in condition set '{setName ?? "(none)"}' threw "
                           + $"{e.GetType().Name}: {e.Message} — it is being evaluated as false. "
                           + "Please report this if a condition set is not behaving as expected.");
    }

    // 鎖內絕不做 I/O：只比時間、記時間、回一個 bool。
    private static bool ShouldLogConditionError(string conditionId, string setName)
    {
        var now = Environment.TickCount64;
        var key = (conditionId, setName ?? string.Empty);

        lock (conditionErrorLogLock)
        {
            if (lastConditionErrorLog.TryGetValue(key, out var last) && now - last < ConditionErrorLogIntervalMs)
                return false;

            if (lastConditionErrorLog.Count >= MaxConditionErrorLogKeys && !lastConditionErrorLog.ContainsKey(key))
                lastConditionErrorLog.Clear();

            lastConditionErrorLog[key] = now;
            return true;
        }
    }

    #endregion

    public static List<bool> GetDebugSteps(CndSetCfg set) => debugSteps.TryGetValue(set, out var steps) ? steps : null;

    public static void UpdateCache()
    {
        if (QoLBar.Config.NoConditionCache)
        {
            conditionCache.Clear();
            return;
        }

        if (QoLBar.RunTime < lastConditionCache + 0.1f) return;

        conditionCache.Clear();
        lastConditionCache = QoLBar.RunTime;
    }

    public static void SwapConditionSet(int from, int to)
    {
        var set = QoLBar.Config.CndSetCfgs[from];

        foreach (var bar in QoLBar.Config.BarCfgs)
        {
            if (bar.ConditionSet == from)
                bar.ConditionSet = to;
            else if (bar.ConditionSet == to)
                bar.ConditionSet = from;
        }

        foreach (var condition in from s in QoLBar.Config.CndSetCfgs from condition in s.Conditions where condition.ID == Conditions.ConditionSetCondition.constID select condition)
        {
            if (condition.Arg == from)
                condition.Arg = to;
            else if (condition.Arg == to)
                condition.Arg = from;
        }

        QoLBar.Config.CndSetCfgs.RemoveAt(from);
        QoLBar.Config.CndSetCfgs.Insert(to, set);
        QoLBar.Config.Save();

        IPC.MovedConditionSetProvider.SendMessage(from, to);
    }

    public static void RemoveConditionSet(int i)
    {
        foreach (var bar in QoLBar.Config.BarCfgs)
        {
            if (bar.ConditionSet > i)
                bar.ConditionSet -= 1;
            else if (bar.ConditionSet == i)
                bar.ConditionSet = -1;
        }

        foreach (var s in QoLBar.Config.CndSetCfgs)
        {
            for (int j = s.Conditions.Count - 1; j >= 0; j--)
            {
                var cond = s.Conditions[j];
                if (cond.ID != Conditions.ConditionSetCondition.constID) continue;

                if (cond.Arg > i)
                    cond.Arg -= 1;
                else if (cond.Arg == i)
                    s.Conditions.RemoveAt(j);
            }
        }

        QoLBar.Config.CndSetCfgs.RemoveAt(i);
        QoLBar.Config.Save();

        IPC.RemovedConditionSetProvider.SendMessage(i);
    }

    public static void ShiftCondition(CndSetCfg set, CndCfg cndCfg, bool increment)
    {
        var i = set.Conditions.IndexOf(cndCfg);
        if (!increment ? i <= 0 : i >= (set.Conditions.Count - 1)) return;

        var j = (increment ? i + 1 : i - 1);
        var condition = set.Conditions[i];
        set.Conditions.RemoveAt(i);
        set.Conditions.Insert(j, condition);
        QoLBar.Config.Save();
    }
}