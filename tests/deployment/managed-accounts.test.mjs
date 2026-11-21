import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { renderChart } from './helm-render.mjs';
const helm = process.env.HELM_BINARY;
const fixture = JSON.parse(fs.readFileSync(new URL('managed-values.json', import.meta.url)));

const render = values => renderChart(helm, values);
const env = object => Object.fromEntries(object.spec.template.spec.containers[0].env.map(e => [e.name, e.value ?? e.valueFrom]));
const deployment = (objects, name) => objects.find(o => o.kind === 'Deployment' && o.metadata.name === name);
const enabled = { skip: !helm && 'Set HELM_BINARY to run actual managed chart renders.' };

test('OSS render does not add account hosts, registration gates or external storage', enabled, () => {
  const docs = render({ redis: { externalEndpoint: 'redis.example.test:6379', credentialsSecret: { name: 'example-redis-auth' } } });
  assert.equal(docs.filter(o => o.kind === 'Deployment').length, 1);
  assert.equal(docs.some(o => ['Secret', 'PersistentVolumeClaim'].includes(o.kind)), false);
  assert.equal(Object.hasOwn(env(docs.find(o => o.kind === 'Deployment')), 'Registration__Required'), false);
});
test('managed render preserves wire routes and isolates trusted credentials and shared keyring', enabled, () => {
  const docs = render(fixture), gateway = deployment(docs, 'example-gateway'), accounts = deployment(docs, 'example-accounts');
  assert.equal(accounts.spec.replicas, 2); assert.equal(accounts.spec.strategy.rollingUpdate.maxUnavailable, 0);
  assert.notDeepEqual(gateway.spec.selector.matchLabels, accounts.spec.selector.matchLabels);
  const gatewayService = docs.find(o => o.kind === 'Service' && o.metadata.name === 'example-gateway');
  assert.equal(Object.entries(gatewayService.spec.selector).every(([k,v]) => accounts.spec.template.metadata.labels[k] === v), false);
  assert.equal(accounts.spec.template.spec.volumes.find(v => v.name === 'keyring').persistentVolumeClaim.claimName, 'example-accounts-keyring');
  assert.equal(accounts.spec.template.spec.containers[0].volumeMounts.find(v => v.name === 'keyring').mountPath, '/var/lib/realtimestack-accounts');
  assert.equal(docs.some(o => ['Secret', 'PersistentVolumeClaim'].includes(o.kind)), false);
  assert.match(accounts.spec.template.spec.containers[0].image, /@sha256:b{64}$/);
  assert.equal(env(accounts).Sso__ClientSecretFile, '/secrets/sso/client-secret');
  assert.equal(env(accounts).Registration__ServiceTokenFile, '/secrets/registration/token');
  assert.equal(env(accounts).Registration__RequireGod, 'true');
  assert.equal(env(accounts).Sso__RedisSentinelPasswordFile, '/secrets/redis/sentinel-password');
  assert.equal(env(accounts).CORMIER_LICENSE_INSTANCE_ID, 'accounts:$(POD_UID)');
  assert.equal(env(gateway).CORMIER_LICENSE_INSTANCE_ID, 'gateway:$(POD_UID)');
  assert.equal(env(gateway).Registration__Required, 'false');
  const routes = docs.filter(o => o.kind === 'IngressRoute').flatMap(o => o.spec.routes);
  const gatewayRoute=routes.find(r=>r.services[0].name==='example-gateway');
  assert.match(gatewayRoute.match, /Path\(`\/legacy\/ws`\)/); assert.match(gatewayRoute.match, /Path\(`\/legacy\/tickets`\)/);
  const accountRoute = routes.find(r=>r.services[0].name==='example-accounts');
  for (const p of ['Path(`/`)', 'Path(`/account`)', 'PathPrefix(`/api/auth/sso/`)', 'PathPrefix(`/api/product/`)']) assert.ok(accountRoute.match.includes(p));
  assert.equal(routes.some(r => r.match.includes('/internal')), false);
  const config = docs.find(o => o.kind === 'ConfigMap').data;
  assert.equal(config.RealtimeStackSetup__GatewayEnabled, 'true');
  assert.equal(Object.hasOwn(docs.find(o=>o.kind==='ConfigMap').metadata,'RealtimeStackSetup__GatewayEnabled'),false);
  assert.equal(config.RealtimeStack__SessionCookieName, 'legacy_session');
  assert.equal(config.RealtimeStack__WebSocketSubProtocols__0, 'legacy.realtime.v1');
  assert.equal(config.RealtimeStack__WebSocketSubProtocols__1, 'realtimestack.v1');
  const policy = docs.find(o => o.kind === 'NetworkPolicy' && o.metadata.name === 'example-accounts');
  assert.ok(policy.spec.egress.some(r => r.ports.some(p => p.port === 8443)));
  assert.equal(policy.spec.ingress.some(r => r.from.some(f => f.podSelector?.matchLabels?.['app.kubernetes.io/name'] === 'realtimestack-gateway')), false);
});

