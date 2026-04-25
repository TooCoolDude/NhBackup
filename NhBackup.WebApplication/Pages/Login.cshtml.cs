using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using NhBackup.WebApplication.Options;
using System.Security.Claims;

namespace NhBackup.WebApplication.Pages
{
    public class LoginModel : PageModel
    {
        [BindProperty]
        public string Password { get; set; }

        public string ErrorMessage { get; set; }

        private string _adminPassword;

        public LoginModel(IOptions<DatabaseOptions> options)
        {
            _adminPassword = options.Value.AdminPassword;
        }

        public void OnGet()
        {
            // Если уже авторизован - перенаправляем на главную
            if (User.Identity.IsAuthenticated)
            {
                RedirectToPage("/Index");
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (Password == _adminPassword)
            {
                // Создаем claims (данные пользователя)
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, "Admin"),
                    new Claim(ClaimTypes.Role, "Admin")
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

                return RedirectToPage("/Index");
            }

            ErrorMessage = "Неверный пароль";
            return Page();
        }

        public async Task<IActionResult> OnPostLogoutAsync()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToPage("/Index");
        }
    }
}
