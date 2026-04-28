using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nh.Api.Models;
using NhBackup.WebApplication.Db;
using NhBackup.WebApplication.Infrastructure.Clients;
using NhBackup.WebApplication.Options;
using System.Diagnostics;

namespace NhentaiBackup.WebApplication
{
    public class Syncronizer : BackgroundService
    {
        private readonly NhSyncronizerOptions _options;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<Syncronizer> _logger;
        private readonly PeriodicTimer _timer;

        // SyncClient убран из конструктора — резолвится через scope в каждом цикле
        public Syncronizer(
            IOptions<NhSyncronizerOptions> options,
            IServiceScopeFactory scopeFactory,
            ILogger<Syncronizer> logger)
        {
            _options = options.Value;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _timer = new(TimeSpan.FromHours(_options.SyncIntevralHours));
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            do
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    await Syncronize(cancellationToken);
                    sw.Stop();
                    _logger.LogInformation(
                        "✅ Sync iteration completed in {Min}m {Sec}s",
                        (int)sw.Elapsed.TotalMinutes,
                        sw.Elapsed.Seconds);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex,
                        "❌ Sync iteration failed after {Min}m {Sec}s",
                        (int)sw.Elapsed.TotalMinutes,
                        sw.Elapsed.Seconds);
                }
            }
            while (await _timer.WaitForNextTickAsync(cancellationToken));
        }

        public async Task Syncronize(CancellationToken cancellationToken)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            await using var db = scope.ServiceProvider.GetRequiredService<NhDbContext>();

            // SyncClient резолвится из scope — теперь Transient работает корректно
            var syncClient = scope.ServiceProvider.GetRequiredService<SyncClient>();

            try
            {
                _logger.LogInformation("\n=== SYNC STARTED at {Time} ===", DateTime.UtcNow);

                var cdns = await syncClient.GetCDNs();
                var favorites = await syncClient.GetAllFavouritesList();
                var favoritesRequiresSync = await FilterFavouritesRequiresSync(favorites, db);
                await FetchGalleriesWithMedia(cdns, db, syncClient, favoritesRequiresSync);

                var tagIds = await db.Tags.Select(t => t.Id).ToListAsync(cancellationToken);
                await LoadTagNamesAndTypes(db, syncClient, tagIds);

                _logger.LogInformation("=== SYNC COMPLETED at {Time} ===", DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "❌ Sync failed");
                throw;
            }
        }

        private async Task<List<GalleryListItem>> FilterFavouritesRequiresSync(
            List<GalleryListItem> all,
            NhDbContext db)
        {
            _logger.LogInformation("Filtering galleries that require sync...");

            // Загружаем все существующие Id одним запросом вместо FindAsync в цикле
            var allIds = all
                .Where(x => x.Id.HasValue)
                .Select(x => x.Id!.Value)
                .ToList();

            var existingGalleries = await db.Galleries
                .Where(g => allIds.Contains(g.Id))
                .ToListAsync();

            var existingMap = existingGalleries.ToDictionary(g => g.Id);

            var requiresSync = new List<GalleryListItem>();

            foreach (var item in all)
            {
                if (item.Id is null)
                    continue;

                var galleryId = item.Id.Value;
                existingMap.TryGetValue(galleryId, out var existing);

                if (existing == null ||
                    !SyncronizerHelpers.ValidateDbGalleryEntity(existing) ||
                    !ValidateMediaFiles(existing))
                {
                    requiresSync.Add(item);
                }
            }

            _logger.LogInformation("Galleries requiring sync: {Count} of {Total}", requiresSync.Count, all.Count);
            return requiresSync;
        }

        private async Task FetchGalleriesWithMedia(
            List<string> cdns,
            NhDbContext db,
            SyncClient syncClient,
            List<GalleryListItem> items)
        {
            _logger.LogInformation("Fetching galleries with media...");

            int index = 0;
            foreach (var item in items)
            {
                var galleryId = item.Id!.Value;
                var full = await syncClient.GetGalleryMetadata(galleryId);

                var galleryFolder = Path.Combine(
                    _options.DataFolder,
                    "downloads",
                    galleryId.ToString());

                var mediaPaths = await DownloadGalleryMedia(cdns, syncClient, full, galleryFolder);

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
                await UpdateTags(db, item);

                // SaveChanges один раз на галерею (gallery + tags вместе)
                await db.SaveChangesAsync();

                index++;
                _logger.LogInformation("Gallery {Index} of {Total} synced", index, items.Count);
            }
        }

        private async Task UpdateTags(NhDbContext db, GalleryListItem item)
        {
            if (item.TagIds == null)
                return;

            var galleryId = item.Id!.Value;
            var tagIds = item.TagIds.Where(x => x.HasValue).Select(x => x!.Value).ToList();

            if (!tagIds.Any())
                return;

            // Загружаем существующие теги одним запросом
            var existingTagIds = await db.Tags
                .Where(t => tagIds.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync();

            var missingTagIds = tagIds.Except(existingTagIds).ToList();
            foreach (var tagId in missingTagIds)
                db.Tags.Add(new Tag { Id = tagId });

            // Загружаем существующие связи одним запросом
            var existingLinks = await db.GalleryTags
                .Where(gt => gt.GalleryId == galleryId && tagIds.Contains(gt.TagId))
                .Select(gt => gt.TagId)
                .ToListAsync();

            var missingLinks = tagIds.Except(existingLinks).ToList();
            foreach (var tagId in missingLinks)
            {
                db.GalleryTags.Add(new GalleryTag
                {
                    GalleryId = galleryId,
                    TagId = tagId
                });
            }

            if (missingLinks.Any())
                _logger.LogInformation("🔄 Tags updated for gallery {GalleryId}", galleryId);
        }

        private bool ValidateMediaFiles(Gallery? existing)
        {
            if (existing?.MediaPaths == null || !existing.MediaPaths.Any())
                return true;

            var missingFiles = existing.MediaPaths.Where(p => !FileExists(p)).ToList();

            if (missingFiles.Any())
                _logger.LogWarning("❌ Missing files: {Count}", missingFiles.Count);

            return !missingFiles.Any();
        }

        private bool FileExists(string relativePath)
        {
            return File.Exists(SyncronizerHelpers.ToFullPath(_options.DataFolder, relativePath));
        }

        private async Task LoadTagNamesAndTypes(NhDbContext db, SyncClient syncClient, List<int> ids)
        {
            _logger.LogInformation("Loading tag names and types for {Count} tags...", ids.Count);
            try
            {
                var tags = await syncClient.GetTags(ids);
                _logger.LogInformation("Fetched {Count} tags from API", tags.Count);
                await SaveTagsAsync(db, tags);
                _logger.LogInformation("Tag names and types updated");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to load tag names and types");
                throw;
            }
        }

        private async Task SaveTagsAsync(NhDbContext db, List<TagResponse> tags)
        {
            var ids = tags.Where(t => t.Id.HasValue).Select(t => t.Id!.Value).ToList();

            var existing = await db.Tags
                .Where(t => ids.Contains(t.Id))
                .ToListAsync();

            var existingMap = existing.ToDictionary(t => t.Id);

            foreach (var tag in tags)
            {
                if (tag.Id == null)
                    continue;

                if (existingMap.TryGetValue(tag.Id.Value, out var dbTag))
                {
                    dbTag.Name = tag.Name;
                    dbTag.Slug = tag.Slug;
                    dbTag.Type = tag.Type;
                }
            }

            await db.SaveChangesAsync();
        }

        private async Task<List<string>?> DownloadGalleryMedia(
    List<string> cdns,
    SyncClient syncClient,
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
                    throw new Exception($"Gallery {galleryId} has no pages info");

                int total = gallery.Pages.Count;
                int successCount = 0;
                int digits = total.ToString().Length;

                var sw = Stopwatch.StartNew();

                for (int i = 0; i < total; i++)
                {
                    var pagePath = gallery.Pages[i].Path;

                    if (!pagePath.StartsWith("/"))
                        pagePath = "/" + pagePath;

                    var fileName = SyncronizerHelpers.NormalizeBeforeDot(
                        Path.GetFileName(pagePath),
                        digits);

                    var fullFilePath = Path.Combine(galleryFolder, fileName);

                    bool success;

                    if (!File.Exists(fullFilePath))
                        success = await TryDownloadFileFromMultipleCDN(cdns, syncClient, pagePath, fullFilePath, galleryId, i);
                    else
                        success = true;

                    if (success)
                    {
                        loadedMedia.Add($"/downloads/{galleryId}/{fileName}");
                        successCount++;
                    }
                    else
                    {
                        _logger.LogWarning("❌ FAILED page {Page}/{Total} ({GalleryId})", i + 1, total, galleryId);
                    }
                }

                sw.Stop();

                var msPerPage = total > 0 ? sw.ElapsedMilliseconds / total : 0;

                _logger.LogInformation(
                    "📄 Gallery {GalleryId} — {Success}/{Total} pages in {Ms}ms (~{MsPerPage}ms/page)",
                    galleryId,
                    successCount,
                    total,
                    sw.ElapsedMilliseconds,
                    msPerPage);

                if (successCount != total)
                    throw new Exception($"Not all pages downloaded: {successCount}/{total}");

                return loadedMedia;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Critical error gallery {GalleryId}", galleryId);
                return null;
            }
        }

        private async Task<bool> TryDownloadFileFromMultipleCDN(
            List<string> cdns,
            SyncClient syncClient,
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
                    await syncClient.DownloadFileByUrl(pageUrl, pageFile);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "❌ CDN {Cdn} failed — page {Page}/{GalleryId}", cdn, pageIndex + 1, galleryId);
                }
            }

            _logger.LogError("❌ All CDNs failed for page {Page}/{GalleryId}", pageIndex + 1, galleryId);
            return false;
        }
    }
}