using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Nh.Api;
using NhBackup.WebApplication.Db;
using NhBackup.WebApplication.Infrastructure;
using NhBackup.WebApplication.Infrastructure.Clients;
using NhBackup.WebApplication.Infrastructure.Handlers;
using NhBackup.WebApplication.Options;
using NhentaiBackup.WebApplication;
using System.Text;

internal class Program
{
    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var builder = WebApplication.CreateBuilder(args);

        builder.Configuration.AddEnvironmentVariables();

        if (builder.Environment.IsDevelopment())
            builder.Configuration.AddUserSecrets<Program>();

        builder.Services.AddRazorPages();

        builder.Services.AddOptions<NhSyncronizerOptions>()
            .Bind(builder.Configuration)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddOptions<DatabaseOptions>()
            .Bind(builder.Configuration)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddDbContext<NhDbContext>();

        builder.Services.AddSingleton<ApiRateLimitStateStore>();
        builder.Services.AddTransient<ApiRateLimitHandler>();
        builder.Services.AddTransient<CdnResilienceHandler>();
        builder.Services.AddSingleton<CdnPool>();

        builder.Services.AddHttpClient("api", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<NhSyncronizerOptions>>().Value;
            client.DefaultRequestHeaders.Add("Authorization", $"Key {options.ApiKey}");
            client.DefaultRequestHeaders.Add("User-Agent", "NhBackup/1.0");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        .AddHttpMessageHandler<ApiRateLimitHandler>();

        builder.Services.AddSingleton<ApiClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient("api");
            var adapter = new HttpClientRequestAdapter(
                authenticationProvider: new AnonymousAuthenticationProvider(),
                httpClient: http);
            adapter.BaseUrl = "https://nhentai.net";
            return new ApiClient(adapter);
        });

        builder.Services.AddHttpClient("cdn")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            })
            .AddHttpMessageHandler<CdnResilienceHandler>();

        builder.Services.AddTransient<SyncClient>();
        builder.Services.AddHostedService<Syncronizer>();

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/Login";
                options.AccessDeniedPath = "/Login";
            });
        builder.Services.AddHealthChecks();
        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();

        var dbOptions = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var downloadsPath = Path.Combine(dbOptions.DataFolder, "downloads");
        Directory.CreateDirectory(downloadsPath);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(downloadsPath),
            RequestPath = "/downloads"
        });

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRazorPages();
        app.MapHealthChecks("/healthz");

        // Favorite toggle — works via GalleryMeta
        app.MapPost("/api/favorite/toggle/{id}", async (int id, NhDbContext db) =>
        {
            var meta = await db.GalleryMetas.FindAsync(id);

            if (meta == null)
                return Results.Json(new { success = false, message = "Error: Gallery not found" });

            meta.IsFavorite = !meta.IsFavorite;
            await db.SaveChangesAsync();

            return Results.Json(new
            {
                success = true,
                isFavorite = meta.IsFavorite,
                message = meta.IsFavorite ? "Added to favorites" : "Deleted from favorites"
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