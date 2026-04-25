using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using NhBackup.WebApplication.Db;
using NhBackup.WebApplication.Options;
using NhentaiBackup.WebApplication;
using static Microsoft.IO.RecyclableMemoryStreamManager;

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

        builder.Services.AddOptions<DatabaseOptions>()
            .Bind(builder.Configuration)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddDbContext<NhDbContext>();

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

        var dbOptions = app.Services.GetService<IOptions<DatabaseOptions>>().Value;
        var downloadsPath = Path.Combine(dbOptions.DatabaseFolder, "downloads");
        if (!Directory.Exists(downloadsPath))
        {
            Directory.CreateDirectory(downloadsPath);
        }
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(
                downloadsPath),
            RequestPath = "/downloads"
        });

        app.UseRouting();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapRazorPages();

        app.MapPost("/api/favorite/toggle/{id}", async (int id, NhDbContext db) =>
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

        using (var scope = app.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<NhDbContext>();
            dbContext.Database.EnsureCreated();
        }

        app.Run();
    }
}