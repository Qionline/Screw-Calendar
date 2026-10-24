using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScrewCalendar.Tests;

// Clipboard listeners can briefly lock the system clipboard between test operations.
internal static class ClipboardTestAccess
{
    public static T Run<T>(Func<T> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return operation(); }
            catch (COMException exception) when (exception.HResult == unchecked((int)0x800401D0) && attempt < 9)
            { Thread.Sleep(50); }
        }
    }

    public static void Run(Action operation) => Run(() => { operation(); return true; });
}
