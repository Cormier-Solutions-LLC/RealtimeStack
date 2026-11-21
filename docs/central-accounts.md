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
| `Registration__RequireGod` | `true` for owner-managed installation enrollment; default `false` for OSS |

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
`god` in the signed ID token, plus standard `acr` equal to
`urn:cormier:access:credential:<64 lowercase hexadecimal revision>`.
The service requests `openid profile email licensing`
and the LicensingServer resource at authorization and token exchange. Its JWT
access token is retained encrypted in Redis, never returned to the browser.
Account identity is issuer plus subject; email does not link or promote accounts.
Managed production values set `managedInstallation.registration.requireGod=true`.
Only the signed Boolean central God can create or replace that installation's
protected receipt. Ordinary registered users can still authenticate as guests;
this operator gate adds no paid tier or connection restriction. The default
remains false for independently operated OSS installations.
Callbacks require the original signed credential revision to equal freshly
retrieved Access account state. The protected session retains both that `acr`
and its matching stamp, together with the token-validated `iss` claim. The OIDC
claim filters preserve both standard claims. A failed callback returns 401 and
issues no account cookie or success redirect. Password/email/verification/lock changes invalidate it;
an old token cannot acquire a newly fetched stamp. Cookie validation refreshes
central name/email and enforces a 30-second cache with a maximum 120-second
transport outage. Disabled/missing accounts or malformed authority fail closed.
The account page links to Access `/account`; only signed God receives
`/admin/accounts`. No account credentials or profiles are edited locally.

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
Current account authority is separate: required readiness also needs the
registration owner's enabled/verified state checked within 120 seconds. A
restarted Gateway retains permanent proof but waits for fresh owner authority
before readiness. A locked owner never erases the free registration receipt.
Missing, invalid, paid or unregistered responses cannot create a registration.
They do not erase an already verified permanent registration. Changing the
service token, endpoint or installation ID requires a fresh verified receipt.

This opt-in configuration verifies installation readiness and registered user
provenance; it adds no charge, connection/resource cap or client envelope change. Keep the
existing Kubernetes initialization key, application credentials and ticket gates.
Do not enable it on a serving installation before owner registration and cache
storage are ready. Shared libraries and default standalone gateway use stay free.

When required, every interactive session also needs trusted host-written
`registeredIdentity: { issuer, subject, emailVerified: true, registered: true, credentialStamp }`
in its existing Redis session record. The issuer must exactly match the configured
Access issuer, and subject is its immutable verified account ID. Guest roles and
names do not bypass this requirement. Hosts derive this attestation from verified
central authentication and its original signed ID-token revision; browsers cannot
supply it. A matching email or arbitrary guest label is insufficient.
`RedisSessionRecord` provides
the additive `RegisteredIdentity` init-property without changing its constructor
or client envelopes. Tickets must remain bound to the same live registered
session; a standalone ticket cannot bypass registration. Upgrade host session
provisioners before enabling the managed requirement.

Gateway uses the same existing dedicated status-service token for
`POST /internal/account-state` with `{subject, credentialStamp}`; that route is
internal only and returns `{subject, authorized, checkedAt}` without profiles or
tokens. Gateway holds no OIDC secret. Tickets retain identical issuer/sub/stamp
provenance and cannot reuse a newly authenticated session's revision. Existing
connections check on commands, deliveries and heartbeats of at most 30 seconds.
Disabled accounts, changed revisions or authority beyond 120 seconds close with
policy code 1008. The HTTP fallback also rejects changed provenance. Applications
whose adapters remain pending do not provision trusted sessions merely by
carrying this metadata.

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
real Redis persistence/encryption and signed OIDC middleware tests, set `REDIS_TEST_ENDPOINT` to an isolated
Redis endpoint before `dotnet test`; CI already supplies its Redis service.
The tests exercise code flow, S256 PKCE, signed token validation, cross-replica
cookie sessions, credential-revision revocation and rejected callbacks without
cookie creation. They create and delete only their own random key prefixes. Verify full
browser sign-in, registration, restart persistence and HTTPS ingress against the
central issuer before reporting a live integration as complete.

Authentication diagnostics use fixed stage/reason enums and boolean metadata.
They distinguish a login challenge, token validation, ticket acceptance,
completed sign-in, cookie validation and the session response. Redis ticket
events report only store/renew/retrieve/remove outcomes. Claim values, subject
IDs, ticket handles, token properties, cookies, request bodies and exception
messages are excluded. Use these events to locate a failed live roundtrip;
an HTTP redirect or a Ready pod alone does not establish an authenticated session.
The signed middleware suite also exercises independent Production hosts using
one filesystem keyring, central-state validation and owner-only registration,
including ordinary authenticated accounts that cannot replace the owner receipt.
