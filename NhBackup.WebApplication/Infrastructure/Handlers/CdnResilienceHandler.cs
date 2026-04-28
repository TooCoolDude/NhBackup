using System.Net;

namespace NhBackup.WebApplication.Infrastructure.Handlers;

public class CdnResilienceHandler : DelegatingHandler
{
    private readonly ILogger<CdnResilienceHandler> _logger;

    private DateTime _cooldownUntil = DateTime.MinValue;
    private int _failureStreak = 0;
    private readonly object _lock = new();

    public CdnResilienceHandler(ILogger<CdnResilienceHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // 🔴 GLOBAL COOLDOWN
        lock (_lock)
        {
            if (DateTime.UtcNow < _cooldownUntil)
            {
                var delay = _cooldownUntil - DateTime.UtcNow;

                _logger.LogWarning(
                    "CDN cooldown active. Delaying request {Url} for {Delay} ms",
                    request.RequestUri,
                    delay.TotalMilliseconds);

                Monitor.Exit(_lock);
                try { Task.Delay(delay, cancellationToken).Wait(cancellationToken); }
                finally { Monitor.Enter(_lock); }
            }
        }

        _logger.LogDebug("Sending request to {Url}", request.RequestUri);

        var response = await base.SendAsync(request, cancellationToken);

        // ✅ SUCCESS → reset state
        if (response.IsSuccessStatusCode)
        {
            lock (_lock)
            {
                if (_failureStreak > 0)
                {
                    _logger.LogTrace(
                        "Request succeeded after failures. Resetting failure streak (was {FailureStreak})",
                        _failureStreak);
                }

                _failureStreak = 0;
                _cooldownUntil = DateTime.MinValue;
            }

            return response;
        }

        _logger.LogWarning(
            "Request to {Url} failed with status {StatusCode}",
            request.RequestUri,
            (int)response.StatusCode);

        // 🔴 429 HANDLING
        if (response.StatusCode == (HttpStatusCode)429)
        {
            response.Dispose();

            double delaySeconds;

            lock (_lock)
            {
                _failureStreak++;

                delaySeconds = Math.Min(30, Math.Pow(2, _failureStreak));
                _cooldownUntil = DateTime.UtcNow.AddSeconds(delaySeconds);

                _logger.LogWarning(
                    "429 received. Failure streak: {FailureStreak}. Applying cooldown {DelaySeconds}s until {CooldownUntil}",
                    _failureStreak,
                    delaySeconds,
                    _cooldownUntil);
            }

            var retryDelay = TimeSpan.FromSeconds(delaySeconds);

            _logger.LogDebug(
                "Retrying request to {Url} after {Delay} ms",
                request.RequestUri,
                retryDelay.TotalMilliseconds);

            await Task.Delay(retryDelay, cancellationToken);

            return await base.SendAsync(request, cancellationToken);
        }

        return response;
    }
}