using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using NhBackup.WebApplication;
using NhBackup.WebApplication.Db;
using NhBackup.WebApplication.Options;
using Nh.Api;
using Nh.Api.Models;
using System.Collections.Generic;

namespace NhentaiBackup.WebApplication
{
    public class Syncronizer : BackgroundService
    {
        private readonly NhSyncronizerOptions _options;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<Syncronizer> _logger;
        private readonly PeriodicTimer _timer;

        public Syncronizer(IOptions<NhSyncronizerOptions> options, IServiceScopeFactory scopeFactory, ILogger<Syncronizer> logger)
        {
            _options = options.Value; 
            _scopeFactory = scopeFactory;
            _logger = logger;
            _timer = new(TimeSpan.FromHours(_options.SyncIntevralHours));
            //_timer = new(TimeSpan.FromSeconds(5));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            do
            {
                try
                {
                    await Syncronize();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                }
            }
            while (await _timer.WaitForNextTickAsync(stoppingToken));
        }

        public async Task Syncronize()
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Key {_options.ApiKey}");
            httpClient.DefaultRequestHeaders.Add("User-Agent", "NhBackup/1.0");

            var adapter = new HttpClientRequestAdapter(
                new AnonymousAuthenticationProvider(),
                httpClient: httpClient
            );
            adapter.BaseUrl = "https://nhentai.net";

            var client = new ApiClient(adapter);

            var all = await GetAllFavourites(client);

            using var scope = _scopeFactory.CreateAsyncScope();
            using var db = scope.ServiceProvider.GetService<NhDbContext>();
            await db.Database.EnsureCreatedAsync();
            
            int added = 0;
            int updated = 0;

            foreach (var item in all)
            {
                if (item.Id == null) continue;

                var galleryId = item.Id.Value;

                var fullGallery = await client.Api.V2.Galleries[galleryId].GetAsync();
                if (fullGallery == null) continue;

                var existing = await db.Galleries.FindAsync(galleryId);
                bool isNew = existing == null;
                bool tagsAdded = false;

                if (isNew)
                {
                    var downloads = Path.Combine(_options.DatabaseFolder, "downloads");
                    var galleryFolder = Path.Combine(downloads, galleryId.ToString());

                    // var isTorrentLoaded = await DownloadTorrent(httpClient, galleryFolder, galleryId.ToString());
                    
                    var mediaPaths = await DownloadGalleryMedia(fullGallery, galleryFolder);
                    
                    if (mediaPaths == null)
                    {
                        Console.WriteLine($"Not able to download media, skipping: {galleryId} - {item.EnglishTitle}");
                        continue;
                    }

                    var gallery = new Gallery
                    {
                        Id = galleryId,
                        MediaId = item.MediaId,
                        MediaPaths = mediaPaths,
                        EnglishTitle = item.EnglishTitle,
                        JapaneseTitle = GetJapaneseTitle(item.JapaneseTitle),
                        NumPages = item.NumPages ?? 0,
                        Thumbnail = item.Thumbnail,
                        ThumbnailWidth = item.ThumbnailWidth ?? 0,
                        ThumbnailHeight = item.ThumbnailHeight ?? 0,
                        Blacklisted = item.Blacklisted ?? false,
                        SyncedAt = DateTime.UtcNow
                    };
                    await db.Galleries.AddAsync(gallery);
                    added++;
                    Console.WriteLine($"✅ Added: {galleryId} - {gallery.EnglishTitle}");
                }

                // Add new tags
                if (item.TagIds != null && item.TagIds.Any())
                {
                    foreach (var tagId in item.TagIds.Where(id => id.HasValue).Select(id => id.Value))
                    {
                        if (!await db.Tags.AnyAsync(t => t.Id == tagId))
                        {
                            db.Tags.Add(new Tag { Id = tagId });
                        }

                        var exists = await db.GalleryTags
                            .AnyAsync(gt => gt.GalleryId == galleryId && gt.TagId == tagId);

                        if (!exists)
                        {
                            db.GalleryTags.Add(new GalleryTag
                            {
                                GalleryId = galleryId,
                                TagId = tagId
                            });
                            tagsAdded = true;
                        }
                    }
                }

                await db.SaveChangesAsync();


                var tagIds = await db.Tags.Select(t => t.Id).ToListAsync();
                await LoadTagNamesAndTypes(db, client, tagIds);

                if (!isNew && tagsAdded)
                {
                    updated++;
                    Console.WriteLine($"🔄 Added new tags: {galleryId}");
                }
                else if (!isNew && !tagsAdded)
                {
                    //Console.WriteLine($"⏭ Без изменений: {galleryId}");
                }
            }


            Console.WriteLine($"\n=== Done ===");
            Console.WriteLine($"Added: {added}");
            Console.WriteLine($"Updated: {updated}");
            Console.WriteLine($"Total in DB: {await db.Galleries.CountAsync()}");
        }

