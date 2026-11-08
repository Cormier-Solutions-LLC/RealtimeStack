# RealtimeStack.Browser

This package exposes the generated RealtimeStack browser SDK as ASP.NET Core static web assets.

It targets `net10.0` and references the ASP.NET Core shared framework. Install it in a .NET 10 web host from your configured NuGet source:

```text
dotnet add package RealtimeStack.Browser --version 2.0.2-beta
```

Enable static file serving in the consuming host, as in the repository's clean-consumer check:

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseStaticFiles();
app.Run();
```

Publish the consuming application with its static web assets and verify the asset URL in the deployed environment. This package does not register gateway endpoints, authenticate users, or include the shared reference application's HTML/CSS; add hosting integration separately when required.

After adding the package to an ASP.NET Core application, use the readable or production artifact beneath `_content/RealtimeStack.Browser/`:

```html
<script src="/_content/RealtimeStack.Browser/realtimestack.iife.min.js"></script>
```

ES modules, IIFE bundles, declarations, source maps, and `version.json` are produced deterministically from `sdk/typescript`. The browser package version follows the same compatibility policy as `@realtimestack/client`; protocol compatibility is recorded in `version.json`.

The IIFE exposes `RealtimeStack.RealtimeStackClient` and `RealtimeStack.DiagnosticsClient`. ESM consumers can import named exports from `/_content/RealtimeStack.Browser/realtimestack.min.js`. Readable counterparts omit `.min`; declarations live under `types/`. Use a matching set of JavaScript, maps and metadata from one build. The SDK currently implements envelope version `1.0` and subprotocol `realtimestack.v1`; browser compatibility follows the locked Playwright matrix, with an ES2022 output target.

For repository builds, run `npm --prefix sdk/typescript ci` and `npm --prefix sdk/typescript run build` with Node.js 22+ before packing this project. The Razor project copies existing `sdk/typescript/dist` files; it does not run npm or regenerate them. Generated `wwwroot` content is not an independent source to edit. `scripts/Test-BrowserPackage.ps1` inspects package entries and builds an isolated .NET 10 consumer/static-asset manifest; SDK browser tests verify runtime behavior separately.

Serve over HTTPS outside loopback development. Allow the external script/module and configured connection origin in CSP; no `unsafe-eval` is required. Choose deliberately whether to publish source maps, never put operator tokens in public assets/storage, and redact ticket-bearing handshake URLs at the edge. See the [SDK guide](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/sdk/typescript/README.md) and [package release policy](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/package-release.md).

## Upgrade to RealtimeStack 2

Update `RealtimeStack.Browser` to `2.0.2-beta` together with the other RealtimeStack packages. NuGet restores the matching transitive dependencies automatically; it does not rewrite application code or configuration. Replace `Realtime.Browser` (or its older `Cormier.`-prefixed equivalent) namespace/assembly references with `RealtimeStack.Browser`. Rename product-prefixed types and registration calls from `Realtime...` to `RealtimeStack...`, then rebuild.

Browser consumers must use `@realtimestack/client`, `RealtimeStack.RealtimeStackClient`, and `/_content/RealtimeStack.Browser/realtimestack*.js`. Update configuration sections, routes, cookie options, metrics, scripts, and deployment selectors as described in the [complete upgrade guide](https://github.com/Cormier-Solutions-LLC/RealtimeStack/blob/main/docs/upgrading-to-realtimestack-2.md). Preserve configured Redis prefixes when existing state must remain reachable. Previously published package files are immutable; upgrading installs the new revision.