test('standalone registration keeps the configurable non-God default', enabled, () => {
  const value=structuredClone(fixture);delete value.managedInstallation.registration.requireGod;
  assert.equal(env(deployment(render(value),'example-accounts')).Registration__RequireGod,'false');
});
test('external legacy gateway route stays outside Helm ownership while Accounts gets its own route', enabled, () => {
  const v=structuredClone(fixture);v.ingressRoute.enabled=false;
  const routes=render(v).filter(o=>o.kind==='IngressRoute');
  assert.equal(routes.length,1);assert.equal(routes[0].metadata.name,'example-accounts');
  assert.equal(routes[0].spec.routes[0].services[0].name,'example-accounts');
  assert.equal(routes[0].spec.tls.secretName,'example-tls');
});
test('managed non-ha uses the existing primary without Sentinel and preserves TLS', enabled, () => {
  const v = structuredClone(fixture); v.topology = 'non-ha'; v.replicaCount = 1;
  v.autoscaling = {enabled:false,minReplicas:1,maxReplicas:1}; v.redis.tls = true;
  v.podDisruptionBudget = {enabled:false}; v.topologySpreadConstraints = {enabled:false};
  const accounts = deployment(render(v), 'example-accounts');
  assert.equal(env(accounts).Sso__RedisEndpoint, 'example-redis-master:6379,ssl=true');
  assert.equal(Object.hasOwn(env(accounts),'Sso__RedisSentinelServiceName'), false);
  assert.equal(accounts.spec.template.spec.volumes.find(v=>v.name==='redis-auth').secret.items.length,1);
});
test('opt-in registration uses a dedicated shared token, persistent receipt and exact gateway-only internal access', enabled, () => {
  const value = structuredClone(fixture); value.managedInstallation.registration.required = true;
  const docs = render(value), gateway = deployment(docs, 'example-gateway');
  assert.equal(env(gateway).Registration__Required, 'true');
  assert.equal(env(gateway).Registration__AccountIssuer, 'https://access.example.test');
  assert.equal(env(gateway).Registration__StatusUrl, 'http://example-accounts:80/internal/product/registration');
  assert.equal(gateway.spec.template.spec.volumes.find(v => v.name === 'registration-cache').persistentVolumeClaim.claimName, 'example-registration-receipt');
  const policy = docs.find(o => o.kind === 'NetworkPolicy' && o.metadata.name === 'example-accounts');
  assert.deepEqual(policy.spec.ingress[1].from[0].podSelector.matchLabels, gateway.spec.selector.matchLabels);
});
for (const [label, mutate] of [
  ['ephemeral keyring', v => v.accounts.keyringExistingClaim = ''], ['mutable account image', v => v.accounts.image.digest = 'latest'],
  ['missing SSO credential', v => v.accounts.sso.clientSecret.name = ''], ['unscoped Redis prefix', v => v.accounts.redisPrefix = 'other:accounts:'],
  ['missing check-in credential', v => v.managedInstallation.checkIn.agentTokenSecret.name = ''],
  ['required registration without Accounts', v => {v.accounts.enabled = false; v.managedInstallation.registration.required = true;}],
  ['ephemeral receipt', v => {v.managedInstallation.registration.required = true; v.managedInstallation.registration.cacheExistingClaim = '';}],
  ['overridden account selector', v => v.accounts.podLabels = { 'app.kubernetes.io/name': 'example-gateway' }],
  ['reused SSO status credential', v => v.managedInstallation.registration.statusTokenSecret = v.accounts.sso.clientSecret],
  ['reused installation agent credential', v => v.managedInstallation.checkIn.agentTokenSecret = v.managedInstallation.registration.statusTokenSecret],
  ['unscoped external Redis egress', v => {v.redis.mode='external';v.redis.externalEndpoint='redis.example.test:6379';v.networkPolicy={allowExternalRedisEgress:true,externalRedisCidrs:[]};}],
]) test(`managed render rejects ${label}`, enabled, () => { const v = structuredClone(fixture); mutate(v); assert.throws(() => render(v)); });
