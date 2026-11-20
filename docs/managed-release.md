# Managed Accounts release and persistent configuration

The optional Accounts host supplies central SSO and free registration. Gateway
Native AOT, application ticket authentication and existing wire protocols stay
separate. Default chart values leave Accounts, host check-in and required
registration disabled. Enabling Accounts does not enable the registration gate.

## Release evidence

Main CI builds/tests the full solution and publishes the gateway Native AOT
archive. It also builds the managed Accounts Dockerfile, checks its read-only
rootless health/account landing, scans the archive and retains its SHA256.
The publisher loads both tested archives without rebuilding, verifies their OCI
revision labels against the successful main CI commit, then publishes immutable
digests to the configured authenticated registry. Additive schema-3 release
evidence records each archive hash, both image digests, the source commit and CI
run ID. `ACCOUNTS_IMAGE_REPOSITORY` optionally selects the second repository;
otherwise it is `${IMAGE_REPOSITORY}-accounts`. Grant the existing publisher
access to that repository before merging a managed release. No credentials or
deployment values belong in Git.

The Accounts build/smoke/archive step has a 15-minute CI deadline. On the existing
Docker-in-Docker runners it uses builder-host networking, matching the network
that successfully reaches the unchanged HTTPS NuGet feed; the isolated bridge
can stall TLS. `CI_ACCOUNTS_BUILD_NETWORK` overrides this CI setting. Standalone
`build-accounts-archive.sh` keeps Docker's default networking unless
`ACCOUNTS_BUILD_NETWORK` is supplied. Locked restore and certificate verification
remain enabled; no global Docker, DNS or production network setting is changed.

