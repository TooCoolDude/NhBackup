using Microsoft.EntityFrameworkCore;

namespace NhBackup.WebApplication.Db
{
    public class NhentaiDbContext : DbContext
    {
        public DbSet<Gallery> Galleries { get; set; }
        public DbSet<Tag> Tags { get; set; }
        public DbSet<GalleryTag> GalleryTags { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseSqlite("Data Source=nhentai.db");
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
