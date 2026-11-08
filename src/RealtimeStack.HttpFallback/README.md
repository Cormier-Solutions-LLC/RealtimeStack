# RealtimeStack.HttpFallback

Optional HTTP fallback transport for hosts that already use `RealtimeStack.AspNetCore`. It preserves the same protocol envelopes, authentication, route authorization, Redis delivery, queue limits, and reconnect behavior while using bounded long-poll requests for server-to-client messages and HTTP POST for client commands. A streaming endpoint is also available to custom clients.

```csharp
builder.Services.AddRealtimeStackGateway(builder.Configuration);
builder.Services.AddRealtimeStackHttpFallback(builder.Configuration);

app.UseRealtimeStackGateway();
app.MapRealtimeStackGateway();
app.MapRealtimeStackHttpFallback();
```

Configure `HttpFallback:BasePath` explicitly when `/realtimestack/http` is not appropriate. `ConnectionTimeoutSeconds` bounds how long a newly created connection may wait for its first receiver, and `PollTimeoutSeconds` bounds each long-poll request. The browser SDK only uses these endpoints when `transports` includes `http-streaming`; installing this package does not change existing WebSocket clients.

Fallback connection state is held by the gateway process. Multi-replica deployments must keep every request for one fallback connection on the same replica; the supplied Traefik `IngressRoute` configures cookie affinity for this purpose. Its affinity cookie defaults to `SameSite=Lax`; set `ingressRoute.affinityCookieSameSite` to `none` for an intentionally cross-site browser origin (the chart always marks this cookie `Secure`). Configure equivalent connection affinity when using a different ingress or load balancer.

## Upgrade to RealtimeStack 2

Update `RealtimeStack.HttpFallback` to `2.0.2-beta` together with the other RealtimeStack packages. NuGet restores the matching transitive dependencies automatically; it does not rewrite application code or configuration. Replace `Realtime.HttpFallback` (or its older `Cormier.`-prefixed equivalent) namespace/assembly references with `RealtimeStack.HttpFallback`. Rename product-prefixed types and registration calls from `Realtime...` to `RealtimeStack...`, then rebuild.

Browser consumers must use `@realtimestack/client`, `RealtimeStack.RealtimeStackClient`, and `/_content/RealtimeStack.Browser/realtimestack*.js`. Update configuration sections, routes, cookie options, metrics, scripts, and deployment selectors as described in the [complete upgrade guide](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/upgrading-to-realtimestack-2.md). Preserve configured Redis prefixes when existing state must remain reachable. Previously published package files are immutable; upgrading installs the new revision.
