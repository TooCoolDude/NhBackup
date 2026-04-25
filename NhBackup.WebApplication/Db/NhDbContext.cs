using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NhBackup.WebApplication.Options;

namespace NhBackup.WebApplication.Db
{
    public class NhDbContext : DbContext
    {
        private DatabaseOptions _options;

        public DbSet<Gallery> Galleries { get; set; }
        public DbSet<Tag> Tags { get; set; }
        public DbSet<GalleryTag> GalleryTags { get; set; }

        public NhDbContext(IOptions<DatabaseOptions> options)
        {
            _options = options.Value;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            var path = Path.Combine(_options.DatabaseFolder, "nhentai.db");
            options.UseSqlite($"Data Source={path}");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Составной ключ для связующей таблицы
            modelBuilder.Entity<GalleryTag>()
                .HasKey(gt => new { gt.GalleryId, gt.TagId });

            // Индексы
            modelBuilder.Entity<Gallery>()
                .HasIndex(g => g.Id)
                .IsUnique();

            modelBuilder.Entity<Tag>()
                .HasIndex(t => t.Id)
                .IsUnique();
        }
    }
}
