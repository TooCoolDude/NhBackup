using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NhBackup.WebApplication.Db;
using NhBackup.WebApplication.Db.Entities;

namespace NhBackup.WebApplication.Pages
{
    [Authorize]
    public class GalleryModel : PageModel
    {
        private readonly NhDbContext _db;

        public GalleryModel(NhDbContext db)
        {
            _db = db;
        }

        public Gallery Gallery { get; set; }
        public List<Tag> Tags { get; set; }
        public List<string> PageImages { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Gallery = await _db.Galleries
                .Include(g => g.Meta)
                .FirstOrDefaultAsync(g => g.Id == id);

            if (Gallery == null)
                return NotFound();

            Tags = await _db.GalleryTags
                .Where(gt => gt.GalleryId == Gallery.Id)
                .Select(gt => gt.Tag)
                .ToListAsync();

            if (Gallery.MediaPaths != null)
                PageImages.AddRange(Gallery.MediaPaths);

            return Page();
        }
    }
}