Accounts uses the same mandatory `HIGH,CRITICAL` vulnerability policy as Gateway.
The full report at `artifacts/trivy-accounts.full.sarif` retains every severity;
the separate gate report limits SARIF to the configured threshold and exits
nonzero for those findings. No finding is ignored or relabeled. The November 20
qualification found six MEDIUM GNU libc findings in the official Noble chiseled
runtime (`libc6` 2.39-0ubuntu8.9), with no HIGH/CRITICAL or NuGet findings. Ubuntu's
[security notice](https://ubuntu.com/security/notices/USN-9065-1) provides 8.12,
but the freshly inspected same-flavor Microsoft image still contained 8.9.
Retain that exposure report until the official image is refreshed; a passing
HIGH/CRITICAL gate does not mean the image has no vulnerabilities. Keep the
chiseled distribution and supported framework; do not copy custom libc files or
silently change runtime distributions to clear a report.

The Swift reference container keeps its full layer cache, with a two-minute
import and a five-minute optional export. A failed cache export is ignored so a
successfully loaded image can proceed to its mandatory vulnerability scan and
browser tests. This uses Docker's supported
[cache timeout/error parameters](https://docs.docker.com/build/cache/backends/gha/);
build, scan and test failures remain blocking. The November 20 run had loaded
the image successfully before spending the remaining time exporting cache.

Publication does not deploy. The existing manual `promote.yml` dispatch, protected
GitHub environment and `KUBERNETES_DEPLOY_ENABLED` gate remain. Promotion validates
the successful main CI/publish provenance and resolves both manifests. It overrides
both image coordinates from trusted evidence; protected values cannot replace
them with a mutable tag or unrelated Accounts image. Existing gateway-only release
evidence remains usable with Accounts disabled. A legacy rollback with Accounts
enabled fails instead of silently ignoring its configuration.

## Protected values

Start with [the complete reserved-name example](../tests/deployment/managed-values.json),
then preserve the current installation's entire reviewed values in the protected
environment's `HELM_VALUES`. The new settings are:

| Values | Meaning |
| --- | --- |
| `accounts.enabled` | Enable the separately managed OIDC host; default false |
| `accounts.fullnameOverride` | Stable Accounts Deployment/Service name, if needed |
| `accounts.replicaCount` | Default two; readiness-gated zero-unavailable updates |
| `accounts.ingressRoute.enabled` | Create an independent Accounts-only IngressRoute; defaults true when Accounts is enabled |
| `accounts.keyringExistingClaim` | Dedicated pre-provisioned shared RWX PVC; never ephemeral |
| `accounts.applicationName` | Stable Data Protection discriminator; retain across upgrades |
| `accounts.redisPrefix` | Dedicated child of `redis.instancePrefix`, ending in `:` |
| `accounts.sso.*` | Exact HTTPS authority/public origins, registered client ID, existing Secret name/key and trusted ingress proxy networks/IPs |
| `accounts.egress.*` | Explicit HTTPS namespace/pod selectors and backend ports, or public endpoint CIDRs |
| `managedInstallation.installationId` | Stable 8–128 character identity retained across pod replacement |
| `managedInstallation.licensingOrigin` | Exact HTTPS LicensingServer origin without trailing slash |
| `managedInstallation.checkIn.*` | Opt-in authenticated host reporting; existing dedicated agent-token Secret |
| `managedInstallation.registration.statusTokenSecret` | Dedicated service-token Secret shared only by Accounts and gateway |
| `managedInstallation.registration.required` | Default false; owner registration/host identity attestation must precede enabling |
| `managedInstallation.registration.cacheExistingClaim` | Separate shared existing RWX receipt PVC, required only with the gate enabled |
| `ingressRoute.ticketPath` | Optional exact existing application ticket route |
| `gateway.realtimeOverrides` | Existing scalar limits, session cookie/source and ASP.NET session-key settings |
| `gateway.webSocketSubProtocols` | Explicit accepted legacy/current subprotocols |

Image repository/digest fields are supplied by the trusted promotion evidence.
For an isolated local render, provide both immutable digests as the example does.
Do not put secret values in these settings: the chart references existing Secrets
and does not create them or PVCs. Preflight the existing keyring and receipt claims
for RWX access and UID/GID 1654 write access. Preserve their bytes, keyring path
`/var/lib/realtimestack-accounts/keys`, application discriminator and dedicated
Redis prefix. Changing these can invalidate existing sessions or cached receipts.

The existing Redis release, ACL user, application password key, Sentinel name,
Sentinel admin password key, TLS flag and namespace-scoped client label are reused.
The chart installs no Redis server and changes no Redis credentials. Configure
`accounts.redisPrefix` beneath the existing permitted prefix; do not widen its ACL.
Existing gateway settings must be copied explicitly, including origins, cookie
and ASP.NET session key, numerical limits and legacy protocols. Keep endpoint
paths in `ingressRoute.path`/`ticketPath` and origins in `gateway.allowedOrigins`.

When NetworkPolicy is enabled, configure `licensing.egress*` for gateway check-in
and `accounts.egress.*` for Accounts discovery/registration. Select the actual
ingress controller pod port when service NAT uses a different port. The internal
status service is limited to the same namespace's gateway selectors when the
registration gate is enabled. `/internal/product/registration` is never routed
through public ingress; it also requires its dedicated bearer token.

## Post-activation promotion

1. Complete central Access initialization and validate the exact registered
   `${accounts.sso.publicUrl}/api/auth/sso/callback` and logout origin.
2. Capture the current release values/manifests, immutable image digests, route
   rules, credentials' references and storage claim identities privately. Retain
   the existing Redis and recovery data. Review the complete protected values,
   not just these additions; promotion intentionally does not use `--reuse-values`.
3. Render the full chart with both trusted digests. Compare gateway selectors,
   Redis/persistence/environment inputs, resources, ticket/WebSocket/fallback
   paths, cookie names, protocols, TLS Secret and existing claim names. The
   independent public Accounts IngressRoute adds exact `/`, `/account`,
   `/api/auth/sso/` and `/api/product/` prefixes on the same HTTPS host. Both hosts
   must keep distinct pod selectors. If the existing ticket/WebSocket route is
   owned outside Helm, preserve `ingressRoute.enabled=false` and retain that
   original object unchanged. Configure its same host/TLS inputs for the new
   Accounts route; no ownership adoption or replacement is needed.
4. Provision only missing empty dedicated claims/Secrets through the normal
   operator process, then merge the reviewed source. Wait for successful main CI
   and publication. Dispatch protected promotion for that publish run and commit.
   Helm atomically waits for both hosts; the workflow checks both Deployments.
5. Verify valid public HTTPS, account landing/logout, OIDC callback, central policy
   and both authenticated component check-ins. Keep `registration.required=false`
   until the owner completes free registration and actual ticket/session
   provisioners attest registered identities, including guests.

Managed resources should enter production through this chart. If an earlier
manual Accounts Deployment exists, compare its immutable selectors and Helm
ownership first; do not force-adopt or delete it to bypass a mismatch. An existing
external keyring PVC is referenced rather than adopted, so rollback never prunes
that claim. A gateway-only application still works with all optional settings off.

For rollback, select prior trusted **paired** release evidence and retain the full
reviewed values and dedicated existing claims/Secrets. Do not overwrite Redis,
reset installation identity, delete storage or undo registration data. An old
gateway-only release cannot serve the enabled Accounts integration; select an
appropriate paired release instead. A rendered plan, local image or passing test
is not a live deployment.
