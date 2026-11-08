ARG RUBY_IMAGE=ruby:4.0.6-alpine3.24@sha256:6ec7aeb4f3f0b67a21fb73a83fc20a4055696e64b933229f68ec805a3de38fcc
ARG GO_IMAGE=golang:1.27.3-alpine@sha256:d200d2dd76b03a99b52651fd1325aff7dcd35422c35c01bac56571b0507f3deb
FROM ${RUBY_IMAGE} AS dependencies
RUN apk add --no-cache build-base yaml-dev
RUN gem install bundler -v 4.0.20 --no-document
WORKDIR /source
COPY examples/ruby-rails/Gemfile examples/ruby-rails/Gemfile.lock ./
RUN bundle _4.0.20_ config set deployment true \
    && bundle _4.0.20_ config set without development:test \
    && bundle _4.0.20_ config set path /bundle \
    && bundle _4.0.20_ install --jobs 4 --retry 3

FROM ${GO_IMAGE} AS caddy
WORKDIR /source
RUN go mod init caddy-build \
    && go get github.com/caddyserver/caddy/v2/cmd/caddy@v2.11.9 \
    && go get github.com/go-jose/go-jose/v3@0e502b87c7aa651155b11ae8e195ec33ce10230a github.com/go-jose/go-jose/v4@v4.1.8 golang.org/x/net@v0.62.0 \
    && CGO_ENABLED=0 go build -o /out/caddy github.com/caddyserver/caddy/v2/cmd/caddy
FROM ${RUBY_IMAGE}
RUN apk upgrade --no-cache && apk add --no-cache tini=0.19.0-r3 tzdata yaml libstdc++
RUN rm -f /usr/local/lib/ruby/gems/4.0.0/specifications/default/json-2.18.0.gemspec \
    /usr/local/lib/ruby/gems/4.0.0/specifications/default/resolv-0.7.0.gemspec
COPY --from=caddy /out/caddy /usr/local/bin/caddy
COPY --from=dependencies /usr/local/bundle /usr/local/bundle
COPY --from=dependencies /bundle /bundle
WORKDIR /app
COPY examples/ruby-rails/app ./app
COPY examples/ruby-rails/bin ./bin
COPY examples/ruby-rails/config ./config
COPY examples/ruby-rails/lib ./lib
COPY examples/ruby-rails/Gemfile examples/ruby-rails/Gemfile.lock examples/ruby-rails/Rakefile examples/ruby-rails/config.ru ./
COPY examples/ruby-rails/Caddyfile /etc/caddy/Caddyfile
COPY examples/ruby-rails/start.sh ./start.sh
COPY examples/shared-web/dist ./shared-web
COPY sdk/typescript/dist ./sdk
RUN mkdir -p /data/caddy /config/caddy tmp/pids \
    && chown -R 65532:65532 /app /data /config /tmp \
    && chmod 0555 /app/start.sh
ENV BUNDLE_DEPLOYMENT=true
ENV BUNDLE_WITHOUT=development:test
ENV BUNDLE_PATH=/bundle
ENV RAILS_ENV=production
ENV SHARED_ASSET_ROOT=/app/shared-web/optimized
ENV SDK_ASSET_ROOT=/app/sdk
USER 65532:65532
ENTRYPOINT ["/sbin/tini", "--", "/app/start.sh"]
