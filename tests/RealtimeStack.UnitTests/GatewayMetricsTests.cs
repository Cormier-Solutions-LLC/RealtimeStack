using System.Diagnostics.Metrics;
using RealtimeStack.Gateway;

namespace RealtimeStack.UnitTests;

public sealed class GatewayMetricsTests
{
    [Fact]
    public void ExporterPreservesBoundedDimensionsAndNeverEmitsCallerValues()
    {
        using var metrics = new GatewayMetrics();
        metrics.RecordAuthentication(false, "attacker-controlled");
        metrics.RecordAuthorizationFailure("tenant/secret/topic");
        metrics.RecordMessage("tenant-123", "user-456");
        metrics.RecordRedisOperation("key-name", false);
        metrics.RecordCloseCode(4008);
        metrics.RecordHandlerDuration("dispatch", TimeSpan.FromMilliseconds(12), "success");

        var rendered = metrics.RenderPrometheus();
        Assert.Contains("method=\"other\"", rendered, StringComparison.Ordinal);
        Assert.Contains("operation=\"other\"", rendered, StringComparison.Ordinal);
        Assert.Contains("direction=\"other\",outcome=\"other\"", rendered, StringComparison.Ordinal);
        Assert.Contains("code=\"4008\"", rendered, StringComparison.Ordinal);
        Assert.Contains("realtimestack_abnormal_websocket_closes_total 1", rendered, StringComparison.Ordinal);
        Assert.Contains("le=\"0.025\"", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker-controlled", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant/secret/topic", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-123", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void HealthRequestsUseBoundedEndpointLabelsInMeterExports()
    {
        using var listener = new MeterListener();
        var endpoints = new List<string>();

        listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "RealtimeStack.Gateway")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "realtimestack_health_requests_total")
            {
                foreach (var tag in tags)
                {
                    if (string.Equals(tag.Key, "endpoint", StringComparison.Ordinal))
                    {
                        endpoints.Add(Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                    }
                }
            }
        });

        listener.Start();
        using var metrics = new GatewayMetrics();
        metrics.RecordHealthRequest("/admin?token=secret");

        Assert.Contains("other", endpoints, StringComparer.Ordinal);
        Assert.DoesNotContain("/admin?token=secret", endpoints, StringComparer.Ordinal);
    }

    [Fact]
    public void QueueAccountingReturnsToZeroAndDistinguishesClosedSocketsFromSaturation()
    {
        using var metrics = new GatewayMetrics();
        metrics.RecordQueueEnqueued();
        metrics.RecordQueueRemoved(1);
        var rendered = metrics.RenderPrometheus();
        Assert.Contains("realtimestack_queue_depth 0", rendered, StringComparison.Ordinal);
        Assert.Contains("realtimestack_queue_dropped_total 0", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AlertRelevantCounterSeriesExistAtZeroBeforeFirstEvent()
    {
        using var metrics = new GatewayMetrics();
        var rendered = metrics.RenderPrometheus();

        Assert.Contains("realtimestack_slow_consumer_disconnects_total 0", rendered, StringComparison.Ordinal);
        Assert.Contains("realtimestack_authentication_total{method=\"session\",outcome=\"failure\"} 0", rendered, StringComparison.Ordinal);
        Assert.Contains("realtimestack_redis_operations_total{operation=\"ticket_consume\",outcome=\"failure\"} 0", rendered, StringComparison.Ordinal);
        Assert.Contains("realtimestack_websocket_closes_total{code=\"4008\"} 0", rendered, StringComparison.Ordinal);
        Assert.Contains("realtimestack_abnormal_websocket_closes_total 0", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionDurationHistogramCoversLongLivedSockets()
    {
        using var metrics = new GatewayMetrics();
        metrics.RecordConnectionOpened();
        metrics.RecordConnectionClosed("client_close", TimeSpan.FromHours(12));

        var rendered = metrics.RenderPrometheus();
        Assert.Contains("realtimestack_connection_duration_seconds_bucket{reason=\"client_close\",le=\"86400\"} 1", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientSuppliedCloseCodesAreBounded()
    {
        using var metrics = new GatewayMetrics();
        metrics.RecordCloseCode(3999);
        metrics.RecordCloseCode(4000);

        var rendered = metrics.RenderPrometheus();
        Assert.Contains("realtimestack_websocket_closes_total{code=\"other\"} 2", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("code=\"3999\"", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("code=\"4000\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconnectMetricRequiresExplicitReconnectContext()
    {
        using var metrics = new GatewayMetrics();

        metrics.RecordAuthentication(true, "ticket");
        metrics.RecordAuthentication(true, "ticket", reconnecting: true);

        var rendered = metrics.RenderPrometheus();
        Assert.Contains("realtimestack_reconnect_authentications_total 1", rendered, StringComparison.Ordinal);
    }
}
