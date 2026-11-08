namespace RealtimeStack.HttpFallback;

public sealed class HttpFallbackOptions
{
    public const string SectionName = "HttpFallback";

    public string BasePath { get; set; } = "/realtimestack/http";

    public int ConnectionTimeoutSeconds { get; set; } = 30;

    public int PollTimeoutSeconds { get; set; } = 10;
}
