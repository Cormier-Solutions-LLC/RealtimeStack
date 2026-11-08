Rails.application.routes.draw do
  root "reference#index"
  get "/fallback.html", to: "reference#fallback"
  get "/failover.html", to: "reference#failover"
  get "/app.css", to: "reference#css"
  get "/app.js", to: "reference#javascript"
  get "/_content/RealtimeStack.Browser/*asset", to: "reference#sdk_asset", format: false
  get "/health", to: "reference#health"
  get "/api/diagnostics", to: "reference#diagnostics"
  post "/api/login", to: "reference#login"
  get "/api/session", to: "reference#session_status"
  post "/api/logout", to: "reference#logout"
  post "/realtimestack/tickets", to: "reference#ticket"
  post "/realtimestack/persistence/records/read", to: "reference#ticket"
  post "/realtimestack/persistence/records/ack", to: "reference#ticket"
  post "/realtimestack/persistence/tasks/read", to: "reference#ticket"
end
