namespace NhBackup.WebApplication.Infrastructure.Handlers;

public class EndpointRateState
{
    public int Remaining;
    public int Limit;
    public DateTime ResetAt;
}
