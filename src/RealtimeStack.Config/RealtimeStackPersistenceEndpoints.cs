using System.Data.Common;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RealtimeStack.Gateway;
using RealtimeStack.Plugins.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace RealtimeStack.Config;

public sealed record PersistenceReadRequest(string StreamId, DurableRecordKind Kind, long AfterSequence = 0, int Limit = 100);
public sealed record PersistenceAcknowledgeRequest(string StreamId, DurableRecordKind Kind, long ThroughSequence);
public sealed record TaskReadRequest(string TaskId);
public sealed record PersistenceAppendRequest(string TenantId, string OwnerId, string StreamId, DurableRecordKind Kind, DurableRecord Record);
public sealed record TaskSaveRequest(string TenantId, string OwnerId, long Sequence, DurableTaskStatus Status);

public static class RealtimeStackPersistenceEndpoints
{
    /// <summary>Maps scoped browser reads/acknowledgments and separate service-authenticated worker writes.</summary>
    public static IEndpointRouteBuilder MapRealtimeStackPersistence(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!SetupValue.Boolean(configuration, "RealtimeStackSetup:Persistence:ApiEnabled")) return endpoints;
        var options = endpoints.ServiceProvider.GetRequiredService<RealtimeStackSetupOptions>();
        if (!options.PersistenceEnabled || !options.GatewayEnabled) throw new InvalidOperationException("The persistence API requires gateway and persistence registration.");
        var path = configuration["RealtimeStackSetup:Persistence:ApiBasePath"] ?? "/realtimestack/persistence";
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.EndsWith('/') || path.Contains('\\') || path.Contains('?') || path.Contains('#') || path.Contains('{') || path.Contains('}'))
            throw new InvalidOperationException("Persistence ApiBasePath must be an absolute route prefix without trailing slash, parameters, query, or fragment.");
        var workerToken = configuration["RealtimeStackSetup:Persistence:WorkerToken"];
        if (!string.IsNullOrEmpty(workerToken) && workerToken.Length < 32)
            throw new InvalidOperationException("The persistence WorkerToken must contain at least 32 characters.");
        var tokenHash = string.IsNullOrEmpty(workerToken) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(workerToken));

        var read = endpoints.MapPost(path + "/records/read", (RequestDelegate)(context => ExecuteAsync(context, async () =>
        {
            var identity = await IdentityAsync(context).ConfigureAwait(false);
            if (identity is null) return;
            var request = await ReadAsync(context, PersistenceApiJsonContext.Default.PersistenceReadRequest).ConfigureAwait(false);
            if (request.AfterSequence > 9007199254740991) throw new ArgumentException("Sequence numbers must be safe integers.");
            if (request.Limit is < 1 or > 1000 || request.Kind == DurableRecordKind.TaskStatus) throw new ArgumentException("Invalid inbox page request.");
            var records = await context.RequestServices.GetRequiredService<IRealtimeStackPersistenceStore>().ReadAsync(
                new(identity.TenantId, identity.UserId, request.StreamId, request.Kind), request.AfterSequence, Math.Min(request.Limit, 100), context.RequestAborted).ConfigureAwait(false);
            // The reference proxies enforce bounded responses. Short pages remain cursor-addressable.
            var page = new List<byte[]>();
            var bytes = 2;
            foreach (var record in records)
            {
                if (record.Sequence > 9007199254740991) throw new ArgumentException("Sequence numbers must be safe integers.");
                var encoded = JsonSerializer.SerializeToUtf8Bytes(record, PersistenceApiJsonContext.Default.DurableRecord);
                if (bytes + encoded.Length + (page.Count == 0 ? 0 : 1) > 65536)
                {
                    if (page.Count == 0) throw new BadHttpRequestException("Record too large.", StatusCodes.Status413PayloadTooLarge);
                    break;
                }
                bytes += encoded.Length + (page.Count == 0 ? 0 : 1);
                page.Add(encoded);
            }
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("[", context.RequestAborted).ConfigureAwait(false);
            for (var index = 0; index < page.Count; index++)
            {
                if (index > 0) await context.Response.WriteAsync(",", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.WriteAsync(page[index], context.RequestAborted).ConfigureAwait(false);
            }
            await context.Response.WriteAsync("]", context.RequestAborted).ConfigureAwait(false);
        })));
        var acknowledge = endpoints.MapPost(path + "/records/ack", (RequestDelegate)(context => ExecuteAsync(context, async () =>
        {
            var identity = await IdentityAsync(context).ConfigureAwait(false);
            if (identity is null) return;
            var request = await ReadAsync(context, PersistenceApiJsonContext.Default.PersistenceAcknowledgeRequest).ConfigureAwait(false);
            if (request.Kind == DurableRecordKind.TaskStatus) throw new ArgumentException("Task history cannot be acknowledged through the inbox API.");
            await context.RequestServices.GetRequiredService<IRealtimeStackPersistenceStore>().AcknowledgeAsync(
                new(identity.TenantId, identity.UserId, request.StreamId, request.Kind), request.ThroughSequence, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        })));
        var task = endpoints.MapPost(path + "/tasks/read", (RequestDelegate)(context => ExecuteAsync(context, async () =>
        {
            var identity = await IdentityAsync(context).ConfigureAwait(false);
            if (identity is null) return;
            var request = await ReadAsync(context, PersistenceApiJsonContext.Default.TaskReadRequest).ConfigureAwait(false);
            var status = await context.RequestServices.GetRequiredService<DurableTasks>().GetAsync(identity.TenantId, identity.UserId, request.TaskId, context.RequestAborted).ConfigureAwait(false);
            if (status is null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            await context.Response.WriteAsJsonAsync(status, PersistenceApiJsonContext.Default.DurableTaskStatus, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        })));
        var policy = endpoints.ServiceProvider.GetRequiredService<RealtimeStackOptions>().AuthorizationPolicy;
        if (!string.IsNullOrWhiteSpace(policy)) { read.RequireAuthorization(policy); acknowledge.RequireAuthorization(policy); task.RequireAuthorization(policy); }

        endpoints.MapPost(path + "/worker/append", (RequestDelegate)(context => ExecuteAsync(context, async () =>
        {
            if (!AuthorizeWorker(context, tokenHash)) return;
            var request = await ReadAsync(context, PersistenceApiJsonContext.Default.PersistenceAppendRequest).ConfigureAwait(false);
            if (request.Kind == DurableRecordKind.TaskStatus) throw new ArgumentException("Use the task-status endpoint for validated task snapshots.");
            if (request.Record is null || request.Record.Sequence > 9007199254740991) throw new ArgumentException("Sequence numbers must be safe integers.");
            if (JsonSerializer.SerializeToUtf8Bytes(request.Record, PersistenceApiJsonContext.Default.DurableRecord).Length > 65534)
                throw new BadHttpRequestException("Record too large.", StatusCodes.Status413PayloadTooLarge);
            await context.RequestServices.GetRequiredService<IRealtimeStackPersistenceStore>().AppendAsync(
                new(request.TenantId, request.OwnerId, request.StreamId, request.Kind), request.Record, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        })));
        endpoints.MapPost(path + "/worker/tasks", (RequestDelegate)(context => ExecuteAsync(context, async () =>
        {
            if (!AuthorizeWorker(context, tokenHash)) return;
            var request = await ReadAsync(context, PersistenceApiJsonContext.Default.TaskSaveRequest).ConfigureAwait(false);
            if (request.Sequence > 9007199254740991) throw new ArgumentException("Sequence numbers must be safe integers.");
            if (JsonSerializer.SerializeToUtf8Bytes(request.Status, PersistenceApiJsonContext.Default.DurableTaskStatus).Length > 65536)
                throw new BadHttpRequestException("Task snapshot too large.", StatusCodes.Status413PayloadTooLarge);
            await context.RequestServices.GetRequiredService<DurableTasks>().SaveAsync(request.TenantId, request.OwnerId, request.Sequence, request.Status, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        })));
        return endpoints;
    }

    private static async Task<Contracts.RealtimeStackIdentity?> IdentityAsync(HttpContext context)
    {
        var result = await context.RequestServices.GetRequiredService<RealtimeStackAuthenticator>().AuthenticateSessionAsync(context, context.RequestAborted).ConfigureAwait(false);
        if (result.Identity is null) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return result.Identity;
    }

    private static bool AuthorizeWorker(HttpContext context, byte[]? expected)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (expected is null || authorization.Length > 8192 || !authorization.StartsWith("Bearer ", StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..])), expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return false;
        }
        return true;
    }

    private static async Task<T> ReadAsync<T>(HttpContext context, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = 65536;
        if (context.Request.ContentLength > 65536) throw new BadHttpRequestException("Request too large.", StatusCodes.Status413PayloadTooLarge);
        var buffer = ArrayPool<byte>.Shared.Rent(65537);
        try
        {
            var length = 0;
            while (length < 65537)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(length, 65537 - length), context.RequestAborted).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            if (length > 65536) throw new BadHttpRequestException("Request too large.", StatusCodes.Status413PayloadTooLarge);
            return JsonSerializer.Deserialize(buffer.AsSpan(0, length), type) ?? throw new JsonException("A request body is required.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static async Task ExecuteAsync(HttpContext context, Func<Task> operation)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { await operation().ConfigureAwait(false); }
        catch (DbException) { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; }
        catch (ArgumentException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; }
        catch (JsonException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; }
        catch (InvalidDataException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; }
        catch (BadHttpRequestException exception) { context.Response.StatusCode = exception.StatusCode; }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PersistenceReadRequest))]
[JsonSerializable(typeof(PersistenceAcknowledgeRequest))]
[JsonSerializable(typeof(TaskReadRequest))]
[JsonSerializable(typeof(PersistenceAppendRequest))]
[JsonSerializable(typeof(TaskSaveRequest))]
[JsonSerializable(typeof(DurableRecord[]))]
[JsonSerializable(typeof(DurableTaskStatus))]
internal sealed partial class PersistenceApiJsonContext : JsonSerializerContext;
