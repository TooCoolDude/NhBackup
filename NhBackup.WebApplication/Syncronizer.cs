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

            using var scope = _scopeFactory.CreateAsyncScope();
            using var db = scope.ServiceProvider.GetService<NhDbContext>();

            int added = 0;
            int updated = 0;

            foreach (var item in all)
            {
                if (item.Id == null) continue;

                var galleryId = item.Id.Value;
                GalleryDetailResponse fullGallery = await _syncClient.GetGallery(galleryId);
                if (fullGallery == null) continue;

                var existing = await db.Galleries.FindAsync(galleryId);
                bool isNew = existing == null;
                bool tagsAdded = false;

                if (isNew)
                {
                    var downloads = Path.Combine(_options.DatabaseFolder, "downloads");
                    var galleryFolder = Path.Combine(downloads, galleryId.ToString());

                    // var isTorrentLoaded = await DownloadTorrent(httpClient, galleryFolder, galleryId.ToString());

                    var mediaPaths = await DownloadGalleryMedia(cdns, fullGallery, galleryFolder);

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
                        JapaneseTitle = SyncronizerHelpers.GetJapaneseTitle(item.JapaneseTitle),
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
                await LoadTagNamesAndTypes(db, tagIds);

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

        private async Task<List<string>> DownloadGalleryMedia(List<string> cdns, GalleryDetailResponse gallery, string galleryFolder)
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
                        
                        var fileName = SyncronizerHelpers.NormalizeBeforeDot(Path.GetFileName(pagePath), digitsCount);

                        var pageFile = Path.Combine(galleryFolder, fileName);

                        if (!File.Exists(pageFile))
                        {
                            if(await TryDownloadFileFromMultipleCDN(cdns, gallery, loadedMedia, galleryId, successCount, i, pagePath, fileName, pageFile))
                                successCount++;
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

        public async Task<bool> TryDownloadFileFromMultipleCDN(List<string> cdns, GalleryDetailResponse gallery, List<string> loadedMedia, int galleryId, int successCount, int i, string pagePath, string fileName, string pageFile)
        {
            foreach (var cdn in cdns)
            {
                var pageUrl = $"{cdn}{pagePath}";

                try
                {
                    await _syncClient.DownloadFileByUrl(pageUrl, pageFile);
                    loadedMedia.Add($"/downloads/{gallery.Id}/{Path.GetFileName(fileName)}");
                    successCount++;
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ❌ Error downloading page {i + 1} {galleryId}: {ex.Message}");                   
                }
            }
            return false;
        }

        
    }
}
