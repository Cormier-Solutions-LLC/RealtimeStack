import * as Realtime from "/_content/Realtime.Browser/realtime.js";

globalThis.RealtimeClient = Realtime.RealtimeClient;
globalThis.Realtime = Realtime;
await import("/app.js");
document.querySelector("#esm-state").textContent = "ready";
