package main

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"testing"
)

func TestPublicGatewayRoutes(t *testing.T) {
	tests := []struct {
		method  string
		path    string
		allowed bool
	}{
		{http.MethodPost, "/realtimestack/http/connect", true},
		{http.MethodGet, "/realtimestack/http/connections/connection-a/stream", true},
		{http.MethodPost, "/realtimestack/http/connections/connection-a/messages", true},
		{http.MethodPost, "/realtimestack/http/connections/connection-a/poll", true},
		{http.MethodDelete, "/realtimestack/http/connections/connection-a", true},
		{http.MethodPost, "/realtimestack/persistence/records/read", true},
		{http.MethodPost, "/realtimestack/persistence/records/ack", true},
		{http.MethodPost, "/realtimestack/persistence/tasks/read", true},
		{http.MethodGet, "/realtimestack/http/connect", false},
		{http.MethodPost, "/realtimestack/http/connections/connection-a/stream", false},
		{http.MethodPost, "/realtimestack/http/connections/connection-a/other", false},
		{http.MethodPost, "/realtimestack/persistence/worker/append", false},
	}
	for _, test := range tests {
		if got := isPublicGatewayRoute(test.method, test.path); got != test.allowed {
			t.Errorf("%s %s: allowed = %t, want %t", test.method, test.path, got, test.allowed)
		}
	}
}

func TestFallbackProxyPreservesPublicRequest(t *testing.T) {
	requests := make(chan *http.Request, 1)
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests <- r
		w.WriteHeader(http.StatusAccepted)
	}))
	defer upstream.Close()
	target, err := url.Parse(upstream.URL)
	if err != nil {
		t.Fatal(err)
	}
	app := &App{config: Config{GatewayURL: target, PublicOrigin: "https://reference.example"}, client: upstream.Client()}
	downstream := httptest.NewServer(app.router())
	defer downstream.Close()
	const path = "/realtimestack/http/connections/connection-a/poll?mode=test"
	request, err := http.NewRequest(http.MethodPost, downstream.URL+path, nil)
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("X-RealtimeStack-Connection", "fixture-token")
	request.Header.Set("X-Forwarded-For", "192.0.2.1")
	request.Header.Set("X-Forwarded-Prefix", "/fixture")
	response, err := downstream.Client().Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusAccepted {
		t.Fatalf("expected upstream status, got %d", response.StatusCode)
	}
	got := <-requests
	if got.URL.RequestURI() != path || got.Header.Get("X-RealtimeStack-Connection") != "fixture-token" {
		t.Fatal("proxy changed the public route, query, or connection header")
	}
	if got.Header.Get("X-Forwarded-For") != "" || got.Header.Get("X-Forwarded-Prefix") != "" ||
		got.Header.Get("X-Forwarded-Proto") != "https" {
		t.Fatal("proxy did not replace client forwarding headers with the configured scheme")
	}

	unknown, err := downstream.Client().Post(downstream.URL+"/realtimestack/http/unknown", "application/json", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer unknown.Body.Close()
	if unknown.StatusCode != http.StatusNotFound || len(requests) != 0 {
		t.Fatal("unlisted route was not rejected locally")
	}
}
