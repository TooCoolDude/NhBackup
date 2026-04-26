using System.Net;

namespace NhBackup.WebApplication;

public class CdnResilienceHandler : DelegatingHandler
{
    private DateTime _cooldownUntil = DateTime.MinValue;
    private int _failureStreak = 0;
    private readonly object _lock = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // 🔴 GLOBAL COOLDOWN (важно даже без параллелизма)
        lock (_lock)
        {
            if (DateTime.UtcNow < _cooldownUntil)
            {
                var delay = _cooldownUntil - DateTime.UtcNow;
                Monitor.Exit(_lock);
                try { Task.Delay(delay, cancellationToken).Wait(cancellationToken); }
                finally { Monitor.Enter(_lock); }
            }
        }

        var response = await base.SendAsync(request, cancellationToken);

        // ✅ SUCCESS → reset state
        if (response.IsSuccessStatusCode)
        {
            lock (_lock)
            {
                _failureStreak = 0;
                _cooldownUntil = DateTime.MinValue;
            }

            return response;
        }

        // 🔴 429 HANDLING
        if (response.StatusCode == (HttpStatusCode)429)
        {
            response.Dispose();

            lock (_lock)
            {
                _failureStreak++;

                // exponential backoff (cap 30s)
                var delaySeconds = Math.Min(30, Math.Pow(2, _failureStreak));

                _cooldownUntil = DateTime.UtcNow.AddSeconds(delaySeconds);
            }

            var retryDelay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, _failureStreak)));

            await Task.Delay(retryDelay, cancellationToken);

            return await base.SendAsync(request, cancellationToken);
        }

        return response;
    }
}
