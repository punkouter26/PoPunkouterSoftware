namespace PoPunkouterSoftware.Features.Azure;

/// <summary>
/// Singleton that tracks the current state of a running Azure report refresh.
/// Thread-safe: all mutations are serialised under a private lock.
/// The /api/diag/refresh-progress endpoint exposes a snapshot for the UI to poll.
/// </summary>
public sealed class RefreshProgressService
{
    private readonly List<string> _log = new();
    private readonly object _lock = new();

    public bool IsRunning { get; private set; }
    public string Step    { get; private set; } = "";
    public int    Percent { get; private set; }

    public void Start()
    {
        lock (_lock)
        {
            IsRunning = true;
            _log.Clear();
            Step    = "Starting…";
            Percent = 0;
            _log.Add($"[{Ts()}] 🚀 Refresh started");
        }
    }

    public void Report(string step, int percent, string? detail = null)
    {
        lock (_lock)
        {
            Step    = step;
            Percent = percent;
            _log.Add($"[{Ts()}] {step}{(detail is not null ? $" — {detail}" : "")}");
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            IsRunning = false;
            Step      = "Complete";
            Percent   = 100;
            _log.Add($"[{Ts()}] ✅ Refresh complete");
        }
    }

    public void Fail(string error)
    {
        lock (_lock)
        {
            IsRunning = false;
            Step      = "Failed";
            _log.Add($"[{Ts()}] ❌ {error}");
        }
    }

    public ProgressSnapshot Snapshot()
    {
        lock (_lock)
            return new ProgressSnapshot(IsRunning, Step, Percent, _log.ToArray());
    }

    private static string Ts() => DateTime.UtcNow.ToString("HH:mm:ss");
}

public sealed record ProgressSnapshot(bool IsRunning, string Step, int Percent, string[] Log);
