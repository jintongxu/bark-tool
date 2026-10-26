using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace BarkTool.Desktop;

internal sealed class SingleInstance : IDisposable
{
    private readonly object gate = new();
    private readonly string eventName;
    private Mutex? mutex;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? listener;
    private bool disposed;

    public bool IsPrimary { get; }

    public SingleInstance(string? name = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User?.Value
            ?? throw new InvalidOperationException("The current user has no Windows SID.");
        // Local scopes these kernel objects to this logon session. Hashing also
        // keeps caller-provided names from introducing kernel namespace separators.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            user + "\0" + (name ?? "BarkTool.Desktop.SingleInstance.v1"))));
        var prefix = @"Local\BarkTool." + key;
        eventName = prefix + ".Activate";
        try
        {
            // Publish the event before exclusion, so an immediately arriving
            // secondary can signal even before StartListening has been called.
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            mutex = new Mutex(false, prefix + ".Instance", out var createdNew);
            IsPrimary = createdNew;
            if (!IsPrimary)
            {
                // A secondary must not keep exclusion alive after the primary exits.
                mutex.Dispose();
                mutex = null;
                activation.Dispose();
                activation = null;
            }
        }
        catch
        {
            activation?.Dispose();
            mutex?.Dispose();
            throw;
        }
    }

    public void StartListening(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsPrimary)
                throw new InvalidOperationException("Only the primary instance can listen.");
            if (listener is not null)
                throw new InvalidOperationException("Activation listening has already started.");
            listener = ThreadPool.RegisterWaitForSingleObject(activation!, (_, _) =>
            {
                lock (gate)
                {
                    // Unregister does not drain queued callbacks. Serialize with
                    // Dispose so none can invoke application code after it returns.
                    if (!disposed)
                        onActivate();
                }
            }, null, Timeout.Infinite, false);
        }
    }

    public void RequestActivation()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                using var signal = EventWaitHandle.OpenExisting(eventName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The primary can exit between exclusion discovery and this call.
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            listener?.Unregister(null);
            listener = null;
            activation?.Dispose();
            activation = null;
            mutex?.Dispose();
            mutex = null;
        }
    }
}
