import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(fileURLToPath(new URL("..", import.meta.url)));
const image = process.argv.find(value => value.startsWith("--docker-image="))?.slice("--docker-image=".length);
const chart = image ? "/chart" : resolve(root, "helm/realtimestack-gateway");
function helm(...args) {
  return execFileSync(image ? "docker" : "helm", image
    ? ["run", "--rm", "--mount", `type=bind,source=${resolve(root, "helm/realtimestack-gateway")},target=/chart,readonly`, image, ...args]
    : args, { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], timeout: 60000 });
}
function render(values = []) {
  return helm("template", "fixture", chart, ...values.flatMap(value => ["--set", value]));
}
const external = ["persistence.enabled=true", "persistence.connectionSecret.name=fixture-database",
  "persistence.egress.cidrs[0]=192.0.2.0/24", "persistence.egress.ports[0]=5432"];
const disabled = render();
assert(!disabled.includes("name: ConnectionStrings__RealtimeStackPersistence"));
assert(!disabled.includes("kind: Job"));
for (const provider of ["PostgreSql", "MySql", "MariaDb", "SqlServer", "AzureSql", "Oracle", "Sqlite", "Db2", "CockroachDb", "Snowflake"]) {
  const sqlite = provider === "Sqlite" ? ["topology=non-ha", "replicaCount=1", "autoscaling.enabled=false",
    "podDisruptionBudget.enabled=false", "autoscaling.minReplicas=1", "autoscaling.maxReplicas=1", "topologySpreadConstraints.enabled=false", "persistence.storage.existingClaim=fixture-data"] : [];
  const output = render([...external, `persistence.provider=${provider}`, "persistence.customProviderImage=true", ...sqlite]);
  assert(output.includes(`RealtimeStackSetup__Persistence__Provider: "${provider}"`));
  assert(output.includes("name: ConnectionStrings__RealtimeStackPersistence"));
  assert(output.includes("secretKeyRef: { name: fixture-database"));
  if (provider === "Sqlite") {
    assert(output.includes("type: Recreate"));
    assert(output.includes('claimName: "fixture-data"'));
  }
}
const setup = render([...external, "persistence.setupJob.enabled=true", "persistence.api.enabled=true",
  "persistence.api.workerTokenSecret.name=fixture-worker", "ingressRoute.enabled=true",
  "ingressRoute.host=gateway.example.test", "ingressRoute.tlsSecretName=fixture-tls"]);
assert(setup.includes('args: ["--realtimestack-setup"]'));
assert(setup.includes('app.kubernetes.io/instance: "fixture-setup"'));
assert(setup.includes("name: fixture-setup"));
assert(setup.includes("activeDeadlineSeconds: 300"));
const ingress = setup.split("---").find(document => document.includes("kind: IngressRoute"));
assert(ingress.includes("/realtimestack/persistence/tasks/read"));
assert(!ingress.includes("/worker/"));
assert(!ingress.includes("PathPrefix(`/realtimestack/persistence"));
assert.throws(() => render(["persistence.enabled=true"]));
assert.throws(() => render([...external, "persistence.provider=Sqlite"]));
assert.throws(() => render([...external, "persistence.provider=SqlServer"]));
assert.throws(() => render(["persistence.enabled=true", "persistence.connectionSecret.name=fixture-database"]));
console.log("Verified all ten persistence providers, disabled defaults, Secret references, setup isolation, ingress, egress and SQLite guards.");
