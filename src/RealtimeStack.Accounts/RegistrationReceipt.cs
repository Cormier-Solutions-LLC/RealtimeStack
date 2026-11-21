using System.Text.Json;

namespace RealtimeStack.Accounts;

public sealed record RegistrationReceipt(string ProductId, string Subject, string InstallationId, string Status, DateTimeOffset RegisteredAt, object? ExpiresAt, RegistrationPricing Pricing)
{
    public static RegistrationReceipt Validate(JsonElement root, string subject, string installation)
    {
        try
        {
            var pricing = root.GetProperty("pricing");
            if (root.GetProperty("productId").GetString() != "realtime" || root.GetProperty("subject").GetString() != subject || root.GetProperty("installationId").GetString() != installation || root.GetProperty("status").GetString() != "registered" ||
                root.GetProperty("expiresAt").ValueKind != JsonValueKind.Null || pricing.GetProperty("model").GetString() != "free-registration" || pricing.GetProperty("amountCents").GetInt64() != 0 || pricing.GetProperty("currency").GetString() != "USD") throw new InvalidDataException("Invalid free registration receipt.");
            return new("realtime", subject, installation, "registered", root.GetProperty("registeredAt").GetDateTimeOffset(), null, new("free-registration", 0, "USD"));
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException) { throw new InvalidDataException("Invalid free registration receipt."); }
    }
}
public sealed record RegistrationPricing(string Model, int AmountCents, string Currency);
