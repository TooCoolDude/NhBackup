using System.Text.Json.Serialization;

namespace NhBackup.WebApplication.Db.Entities
{
    public class Gallery
    {
        public int Id { get; set; }
        public List<string>? MediaPaths { get; set; }
        public string? EnglishTitle { get; set; }
        public string? JapaneseTitle { get; set; }
        public int NumPages { get; set; }
        public List<Tag> Tags { get; set; } = new();

        [JsonIgnore]
        public GalleryMeta? Meta { get; set; }
    }
}