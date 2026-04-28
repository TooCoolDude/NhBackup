using System.Net;

namespace NhBackup.WebApplication.Infrastructure.Handlers;

/// <summary>
/// Handles API rate limiting by reading x-ratelimit headers and retrying on 429.
/// State is shared across requests via ApiRateLimitStateStore (singleton).
/// </summary>
public class ApiRateLimitHandler : DelegatingHandler
{
    private readonly ApiRateLimitStateStore _store;

    public ApiRateLimitHandler(ApiRateLimitStateStore store)
    {
        _store = store;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var endpoint = Normalize(request.RequestUri);

        // Wait if rate limit already known to be exceeded
        if (_store.States.TryGetValue(endpoint, out var state))
        {
            if (state.Remaining <= 0 && state.ResetAt > DateTime.UtcNow)
            {
                var delay = state.ResetAt - DateTime.UtcNow;
                await Task.Delay(delay, cancellationToken);
            }
        }

        var response = await base.SendAsync(request, cancellationToken);

        state ??= _store.States.GetOrAdd(endpoint, _ => new EndpointRateState());

        // Update rate limit state from response headers
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var rem))
            state.Remaining = int.Parse(rem.First());

        if (response.Headers.TryGetValues("x-ratelimit-limit", out var lim))
            state.Limit = int.Parse(lim.First());

        if (response.Headers.RetryAfter?.Delta is TimeSpan ts)
            state.ResetAt = DateTime.UtcNow.Add(ts);

        // Retry once on 429 — call SendAsync (not base) to go through full pipeline
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
            response.Dispose();
            await Task.Delay(retry, cancellationToken);
            return await SendAsync(request, cancellationToken);
        }

        return response;
    }

    private static string Normalize(Uri? uri) => uri?.AbsolutePath ?? "/";
}