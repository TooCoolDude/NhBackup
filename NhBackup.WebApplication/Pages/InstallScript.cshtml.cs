using Microsoft.AspNetCore.Mvc.RazorPages;

namespace NhBackup.WebApplication.Pages;

public class InstallScriptModel : PageModel
{
    public string PrimaryBaseUrl { get; private set; } = string.Empty;

    public void OnGet()
    {
        // Build base URL from current request: https://yourhost:port
        var req = Request;
        PrimaryBaseUrl = $"{req.Scheme}://{req.Host}";
    }
}