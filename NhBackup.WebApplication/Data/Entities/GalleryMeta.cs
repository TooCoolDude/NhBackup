namespace NhBackup.WebApplication.Db.Entities
{
    public class GalleryMeta
    {
        public int GalleryId { get; set; }
        public Gallery Gallery { get; set; } = null!;
        public DateTime SyncedAt { get; set; }
        public bool IsFavorite { get; set; }
    }
}