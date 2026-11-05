defmodule RealtimeExample.Endpoint do
  use Phoenix.Endpoint, otp_app: :realtime_example
  plug(RealtimeExample.Web)
end
