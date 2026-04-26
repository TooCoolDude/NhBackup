using System.Threading.RateLimiting;
namespace NhentaiBackup.WebApplication;


public static class ApiRateLimiters
{
    public static readonly RateLimiter TagsApi = new TokenBucketRateLimiter(
        new TokenBucketRateLimiterOptions
        {
            TokenLimit = 15,
            TokensPerPeriod = 15,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
            QueueLimit = 1000, // allow waiting
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });

    public static readonly RateLimiter FavoritesApi = new TokenBucketRateLimiter(
        new TokenBucketRateLimiterOptions
        {
            TokenLimit = 14,
            TokensPerPeriod = 14,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
            QueueLimit = 1000, // allow waiting
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
}