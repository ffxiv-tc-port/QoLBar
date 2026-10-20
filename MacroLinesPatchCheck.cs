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
    }

    /// <summary>特徵碼恰好命中一處、命中位址＋<paramref name="offset"/> 讀得到且等於 <paramref name="expected"/> 才回
    /// <see cref="Result.Ok"/>，此時 <paramref name="site"/> 是要改寫的位址、<paramref name="original"/> 是讀到的原值。
    /// 其餘結果 <paramref name="site"/> 一律為 0；<paramref name="original"/> 只在 Mismatch 時有意義。</summary>
    public static Result Verify(IReadOnlyList<nint> hits, int offset, byte expected, Func<nint, byte?> readByte,
        out nint site, out byte original)
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
        if (original != expected)
            return Result.Mismatch;

        site = address;
        return Result.Ok;
    }
}
