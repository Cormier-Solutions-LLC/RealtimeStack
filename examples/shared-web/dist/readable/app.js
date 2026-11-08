/*! RealtimeStack reference assets | MIT */
(() => {
  const output = document.querySelector("#events");
  const state = document.querySelector("#state");
  const transport = document.querySelector("#transport");
  const pageMode = location.pathname.endsWith("/fallback.html") ? "fallback"
    : location.pathname.endsWith("/failover.html") ? "failover" : "websocket";
  const transportControls = document.querySelector("#transport-controls");
  transportControls.hidden = pageMode !== "failover";
  document.querySelector("h1").textContent = pageMode === "fallback"
    ? "RealtimeStack HTTP streaming"
    : pageMode === "failover" ? "RealtimeStack transport failover" : "RealtimeStack full circle";
  const messageModal = document.querySelector("#message-modal");
  const messageRoute = document.querySelector("#message-route");
  const messagePayload = document.querySelector("#message-payload");
  const durable = new RealtimeStack.RealtimeStackPersistenceClient();
  let displayedInbox;
  let durableRequests = new AbortController();
  const durableOutput = document.querySelector("#durable-result");
  const acknowledgeButton = document.querySelector("#durable-ack");
  const durableAction = async (operation) => {
    try { await operation(); }
    catch (error) { if (error.name === "AbortError") return; durableOutput.textContent = error.status === 404 ? "Persistence is unavailable or not enabled for this host." : error.message; }
  };
  document.querySelector("#durable-read").onclick = () => durableAction(async () => {
    displayedInbox = null;
    acknowledgeButton.disabled = true;
    const stream = document.querySelector("#durable-stream").value;
    const kind = document.querySelector("#durable-kind").value;
    const records = await durable.read(stream, kind, 0, 100, durableRequests.signal);
    durableOutput.textContent = JSON.stringify(records, null, 2);
    if (records.length > 0) {
      displayedInbox = { stream, kind, sequence: records.at(-1).sequence };
      acknowledgeButton.disabled = false;
    }
  });
  acknowledgeButton.onclick = () => durableAction(async () => {
    if (!displayedInbox) return;
    await durable.acknowledge(displayedInbox.stream, displayedInbox.kind, displayedInbox.sequence, durableRequests.signal);
    displayedInbox = null;
    acknowledgeButton.disabled = true;
    durableOutput.textContent = "Displayed items acknowledged.";
  });
  document.querySelector("#durable-task-read").onclick = () => durableAction(async () => {
    const status = await durable.getTaskStatus(document.querySelector("#durable-task").value, durableRequests.signal);
    durableOutput.textContent = status ? JSON.stringify(status, null, 2) : "No task found for this account.";
  });
  for (const id of ["login", "logout"]) document.querySelector(`#${id}`).addEventListener("click", () => {
    durableRequests.abort();
    durableRequests = new AbortController();
    displayedInbox = null;
    acknowledgeButton.disabled = true;
    durableOutput.textContent = "";
  }, { capture: true });
  let client;
  let unsubscribe;
  const write = (kind, value) => {
    const safe = typeof value === "string" ? value : JSON.stringify(value);
    output.textContent = `${new Date().toISOString()} ${kind} ${safe}\n${output.textContent}`.slice(0, 12000);
  };
  const showMessage = event => {
    messageRoute.textContent = event.route;
    messagePayload.textContent = JSON.stringify(event.payload ?? null, null, 2);
    if (!messageModal.open) messageModal.showModal();
  };
  const invoke = async (action) => { try { await action(); } catch (error) { write("error", { name: error.name, code: error.code, message: error.message }); } };
  const runtimeConfiguration = fetch("/api/diagnostics").then(r => r.json()).then(value => {
    if (!Number.isSafeInteger(value.heartbeatIntervalMilliseconds) || value.heartbeatIntervalMilliseconds <= 0) {
      throw new Error("The server returned an invalid heartbeat interval.");
    }
    const stack = value.stack ? `Stack: ${value.stack}; ` : "";
    document.querySelector("#diagnostics").textContent = `${stack}Topology: ${value.topology}; instance: ${value.instance}; Redis: ${value.redis}`;
    const links = document.querySelector("#stack-links");
    for (const item of Array.isArray(value.links) ? value.links : []) {
      if (typeof item?.href !== "string" || !item.href.startsWith("/") || typeof item.label !== "string") continue;
      const paragraph = document.createElement("p");
      const anchor = document.createElement("a");
      anchor.href = item.href;
      anchor.textContent = item.label;
      paragraph.append(anchor);
      links.append(paragraph);
    }
    return value;
  });
  void runtimeConfiguration.catch(error => write("diagnostics", error.message));
  document.querySelector("#login").onclick = () => invoke(async () => {
    const response = await fetch("/api/login", { method: "POST", headers: { "content-type": "application/json" },
      body: JSON.stringify({ tenantId: document.querySelector("#tenant").value, userId: document.querySelector("#user").value }) });
    const value = await response.json(); if (!response.ok) throw new Error(value.message); write("session", value);
  });
  document.querySelector("#logout").onclick = () => invoke(async () => {
    const response = await fetch("/api/logout", { method: "POST" });
    if (!response.ok) {
      const value = await response.json().catch(() => ({}));
      throw new Error(value.message || "Logout failed.");
    }
    write("session", "logged out");
  });
  document.querySelector("#connect").onclick = () => invoke(async () => {
    const configuration = await runtimeConfiguration;
    const transportOptions = pageMode === "fallback"
      ? { transports: ["http-streaming"], httpStreaming: { url: "/realtimestack/http" } }
      : pageMode === "failover"
        ? { transports: ["websocket", "http-streaming"], httpStreaming: { url: "/realtimestack/http" },
          ...(document.querySelector("#disable-websocket").checked
            ? { webSocketFactory: () => { throw new Error("WebSockets disabled by the example page."); } } : {}) }
        : {};
    client = new globalThis.RealtimeStack.RealtimeStackClient({ url: "/realtimestack/ws", authentication: { kind: "ticket" },
      ...transportOptions,
      heartbeatIntervalMilliseconds: configuration.heartbeatIntervalMilliseconds,
      reconnect: { initialDelayMilliseconds: 50, maximumDelayMilliseconds: 500, jitterRatio: 0, maximumAttempts: 20 } });
    client.on("state", value => { state.textContent = value; transport.textContent = client.activeTransport ?? "none"; write("state", value); });
    client.on("error", error => write("error", { code: error.code, message: error.message }));
    client.on("close", value => write("close", value)); await client.connect();
    transport.textContent = client.activeTransport ?? "none";
    write("transport", client.activeTransport);
  });
  document.querySelector("#disconnect").onclick = () => invoke(() => client.disconnect());
  document.querySelector("#subscribe").onclick = () => invoke(async () => {
    const route = document.querySelector("#route").value; unsubscribe = await client.subscribe(route, event => { write("event", event); showMessage(event); }); write("subscribed", route);
  });
  document.querySelector("#unsubscribe").onclick = () => invoke(async () => { if (unsubscribe) await unsubscribe(); unsubscribe = undefined; write("unsubscribed", "ok"); });
  document.querySelector("#publish").onclick = () => invoke(async () => {
    const payload = JSON.parse(document.querySelector("#payload").value); await client.publish(document.querySelector("#route").value, payload); write("published", payload);
  });
  window.fullCircle = { get client() { return client; } };
})();
