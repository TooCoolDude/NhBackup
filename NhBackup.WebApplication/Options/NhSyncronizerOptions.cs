using System.ComponentModel.DataAnnotations;

namespace NhBackup.WebApplication.Options
{
    public class NhSyncronizerOptions
    {
        [Required]
        public string Username { get; set; }

        [Required]
        public string Password { get; set; }

        [Required]
        public string ApiKey { get; set; }

        public int SyncIntevralHours { get; set; } = 2;

        [Required]
        public string DatabaseFolder { get; set; }
    }
}
