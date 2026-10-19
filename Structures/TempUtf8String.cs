using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace QoLBar.Structures;

/// <summary>
/// Fills a game <see cref="Utf8String"/> that lives in caller-owned memory and is only read by the game.
/// Text that does not fit the inline buffer goes to an HGlobal buffer freed by <see cref="Dispose"/>,
/// so the destination must not be used after disposing.
/// </summary>
public readonly unsafe struct TempUtf8String : IDisposable
{
    private readonly nint heapPtr;

    public TempUtf8String(Utf8String* dest, string text) : this(dest, Encoding.UTF8.GetBytes(text)) { }

    public TempUtf8String(Utf8String* dest, byte[] text)
    {
        NativeMemory.Clear(dest, (nuint)sizeof(Utf8String));
        var inline = dest->InlineBuffer;
        var used = text.Length + 1;
        if (used > inline.Length)
        {
            heapPtr = Marshal.AllocHGlobal(used);
            var heap = new Span<byte>((void*)heapPtr, used);
            text.CopyTo(heap);
            heap[text.Length] = 0;
            dest->StringPtr = (byte*)heapPtr;
            dest->BufSize = used;
        }
        else
        {
            text.CopyTo(inline);
            dest->StringPtr = (byte*)Unsafe.AsPointer(ref inline[0]);
            dest->BufSize = inline.Length;
            dest->IsUsingInlineBuffer = true;
        }

        // StringLength is deliberately left at 0, matching what QoL Bar has always sent.
        dest->BufUsed = used;
        dest->IsEmpty = used == 1;
    }

    public void Dispose()
    {
        if (heapPtr != nint.Zero)
            Marshal.FreeHGlobal(heapPtr);
    }
}
