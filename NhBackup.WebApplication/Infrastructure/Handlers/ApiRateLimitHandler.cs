namespace NhBackup.WebApplication.Infrastructure.Handlers;

using System.Net;

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

        // Do not run if limit exceeded
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

        // 📊 update rate limit
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var rem))
            state.Remaining = int.Parse(rem.First());

        if (response.Headers.TryGetValues("x-ratelimit-limit", out var lim))
            state.Limit = int.Parse(lim.First());

        if (response.Headers.RetryAfter?.Delta is TimeSpan ts)
            state.ResetAt = DateTime.UtcNow.Add(ts);

        // 🔁 retry on 429
        if (response.StatusCode == (HttpStatusCode)429)
        {
            var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);

            response.Dispose();
            await Task.Delay(retry, cancellationToken);

            return await base.SendAsync(request, cancellationToken);
        }

        return response;
    }

    private static string Normalize(Uri? uri)
    {
        return uri?.AbsolutePath ?? "/";
    }
}