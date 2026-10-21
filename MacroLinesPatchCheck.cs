using System;
using System.Collections.Generic;

namespace QoLBar;

/// <summary>巨集行數修補的寫入前核對。純邏輯、不碰 Dalamud：讀記憶體由呼叫端注入，方便離線驗。</summary>
public static class MacroLinesPatchCheck
{
    public enum Result
    {
        Ok,
        NotFound,
        Ambiguous,
        Unreadable,
        Mismatch,
        /// <summary>命中唯一、讀到的值落在 (expected, maxOwnValue]：本插件上一個實例沒還原就卸載留下的，要寫回 expected。</summary>
        LeftoverFromPreviousInstance,
    }

    /// <summary>兩個位址合起來的處置。</summary>
    public enum Decision
    {
        /// <summary>兩個都設位址；Leftover 的那個當場寫回遊戲原值。</summary>
        Apply,
        /// <summary>只是特徵碼沒命中（既有的「對不上」），兩個都不設、不報核對失敗。</summary>
        NotFound,
        /// <summary>核對不過，兩個都不設、一個位元組都不寫。</summary>
        Failed,
    }

    /// <summary>特徵碼恰好命中一處、命中位址＋<paramref name="offset"/> 讀得到且等於 <paramref name="expected"/> 才回
    /// <see cref="Result.Ok"/>；讀到的值在 (<paramref name="expected"/>, <paramref name="maxOwnValue"/>] 回
    /// <see cref="Result.LeftoverFromPreviousInstance"/>。這兩種 <paramref name="site"/> 是要改寫的位址、
    /// <paramref name="original"/> 是讀到的值；其餘結果 <paramref name="site"/> 一律為 0，
    /// <paramref name="original"/> 只在 Mismatch 時有意義。</summary>
    public static Result Verify(IReadOnlyList<nint> hits, int offset, byte expected, byte maxOwnValue,
        Func<nint, byte?> readByte, out nint site, out byte original)
    {
        site = nint.Zero;
        original = 0;

        if (hits == null || hits.Count == 0)
            return Result.NotFound;
        if (hits.Count != 1)
            return Result.Ambiguous;

        var address = hits[0] + offset;
        var value = readByte(address);
        if (value == null)
            return Result.Unreadable;

        original = value.Value;
        if (original == expected)
        {
            site = address;
            return Result.Ok;
        }

        if (original > expected && original <= maxOwnValue)
        {
            site = address;
            return Result.LeftoverFromPreviousInstance;
        }

        return Result.Mismatch;
    }

    public static bool IsUsable(Result result) => result is Result.Ok or Result.LeftoverFromPreviousInstance;

    public static Decision Decide(Result copied, Result executed)
    {
        if (IsUsable(copied) && IsUsable(executed))
            return Decision.Apply;
        if ((IsUsable(copied) || copied == Result.NotFound) && (IsUsable(executed) || executed == Result.NotFound))
            return Decision.NotFound;
        return Decision.Failed;
    }
}
