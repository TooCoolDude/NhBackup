using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using NhBackup.WebApplication;
using NhBackup.WebApplication.Db;
using NhBackup.WebApplication.Options;
using Nhentai.Api;
using Nhentai.Api.Models;
using System.Collections.Generic;

namespace NhentaiBackup.WebApplication
{
    public class Syncronizer : BackgroundService
    {
        private readonly NhSyncronizerOptions _options;

        private readonly PeriodicTimer _timer;

        public Syncronizer(IOptions<NhSyncronizerOptions> options)
        {
            _options = options.Value; 
            _timer = new(TimeSpan.FromHours(_options.SyncIntevralHours));
            //_timer = new(TimeSpan.FromSeconds(5));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            do
            {
                //await DoWorkAsync();
            }
            while (await _timer.WaitForNextTickAsync(stoppingToken));
        }

        public async Task Sync(string apiKey, string downloadPath = "downloads")
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Key {_options.ApiKey}");
            httpClient.DefaultRequestHeaders.Add("User-Agent", "NhentaiBackup/1.0");

            var adapter = new HttpClientRequestAdapter(
                new AnonymousAuthenticationProvider(),
                httpClient: httpClient
            );
            adapter.BaseUrl = "https://nhentai.net";

            var client = new ApiClient(adapter);

            var all = await GetAllFavourites(client);

            using var db = new NhentaiDbContext();
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
                    var isMediaLoaded = await DownloadGalleryMedia(fullGallery, downloadPath);
                    if (!isMediaLoaded)
                    {
                        Console.WriteLine($"Не удалось загрузить медиа, пропускаем: {galleryId} - {item.EnglishTitle}");
                        continue;
                    }

                    var gallery = new Gallery
                    {
                        Id = galleryId,
                        MediaId = item.MediaId,
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
                    Console.WriteLine($"✅ Добавлена: {galleryId} - {gallery.EnglishTitle}");
                }

                // Добавляем новые теги
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
                await LoadTagNamesAndTypes(client, tagIds);

                if (!isNew && tagsAdded)
                {
                    updated++;
                    Console.WriteLine($"🔄 Добавлены новые теги: {galleryId}");
                }
                else if (!isNew && !tagsAdded)
                {
                    //Console.WriteLine($"⏭ Без изменений: {galleryId}");
                }

                //break; //remove after test!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
            }


            Console.WriteLine($"\n=== Готово ===");
            Console.WriteLine($"Добавлено: {added}");
            Console.WriteLine($"Обновлено: {updated}");
            Console.WriteLine($"Всего в БД: {await db.Galleries.CountAsync()}");
        }

        private async Task LoadTagNamesAndTypes(ApiClient client, List<int> ids)
        {
            using var db = new NhentaiDbContext();

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
                Console.WriteLine($"Страница {page}: {data.Result.Count} (всего {all.Count})");

                page++;
                await Task.Delay(6000);

                break; //remove after test!!!!!!!!!!!!!!!
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

        private async Task<bool> DownloadGalleryMedia(GalleryDetailResponse gallery, string downloadPath)
        {
            if (gallery.Id == null) return false;

            var galleryId = gallery.Id.Value;
            var galleryFolder = Path.Combine(downloadPath, galleryId.ToString());

            try
            {
                Directory.CreateDirectory(galleryFolder);

                // Скачиваем обложку
                if (!string.IsNullOrEmpty(gallery.Cover?.Path))
                {
                    var coverPath = gallery.Cover.Path;
                    if (!coverPath.StartsWith("/")) coverPath = "/" + coverPath;

                    var coverFile = Path.Combine(galleryFolder, "cover.jpg");

                    bool success = false;
                    if (!File.Exists(coverFile))
                    {
                        for (int t = 1; t < 5; t++)
                        {
                            var coverUrl = $"https://t{t}.nhentai.net{coverPath}";

                            try
                            {
                                await DownloadFile(coverUrl, coverFile);
                                Console.WriteLine($"  📸 Обложка: {galleryId}");
                                success = true;
                                break;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  ❌ Ошибка обложки {galleryId}: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        success = true;
                    }
                }

                // Скачиваем страницы
                if (gallery.Pages != null && gallery.Pages.Count > 0)
                {
                    int successCount = 0;
                    for (int i = 0; i < gallery.Pages.Count; i++)
                    {
                        var pagePath = gallery.Pages[i].Path;
                        if (!pagePath.StartsWith("/")) pagePath = "/" + pagePath;

                        var pageFile = Path.Combine(galleryFolder, $"{i + 1}.jpg");

                        if (!File.Exists(pageFile))
                        {
                            for (int n = 1; n < 5; n++)
                            {
                                var pageUrl = $"https://i{n}.nhentai.net{pagePath}";

                                try
                                {
                                    await DownloadFile(pageUrl, pageFile);
                                    successCount++;
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"  ❌ Ошибка страницы {i + 1} {galleryId}: {ex.Message}");
                                }
                            }
                        }
                        else
                        {
                            successCount++;
                        }
                    }
                    Console.WriteLine($"  📄 Страницы: {galleryId} ({successCount}/{gallery.Pages.Count})");

                    if (successCount == gallery.Pages.Count)
                        return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ Критическая ошибка при скачивании галереи {galleryId}: {ex.Message}");
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
                throw new Exception($"Таймаут при загрузке {url}");
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"HTTP ошибка: {ex.Message}");
            }
        }
    }
}
