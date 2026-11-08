using System.Data.Common;
using RealtimeStack.AspNetCore;
using RealtimeStack.Client;
using RealtimeStack.HttpFallback;
using RealtimeStack.Plugins.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace RealtimeStack.Config;

public static class RealtimeStackConfigurationExtensions
{
    public static IServiceCollection AddRealtimeStackConfiguration(this IServiceCollection services,
        IConfiguration configuration, Action<RealtimeStackRegistrationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(RealtimeStackSetupOptions)))
            throw new InvalidOperationException("RealtimeStack configuration has already been registered.");
        var options = RealtimeStackSetupOptions.Read(configuration);
        var errors = options.Validate(configuration);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(" ", errors));
        var builder = new RealtimeStackRegistrationBuilder();
        configure?.Invoke(builder);
        // Resolve the selected factory before mutating the service collection.
        var factory = options.PersistenceEnabled ? builder.GetFactory(options.Provider!.Value) : null;
        services.AddSingleton(options);
        services.AddSingleton<RealtimeStackSetupManager>();
        if (options.GatewayEnabled)
        {
            services.AddRealtimeStackGateway(configuration);
            services.AddSingleton<IRealtimeStackSetupStep>(new ConfiguredSetupStep("gateway", "Gateway and Redis services registered; use gateway health endpoints for runtime readiness."));
        }
        if (options.HttpFallbackEnabled)
        {
            services.AddRealtimeStackHttpFallback(configuration);
            services.AddSingleton<IRealtimeStackSetupStep>(new ConfiguredSetupStep("http-fallback", "HTTP fallback registered; map its endpoints in the host."));
        }
        if (options.ClientEnabled)
        {
            services.AddSingleton(provider =>
            {
                var clientOptions = new RealtimeStackClientOptions { Endpoint = new Uri(configuration["RealtimeStackClient:Endpoint"]!, UriKind.Absolute) };
                builder.ConfigureClient?.Invoke(clientOptions);
                return new RealtimeStackClient(clientOptions, provider.GetService<IRealtimeStackAuthenticationProvider>(),
                    provider.GetService<IRealtimeStackTransportFactory>(), provider.GetService<IRealtimeStackClientLogger>(),
                    provider.GetService<IRealtimeStackClientClock>(), provider.GetService<IRealtimeStackRetryPolicy>());
            });
            services.AddSingleton<IRealtimeStackSetupStep>(new ConfiguredSetupStep("client", "Client registered; connecting remains an explicit host operation."));
        }
        if (factory is not null)
        {
            services.AddSingleton(provider =>
            {
                var connectionString = configuration.GetConnectionString(options.ConnectionStringName)!;
                DbConnection Connections() => factory(provider, connectionString);
                void Command(DbCommand command)
                {
                    command.CommandTimeout = options.CommandTimeoutSeconds;
                    builder.ConfigureCommand?.Invoke(command);
                }
                return RealtimeStackRegistrationBuilder.CreateStore(options.Provider!.Value, Connections, options.TableName,
                    provider.GetService<TimeProvider>() ?? TimeProvider.System, Command);
            });
            services.AddSingleton<IRealtimeStackPersistenceStore>(provider => provider.GetRequiredService<RelationalPersistenceStore>());
            services.AddSingleton(provider => new DurableTasks(provider.GetRequiredService<IRealtimeStackPersistenceStore>(), provider.GetService<TimeProvider>()));
            services.AddSingleton<IRealtimeStackSetupStep>(provider => new PersistenceSetupStep(
                provider.GetRequiredService<RelationalPersistenceStore>(),
                () => factory(provider, configuration.GetConnectionString(options.ConnectionStringName)!),
                command => { command.CommandTimeout = options.CommandTimeoutSeconds; builder.ConfigureCommand?.Invoke(command); }));
        }
        foreach (var step in builder.Steps) services.AddSingleton(step);
        return services;
    }
}
