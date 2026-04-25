using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NhBackup.WebApplication.Db;
using NhBackup.WebApplication.Options;

namespace NhBackup.WebApplication.Pages
{
    [Authorize]
    public class GalleryModel : PageModel
    {
        private readonly NhDbContext _db;
        private readonly string _folderPath;

        public GalleryModel(NhDbContext db, IOptions<DatabaseOptions> options)
        {
            _db = db;
            _folderPath = options.Value.DatabaseFolder;
        }

        public Gallery Gallery { get; set; }
        public List<Tag> Tags { get; set; }
        public List<string> PageImages { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Gallery = await _db.Galleries.FindAsync(id);
            if (Gallery == null)
                return NotFound();

            Tags = await _db.GalleryTags.Where(gt => gt.GalleryId == Gallery.Id).Select(gt => gt.Tag).ToListAsync();

            // Путь к папке с картинками
            var folderPath = Path.Combine(_folderPath, "downloads", id.ToString());

            if (Directory.Exists(folderPath))
            {
                var files = Directory.GetFiles(folderPath, "*.jpg")
                    .Where(f => !Path.GetFileName(f).Contains("cover"))
                    .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)))
                    .ToList();

                foreach (var file in files)
                {
                    PageImages.Add($"/downloads/{id}/{Path.GetFileName(file)}");
                }
            }

            return Page();
        }
    }
}