        private async Task LoadTagNamesAndTypes(NhDbContext db, ApiClient client, List<int> ids)
        {
            var tags = await client.Api.V2.Tags.Ids.GetAsync(config =>
            {
                config.QueryParameters.Ids = string.Join(", ", ids);
            });

            if (tags == null) return;

            foreach (var tag in tags)
            {
                if (tag.Id == null) continue;

                var existing = await db.Tags.FindAsync(tag.Id.Value);
                if (existing != null)
                {
                    existing.Name = tag.Name;
                    existing.Slug = tag.Slug;
                    existing.Type = tag.Type;
                }
            }

            await db.SaveChangesAsync();
        }

        private async Task<List<GalleryListItem>> GetAllFavourites(ApiClient client)
        {
            var all = new List<GalleryListItem>();
            int page = 1;

            while (true)
            {
                var data = await client.Api.V2.Favorites.GetAsync(config =>
                {
                    config.QueryParameters.Page = page;
                });

                if (data?.Result == null || data.Result.Count == 0) break;

                all.AddRange(data.Result);
                Console.WriteLine($"Page {page}: {data.Result.Count} (Total {all.Count})");

                page++;
                await Task.Delay(6000);
            }

            return all;
        }

        private string GetJapaneseTitle(object jpTitle)
        {
            if (jpTitle == null) return null;

            var prop = jpTitle.GetType().GetProperty("String");
            if (prop != null)
            {
                var value = prop.GetValue(jpTitle);
                return value as string;
            }

            return null;
        }

        private async Task<List<string>> DownloadGalleryMedia(GalleryDetailResponse gallery, string galleryFolder)
        {
            if (gallery.Id == null) return null;

            var loadedMedia = new List<string>();

            var galleryId = gallery.Id.Value;

            try
            {
                Directory.CreateDirectory(galleryFolder);

                // Download pages
                if (gallery.Pages != null && gallery.Pages.Count > 0)
                {
                    int successCount = 0;
                    var digitsCount = gallery.Pages.Count.ToString().Length;
                    for (int i = 0; i < gallery.Pages.Count; i++)
                    {
                        var pagePath = gallery.Pages[i].Path;
                        if (!pagePath.StartsWith("/")) pagePath = "/" + pagePath;
                        
                        var fileName = NormalizeBeforeDot(Path.GetFileName(pagePath), digitsCount);

                        var pageFile = Path.Combine(galleryFolder, fileName);

                        if (!File.Exists(pageFile))
                        {
                            for (int n = 1; n < 5; n++)
                            {
                                var pageUrl = $"https://i{n}.nhentai.net{pagePath}";

                                try
                                {
                                    await DownloadFile(pageUrl, pageFile);
                                    loadedMedia.Add($"/downloads/{gallery.Id}/{Path.GetFileName(fileName)}");
                                    successCount++;
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"  ❌ Error downloading page {i + 1} {galleryId}: {ex.Message}");
                                }
                            }
                        }
                        else
                        {
                            successCount++;
                        }
                    }
                    Console.WriteLine($"  📄 Pages: {galleryId} ({successCount}/{gallery.Pages.Count})");

                    if (successCount == gallery.Pages.Count)
                        return loadedMedia;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ Critical error downloading gallery {galleryId}: {ex.Message}");
            }

            return null;
        }
        private async Task<bool> DownloadTorrent(HttpClient httpClient, string galleryPath, string galleryId)
        {
            try
            {
                var torrentUrl = $"https://nhentai.net/g/{galleryId}/download";
                var response = await httpClient.GetAsync(torrentUrl);
                response.EnsureSuccessStatusCode();
                var torrentBytes = await response.Content.ReadAsByteArrayAsync();
                var torrentPath = Path.Combine(galleryPath, $"{galleryId}.torrent");
                await File.WriteAllBytesAsync(torrentPath, torrentBytes);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ Error downloading torrent for gallery {galleryId}: {ex.Message}");
                
            }
            return false;
        }
        private async Task DownloadFile(string url, string path)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            http.DefaultRequestHeaders.Add("Referer", "https://nhentai.net/");
            http.Timeout = TimeSpan.FromSeconds(30);

            try
            {
                var response = await http.GetAsync(url);
                response.EnsureSuccessStatusCode();

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
        }

        public static string NormalizeBeforeDot(string input, int totalLength)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            int dotIndex = input.IndexOf('.');

            string numberPart = dotIndex >= 0
                ? input.Substring(0, dotIndex)
                : input;

            if (!int.TryParse(numberPart, out var number))
                return input;

            string normalized = number.ToString($"D{totalLength}");

            return dotIndex >= 0
                ? normalized + input.Substring(dotIndex)
                : normalized;
        }
    }
}
