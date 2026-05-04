using System.ComponentModel.DataAnnotations;

namespace NhBackup.WebApplication.Options
{
    public class NhSyncronizerOptions
    {
        [Required]
        public string ApiKey { get; set; }

        [Required]
        public string AdminPassword { get; set; }

        public int SyncIntevralHours { get; set; } = 2;

        [Required]
        public string DataFolder { get; set; }
    }
}
