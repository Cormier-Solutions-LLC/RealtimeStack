import Config

config :logger,
  level: :warning,
  handle_otp_reports: false,
  handle_sasl_reports: false

config :logger, :default_handler,
  formatter:
    Logger.Formatter.new(format: "$time [$level] $message $metadata\n", metadata: [:event])

config :phoenix, :json_library, Jason

config :realtimestack_example, RealtimeStackExample.Endpoint,
  adapter: Bandit.PhoenixAdapter,
  server: true,
  render_errors: [formats: [json: RealtimeStackExample.ErrorJSON], layout: false],
  pubsub_server: RealtimeStackExample.PubSub
