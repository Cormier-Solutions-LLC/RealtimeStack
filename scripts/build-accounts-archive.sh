#!/usr/bin/env bash
set -euo pipefail
: "${GITHUB_SHA:?The tested source commit is required}"
[[ "$GITHUB_SHA" =~ ^[a-f0-9]{40}$ ]]
mkdir -p artifacts
image="realtimestack-accounts-ci:$GITHUB_SHA"
docker build --file deploy/accounts.Dockerfile \
  --label "org.opencontainers.image.revision=$GITHUB_SHA" \
  --label "org.opencontainers.image.source=${GITHUB_SERVER_URL:?}/${GITHUB_REPOSITORY:?}" \
  --tag "$image" .
test "$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image")" = "$GITHUB_SHA"
container="realtimestack-accounts-${GITHUB_RUN_ID:-local}"
cleanup() { docker rm --force "$container" >/dev/null 2>&1 || true; }
trap cleanup EXIT
docker run --detach --name "$container" --read-only --tmpfs /tmp:rw,noexec,nosuid,size=32m \
  --cap-drop ALL --security-opt no-new-privileges:true --publish 127.0.0.1::8080 "$image" >/dev/null
port=$(docker port "$container" 8080/tcp | sed -n 's/^127\.0\.0\.1://p')
test -n "$port"
ready=false
for _ in $(seq 1 30); do
  if curl --fail --silent "http://127.0.0.1:$port/health/ready" >/dev/null; then ready=true; break; fi
  sleep 1
done
test "$ready" = true
test "$(curl --silent --output /dev/null --write-out '%{http_code}' "http://127.0.0.1:$port/")" = 302
curl --fail --silent "http://127.0.0.1:$port/account" >/dev/null
docker save "$image" | gzip > artifacts/realtimestack-accounts.tar.gz
sha256sum artifacts/realtimestack-accounts.tar.gz > artifacts/realtimestack-accounts.tar.gz.sha256
