using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Nh.Api;
using NhBackup.WebApplication.Options;
using System.Collections.Concurrent;

namespace NhBackup.WebApplication;

public static class NhHttpClientFactory
{
    private static readonly ConcurrentDictionary<string, EndpointRateState> ApiStates = new();

    public static ApiClient CreateApi(NhSyncronizerOptions options)
    {
        var handler = new ApiRateLimitHandler(ApiStates)
        {
            InnerHandler = new HttpClientHandler()
        };

        var http = new HttpClient(handler);

        // 🔐 headers
        http.DefaultRequestHeaders.Add("Authorization", $"Key {options.ApiKey}");
        http.DefaultRequestHeaders.Add("User-Agent", "NhBackup/1.0");

        var adapter = new HttpClientRequestAdapter(
            new AnonymousAuthenticationProvider(),
            httpClient: http
        );

        adapter.BaseUrl = "https://nhentai.net";

        return new ApiClient(adapter);
    }

    public static HttpClient CreateCdn()
    {
        var handler = new CdnResilienceHandler
        {
            InnerHandler = new HttpClientHandler()
        };

        var http = new HttpClient(handler);

        http.DefaultRequestHeaders.Add(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
        );

        http.DefaultRequestHeaders.Add("Referer", "https://nhentai.net/");
        http.Timeout = TimeSpan.FromSeconds(30);

        return http;
    }
}
