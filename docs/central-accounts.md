# Central accounts and free registration

RealtimeStack remains free and open source with registration. Its gateway keeps
its Native AOT build, existing application authentication and wire protocols.
`RealtimeStack.Accounts` is a separate managed ASP.NET Core service for interactive
account login and free installation registration. It creates no paid tier or
connection/resource limits. It does not replace application ticket authentication.

## Configure

All values below are deployment inputs. No production host, Redis credential,
namespace or client secret is compiled into the service.

| Environment variable | Value |
| --- | --- |
| `Sso__Enabled` | `true` to enable central accounts |
| `Sso__Authority` | Exact HTTPS Access issuer origin |
| `Sso__ClientId` | `realtime`, matching the central confidential client |
| `Sso__ClientSecretFile` | Mounted Secret file, at least 32 characters |
| `Sso__PublicUrl` | Exact public HTTPS RealtimeStack origin |
| `Sso__RedisConnectionFile` | Mounted Redis connection Secret file |
| `Sso__RedisEndpoint` | Alternative endpoint(s), with credentials supplied separately |
| `Sso__RedisUser` | Existing scoped Redis ACL user |
| `Sso__RedisPasswordFile` | Mounted existing Redis password Secret key |
| `Sso__RedisSentinelServiceName` | Optional existing Sentinel service name |
| `Sso__RedisSentinelPasswordFile` | Mounted existing Sentinel password Secret key |
| `Sso__RedisPrefix` | Dedicated prefix ending in `:`, for example `realtime-accounts:production:` |
| `Sso__DataProtectionPath` | Persistent, shared key directory writable by UID 1654 |
| `Sso__ApplicationName` | Stable shared key discriminator; defaults to `RealtimeStack.Accounts` |
| `Sso__KnownProxyNetworks__0` | Actual trusted ingress source CIDR; add numbered entries as needed |
| `Sso__KnownProxies__0` | Alternatively, the exact trusted ingress proxy IP |
| `Licensing__Origin` | Exact HTTPS LicensingServer origin |
| `ProductConfiguration__Url` | HTTPS `/v1/products/realtime/config` endpoint |
| `ProductConfiguration__PollSeconds` | 30–600; default 60 |
| `Registration__InstallationId` | Stable installation ID, 8–128 characters; retain across upgrades |
| `Registration__DeploymentType` | `kubernetes`, `docker`, `linux` or `windows` |
| `Registration__ApplicationVersion` | Deployed gateway version |
| `Registration__ServiceTokenFile` | Optional separate mounted token for internal status polling |

Direct secret-valued configuration alternatives (`Sso__ClientSecret`,
`Sso__RedisConnection`, `Registration__ServiceToken`) are supported for secret
injection. Never put those values in a ConfigMap, command line or image. Reuse the
existing Redis connection Secret through a mounted file, with a dedicated prefix;
prefer a restricted Redis ACL when available. Do not copy credentials to source.

Persist Data Protection keys on a dedicated volume shared by account replicas.
An externally provisioned keyring may be mounted read-only when its rotation and
key lifetime are managed externally. A new empty read-only directory cannot work.
The same application discriminator/keyring must survive pod replacement so both
opaque session cookies and encrypted registration receipts remain readable.

Register the exact callback `${Sso__PublicUrl}/api/auth/sso/callback` in Access.
This uses authorization code + S256 PKCE and `client_secret_post`. Access must
include `iss`, immutable `sub`, Boolean `email_verified` and optional Boolean
`god` in the signed ID token. The service requests `openid profile email licensing`
and the LicensingServer resource at authorization and token exchange. Its JWT
access token is retained encrypted in Redis, never returned to the browser.
Account identity is issuer plus subject; email does not link or promote accounts.

## Build and route

```powershell
dotnet test tests/RealtimeStack.Accounts.Tests -c Release
docker build -f deploy/accounts.Dockerfile -t YOUR_REGISTRY/realtime-accounts:SOURCE_COMMIT .
docker push YOUR_REGISTRY/realtime-accounts:SOURCE_COMMIT
```

Deploy the resolved immutable digest separately from the AOT gateway. Use a
readiness-gated rolling update with zero unavailable replicas; retain the existing
gateway deployment, Redis and first-initialization Kubernetes code gates.
Route these exact paths under the gateway's public HTTPS origin to the accounts
service on port 8080:

- `/` (launch and post-logout landing; redirects to `/account`)
- `/account`
- `/api/auth/sso/` and all its children
- `/api/product/` and all its children

The Helm chart can manage both hosts and their routes together. See
[managed release configuration](managed-release.md) for protected values, existing
keyring requirements and the paired immutable-image promotion/rollback process.

Keep gateway/WebSocket/application protocol routes on the gateway. Forward HTTPS
scheme only from explicitly configured trusted ingress networks/proxies. Apply
Traefik/cert-manager HTTPS as for other applications. Never log callback query
strings or Authorization headers. Probe `/health/live` and `/health/ready` on the
accounts service directly; readiness requires Redis connectivity when enabled.

