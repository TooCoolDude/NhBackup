using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NhBackup.WebApplication.Db;

namespace NhentaiBackup.WebApplication.Pages
{
    public class IndexModel : PageModel
    {
        private readonly ILogger<IndexModel> _logger;
        private readonly NhentaiDbContext _db;

        public IndexModel(ILogger<IndexModel> logger, NhentaiDbContext db)
        {
            _logger = logger;
            _db = db;
        }

        public int TotalPosts { get; set; }

        public async Task OnGetAsync()
        {
            TotalPosts = await _db.Galleries.CountAsync();
        }
    }
}
