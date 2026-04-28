namespace NhBackup.WebApplication.Infrastructure;

/// <summary>
/// Manages a pool of CDN hosts with per-host cooldown tracking.
/// Provides round-robin selection, skipping hosts in cooldown.
/// Thread-safe.
/// </summary>
public class CdnPool
{
    private readonly ILogger<CdnPool> _logger;

    private readonly record struct CdnState(DateTime CooldownUntil, int FailureStreak);

    private readonly Dictionary<string, CdnState> _states = new();
    private readonly object _lock = new();
    private int _roundRobinIndex = 0;
    private List<string> _cdns = [];

    public CdnPool(ILogger<CdnPool> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initializes the pool with a fresh list of CDN hosts for each sync run.
    /// </summary>
    public void Initialize(List<string> cdns)
    {
        lock (_lock)
        {
            _cdns = cdns;
            _states.Clear();
            _roundRobinIndex = 0;
            foreach (var cdn in cdns)
                _states[cdn] = new CdnState(DateTime.MinValue, 0);
        }

        _logger.LogInformation("CdnPool initialized with {Count} CDNs: {Cdns}",
            cdns.Count, string.Join(", ", cdns));
    }

    /// <summary>
    /// Returns the next available CDN host, skipping those in cooldown.
    /// If all are in cooldown — waits for the soonest one to recover.
    /// </summary>
    public async Task<string> GetNextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? candidate = null;
            DateTime soonestRecovery = DateTime.MaxValue;

            lock (_lock)
            {
                int count = _cdns.Count;

                for (int i = 0; i < count; i++)
                {
                    var cdn = _cdns[(_roundRobinIndex + i) % count];
                    var state = _states[cdn];

                    if (DateTime.UtcNow >= state.CooldownUntil)
                    {
                        candidate = cdn;
                        _roundRobinIndex = (_roundRobinIndex + i + 1) % count;
                        break;
                    }

                    if (state.CooldownUntil < soonestRecovery)
                        soonestRecovery = state.CooldownUntil;
                }
            }

            if (candidate != null)
                return candidate;

            // All CDNs are in cooldown — wait for the soonest recovery
            var wait = soonestRecovery - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                _logger.LogWarning("All CDNs in cooldown. Waiting {Seconds:F1}s for recovery", wait.TotalSeconds);
                await Task.Delay(wait, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Reports a 429 or failure for a CDN host — applies exponential backoff cooldown.
    /// </summary>
    public void ReportFailure(string cdn)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(cdn, out var state))
                return;

            var streak = state.FailureStreak + 1;
            var cooldownSeconds = Math.Min(60, Math.Pow(2, streak)); // 2s, 4s, 8s, ... max 60s
            var cooldownUntil = DateTime.UtcNow.AddSeconds(cooldownSeconds);

            _states[cdn] = new CdnState(cooldownUntil, streak);

            _logger.LogWarning(
                "CDN {Cdn} failed (streak {Streak}). Cooldown {Seconds:F0}s until {Until:HH:mm:ss}",
                cdn, streak, cooldownSeconds, cooldownUntil);
        }
    }

    /// <summary>
    /// Reports a successful response for a CDN host — resets its failure streak.
    /// </summary>
    public void ReportSuccess(string cdn)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(cdn, out var state) || state.FailureStreak == 0)
                return;

            _states[cdn] = new CdnState(DateTime.MinValue, 0);
            _logger.LogDebug("CDN {Cdn} recovered", cdn);
        }
    }
}