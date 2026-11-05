# Persistence across integrations

The gateway exposes an optional HTTP interface independent of the WebSocket wire protocol. Node/Express, Spring Boot, Ktor, Axum, Gin, Phoenix, Laravel, Rails, and Vapor reference adapters forward the three browser operations at `/realtime/persistence`. Python/FastAPI does the same. They preserve the public Host, Origin, cookie and configured forwarding scheme. They do not proxy worker-write routes. The ASP.NET and full-circle examples map the same endpoints directly. Helm public ingress follows the same boundary.

Enable the gateway's persistence and API settings, register the selected driver, supply its connection Secret, and initialize the schema as described in [configuration](configuration.md). Reference adapters and the shared UI use the default API path; changing it requires matching proxy/client route configuration. A disabled API returns not found. The shared browser examples offer saved-item reads, acknowledgment of displayed items, and task lookup by ID.

## Browser and TypeScript

```javascript
const saved = new Realtime.RealtimePersistenceClient();
const records = await saved.read("inbox", "notification", lastProcessedSequence, 100);
// Process and deduplicate records, then acknowledge only successfully processed data.
if (records.length) await saved.acknowledge("inbox", "notification", records.at(-1).sequence);
const task = await saved.getTaskStatus(taskId); // null means no task found for the signed-in owner
```

The ESM/TypeScript export has the same class name. Browser operations use same-origin session credentials, prohibit redirects, and never accept tenant/owner IDs or worker credentials. Sequences exposed over this HTTP interface must be positive JavaScript-safe integers, up to 9007199254740991. Page cursors may be zero. Store APIs used directly from .NET retain Int64 sequences. Direct writers whose records will be served through HTTP must also respect the HTTP byte and safe-integer bounds.

## Portable .NET client

`RealtimeStack.Client` includes `RealtimePersistenceClient`, `RealtimeInboxKind`, `RealtimeSavedRecord`, and `RealtimeTaskStatus`, independently of ASP.NET or the database plugins. Supply a host-owned `HttpClient` with the correct BaseAddress, cookies, Origin and TLS policy; disable automatic redirects for credential-bearing traffic.

```csharp
var saved = new Realtime.Client.RealtimePersistenceClient(http);
var notifications = await saved.ReadAsync("inbox", RealtimeInboxKind.Notification, cancellationToken: token);
var status = await saved.GetTaskStatusAsync(taskId, token);
```

The .NET console example also accepts `--task-status TASK_ID`, using `REALTIME_ENDPOINT`, `REALTIME_ORIGIN` and `REALTIME_COOKIE`. Ticket-only authentication cannot read persistent data: task and inbox reads require the existing session. Do not log cookie values.

## HTTP contract for every server language

All endpoints use POST with JSON and `Cache-Control: no-store`. Reader authentication uses the gateway's same-origin session validation and configured realtime authorization policy. Identity always comes from that session; extra tenant/owner fields in reader JSON do not select another account.

| Suffix under the configured API path | JSON body | Authentication |
| --- | --- | --- |
| `/records/read` | `{"streamId":"inbox","kind":1,"afterSequence":0,"limit":100}` | User session and matching Origin |
| `/records/ack` | `{"streamId":"inbox","kind":1,"throughSequence":10}` | User session and matching Origin |
| `/tasks/read` | `{"taskId":"import-1"}` | User session and matching Origin |
| `/worker/append` | tenantId, ownerId, streamId, kind, record | Private worker Bearer token |
| `/worker/tasks` | tenantId, ownerId, sequence, status | Private worker Bearer token |

Kind 0 means a replay message, 1 a notification. Task status uses the task endpoint, not inbox acknowledgment or arbitrary task payload writes. Read returns an ordered JSON array of `{sequence,payload,createdAt,expiresAt}`. Task lookup returns `{taskId,state,progress,error,finishedAt}` or 404. Task state values are Queued=0, Running=1, Succeeded=2, Failed=3, Cancelled=4. Terminal task statuses require `finishedAt`; Failed additionally requires a safe error description. Dates use ISO 8601. Request bodies and inbox response pages are limited to 64 KiB. A request may specify a limit up to 1000, but the HTTP endpoint fetches at most 100 rows per query and may return a shorter page to fit the byte bound. Continue with the last returned sequence until an empty page, rather than treating a short page as the end of the stream. Worker writes reject records or task snapshots that cannot fit a single bounded response. Cancellation propagates to the store. Invalid input returns 400, missing/invalid identity or worker credentials 401, and database failures 503 without provider details. Configured authorization policies can return 403. Do not interpret an authentication or dependency failure as a completed task.

An import worker in any language can POST this body to the **internal** gateway's `/worker/tasks` with `Authorization: Bearer <secret loaded by the worker>`:

```json
{
  "tenantId": "tenant-a",
  "ownerId": "user-a",
  "sequence": 3,
  "status": {
    "taskId": "import-1",
    "state": 3,
    "progress": "500 rows imported",
    "error": "A row failed validation",
    "finishedAt": "2026-01-01T00:00:00Z"
  }
}
```

For a notification, POST to `/worker/append`:

```json
{
  "tenantId": "tenant-a",
  "ownerId": "user-a",
  "streamId": "inbox",
  "kind": 1,
  "record": {
    "sequence": 10,
    "payload": "Import import-1 finished with a validation error",
    "createdAt": "2026-01-01T00:00:00Z"
  }
}
```

The worker token is a privileged service credential granting writes across owners. Restrict its distribution and network reachability, use TLS across trust boundaries, rotate it through the host's secret configuration and restart the host, and send no browser cookies to worker endpoints. The reference browser proxies and ingress rules intentionally omit these routes. Application servers call the internal Service directly; keep the worker credential out of diagnostics and browser configuration.

Imports must execute in a server-owned worker independent of request/WebSocket cancellation. Persist and rediscover the task ID in the host application. Allocate durable, increasing sequences with one ordered writer per stream/task; handle duplicate/transient/ambiguous writes explicitly. Persist before live delivery. Consumer delivery remains at least once and acknowledgment is shared within a stream. These features do not automatically save every WebSocket publish, schedule imports, claim jobs, recover abandoned workers, or guarantee exactly-once business side effects. The host must integrate those policies and protect its job operations. See [persistence semantics](persistence.md).
