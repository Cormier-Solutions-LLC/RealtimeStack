namespace RealtimeStack.KubernetesTests;

public sealed class NamingConventionTests
{
    [Theory]
    [InlineData("dev-realtimestack")]
    [InlineData("test-realtimestack")]
    [InlineData("prod-realtimestack")]
    public void EnvironmentApplicationNameIsAccepted(string value)
    {
        Assert.Matches("^[a-z0-9]+-[a-z0-9][a-z0-9-]*$", value);
    }

    [Theory]
    [InlineData("Dev-realtimestack")]
    [InlineData("prod")]
    [InlineData("prod_realtimestack")]
    [InlineData("prod-")]
    public void InvalidEnvironmentApplicationNameIsRejected(string value)
    {
        Assert.DoesNotMatch("^[a-z0-9]+-[a-z0-9][a-z0-9-]*$", value);
    }
}
