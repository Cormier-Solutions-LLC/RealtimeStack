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
