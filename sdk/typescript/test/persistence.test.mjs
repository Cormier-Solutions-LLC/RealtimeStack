import assert from "node:assert/strict";
import test from "node:test";
import { RealtimePersistenceClient, RealtimePersistenceError } from "../dist/cormier-realtime.js";

test("durable reads use session credentials and never send tenant or worker credentials", async () => {
  const calls = [];
  const client = new RealtimePersistenceClient("/realtime/persistence", async (url, options) => {
    calls.push({ url, options });
    return new Response(url.endsWith("ack") ? null : JSON.stringify([{ sequence: 2, payload: "done" }]), { status: url.endsWith("ack") ? 204 : 200 });
  });
  assert.equal((await client.read("inbox", "notification", 1))[0].payload, "done");
  await client.acknowledge("inbox", "notification", 2);
  assert.equal(calls[0].url, "/realtime/persistence/records/read");
  assert.equal(calls[0].options.credentials, "same-origin");
  assert.equal(calls[0].options.redirect, "error");
  assert.deepEqual(JSON.parse(calls[0].options.body), { streamId: "inbox", kind: 1, afterSequence: 1, limit: 100 });
  assert.equal(calls[0].options.headers.Authorization, undefined);
});

test("task lookup distinguishes missing tasks from authentication and dependency failures", async () => {
  const missing = new RealtimePersistenceClient(undefined, async () => new Response(null, { status: 404 }));
  assert.equal(await missing.getTaskStatus("import-1"), null);
  for (const status of [401, 403, 503]) {
    const client = new RealtimePersistenceClient(undefined, async () => new Response(null, { status }));
    await assert.rejects(client.getTaskStatus("import-1"), error => error instanceof RealtimePersistenceError && error.status === status);
  }
});

test("durable client rejects unsafe URLs, cursors, and inbox kinds before requesting", async () => {
  for (const url of ["https://example.com", "//example.com", "/api?token=x", "/api/"]) assert.throws(() => new RealtimePersistenceClient(url));
  const client = new RealtimePersistenceClient(undefined, async () => { throw new Error("Unexpected request"); });
  await assert.rejects(client.read("inbox", "message", Number.MAX_SAFE_INTEGER + 1), RangeError);
  await assert.rejects(client.read("inbox", "task"), TypeError);
  await assert.rejects(client.acknowledge("", "message", 1), TypeError);
});

test("durable client binds the browser fetch implementation to the global object", async () => {
  const original = globalThis.fetch;
  globalThis.fetch = function () {
    assert.equal(this, globalThis);
    return Promise.resolve(new Response("[]"));
  };
  try { assert.deepEqual(await new RealtimePersistenceClient().read("inbox", "message"), []); }
  finally { globalThis.fetch = original; }
});
