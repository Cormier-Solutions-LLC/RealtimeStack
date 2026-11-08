import { spawnSync } from "node:child_process";

export default function stopSwiftVaporContainer() {
  spawnSync("docker", ["stop", "--timeout", "15", "realtimestack-swift-vapor-playwright"], { stdio: "ignore" });
}
