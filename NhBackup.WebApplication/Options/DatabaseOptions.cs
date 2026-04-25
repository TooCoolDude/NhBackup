using System.ComponentModel.DataAnnotations;

namespace NhBackup.WebApplication.Options
{
    public class DatabaseOptions
    {
        [Required]
        public string DatabaseFolder { get; set; }

        [Required]
        public string AdminPassword { get; set; }
    }
}