The page `/account` signs in, shows installation registration and explicitly
registers through `POST /api/product/registration`, protected by account cookies
and antiforgery tokens. Account logout ends the local account session. This is
not global SSO logout. Central registration is idempotent for product, subject and
installation ID and remains free forever with no expiration. A receipt is checked
for matching subject/installation, zero USD price and no expiration before storage.

`GET /api/product/config` returns the last validated central catalog envelope;
an initial configured-but-unavailable catalog returns 503. Refresh failure keeps
the last valid snapshot. Versions cannot move backward and a revision cannot be
reused for different content. A same-version new revision is supported for live
pricing updates. RealtimeStack rejects any nonzero or non-USD registration price.

Optional `GET /internal/product/registration` requires the separately configured
Bearer service token and exposes only installation registration and zero cost.
Restrict it to namespace-local network access; do not publish it through ingress.
Registration status does not change gateway resource or connection limits.

## Require registration for a managed installation

The gateway defaults to `Registration__Required=false`. After the operator has
signed in and registered the installation on `/account`, an explicit cutover can
enable these gateway settings:

| Environment variable | Value |
| --- | --- |
| `Registration__Required` | `true` only for installations requiring registration |
| `Registration__AccountIssuer` | Exact HTTPS central Access issuer for registered user provenance |
| `Registration__StatusUrl` | Exact internal accounts `/internal/product/registration` endpoint |
| `Registration__InstallationId` | Same stable installation ID as the accounts service |
| `Registration__ServiceTokenFile` | Mounted dedicated internal status token, also configured on accounts |
| `Registration__CacheFile` | Absolute path on persistent storage, writable by gateway UID 1654 |
| `Registration__PollSeconds` | 30–600; default 60 |
| `Registration__AllowInsecureHttp` | Explicit `true` only for a namespace-isolated HTTP service; HTTPS is the default |

Before its first verified free receipt, an explicitly required installation
returns readiness 503 with `setup-required`. A confirmed zero-cost registration
makes that readiness check pass. The gateway authenticates the permanent receipt
cache with HMAC using the dedicated service token, bound to endpoint and
installation ID, and writes it atomically with private file permissions.
The last confirmed permanent free registration remains valid during sidecar or
central outages, including gateway replacement when the cache volume is retained.
Missing, invalid, paid or unregistered responses cannot create a registration.
They do not erase an already verified permanent registration. Changing the
service token, endpoint or installation ID requires a fresh verified receipt.

This opt-in configuration verifies installation readiness and registered user
provenance; it adds no charge, connection/resource cap or client envelope change. Keep the
existing Kubernetes initialization key, application credentials and ticket gates.
Do not enable it on a serving installation before owner registration and cache
storage are ready. Shared libraries and default standalone gateway use stay free.

When required, every interactive session also needs trusted host-written
`registeredIdentity: { issuer, subject, emailVerified: true, registered: true }`
in its existing Redis session record. The issuer must exactly match the configured
Access issuer, and subject is its immutable verified account ID. Guest roles and
names do not bypass this requirement. Hosts derive this attestation from verified
central authentication; browsers cannot supply it. `RedisSessionRecord` provides
the additive `RegisteredIdentity` init-property without changing its constructor
or client envelopes. Tickets must remain bound to the same live registered
session; a standalone ticket cannot bypass registration. Upgrade host session
provisioners before enabling the managed requirement.

## Authenticated host inventory

Both managed components report their relevant host data at startup and every
60 seconds to `/v1/installations/check-in`. Mount the product's separate
`licensing-checkin` Secret key `agent-token` and set
`CORMIER_LICENSE_AGENT_TOKEN_FILE`. Set stable `CORMIER_LICENSE_INSTALLATION_ID`
and distinct `CORMIER_LICENSE_INSTANCE_ID` values, for example component prefix
plus pod UID. Populate `POD_NAMESPACE`, `POD_NAME`, `POD_UID`, `NODE_NAME` from the
downward API and supply the HTTPS `CORMIER_LICENSE_SERVER_URL`.

The agent credential is bound centrally to product and installation; SSO, lease
and administrator credentials cannot substitute. Payloads contain only component
version, deployment type, hostname, OS/version, architecture, CPU/memory and
host/container scope plus Kubernetes identity. The server timestamps receipt.
Failures are reported as failed/stale while preserving existing valid work.
Gateway detailed health checks include check-in status, and fixed warning logs
report failure without credentials or request bodies.

## Validation

The test project covers identity binding, signed claim types, code/PKCE settings,
free-only catalog/receipt validation and account/installation swaps. To run the
real Redis persistence/encryption test, set `REDIS_TEST_ENDPOINT` to an isolated
Redis endpoint before `dotnet test`; CI already supplies its Redis service.
The test creates and deletes only its own random key prefix. Verify full
browser sign-in, registration, restart persistence and HTTPS ingress against the
central issuer before reporting a live integration as complete.
