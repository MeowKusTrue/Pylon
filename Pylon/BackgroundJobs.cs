namespace Pylon;

public enum JobState { Running, Completed, Failed, Cancelled }

/// <summary>One operation, including its cancellation, result and retry; successful work releases its closure.</summary>
public sealed class BackgroundJob
{
    Func<BackgroundJob, CancellationToken, Task<string?>>? _work;
    CancellationTokenSource _cancel = new();
    public string Name { get; }
    public string Detail { get; private set; } = "";
    public string? Output { get; set; }
    public double Progress { get; private set; }
    public JobState State { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;

    internal BackgroundJob(string name, Func<BackgroundJob, CancellationToken, Task<string?>> work) => (Name, _work) = (name, work);
    public void Report(double progress, string? detail = null)
    {
        if (State != JobState.Running) return;
        Progress = Math.Clamp(progress, 0, 1);
        if (detail != null) Detail = detail;
        BackgroundJobs.Notify();
    }
    public void Cancel() { if (State == JobState.Running) _cancel.Cancel(); }
    public void Retry()
    {
        if (State == JobState.Running || _work == null) return;
        _cancel.Dispose();
        _cancel = new();
        Start();
    }
    internal void Release() { _work = null; _cancel.Dispose(); }
    internal void Start()
    {
        State = JobState.Running;
        Progress = 0;
        Detail = "";
        Completion = Execute();
        BackgroundJobs.Notify();
    }
    async Task Execute()
    {
        try
        {
#if DEBUG
            if (BackgroundJobs.DebugGate is { } gate) await gate;
#endif
            _cancel.Token.ThrowIfCancellationRequested();
            Output = await _work!(this, _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
            Progress = 1;
            State = JobState.Completed;
            _work = null;
        }
        catch (OperationCanceledException) { State = JobState.Cancelled; }
        catch (Exception e) { State = JobState.Failed; Detail = e.Message; ErrorLog.Note("background task", e.GetType().Name); }
        finally { BackgroundJobs.Notify(); }
    }
}

public static class BackgroundJobs
{
#if DEBUG
    internal static Task? DebugGate;
#endif
    static readonly List<BackgroundJob> Jobs = [];
    public static event Action? Changed;
    public static IReadOnlyList<BackgroundJob> All { get { lock (Jobs) return Jobs.ToArray(); } }
    public static void CancelAll() { foreach (var job in All) job.Cancel(); }
    internal static void Notify()
    {
        lock (Jobs)
            while (Jobs.Count > 30 && Jobs.FirstOrDefault(j => j.State != JobState.Running) is { } old)
            {
                Jobs.Remove(old);
                old.Release();
            }
        Changed?.Invoke();
    }
    public static BackgroundJob Start(string name, Func<BackgroundJob, CancellationToken, Task<string?>> work)
    {
        var job = new BackgroundJob(name, work);
        lock (Jobs) Jobs.Add(job);
        job.Start();
        return job;
    }
}
