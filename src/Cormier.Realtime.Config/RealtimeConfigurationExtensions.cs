using System.Data.Common;
using Cormier.Realtime.AspNetCore;
using Cormier.Realtime.Client;
using Cormier.Realtime.HttpFallback;
using Cormier.Realtime.Plugins.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cormier.Realtime.Config;

public static class RealtimeConfigurationExtensions
{
    public static IServiceCollection AddRealtimeConfiguration(this IServiceCollection services,
        IConfiguration configuration, Action<RealtimeRegistrationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(RealtimeSetupOptions)))
            throw new InvalidOperationException("Realtime configuration has already been registered.");
        var options = RealtimeSetupOptions.Read(configuration);
        var errors = options.Validate(configuration);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(" ", errors));
        var builder = new RealtimeRegistrationBuilder();
        configure?.Invoke(builder);
        // Resolve the selected factory before mutating the service collection.
        var factory = options.PersistenceEnabled ? builder.GetFactory(options.Provider!.Value) : null;
        services.AddSingleton(options);
        services.AddSingleton<RealtimeSetupManager>();
        if (options.GatewayEnabled)
        {
            services.AddRealtimeGateway(configuration);
            services.AddSingleton<IRealtimeSetupStep>(new ConfiguredSetupStep("gateway", "Gateway and Redis services registered; use gateway health endpoints for runtime readiness."));
        }
        if (options.HttpFallbackEnabled)
        {
            services.AddRealtimeHttpFallback(configuration);
            services.AddSingleton<IRealtimeSetupStep>(new ConfiguredSetupStep("http-fallback", "HTTP fallback registered; map its endpoints in the host."));
        }
        if (options.ClientEnabled)
        {
            services.AddSingleton(provider =>
            {
                var clientOptions = new RealtimeClientOptions { Endpoint = new Uri(configuration["RealtimeClient:Endpoint"]!, UriKind.Absolute) };
                builder.ConfigureClient?.Invoke(clientOptions);
                return new RealtimeClient(clientOptions, provider.GetService<IRealtimeAuthenticationProvider>(),
                    provider.GetService<IRealtimeTransportFactory>(), provider.GetService<IRealtimeClientLogger>(),
                    provider.GetService<IRealtimeClientClock>(), provider.GetService<IRealtimeRetryPolicy>());
            });
            services.AddSingleton<IRealtimeSetupStep>(new ConfiguredSetupStep("client", "Client registered; connecting remains an explicit host operation."));
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
                return RealtimeRegistrationBuilder.CreateStore(options.Provider!.Value, Connections, options.TableName,
                    provider.GetService<TimeProvider>() ?? TimeProvider.System, Command);
            });
            services.AddSingleton<IRealtimePersistenceStore>(provider => provider.GetRequiredService<RelationalPersistenceStore>());
            services.AddSingleton(provider => new DurableTasks(provider.GetRequiredService<IRealtimePersistenceStore>(), provider.GetService<TimeProvider>()));
            services.AddSingleton<IRealtimeSetupStep>(provider => new PersistenceSetupStep(
                provider.GetRequiredService<RelationalPersistenceStore>(),
                () => factory(provider, configuration.GetConnectionString(options.ConnectionStringName)!),
                command => { command.CommandTimeout = options.CommandTimeoutSeconds; builder.ConfigureCommand?.Invoke(command); }));
        }
        foreach (var step in builder.Steps) services.AddSingleton(step);
        return services;
    }
}
