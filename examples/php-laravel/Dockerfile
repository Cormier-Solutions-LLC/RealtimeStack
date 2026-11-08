ARG COMPOSER_IMAGE=composer:2.10.3@sha256:d8f6343d3fae98107426bc49163ccad46ef85aabd4a27d80a74401fab4aba332
ARG FRANKENPHP_BUILDER_IMAGE=dunglas/frankenphp:1.13.4-builder-php8.5-alpine@sha256:b3406473a82344fb413660af339a5177148f8594176562ebd8416c69a7af3620
ARG FRANKENPHP_IMAGE=dunglas/frankenphp:1.13.4-php8.5-alpine@sha256:974e767f7694964fdb23ead3172dd0a12cedefbd929fbe0db7f69a680663295d
ARG GO_IMAGE=golang:1.27.3-alpine@sha256:d200d2dd76b03a99b52651fd1325aff7dcd35422c35c01bac56571b0507f3deb

FROM ${GO_IMAGE} AS go-toolchain
FROM ${COMPOSER_IMAGE} AS dependencies
WORKDIR /source
COPY examples/php-laravel/composer.json examples/php-laravel/composer.lock ./
RUN composer install --no-dev --no-interaction --no-progress --prefer-dist --optimize-autoloader --no-scripts

FROM ${FRANKENPHP_BUILDER_IMAGE} AS frankenphp
COPY --from=go-toolchain /usr/local/go /usr/local/go
WORKDIR /go/src/app/caddy
RUN go get github.com/caddyserver/caddy/v2@v2.11.9 \
    github.com/getkin/kin-openapi@v0.149.0 \
    github.com/go-jose/go-jose/v3@0e502b87c7aa651155b11ae8e195ec33ce10230a github.com/go-jose/go-jose/v4@v4.1.8 golang.org/x/net@v0.62.0 \
    && cd frankenphp \
    && GOBIN=/usr/local/bin ../../go.sh install -buildvcs=false -ldflags "-w -s -X 'github.com/caddyserver/caddy/v2.CustomVersion=FrankenPHP v1.13.4 PHP $PHP_VERSION Caddy' -X 'github.com/caddyserver/caddy/v2.CustomBinaryName=frankenphp' -X 'github.com/caddyserver/caddy/v2/modules/caddyhttp.ServerHeader=FrankenPHP Caddy'"

FROM ${FRANKENPHP_IMAGE}
RUN apk upgrade --no-cache && apk add --no-cache tini=0.19.0-r3
COPY --from=frankenphp /usr/local/bin/frankenphp /usr/local/bin/frankenphp
WORKDIR /app
COPY --from=dependencies /source/vendor ./vendor
COPY examples/php-laravel/app ./app
COPY examples/php-laravel/bootstrap ./bootstrap
COPY examples/php-laravel/config ./config
COPY examples/php-laravel/public ./public
COPY examples/php-laravel/routes ./routes
COPY examples/php-laravel/artisan ./artisan
COPY examples/php-laravel/Caddyfile /etc/frankenphp/Caddyfile
COPY examples/php-laravel/start.sh ./start.sh
COPY examples/shared-web/dist ./shared-web
COPY sdk/typescript/dist ./sdk
RUN mkdir -p storage/framework/sessions storage/framework/views storage/framework/cache bootstrap/cache /data/caddy /config/caddy \
    && chown -R 65532:65532 /app /data /config \
    && chmod 0555 /app/start.sh
ENV SHARED_ASSET_ROOT=/app/shared-web/optimized
ENV SDK_ASSET_ROOT=/app/sdk
USER 65532:65532
ENTRYPOINT ["/sbin/tini", "--", "/app/start.sh"]
