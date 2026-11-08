defmodule RealtimeStackExample.Application do
  use Application
  require Logger

  @impl true
  def start(_type, _args) do
    with {:ok, config} <- RealtimeStackExample.Config.load(),
         {:ok, listen_address} <- RealtimeStackExample.Config.listen_address(config.listen_host) do
      Application.put_env(:realtimestack_example, :runtime_config, config)

      Application.put_env(
        :realtimestack_example,
        RealtimeStackExample.Endpoint,
        Keyword.merge(
          Application.get_env(:realtimestack_example, RealtimeStackExample.Endpoint),
          http: [ip: listen_address, port: config.port],
          secret_key_base: :crypto.strong_rand_bytes(64) |> Base.encode64()
        )
      )

      children = [
        {Phoenix.PubSub, name: RealtimeStackExample.PubSub},
        {Redix, {config.redis_url, redis_start_options(config.redis_url)}},
        RealtimeStackExample.Endpoint
      ]

      case Supervisor.start_link(children,
             strategy: :one_for_one,
             name: RealtimeStackExample.Supervisor
           ) do
        {:ok, pid} ->
          Logger.warning("application started", event: "application_started")
          {:ok, pid}

        other ->
          startup_failure(other)
      end
    else
      other -> startup_failure(other)
    end
  end

  @impl true
  def stop(_state), do: Logger.warning("application stopped", event: "application_stopped")

  def redis_start_options(url) do
    options = [name: RealtimeStackExample.Redis, sync_connect: true, timeout: 5_000]
    uri = URI.parse(url)

    if uri.scheme == "rediss" do
      socket_options = [
        verify: :verify_peer,
        cacertfile: CAStore.file_path(),
        customize_hostname_check: [
          match_fun: :public_key.pkix_verify_hostname_match_fun(:https)
        ]
      ]

      socket_options =
        if match?({:error, _}, :inet.parse_address(String.to_charlist(uri.host))) do
          Keyword.put(socket_options, :server_name_indication, String.to_charlist(uri.host))
        else
          socket_options
        end

      Keyword.put(options, :socket_opts, socket_options)
    else
      options
    end
  end

  defp startup_failure(_reason) do
    Logger.error("application startup failed", event: "startup_failed")
    {:error, :startup_failed}
  end
end
