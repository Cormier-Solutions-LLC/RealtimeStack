# Central licensing

The executable gateway registers the shared Cormier product licensing host
service with application entitlement `RealtimeStack.Gateway`. Licensing is
independent of session provisioning, route authorization and Redis credentials.
No existing connection, ticket or persisted message is reset by the migration.

Supply `CORMIER_LICENSE_SERVER_URL` through environment or deployment values,
using the owner's central HTTPS endpoint. Configure trusted Ed25519 public keys
with `CORMIER_LICENSE_PUBLIC_KEYS=keyId=base64PublicKey`. Supply an owner-issued
key using `CORMIER_LICENSE_KEY_FILE` or a Secret-backed `CORMIER_LICENSE_KEY`.
Use `CORMIER_LICENSE_DEPLOYMENT=kubernetes` and HOSTNAME as the pod instance ID.
Production endpoints, key values and Secret names stay in local deployment
configuration. The SDK placeholder address never establishes production trust.

The client validates signed leases, renews in the background, reports CPU/memory
usage and deactivates on shutdown. No key means an idle client, preserving
existing gateway behavior. No local product license records existed to copy.
This integration introduces no paid subscription, connection rejection or new
authorization rule. The central owner decides rates and issues licenses.
Readiness remains based on gateway and persistence health, independent of the
licensing service's availability. Native AOT serialization uses generated
metadata rather than runtime reflection.

For an existing installation, preserve its WebSocket and ticket paths, cookie
name, allowed origins and Redis prefix through the `RealtimeStack` configuration
section before rolling a rebuilt gateway. `RealtimeStack:WebSocketSubProtocols`
is an explicit list of accepted wire protocol tokens; its default remains
`realtimestack.v1`. Configure an older installation's existing token during its
transition rather than requiring every connected client to upgrade at once.
Set `licensing.egressCidrs` to the configured licensing endpoint addresses when
using the chart's NetworkPolicy. No production network identity is baked into
the shared application code or chart defaults.

When HTTPS ingress is translated to an ingress-controller pod port, configure
`licensing.egressNamespaceSelector`, `licensing.egressPodSelector` and
`licensing.egressTargetPorts` for that controller. Keep this exception scoped to
the licensing ingress instead of allowing unrestricted private-network egress.

For installations managed outside this chart, fill in the deployment and ingress
selectors in [the additive HTTPS policy example](../deploy/central-https-egress.example.yaml).
Use the ingress pod's observed target port after Service translation. The example
uses port `8443`; replace it when the controller uses a different port. Keep the
existing ingress, Redis and DNS policy in place. Check both Access discovery and
Licensing readiness over verified HTTPS under the workload's actual policy.
Any temporary probe with the gateway's labels must remain unready so the serving
Service cannot route client traffic to it.
