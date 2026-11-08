using System.Text.Json;
using RealtimeStack.Client;
using RealtimeStack.Contracts;

var configuredEndpoint = Environment.GetEnvironmentVariable("REALTIMESTACK_ENDPOINT");
if (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var endpoint)
    || endpoint.Scheme is not ("ws" or "wss"))
{
    Console.WriteLine("Set REALTIMESTACK_ENDPOINT to a ws:// or wss:// gateway endpoint.");
    return;
}

if (args.Length == 2 && args[0] == "--task-status")
{
    var httpEndpoint = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "wss" ? "https" : "http", Path = "/", Query = "" }.Uri;
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = httpEndpoint };
    http.DefaultRequestHeaders.Add("Origin", Environment.GetEnvironmentVariable("REALTIMESTACK_ORIGIN"));
    http.DefaultRequestHeaders.Add("Cookie", Environment.GetEnvironmentVariable("REALTIMESTACK_COOKIE"));
    var saved = await new RealtimeStackPersistenceClient(http).GetTaskStatusAsync(args[1]);
    Console.WriteLine(saved is null ? "No task found for this account." : $"Task {saved.TaskId}: state={saved.State}; progress={saved.Progress}; error={saved.Error}; finished={saved.FinishedAt}");
    return;
}

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

using var client = new RealtimeStackClient(
    new RealtimeStackClientOptions { Endpoint = endpoint },
    new EnvironmentAuthenticationProvider());
try
{
    await client.ConnectAsync(stopping.Token);

    var orders = RealtimeStackRoute.ForTopic("orders");
    await client.SubscribeAsync(orders, cancellationToken: stopping.Token);
    await client.PublishAsync(
        orders,
        JsonSerializer.SerializeToElement(new { source = "dotnet-client-example" }),
        cancellationToken: stopping.Token);

    while (!stopping.IsCancellationRequested)
    {
        var message = await client.ReceiveAsync(stopping.Token);
        Console.WriteLine($"Received {message.Type} for {message.Route}.");
    }
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
    // Ctrl+C requests a cooperative, successful shutdown.
}

internal sealed class EnvironmentAuthenticationProvider : IRealtimeStackAuthenticationProvider
{
    public Task<RealtimeStackAuthenticationMaterial> GetAuthenticationAsync(CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var origin = Environment.GetEnvironmentVariable("REALTIMESTACK_ORIGIN");
        if (!string.IsNullOrWhiteSpace(origin))
        {
            headers["Origin"] = origin;
        }
        return Task.FromResult(new RealtimeStackAuthenticationMaterial(
            connectionTicket: Environment.GetEnvironmentVariable("REALTIMESTACK_TICKET"),
            cookieHeader: Environment.GetEnvironmentVariable("REALTIMESTACK_COOKIE"),
            headers: headers));
    }
}
