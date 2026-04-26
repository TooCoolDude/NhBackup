using Microsoft.Extensions.Options;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Nh.Api;
using Nh.Api.Models;
using NhBackup.WebApplication.Options;
using NhentaiBackup.WebApplication;
using System.Collections.Generic;

namespace NhBackup.WebApplication;

public class SyncClient
{

    private readonly NhSyncronizerOptions _options;
    private readonly ApiClient _apiClient;
    private readonly HttpClient _apiHttpClient; // HttpClient for API calls with authentication headers
    private readonly HttpClient _cdnHttpClient; // Separate HttpClient for CDN with different headers and timeout to prevent redirect
    public SyncClient(IOptions<NhSyncronizerOptions> options)
    {
        _options = options.Value;

        _apiClient = NhHttpClientFactory.CreateApi(_options);

        _cdnHttpClient =
            NhHttpClientFactory.CreateCdn();
    }
    /// <summary>
    /// Retrieves the list of CDN image servers.
    /// </summary>
    /// <returns>A list of CDN image server URLs.</returns>
    /// <exception cref="Exception">Thrown when the CDN configuration cannot be retrieved or contains no image servers.</exception>
    public async Task<List<string>> GetCDNs()
    {
        var cdnsResponse = await _apiClient.Api.V2.Cdn.GetAsync();
        if (cdnsResponse == null)
        {
            throw new Exception("Failed to retrieve CDN configuration");
        }
        if (cdnsResponse.ImageServers == null || cdnsResponse.ImageServers.Count == 0)
        {
            throw new Exception("No image servers found in CDN configuration");
        }
        return cdnsResponse.ImageServers;
    }
    public async Task<List<GalleryListItem>> GetAllFavourites()
    {
        var all = new List<GalleryListItem>();
        int page = 1;

        while (true)
        {
            await ApiRateLimiters.FavoritesApi.AcquireAsync(1);
            var data = await _apiClient.Api.V2.Favorites.GetAsync(config =>
            {
                config.QueryParameters.Page = page;
            });

            if (data?.Result == null || data.Result.Count == 0) break;

            all.AddRange(data.Result);
            Console.WriteLine($"Page {page}: {data.Result.Count} (Total {all.Count})");

            page++;
        }

        return all;
    }
    // Ga
    public async Task<GalleryDetailResponse> GetGallery(int galleryId)
    {
        return await _apiClient.Api.V2.Galleries[galleryId].GetAsync();
    }

    public async Task DownloadFileByUrl(string url, string path)
    {
        try
        {
            var response = await _cdnHttpClient.GetAsync(url);
            //response.EnsureSuccessStatusCode();
            if(!response.IsSuccessStatusCode)
            {
                Console.WriteLine("TEST");
            }
            var bytes = await response.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(path, bytes);
        }
        catch (TaskCanceledException)
        {
            throw new Exception($"Timeout downloading {url}");
        }
        catch (HttpRequestException ex)
        {
            throw new Exception($"HTTP error downloading {url}: {ex.Message}");
        }
        catch (Exception ex)
        {
            throw;
        }
    }
    public async Task<List<TagResponse>> GetTags(List<int> ids)
    {
        var result = new List<TagResponse>();

        foreach (var chunk in ids.Chunk(50))
        {
            try
            {
                // await ApiRateLimiters.TagsApi.AcquireAsync(1);
                var tags = await _apiClient.Api.V2.Tags.Ids.GetAsync(config =>
                {
                    config.QueryParameters.Ids = string.Join(",", chunk);
                });
                if (tags == null)
                    continue;

                result.AddRange(tags);
            }
            catch (Exception ex)
            {
                throw;
            }

        }

        return result;
    }

    //private async Task<bool> DownloadTorrent(HttpClient httpClient, string galleryPath, string galleryId)
    //{
    //    try
    //    {
    //        var torrentUrl = $"https://nhentai.net/g/{galleryId}/download";
    //        var response = await httpClient.GetAsync(torrentUrl);
    //        response.EnsureSuccessStatusCode();
    //        var torrentBytes = await response.Content.ReadAsByteArrayAsync();
    //        var torrentPath = Path.Combine(galleryPath, $"{galleryId}.torrent");
    //        await File.WriteAllBytesAsync(torrentPath, torrentBytes);
    //        return true;
    //    }
    //    catch (Exception ex)
    //    {
    //        Console.WriteLine($"  ❌ Error downloading torrent for gallery {galleryId}: {ex.Message}");

    //    }
    //    return false;
    //}
}
