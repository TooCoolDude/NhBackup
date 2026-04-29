using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace NhBackup.WebApplication.Db.Entities
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
        [JsonIgnore]
        public DateTime SyncedAt { get; set; }
        public List<Tag> Tags { get; set; } = new();
        [JsonIgnore]
        public bool IsFavorite { get; set; }
    }
}
