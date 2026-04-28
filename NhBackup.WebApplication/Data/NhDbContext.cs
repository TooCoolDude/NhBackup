using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NhBackup.WebApplication.Db.Entities;
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
            var path = Path.Combine(_options.DataFolder, "nhentai.db");
            options.UseSqlite($"Data Source={path}");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GalleryTag>()
                .HasKey(gt => new { gt.GalleryId, gt.TagId });

            modelBuilder.Entity<GalleryTag>()
                .HasOne(gt => gt.Gallery)
                .WithMany()
                .HasForeignKey(gt => gt.GalleryId);

            modelBuilder.Entity<GalleryTag>()
                .HasOne(gt => gt.Tag)
                .WithMany()
                .HasForeignKey(gt => gt.TagId);

            modelBuilder.Entity<Gallery>()
                .HasMany(g => g.Tags)
                .WithMany(t => t.Galleries)
                .UsingEntity<GalleryTag>();

            modelBuilder.Entity<Gallery>()
                .HasIndex(g => g.Id)
                .IsUnique();

            modelBuilder.Entity<Tag>()
                .HasIndex(t => t.Id)
                .IsUnique();
        }
    }
}
