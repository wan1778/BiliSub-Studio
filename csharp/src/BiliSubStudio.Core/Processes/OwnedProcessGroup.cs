using System.Collections.Concurrent;
using System.Diagnostics;

namespace BiliSubStudio.Core.Processes;

public sealed class OwnedProcessGroup : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, Process> _processes = new();
    private int _stopping;

    public int ActiveCount
    {
        get
        {
            RemoveExited();
            return _processes.Count;
        }
    }

    internal IDisposable Track(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!_processes.TryAdd(process.Id, process))
            throw new InvalidOperationException($"Tiến trình {process.Id} đã được đăng ký trong nhóm.");
        if (Volatile.Read(ref _stopping) != 0)
        {
            Kill(process);
            throw new OperationCanceledException("Nhóm tiến trình đang được dừng.");
        }
        return new Lease(this, process.Id);
    }

    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var active = _processes.ToArray();
            if (active.Length == 0) return;
            foreach (var pair in active) Kill(pair.Value);
            await Task.WhenAll(active.Select(pair => WaitAsync(pair.Value)));
            RemoveExited();
            if (_processes.IsEmpty) return;
            if (DateTime.UtcNow >= deadline)
                throw new IOException($"Không thu hồi được {_processes.Count} tiến trình con do tác vụ sở hữu.");
            await Task.Delay(50);
        }
    }

    private void RemoveExited()
    {
        foreach (var pair in _processes.ToArray())
        {
            try
            {
                if (pair.Value.HasExited) _processes.TryRemove(pair.Key, out _);
            }
            catch (InvalidOperationException)
            {
                _processes.TryRemove(pair.Key, out _);
            }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static async Task WaitAsync(Process process)
    {
        try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
    }

    private void Release(int processId) => _processes.TryRemove(processId, out _);

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed class Lease(OwnedProcessGroup owner, int processId) : IDisposable
    {
        private OwnedProcessGroup? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(processId);
    }
}
