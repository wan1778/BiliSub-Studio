using System.Collections.Concurrent;
using BiliSubStudio.Core.Diagnostics;

namespace BiliSubStudio.Core.Jobs;

public sealed class JobManager : IDisposable
{
    private readonly ConcurrentDictionary<string, AppJob> _jobs = new(StringComparer.Ordinal);
    private ApplicationLog? _applicationLog;

    public JobManager(ApplicationLog? applicationLog = null) => _applicationLog = applicationLog;

    public void AttachLog(ApplicationLog applicationLog)
    {
        ArgumentNullException.ThrowIfNull(applicationLog);
        if (_jobs.Values.Any(x => !x.Snapshot().Done))
            throw new InvalidOperationException("Không thể đổi log khi đang có tác vụ.");
        _applicationLog = applicationLog;
    }

    public AppJob Create(string kind, bool pausable = false, bool cleanupAwareCancel = false)
    {
        var id = $"{kind}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";
        var job = new AppJob(id, kind, pausable, _applicationLog, cleanupAwareCancel);
        if (!_jobs.TryAdd(id, job))
        {
            job.Dispose();
            throw new InvalidOperationException("Không thể tạo tác vụ.");
        }
        return job;
    }

    public bool TryGet(string id, out AppJob? job) => _jobs.TryGetValue(id, out job);

    public JobSnapshot GetSnapshot(string id, int after = 0) =>
        _jobs.TryGetValue(id, out var job)
            ? job.Snapshot(after)
            : throw new KeyNotFoundException("Tác vụ không tồn tại.");

    public IReadOnlyList<JobSnapshot> ActiveSnapshots() =>
        _jobs.Values.Select(x => x.Snapshot()).Where(x => !x.Done).ToArray();

    public bool HasActiveJobs => _jobs.Values.Any(x => !x.Snapshot().Done);

    public void Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            throw new KeyNotFoundException("Tác vụ không tồn tại.");
        }
        job.Cancel();
    }

    public void CancelAll()
    {
        foreach (var job in _jobs.Values)
        {
            if (!job.Snapshot().Done)
            {
                job.Cancel();
            }
        }
    }

    public void Dispose()
    {
        CancelAll();
        foreach (var job in _jobs.Values)
        {
            job.Dispose();
        }
        _jobs.Clear();
    }
}
