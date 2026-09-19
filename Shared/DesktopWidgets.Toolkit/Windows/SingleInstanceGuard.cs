using System;
using System.Threading;

namespace DesktopWidgets.Toolkit.Windows;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;

    public SingleInstanceGuard(string mutexName)
    {
        if (string.IsNullOrWhiteSpace(mutexName)) throw new ArgumentException("A mutex name is required.", nameof(mutexName));
        _mutex = new Mutex(true, mutexName, out _ownsMutex);
    }

    public bool IsPrimaryInstance => _ownsMutex;

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
