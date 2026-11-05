import { RealtimeClient } from "/_content/Realtime.Browser/realtime.js";

globalThis.RealtimeClient = RealtimeClient;
globalThis.Realtime = { RealtimeClient };
await import("/app.js");
document.querySelector("#esm-state").textContent = "ready";
