using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NhBackup.WebApplication.Db
{
    public class Gallery
    {
        public int Id { get; set; }
        public string? MediaId { get; set; }
        public List<string>? MediaPaths { get; set; }
        public string? EnglishTitle { get; set; }
        public string? JapaneseTitle { get; set; }
        public int NumPages { get; set; }
        public string? Thumbnail { get; set; }
        public int ThumbnailWidth { get; set; }
        public int ThumbnailHeight { get; set; }
        public bool Blacklisted { get; set; }
        public DateTime SyncedAt { get; set; }

        // Навигация
        public List<Tag> Tags { get; set; } = new();
        public bool IsFavorite { get; set; }
    }
}
