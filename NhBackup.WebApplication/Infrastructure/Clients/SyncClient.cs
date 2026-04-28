using Nh.Api;
using Nh.Api.Models;

namespace NhBackup.WebApplication.Infrastructure.Clients;

public class SyncClient
{
    private readonly ApiClient _apiClient;
    private readonly ILogger<SyncClient> _logger;
    private readonly HttpClient _cdnClient;

    public SyncClient(IHttpClientFactory factory, ApiClient apiClient, ILogger<SyncClient> logger)
    {
        _cdnClient = factory.CreateClient("cdn");
        _apiClient = apiClient;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves the list of CDN image servers.
    /// </summary>
    public async Task<List<string>> GetCDNs()
    {
        _logger.LogInformation("Retrieving CDN configuration...");

        var cdnsResponse = await _apiClient.Api.V2.Cdn.GetAsync();

        if (cdnsResponse == null)
            throw new Exception("Failed to retrieve CDN configuration");

        if (cdnsResponse.ImageServers == null || cdnsResponse.ImageServers.Count == 0)
            throw new Exception("No image servers found in CDN configuration");

        _logger.LogInformation("CDN image servers:\n{ImageServers}", string.Join("\n", cdnsResponse.ImageServers));

        return cdnsResponse.ImageServers;
    }

    /// <summary>
    /// Retrieves all favourite galleries across all pages.
    /// </summary>
    public async Task<List<GalleryListItem>> GetAllFavouritesList()
    {
        _logger.LogInformation("Retrieving all favorite galleries...");

        var all = new List<GalleryListItem>();
        int page = 1;

        while (true)
        {
            var data = await _apiClient.Api.V2.Favorites.GetAsync(config =>
            {
                config.QueryParameters.Page = page;
            });

            if (data?.Result == null || data.Result.Count == 0)
                break;

            all.AddRange(data.Result);
            _logger.LogInformation("Page {Page}: {Count} items (Total {Total})", page, data.Result.Count, all.Count);

            page++;
        }

        _logger.LogInformation("All favorites retrieved. Total: {TotalCount}", all.Count);
        return all;
    }

    /// <summary>
    /// Retrieves full gallery metadata by id.
    /// </summary>
    public async Task<GalleryDetailResponse> GetGalleryMetadata(int galleryId)
    {
        return await _apiClient.Api.V2.Galleries[galleryId].GetAsync();
    }

    /// <summary>
    /// Downloads a file from a URL and saves it to the given path.
    /// </summary>
    public async Task DownloadFileByUrl(string url, string path)
    {
        _logger.LogTrace("Downloading {Url}", url);

        using var response = await _cdnClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await stream.CopyToAsync(fileStream);

        _logger.LogTrace("Saved to {Path}", path);

        if (!File.Exists(path))
            throw new Exception($"File was not created: {path}");
    }

    /// <summary>
    /// Retrieves tag metadata for the given list of ids in chunks of 50.
    /// </summary>
    public async Task<List<TagResponse>> GetTags(List<int> ids)
    {
        var result = new List<TagResponse>();

        foreach (var chunk in ids.Chunk(50))
        {
            var tags = await _apiClient.Api.V2.Tags.Ids.GetAsync(config =>
            {
                config.QueryParameters.Ids = string.Join(",", chunk);
            });

            if (tags != null)
                result.AddRange(tags);
        }

        return result;
    }
}