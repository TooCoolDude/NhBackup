using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NhBackup.WebApplication.Db;

namespace NhBackup.WebApplication.Pages
{
    [Authorize]
    public class BrowseModel : PageModel
    {
        private readonly NhDbContext _db;

        public BrowseModel(NhDbContext db)
        {
            _db = db;
        }

        public List<Gallery> Galleries { get; set; } = new();

        [BindProperty(SupportsGet = true)] //save search query
        public string SearchTags { get; set; }

        public async Task OnGetAsync()
        {
            var query = _db.Galleries.AsQueryable();

            if (!string.IsNullOrWhiteSpace(SearchTags))
            {
                var tags = SearchTags.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                var galleryIds = await _db.GalleryTags
                    .Where(gt => tags.Contains(gt.Tag.Name))
                    .GroupBy(gt => gt.GalleryId)
                    .Where(g => g.Count() == tags.Length)
                    .Select(g => g.Key)
                    .ToListAsync();

                query = query.Where(g => galleryIds.Contains(g.Id));
            }

            Galleries = await query
                .OrderByDescending(g => g.SyncedAt)
                .Take(50)
                .ToListAsync();
        }
    }
}
