using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace QoLBar.Structures;

/// <summary>
/// A game <see cref="RaptureMacroModule.Macro"/> followed by extra lines (QoL Bar's own extension: the game only
/// reads past <see cref="GameLines"/> while its line-count bytes are patched). Written into caller-owned memory
/// of <see cref="size"/> bytes, which must outlive this value.
/// </summary>
public readonly unsafe struct ExtendedMacro : IDisposable
{
    public const int numLines = 30;
    public static readonly int GameLines = CountGameLines();
    public static readonly int size = sizeof(RaptureMacroModule.Macro) + (numLines - GameLines) * sizeof(Utf8String);

    private readonly TempUtf8String title;
    private readonly TempUtf8String[] lines;

    public ExtendedMacro(nint loc, string t, IReadOnlyList<string> commands)
    {
        NativeMemory.Clear((void*)loc, (nuint)size);
        var macro = (RaptureMacroModule.Macro*)loc;
        macro->IconId = 0x101D1; // 66001
        macro->MacroIconRowId = 1;
        title = new TempUtf8String(&macro->Name, t);

        var firstLine = (Utf8String*)Unsafe.AsPointer(ref macro->Lines[0]);
        lines = new TempUtf8String[numLines];
        for (int i = 0; i < numLines; i++)
        {
            var command = (commands.Count > i) ? commands[i] : string.Empty;
            lines[i] = new TempUtf8String(firstLine + i, command);
        }
    }

    private static int CountGameLines()
    {
        RaptureMacroModule.Macro macro = default;
        return macro.Lines.Length;
    }

    public void Dispose()
    {
        title.Dispose();
        for (int i = 0; i < numLines; i++)
            lines[i].Dispose();
    }
}
