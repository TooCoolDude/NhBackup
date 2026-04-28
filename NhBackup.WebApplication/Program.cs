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

        // ----------------------------
        // CONFIG
        // ----------------------------
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

        // ----------------------------
        // HANDLERS + STATE STORE
        // ----------------------------
        builder.Services.AddSingleton<ApiRateLimitStateStore>();
        builder.Services.AddTransient<ApiRateLimitHandler>();
        builder.Services.AddTransient<CdnResilienceHandler>();

        // ----------------------------
        // CDN POOL
        // ----------------------------
        // Singleton — holds per-CDN cooldown state across the sync run
        builder.Services.AddSingleton<CdnPool>();

        // ----------------------------
        // API CLIENT (Kiota)
        // ----------------------------
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
                httpClient: http
            );

            adapter.BaseUrl = "https://nhentai.net";

            return new ApiClient(adapter);
        });

        // ----------------------------
        // CDN CLIENT
        // ----------------------------
        builder.Services.AddHttpClient("cdn")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            })
            .AddHttpMessageHandler<CdnResilienceHandler>();

        // ----------------------------
        // APP SERVICES
        // ----------------------------
        builder.Services.AddTransient<SyncClient>();
        builder.Services.AddHostedService<Syncronizer>();

        // ----------------------------
        // AUTH
        // ----------------------------
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/Login";
                options.AccessDeniedPath = "/Login";
            });

        var app = builder.Build();

        // ----------------------------
        // PIPELINE
        // ----------------------------
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

        // ----------------------------
        // API ENDPOINT
        // ----------------------------
        app.MapPost("/api/favorite/toggle/{id}", async (int id, NhDbContext db) =>
        {
            var gallery = await db.Galleries.FindAsync(id);

            if (gallery == null)
                return Results.Json(new { success = false, message = "Error: Gallery not found" });

            gallery.IsFavorite = !gallery.IsFavorite;
            await db.SaveChangesAsync();

            return Results.Json(new
            {
                success = true,
                isFavorite = gallery.IsFavorite,
                message = gallery.IsFavorite ? "Added to favorites" : "Deleted from favorites"
            });
        });

        // ----------------------------
        // DB INIT
        // ----------------------------
        using (var scope = app.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<NhDbContext>();
            dbContext.Database.EnsureCreated();
        }

        app.Run();
    }
}