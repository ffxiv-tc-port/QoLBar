using System;
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

        if (snapshotStates.Length != n)
        {
            snapshotStates = new byte[n];
            snapshotNames = new string[n];
        }

        for (var i = 0; i < n; i++)
        {
            var set = sets[i];
            snapshotNames[i] = set.Name;
            snapshotStates[i] = conditionSetCache.TryGetValue(set, out var c)
                ? (c.prev ? ConditionSetSnapshot.KnownTrue : ConditionSetSnapshot.KnownFalse)
                : ConditionSetSnapshot.Unknown;
        }

        var current = Volatile.Read(ref publishedSnapshot);
        if (current.Matches(snapshotStates, snapshotNames)) return;

        Volatile.Write(ref publishedSnapshot,
            new ConditionSetSnapshot((byte[])snapshotStates.Clone(), (string[])snapshotNames.Clone()));
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
        var snapshot = Snapshot;
        if (snapshot.TryGet(i, out var value)) return value;

        LogSnapshotMiss(i, snapshot.Count);
        return false;
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
        return condition != null && (!negate ? CheckCondition(condition, arg) : !CheckCondition(condition, arg));
    }

    private static bool CheckCondition(ICondition condition, dynamic arg)
    {
        if (conditionCache.TryGetValue((condition, arg), out bool cache)) // ReSharper / Rider hates this being a var for some reason
            return cache;

        try
        {
            cache = condition.Check(arg);
        }
        catch
        {
            cache = false;
        }

        conditionCache[(condition, arg)] = cache;
        return cache;
    }

    private static bool CheckUnaryCondition(bool negate, ICondition condition, dynamic arg)
    {
        try
        {
            return !negate ? condition.Check(arg) : !condition.Check(arg);
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckBinaryCondition(bool prev, BinaryOperator op, bool negate, ICondition condition, dynamic arg)
    {
        return op switch
        {
            BinaryOperator.AND => prev && CheckUnaryCondition(negate, condition, arg),
            BinaryOperator.OR => prev || CheckUnaryCondition(negate, condition, arg),
            BinaryOperator.EQUALS => prev == CheckUnaryCondition(negate, condition, arg),
            BinaryOperator.XOR => prev ^ CheckUnaryCondition(negate, condition, arg),
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
                prev = CheckUnaryCondition(cnd.Negate, condition, cnd.Arg);
                first = false;
            }
            else
            {
                prev = CheckBinaryCondition(prev, cnd.Operator, cnd.Negate, condition, cnd.Arg);
            }

            steps.Add(prev);
        }

        lockedSets.Remove(set);

        conditionSetCache[set] = (prev, QoLBar.RunTime);
        debugSteps[set] = steps;
        return prev;
    }

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