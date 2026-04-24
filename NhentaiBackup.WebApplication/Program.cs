using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using NhBackup.WebApplication.Db;
using NhBackup.WebApplication.Options;
using NhentaiBackup.WebApplication;

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddRazorPages();

        builder.Configuration.AddEnvironmentVariables();

        if (builder.Environment.IsDevelopment())
        {
            builder.Configuration.AddUserSecrets<Program>();
        }

        builder.Services.AddOptions<NhSyncronizerOptions>()
            .Bind(builder.Configuration)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddDbContext<NhentaiDbContext>();

        builder.Services.AddHostedService<Syncronizer>();

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/Login";
                options.AccessDeniedPath = "/Login";
            });

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();

        if (!Directory.Exists("downloads"))
        {
            Directory.CreateDirectory("downloads");
        }
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(
                Path.Combine(Directory.GetCurrentDirectory(), "downloads")),
            RequestPath = "/downloads"
        });

        app.UseRouting();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapRazorPages();

        app.MapPost("/api/favorite/toggle/{id}", async (int id, NhentaiDbContext db) =>
        {
            var gallery = await db.Galleries.FindAsync(id);
            if (gallery == null)
                return Results.Json(new { success = false, message = "Галерея не найдена" });

            gallery.IsFavorite = !gallery.IsFavorite;
            await db.SaveChangesAsync();

            return Results.Json(new
            {
                success = true,
                isFavorite = gallery.IsFavorite,
                message = gallery.IsFavorite ? "Добавлено в избранное" : "Удалено из избранного"
            });
        });

        app.Run();
    }
}