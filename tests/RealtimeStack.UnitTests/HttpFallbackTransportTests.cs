using RealtimeStack.HttpFallback;

namespace RealtimeStack.UnitTests;

public sealed class HttpFallbackTransportTests
{
    [Fact]
    public async Task SendPublishedAfterCancellationDoesNotWaitForAReceiver()
    {
        await using var transport = new HttpFallbackTransport();
        transport.CancelPendingSend();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.SendAsync("payload"u8.ToArray(), CancellationToken.None).AsTask());
    }
}
