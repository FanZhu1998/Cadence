using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cadence.App.Launch;

/// <summary>
/// One Cadence per sign-in session, and a way for a later launch to bring that one forward.
/// </summary>
/// <remarks>
/// Two things are needed and a mutex alone gives only the first. The mutex answers "is Cadence
/// already running?"; a named event carries "the user just launched it again, show yourself".
/// Without the second, double-clicking the exe while Cadence runs does nothing visible, which
/// reads to the user as a broken app.
/// <para>
/// Both names are constants in the <c>Local\</c> namespace, which Windows already scopes to one
/// sign-in session, so two people signed in to one machine each get their own copy. An earlier
/// version built the name from <c>string.GetHashCode()</c>: .NET randomises that per process, so
/// every launch got a different name, the guard never matched, and each double-click started
/// another copy polling the same private endpoints.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class SingleInstance : IDisposable
{
    public const string DefaultName = "Cadence";

    private readonly Mutex _lock;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _listener;
    private bool _disposed;

    public SingleInstance(string name = DefaultName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _lock = new Mutex(initiallyOwned: true, $@"Local\{name}.Instance", out var createdNew);
        IsFirstInstance = createdNew;

        // Opens the existing event when another copy created it; the initial state is then ignored.
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Activate");
    }

    /// <summary>True when this process is the one Cadence that should keep running.</summary>
    public bool IsFirstInstance { get; }

    /// <summary>Tells the running copy that the user launched Cadence again.</summary>
    /// <remarks>
    /// Windows lets only the process that received the user's latest input take the foreground.
    /// That is this one, which the user just launched, not the copy already running in the tray.
    /// Without handing the right over first, the running copy's panel would open behind whatever
    /// the user was doing, or only flash on the taskbar.
    /// </remarks>
    public bool SignalFirstInstance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _ = AllowSetForegroundWindow(AsfwAny);
        return _activate.Set();
    }

    /// <summary>ASFW_ANY: any process may take the foreground, until the next input event.</summary>
    private const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// Invokes <paramref name="onActivate"/> on a thread-pool thread each time another launch
    /// signals. Callers marshal to the UI thread themselves.
    /// </summary>
    /// <remarks>
    /// A registered wait rather than a dedicated thread: it costs nothing while idle, which is
    /// almost always. The event auto-resets and the wait re-arms after every callback, so each
    /// later launch is heard, not only the first.
    /// </remarks>
    public void ListenForActivation(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _listener?.Unregister(null);
        _listener = ThreadPool.RegisterWaitForSingleObject(
            _activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _listener?.Unregister(null);

        if (IsFirstInstance)
        {
            try
            {
                _lock.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from a thread that did not acquire it. Closing the handle below still
                // frees the lock, because this was the last handle open on it.
            }
        }

        _lock.Dispose();
        _activate.Dispose();
    }
}
