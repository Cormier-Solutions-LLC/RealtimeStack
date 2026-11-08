namespace RealtimeStack.Contracts;

public readonly record struct RealtimeStackRoute
{
    private RealtimeStackRoute(string value, string topic, string? userId)
    {
        Value = value;
        Topic = topic;
        UserId = userId;
    }

    public string Value { get; }

    public string Topic { get; }

    public string? UserId { get; }

    public bool IsUserScoped => UserId is not null;

    public static RealtimeStackRoute ForTopic(string topic)
    {
        ValidateSegment(topic, nameof(topic));
        return new RealtimeStackRoute($"topics/{topic}", topic, null);
    }

    public static RealtimeStackRoute ForGroup(string group) => ForTopic(group);

    public static RealtimeStackRoute ForUserTopic(string userId, string topic)
    {
        ValidateSegment(userId, nameof(userId));
        ValidateSegment(topic, nameof(topic));
        var value = $"users/{userId}/topics/{topic}";
        if (value.Length > ProtocolValidator.MaximumRouteLength)
        {
            throw new ArgumentException("The user topic route must not exceed 256 characters.");
        }
        return new RealtimeStackRoute(value, topic, userId);
    }

    public static bool TryParse(string? value, out RealtimeStackRoute route)
    {
        route = default;
        if (string.IsNullOrWhiteSpace(value) || value!.Length > ProtocolValidator.MaximumRouteLength)
        {
            return false;
        }

        var segments = value.Split('/');
        if (segments.Length == 2 && segments[0] == "topics" && IsValidSegment(segments[1]))
        {
            route = new RealtimeStackRoute(value, segments[1], null);
            return true;
        }
        if (segments.Length == 4 && segments[0] == "users" && segments[2] == "topics" &&
            IsValidSegment(segments[1]) && IsValidSegment(segments[3]))
        {
            route = new RealtimeStackRoute(value, segments[3], segments[1]);
            return true;
        }
        return false;
    }

    public override string ToString() => Value ?? string.Empty;

    private static void ValidateSegment(string value, string parameterName)
    {
        if (!IsValidSegment(value))
        {
            throw new ArgumentException(
                "Route segments must contain 1-128 letters, digits, hyphens, underscores, or periods.",
                parameterName);
        }
    }

    private static bool IsValidSegment(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value!.Length <= 128 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
}
