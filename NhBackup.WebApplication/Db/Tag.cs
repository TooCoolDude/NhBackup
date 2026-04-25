namespace NhBackup.WebApplication.Db
{
    public class Tag
    {
        public int Id { get; set; }
        public string? Name { get; set; }      // можно потом заполнить из /tags/{id}
        public string? Slug { get; set; }
        public string? Type { get; set; }      // artist, parody, character и т.д.

        // Навигация
        public List<Gallery> Galleries { get; set; } = new();
    }
}
