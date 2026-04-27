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
        private readonly SyncClient _syncClient;

        public Syncronizer(IOptions<NhSyncronizerOptions> options, IServiceScopeFactory scopeFactory, ILogger<Syncronizer> logger)
        {
            _options = options.Value; 
            _scopeFactory = scopeFactory;
            _logger = logger;
            _timer = new(TimeSpan.FromHours(_options.SyncIntevralHours));
            _syncClient = new SyncClient(options);
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
            var cdns = await _syncClient.GetCDNs();
            var all = await _syncClient.GetAllFavourites();

            await using var scope = _scopeFactory.CreateAsyncScope();
            await using var db = scope.ServiceProvider.GetRequiredService<NhDbContext>();

            int processed = 0;
            int created = 0;
            int updated = 0;
            int brokenMedia = 0;

            foreach (var item in all)
            {
                if (item.Id is null) continue;

                var galleryId = item.Id.Value;

                Console.WriteLine($"\n=== {galleryId} ===");

                var existing = await db.Galleries.FindAsync(galleryId);
                bool isNew = existing == null;

                bool isValidInDb = false;

                if (!isNew)
                {
                    isValidInDb = IsValidGallery(existing);

                    // 🔴 КЛЮЧЕВАЯ ДИАГНОСТИКА
                    if (existing.MediaPaths == null)
                    {
                        Console.WriteLine("❌ MediaPaths = NULL");
                    }
                    else if (!existing.MediaPaths.Any())
                    {
                        Console.WriteLine("❌ MediaPaths = EMPTY");
                    }
                    else
                    {
                        Console.WriteLine($"MediaPaths count: {existing.MediaPaths.Count}");
                    }

                    // Доп проверка файлов на диске
                    if (existing.MediaPaths != null && existing.MediaPaths.Any())
                    {
                        var missingFiles = existing.MediaPaths.Where(p => !FileExists(p)).ToList();
                        if (missingFiles.Any())
                        {
                            Console.WriteLine($"❌ Missing files: {missingFiles.Count}");
                            isValidInDb = false;
                        }
                    }
                }

                // 🔥 ПРИНУДИТЕЛЬНАЯ ПРОВЕРКА
                bool needRedownload =
                    isNew ||
                    !isValidInDb ||
                    existing?.MediaPaths == null ||
                    !existing.MediaPaths.Any();

                if (needRedownload)
                {
                    if (!isNew)
                    {
                        Console.WriteLine("⚠️ Forcing re-download");

                        db.Galleries.Remove(existing);
                        await db.SaveChangesAsync();
                    }

                    var full = await _syncClient.GetGallery(galleryId);
                    if (full == null)
                    {
                        Console.WriteLine("❌ full == null");
                        continue;
                    }

                    var galleryFolder = Path.Combine(
                        _options.DatabaseFolder,
                        "downloads",
                        galleryId.ToString());

                    var mediaPaths = await DownloadGalleryMedia(cdns, full, galleryFolder);

                    // 🔴 КРИТИЧЕСКАЯ ПРОВЕРКА
                    if (mediaPaths == null)
                    {
                        Console.WriteLine("❌ mediaPaths == null");
                        brokenMedia++;
                        continue;
                    }

                    if (!mediaPaths.Any())
                    {
                        Console.WriteLine("❌ mediaPaths EMPTY после скачивания");
                        brokenMedia++;
                        continue;
                    }

                    // Проверка существования файлов
                    var notExists = mediaPaths.Where(p => !FileExists(p)).ToList();
                    if (notExists.Any())
                    {
                        Console.WriteLine($"❌ скачали, но файлов нет: {notExists.Count}");
                        brokenMedia++;
                        continue;
                    }

                    var gallery = new Gallery
                    {
                        Id = galleryId,
                        MediaId = item.MediaId,
                        MediaPaths = mediaPaths,
                        EnglishTitle = item.EnglishTitle,
                        JapaneseTitle = SyncronizerHelpers.GetJapaneseTitle(item.JapaneseTitle),
                        NumPages = item.NumPages ?? 0,
                        Thumbnail = item.Thumbnail,
                        ThumbnailWidth = item.ThumbnailWidth ?? 0,
                        ThumbnailHeight = item.ThumbnailHeight ?? 0,
                        Blacklisted = item.Blacklisted ?? false,
                        SyncedAt = DateTime.UtcNow
                    };

                    await db.Galleries.AddAsync(gallery);
                    await db.SaveChangesAsync();

                    Console.WriteLine($"✅ CREATED with {mediaPaths.Count} files");

                    created++;
                }
                else
                {
                    Console.WriteLine("OK existing");
                }

                // --- TAGS ---
                bool tagsAdded = false;

                if (item.TagIds != null)
                {
                    foreach (var tagId in item.TagIds.Where(x => x.HasValue).Select(x => x.Value))
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

                if (tagsAdded)
                {
                    await db.SaveChangesAsync();
                    updated++;
                    Console.WriteLine("🔄 tags updated");
                }

                processed++;
            }

            var tagIds = await db.Tags.Select(t => t.Id).ToListAsync();
            await LoadTagNamesAndTypes(db, tagIds);

            Console.WriteLine("\n=== DONE ===");
            Console.WriteLine($"Processed: {processed}");
            Console.WriteLine($"Created: {created}");
            Console.WriteLine($"Updated: {updated}");
            Console.WriteLine($"Broken media: {brokenMedia}");
        }

        private string ToFullPath(string relativePath)
        {
            return Path.Combine(
                _options.DatabaseFolder,
                relativePath.TrimStart('/'));
        }

        private bool FileExists(string relativePath)
        {
            return File.Exists(ToFullPath(relativePath));
        }

        private static bool IsValidGallery(Gallery? existing)
        {
            if (existing.MediaPaths == null)
            {
                return false;
            }
            if(existing.NumPages != existing.MediaPaths.Count)
            {
                return false;
            }
            return true;
        }

        private async Task LoadTagNamesAndTypes(NhDbContext db, List<int> ids)
        {
            try
            {
                var tags = await _syncClient.GetTags(ids);
                await SaveTagsAsync(db, tags);
            }
            catch(Exception ex)
            {
                throw;
            }
        }

        private async Task SaveTagsAsync(NhDbContext db, List<TagResponse> tags)
        {
            foreach (var tag in tags)
            {
                if (tag.Id == null)
                    continue;

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

        private async Task<List<string>?> DownloadGalleryMedia(
    List<string> cdns,
    GalleryDetailResponse gallery,
    string galleryFolder)
        {
            if (gallery.Id == null)
                return null;

            var galleryId = gallery.Id.Value;
            var loadedMedia = new List<string>();

            try
            {
                Directory.CreateDirectory(galleryFolder);

                if (gallery.Pages == null || gallery.Pages.Count == 0)
                {
                    Console.WriteLine($"❌ No pages for {galleryId}");
                    return null;
                }

                int total = gallery.Pages.Count;
                int successCount = 0;
                int digits = total.ToString().Length;

                for (int i = 0; i < total; i++)
                {
                    var pagePath = gallery.Pages[i].Path;

                    if (!pagePath.StartsWith("/"))
                        pagePath = "/" + pagePath;

                    var fileName = SyncronizerHelpers.NormalizeBeforeDot(
                        Path.GetFileName(pagePath),
                        digits);

                    var fullFilePath = Path.Combine(galleryFolder, fileName);

                    bool success = false;

                    // 🔥 скачиваем только если нет файла
                    if (!File.Exists(fullFilePath))
                    {
                        foreach (var cdn in cdns)
                        {
                            var url = $"{cdn}{pagePath}";

                            try
                            {
                                await _syncClient.DownloadFileByUrl(url, fullFilePath);

                                if (File.Exists(fullFilePath))
                                {
                                    success = true;
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"❌ CDN fail {cdn} page {i + 1}/{total}: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        success = true;
                    }

                    if (success)
                    {
                        // 🔥 В БД храним RELATIVE path
                        loadedMedia.Add($"/downloads/{galleryId}/{fileName}");
                        successCount++;
                    }
                    else
                    {
                        Console.WriteLine($"❌ FAILED page {i + 1}/{total} ({galleryId})");
                    }
                }

                Console.WriteLine($"📄 Pages: {galleryId} ({successCount}/{total})");

                if (successCount != total)
                    throw new Exception($"Not all pages downloaded: {successCount}/{total}");

                return loadedMedia;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Critical error gallery {galleryId}: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> TryDownloadFileFromMultipleCDN(
            List<string> cdns,
            string pagePath,
            string pageFile,
            int galleryId,
            int pageIndex)
        {
            foreach (var cdn in cdns)
            {
                var pageUrl = $"{cdn}{pagePath}";

                try
                {
                    await _syncClient.DownloadFileByUrl(pageUrl, pageFile);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ❌ Error downloading page {pageIndex + 1} {galleryId}: {ex.Message}");
                }
            }
            return false;
        }


    }
}
