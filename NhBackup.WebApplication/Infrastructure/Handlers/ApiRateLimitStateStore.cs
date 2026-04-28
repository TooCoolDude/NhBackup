using NhBackup.WebApplication.Infrastructure.Handlers;
using System.Collections.Concurrent;

public class ApiRateLimitStateStore
{
    public ConcurrentDictionary<string, EndpointRateState> States { get; } = new();
}