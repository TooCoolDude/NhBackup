using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nh.Api.Models;
using NhBackup.WebApplication.Db;
using NhBackup.WebApplication.Db.Entities;
using NhBackup.WebApplication.Infrastructure;
using NhBackup.WebApplication.Infrastructure.Clients;
using NhBackup.WebApplication.Options;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NhentaiBackup.WebApplication;

public class Syncronizer : BackgroundService
{
    private readonly NhSyncronizerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Syncronizer> _logger;
    private readonly CdnPool _cdnPool;
    private readonly PeriodicTimer _timer;

    public Syncronizer(
        IOptions<NhSyncronizerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<Syncronizer> logger,
        CdnPool cdnPool)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _cdnPool = cdnPool;
        _timer = new PeriodicTimer(TimeSpan.FromHours(_options.SyncIntevralHours));
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
        var syncClient = scope.ServiceProvider.GetRequiredService<SyncClient>();

        try
        {
            _logger.LogInformation("\n=== SYNC STARTED at {Time} ===", DateTime.UtcNow);

            var cdns = await syncClient.GetCDNs();
            _cdnPool.Initialize(cdns);

            var favorites = await syncClient.GetAllFavouritesList();
            var favoritesRequiresSync = await FilterFavouritesRequiresSync(favorites, db);
            await FetchGalleriesWithMedia(db, syncClient, favoritesRequiresSync, cancellationToken);

            var tagIds = await db.Tags.Select(t => t.Id).ToListAsync(cancellationToken);
            await LoadTagNamesAndTypes(db, syncClient, tagIds);
            await SaveGalleryBackups(db, cancellationToken);
            _logger.LogInformation("=== SYNC COMPLETED at {Time} ===", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "❌ Sync failed");
            throw;
        }
    }

    // ---------------------------------------------------------------------------
    // Filtering
    // ---------------------------------------------------------------------------

    private async Task<List<GalleryListItem>> FilterFavouritesRequiresSync(
        List<GalleryListItem> all,
        NhDbContext db)
    {
        _logger.LogInformation("Filtering galleries that require sync...");

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

            existingMap.TryGetValue(item.Id.Value, out var existing);

            if (existing == null ||
                !SyncronizerHelpers.ValidateDbGalleryEntity(existing) ||
                !ValidateMediaFiles(existing))
            {
                requiresSync.Add(item);
            }
        }

        _logger.LogInformation("Galleries requiring sync: {Count} of {Total}",
            requiresSync.Count, all.Count);

        return requiresSync;
    }

    // ---------------------------------------------------------------------------
    // Gallery fetch loop
    // ---------------------------------------------------------------------------

    private async Task FetchGalleriesWithMedia(
        NhDbContext db,
        SyncClient syncClient,
        List<GalleryListItem> items,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching {Count} galleries with media...", items.Count);

        int index = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var galleryId = item.Id!.Value;
            var full = await syncClient.GetGalleryMetadata(galleryId);

            var galleryFolder = Path.Combine(
                _options.DataFolder,
                "downloads",
                galleryId.ToString());

            var mediaPaths = await DownloadGalleryMedia(syncClient, full, galleryFolder, cancellationToken);

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

            await db.Galleries.AddAsync(gallery, cancellationToken);
            await UpdateTags(db, item);
            await db.SaveChangesAsync(cancellationToken);

            index++;
            _logger.LogInformation("Gallery {Index}/{Total} synced", index, items.Count);
        }
    }

    // ---------------------------------------------------------------------------
    // Download with CDN rotation
    // ---------------------------------------------------------------------------

    private async Task<List<string>?> DownloadGalleryMedia(
        SyncClient syncClient,
        GalleryDetailResponse gallery,
        string galleryFolder,
        CancellationToken cancellationToken)
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
                cancellationToken.ThrowIfCancellationRequested();

                var pagePath = gallery.Pages[i].Path;
                if (!pagePath.StartsWith("/"))
                    pagePath = "/" + pagePath;

                var fileName = SyncronizerHelpers.NormalizeBeforeDot(
                    Path.GetFileName(pagePath), digits);

                var fullFilePath = Path.Combine(galleryFolder, fileName);

                bool success;

                if (File.Exists(fullFilePath))
                {
                    success = true;
                }
                else
                {
                    success = await DownloadPageWithCdnRotation(
                        syncClient, pagePath, fullFilePath, galleryId, i, total, cancellationToken);
                }

                if (success)
                {
                    loadedMedia.Add($"/downloads/{galleryId}/{fileName}");
                    successCount++;
                }
                else
                {
                    _logger.LogWarning("❌ FAILED page {Page}/{Total} ({GalleryId})",
                        i + 1, total, galleryId);
                }
            }

            sw.Stop();

            var msPerPage = total > 0 ? sw.ElapsedMilliseconds / total : 0;
            _logger.LogInformation(
                "📄 Gallery {GalleryId} — {Success}/{Total} pages in {Ms}ms (~{MsPerPage}ms/page)",
                galleryId, successCount, total, sw.ElapsedMilliseconds, msPerPage);

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

    /// <summary>
    /// Tries to download a single page, rotating through CDN pool on failures.
    /// Each CDN gets one attempt — on failure it goes into cooldown and we try the next.
    /// </summary>
    private async Task<bool> DownloadPageWithCdnRotation(
        SyncClient syncClient,
        string pagePath,
        string fullFilePath,
        int galleryId,
        int pageIndex,
        int totalPages,
        CancellationToken cancellationToken)
    {
        // We try each CDN at most once per page — avoid infinite loops
        // by limiting attempts to the number of CDNs (tracked inside CdnPool)
        const int maxAttempts = 5;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string cdn;
            try
            {
                cdn = await _cdnPool.GetNextAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            var url = $"{cdn}{pagePath}";

            try
            {
                await syncClient.DownloadFileByUrl(url, fullFilePath);
                _cdnPool.ReportSuccess(cdn);
                return true;
            }
            catch (HttpRequestException ex) when ((int?)ex.StatusCode == 429)
            {
                _logger.LogWarning(
                    "429 from CDN {Cdn} — page {Page}/{Total} gallery {GalleryId}. Rotating.",
                    cdn, pageIndex + 1, totalPages, galleryId);
                _cdnPool.ReportFailure(cdn);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "❌ CDN {Cdn} failed — page {Page}/{Total} gallery {GalleryId}. Rotating.",
                    cdn, pageIndex + 1, totalPages, galleryId);
                _cdnPool.ReportFailure(cdn);
            }
        }

        _logger.LogError("❌ All CDN attempts exhausted — page {Page}/{Total} gallery {GalleryId}",
            pageIndex + 1, totalPages, galleryId);

        return false;
    }

    // ---------------------------------------------------------------------------
    // Tags
    // ---------------------------------------------------------------------------

    private async Task UpdateTags(NhDbContext db, GalleryListItem item)
    {
        if (item.TagIds == null)
            return;

        var galleryId = item.Id!.Value;
        var tagIds = item.TagIds.Where(x => x.HasValue).Select(x => x!.Value).ToList();

        if (!tagIds.Any())
            return;

        var existingTagIds = await db.Tags
            .Where(t => tagIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync();

        foreach (var tagId in tagIds.Except(existingTagIds))
            db.Tags.Add(new Tag { Id = tagId });

        var existingLinks = await db.GalleryTags
            .Where(gt => gt.GalleryId == galleryId && tagIds.Contains(gt.TagId))
            .Select(gt => gt.TagId)
            .ToListAsync();

        var newLinks = tagIds.Except(existingLinks).ToList();
        foreach (var tagId in newLinks)
        {
            db.GalleryTags.Add(new GalleryTag
            {
                GalleryId = galleryId,
                TagId = tagId
            });
        }

        if (newLinks.Any())
            _logger.LogInformation("🔄 Tags updated for gallery {GalleryId}", galleryId);
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

    private async Task SaveGalleryBackups(NhDbContext db, CancellationToken cancellationToken)
    {
        const int batchSize = 100;

        var totalIds = await db.Galleries
            .Select(g => g.Id)
            .ToListAsync(cancellationToken);

        int saved = 0;

        foreach (var batch in totalIds.Chunk(batchSize))
        {
            var galleries = await db.Galleries
                .AsNoTracking()
                .Where(g => batch.Contains(g.Id))
                .Include(g => g.Tags)
                .ToListAsync(cancellationToken);

            foreach (var gallery in galleries)
            {
                var folder = Path.Combine(_options.DataFolder, "downloads", gallery.Id.ToString());
                var jsonPath = Path.Combine(folder, "gallery.json");

                if (File.Exists(jsonPath))
                    continue;

                var json = JsonSerializer.Serialize(gallery, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    ReferenceHandler = ReferenceHandler.IgnoreCycles
                });

                await File.WriteAllTextAsync(jsonPath, json, cancellationToken);
                saved++;
            }
        }

        _logger.LogInformation("Gallery backups saved: {Saved} of {Total}", saved, totalIds.Count);
    }

    // ---------------------------------------------------------------------------
    // Validation
    // ---------------------------------------------------------------------------

    private bool ValidateMediaFiles(Gallery? existing)
    {
        if (existing?.MediaPaths == null || !existing.MediaPaths.Any())
            return true;

        var missingFiles = existing.MediaPaths.Where(p => !FileExists(p)).ToList();

        if (missingFiles.Any())
            _logger.LogWarning("❌ Missing files: {Count}", missingFiles.Count);

        return !missingFiles.Any();
    }

    private bool FileExists(string relativePath) =>
        File.Exists(SyncronizerHelpers.ToFullPath(_options.DataFolder, relativePath));
}