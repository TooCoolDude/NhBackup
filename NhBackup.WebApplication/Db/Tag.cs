using System.Text.Json.Serialization;

namespace NhBackup.WebApplication.Db
{
    public class Tag
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Slug { get; set; }
        public string? Type { get; set; }

        [JsonIgnore]
        public List<Gallery> Galleries { get; set; } = new();
    }
}
