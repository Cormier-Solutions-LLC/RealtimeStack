ARG SWIFT_BUILD_IMAGE=swift:6.3.3-noble@sha256:dd115b20969a2acf74c8dcf6da7c4aeda086053c27453dad59052a65bafc8d0c
ARG GO_IMAGE=golang:1.27.3-alpine@sha256:d200d2dd76b03a99b52651fd1325aff7dcd35422c35c01bac56571b0507f3deb
ARG SWIFT_RUNTIME_IMAGE=swift:6.3.3-noble-slim@sha256:d20e340aeaf7109919b2a15c95569470070cca4d34dae40dfa58339a2aa734df
FROM ${SWIFT_BUILD_IMAGE} AS build
WORKDIR /source
COPY examples/swift-vapor/Package.swift examples/swift-vapor/Package.resolved ./
RUN --mount=type=cache,id=swift-vapor-build,target=/source/.build \
    swift package resolve --disable-sandbox
COPY examples/swift-vapor/Sources ./Sources
COPY examples/swift-vapor/Tests ./Tests
RUN --mount=type=cache,id=swift-vapor-build,target=/source/.build \
    swift build --configuration release --static-swift-stdlib --disable-sandbox \
    && cp "$(swift build --configuration release --show-bin-path)/RealtimeVaporExample" /server

FROM ${GO_IMAGE} AS caddy
WORKDIR /source
RUN go mod init caddy-build \
    && go get github.com/caddyserver/caddy/v2/cmd/caddy@v2.11.9 \
    && go get github.com/go-jose/go-jose/v3@0e502b87c7aa651155b11ae8e195ec33ce10230a github.com/go-jose/go-jose/v4@v4.1.8 \
    && CGO_ENABLED=0 go build -o /out/caddy github.com/caddyserver/caddy/v2/cmd/caddy
FROM ${SWIFT_RUNTIME_IMAGE}
RUN apt-get update \
    && apt-get upgrade --yes \
    && apt-get install --yes --no-install-recommends curl tini=0.19.0-1 \
    && apt-get purge --yes --allow-remove-essential apt libapt-pkg6.0t64 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=caddy /out/caddy /usr/local/bin/caddy
WORKDIR /app
COPY --from=build /server ./server
COPY examples/swift-vapor/Caddyfile /etc/caddy/Caddyfile
COPY examples/swift-vapor/start.sh ./start.sh
COPY examples/shared-web/dist ./shared-web
COPY sdk/typescript/dist ./sdk
RUN mkdir -p /data/caddy /config/caddy \
    && chown -R 65532:65532 /app /data /config /tmp \
    && chmod 0555 /app/server /app/start.sh
ENV SHARED_ASSET_ROOT=/app/shared-web/optimized
ENV SDK_ASSET_ROOT=/app/sdk
USER 65532:65532
ENTRYPOINT ["/usr/bin/tini", "--", "/app/start.sh"]
