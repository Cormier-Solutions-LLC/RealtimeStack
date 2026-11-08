import * as RealtimeStack from "/_content/RealtimeStack.Browser/realtimestack.js";

globalThis.RealtimeStackClient = RealtimeStack.RealtimeStackClient;
globalThis.RealtimeStack = RealtimeStack;
await import("/app.js");
document.querySelector("#esm-state").textContent = "ready";
