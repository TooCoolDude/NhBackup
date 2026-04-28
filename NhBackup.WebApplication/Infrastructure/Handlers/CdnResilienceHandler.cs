using System.Net;

namespace NhBackup.WebApplication.Infrastructure.Handlers;

/// <summary>
/// Minimal CDN handler — just enforces success/failure detection.
/// Rate limit logic and CDN rotation are handled by CdnPool.
/// </summary>
public class CdnResilienceHandler : DelegatingHandler
{
    private readonly ILogger<CdnResilienceHandler> _logger;

    public CdnResilienceHandler(ILogger<CdnResilienceHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "CDN request failed: {StatusCode} {Url}",
                (int)response.StatusCode,
                request.RequestUri);
        }

        return response;
    }
}