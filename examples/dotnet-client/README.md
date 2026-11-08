# .NET client example

This .NET 10 example uses the runtime-neutral `RealtimeStack.Client` package to connect, authenticate, subscribe to `topics/orders`, publish one JSON event, receive messages, and stop cooperatively with Ctrl+C. The authenticated identity must be authorized for the `orders` topic.

Supply the gateway endpoint and either a short-lived connection ticket or same-origin-compatible Cookie header through process configuration:

```powershell
$env:REALTIMESTACK_ENDPOINT = 'wss://gateway.example/realtimestack/ws'
$env:REALTIMESTACK_TICKET = '<short-lived-ticket>'
dotnet run --project ./examples/dotnet-client
```

For cookie authentication, supply the browser-equivalent origin alongside the cookie so the gateway can enforce its configured same-origin policy:

```powershell
$env:REALTIMESTACK_ENDPOINT = 'wss://gateway.example/realtimestack/ws'
$env:REALTIMESTACK_COOKIE = '<session-cookie>'
$env:REALTIMESTACK_ORIGIN = 'https://gateway.example'
dotnet run --project ./examples/dotnet-client
```

The environment-backed provider returns the same configured value on every attempt. A `REALTIMESTACK_TICKET` is short-lived and single-use, so this sample mode demonstrates one initial connection and cannot authenticate an automatic reconnect by reusing that ticket. Cookie mode can reconnect while the backing session remains valid. Production applications should implement `IRealtimeStackAuthenticationProvider` against their own secure session or ticket source so every ticket-authenticated reconnect receives fresh material. The example never writes authentication values.

## Saved messages and background tasks

See [durable integration](../../docs/persistence-integrations.md) for session-scoped replay, notifications, task status, and private server-worker writes. Enable and initialize persistence on the gateway using [RealtimeStack.Config](../../docs/configuration.md). The browser examples include optional saved-item and task lookup controls. Persistence does not itself execute import jobs.
