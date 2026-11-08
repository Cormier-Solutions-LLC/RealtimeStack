import { spawnSync } from "node:child_process";

export default function stopPythonFastApiContainer() {
  spawnSync("docker", ["stop", "--timeout", "15", "realtimestack-python-fastapi-playwright"], { stdio: "ignore" });
}
