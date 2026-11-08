# RealtimeStack.Contracts

Runtime-neutral, versioned wire contracts shared by the RealtimeStack gateway and clients. The package targets .NET Standard 2.0 and .NET 10, includes source-generated JSON metadata, and preserves camel-case compatibility with the TypeScript SDK and language-neutral fixtures.

Install from your approved NuGet source with `dotnet add package RealtimeStack.Contracts --version 2.0.2-beta`. The .NET Standard target depends on System.Text.Json `[10.0.12,11.0.0)`; the .NET 10 target uses its framework implementation. No ASP.NET Core or Redis runtime is required by this package.

Use `RealtimeStackJsonSerializerContext` for source-generated, camel-case serialization and `ProtocolValidator` for envelope validation. Serialization does not itself validate or authorize a message:

```csharp
using System.Text.Json;
using RealtimeStack.Contracts;

using var payload = JsonDocument.Parse("{\"orderId\":\"example-order\"}");
var now = DateTimeOffset.UtcNow;
var message = new MessageEnvelope(
    ProtocolVersions.Current,
    ProtocolMessageTypes.Publish,
    Guid.NewGuid().ToString("N"),
    now,
    RealtimeStackRoute.ForTopic("orders").Value,
    payload.RootElement.Clone());
var validation = ProtocolValidator.Validate(message, now);
if (!validation.IsValid)
    throw new InvalidOperationException(validation.ErrorCode);
var json = JsonSerializer.Serialize(
    message, RealtimeStackJsonSerializerContext.Default.MessageEnvelope);
```

`MessageEnvelope` represents client ping/subscribe/unsubscribe/publish commands. `ServerMessageEnvelope` represents ack/event/error/ping/service.restart responses with optional payload, structured error and reconnect advice. The package also defines session, identity, single-use-ticket, bus, durable-stream and health records plus stable protocol error/close-code constants. These are data contracts, not implementations of storage, transport or authentication.

`ProtocolVersions.Current` is exactly `1.0`. Client validation requires a supported type, nonblank correlation ID of at most 128 characters, a nonblank route of at most 256 characters, a timestamp between five minutes before and one minute after the supplied clock, and a non-null publish payload. Server validation checks its supported type, correlation/route/timestamp fields, structured errors and reconnect bounds. It does not apply the client timestamp window to server envelopes. Malformed JSON can fail deserialization before validation.

`RealtimeStackRoute.ForTopic` creates `topics/<topic>`; `ForGroup` is an alias for that same route shape, not a separate group namespace. `ForUserTopic` creates `users/<userId>/topics/<topic>`. `TryParse` checks those route shapes and segment limits; `ProtocolValidator` alone only checks the envelope route's presence/length. Tenant and user authorization, duplicate correlations, frame/message limits and session expiry are enforced by the hosting/client layers, not by this contract validator.

Generated metadata covers the declared contract types and keeps the gateway's serialization path suitable for Native AOT. Custom application payload serialization must supply its own appropriate metadata; this package does not make arbitrary reflection-based application serialization AOT-safe. Retain or clone a `JsonElement` before disposing its owning `JsonDocument`.

Additive optional fields under version `1.0` can be ignored by consumers; other version strings are rejected. Coordinate incompatible contract changes with gateway/client versions and the language-neutral fixtures. Repository verification includes `ProtocolContractTests`, `ProtocolSecurityTests`, TypeScript validation and `protocol/fixtures/v1/envelopes.json`. See the [protocol guide](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/protocol.md) and [package compatibility policy](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/package-release.md).

## Upgrade to RealtimeStack 2

Update `RealtimeStack.Contracts` to `2.0.2-beta` together with the other RealtimeStack packages. NuGet restores the matching transitive dependencies automatically; it does not rewrite application code or configuration. Replace `Realtime.Contracts` (or its older `Cormier.`-prefixed equivalent) namespace/assembly references with `RealtimeStack.Contracts`. Rename product-prefixed types and registration calls from `Realtime...` to `RealtimeStack...`, then rebuild.

Browser consumers must use `@realtimestack/client`, `RealtimeStack.RealtimeStackClient`, and `/_content/RealtimeStack.Browser/realtimestack*.js`. Update configuration sections, routes, cookie options, metrics, scripts, and deployment selectors as described in the [complete upgrade guide](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/upgrading-to-realtimestack-2.md). Preserve configured Redis prefixes when existing state must remain reachable. Previously published package files are immutable; upgrading installs the new revision.
