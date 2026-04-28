namespace NhBackup.WebApplication.Db.Entities
{
    public class GalleryTag
    {
        public int GalleryId { get; set; }
        public int TagId { get; set; }

        public Gallery Gallery { get; set; } = null!;
        public Tag Tag { get; set; } = null!;
    }
}
