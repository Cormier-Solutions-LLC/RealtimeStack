using Realtime.Config;
using Realtime.Plugins.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Realtime.Plugins.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string path = Path.Join(Path.GetTempPath(), $"realtime-config-{Guid.NewGuid():N}.db");

    private IConfiguration Config(string provider = "Sqlite") => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["RealtimeSetup:Persistence:Enabled"] = "true",
        ["RealtimeSetup:Persistence:Provider"] = provider,
        ["ConnectionStrings:RealtimePersistence"] = $"Data Source={path};Pooling=False"
    }).Build();

    [Fact]
    public async Task RegistrationDoesNotConnectAndExplicitSetupIsRepeatableAndPreservesRecords()
    {
        var opened = 0;
        var commands = 0;
        var services = new ServiceCollection();
        services.AddRealtimeConfiguration(Config(), builder => builder.UseSqlite((_, connectionString) =>
        {
            opened++;
            return new SqliteConnection(connectionString);
        }).ConfigureDatabaseCommands(command => { Assert.Equal(30, command.CommandTimeout); commands++; }));
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<RealtimeSetupManager>();
        Assert.Equal(0, opened);
        Assert.False(File.Exists(path));
        Assert.Equal(RealtimeSetupState.Unavailable, Assert.Single(await manager.InspectAsync()).State);
        await manager.InitializeAsync();
        Assert.Equal(RealtimeSetupState.Ready, Assert.Single(await manager.InspectAsync()).State);
        var store = provider.GetRequiredService<IRealtimePersistenceStore>();
        Assert.IsType<Sqlite.SqlitePersistenceStore>(store);
        var stream = new DurableStreamKey("tenant", "owner", "notifications", DurableRecordKind.Notification);
        await store.AppendAsync(stream, new(1, "keep me", DateTimeOffset.UtcNow));
        await manager.InitializeAsync();
        Assert.Equal("keep me", Assert.Single(await store.ReadAsync(stream)).Payload);
        Assert.True(commands > 0);
        Assert.NotNull(provider.GetRequiredService<DurableTasks>());
        Assert.Throws<InvalidOperationException>(() => services.AddRealtimeConfiguration(Config()));
    }

    [Fact]
    public async Task TemplateIsDisabledAndCannotOverwriteExistingConfiguration()
    {
        var file = path + ".json";
        try
        {
            await RealtimeFirstRun.WriteTemplateAsync(file);
            var config = new ConfigurationBuilder().AddJsonFile(file).Build();
            var options = RealtimeSetupOptions.Read(config);
            Assert.False(options.PersistenceEnabled);
            Assert.False(options.GatewayEnabled);
            Assert.False(options.ClientEnabled);
            Assert.Empty(options.Validate(config));
            await Assert.ThrowsAsync<IOException>(() => RealtimeFirstRun.WriteTemplateAsync(file));
            Assert.Equal(RealtimeFirstRun.Template, await File.ReadAllTextAsync(file));
            using var services = new ServiceCollection().AddRealtimeConfiguration(config).BuildServiceProvider();
            Assert.Empty(await services.GetRequiredService<RealtimeSetupManager>().InspectAsync());
            Assert.Null(services.GetService<IRealtimePersistenceStore>());
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("MySql")]
    [InlineData("MariaDb")]
    [InlineData("SqlServer")]
    [InlineData("AzureSql")]
    [InlineData("Oracle")]
    [InlineData("Sqlite")]
    [InlineData("Db2")]
    [InlineData("CockroachDb")]
    [InlineData("Snowflake")]
    public void SelectsAllDatabaseAdaptersWithoutLoadingDriversOrConnecting(string name)
    {
        var dialect = Enum.Parse<RelationalDialect>(name);
        using var services = new ServiceCollection().AddRealtimeConfiguration(Config(name), builder =>
            builder.UseConnectionFactory(dialect, (_, _) => throw new InvalidOperationException("Should not connect during registration"))).BuildServiceProvider();
        Assert.Equal(name + "PersistenceStore", services.GetRequiredService<IRealtimePersistenceStore>().GetType().Name);
    }

    [Theory]
    [InlineData("0")]
    [InlineData(" 0")]
    [InlineData("Unknown")]
    public void RejectsUnknownAndNumericProviders(string name) =>
        Assert.Throws<ArgumentException>(() => RealtimeSetupOptions.Read(Config(name)));

    [Fact]
    public void MissingCredentialsFactoryAndInvalidTableFailBeforeServiceMutation()
    {
        var config = Config();
        config["ConnectionStrings:RealtimePersistence"] = null;
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddRealtimeConfiguration(config));
        Assert.Empty(services);
        config["ConnectionStrings:RealtimePersistence"] = "not logged";
        Assert.Throws<InvalidOperationException>(() => services.AddRealtimeConfiguration(config));
        Assert.Empty(services);
        config["RealtimeSetup:Persistence:TableName"] = "records; DROP TABLE x";
        Assert.NotEmpty(RealtimeSetupOptions.Read(config).Validate(config));
    }

    [Fact]
    public async Task CustomPluginsParticipateAndCancellationStopsSetup()
    {
        var custom = new ExampleStep();
        using var services = new ServiceCollection().AddRealtimeConfiguration(new ConfigurationBuilder().Build(), builder =>
            builder.AddSetupStep(_ => custom)).BuildServiceProvider();
        var manager = services.GetRequiredService<RealtimeSetupManager>();
        Assert.Equal("example", Assert.Single(await manager.InspectAsync()).Id);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.InitializeAsync(cancellation.Token));
        Assert.Equal(0, custom.Initializations);
        await manager.InitializeAsync();
        Assert.Equal(1, custom.Initializations);
        Assert.Throws<ArgumentException>(() => new RealtimeSetupManager([custom, custom]));
    }

    public void Dispose() => File.Delete(path);
    private sealed class ExampleStep : IRealtimeSetupStep
    {
        public string Id => "example";
        public int Initializations { get; private set; }
        public Task<RealtimeSetupStatus> InspectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RealtimeSetupStatus(Id, RealtimeSetupState.Configured, "Registered"));
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            Initializations++;
            return Task.CompletedTask;
        }
    }
}
