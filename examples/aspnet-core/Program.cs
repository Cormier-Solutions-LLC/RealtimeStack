using Realtime.Config;
using Microsoft.Data.Sqlite;
using Npgsql;
using Realtime.AspNetCore;
using Realtime.HttpFallback;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromMinutes(20);
});
builder.Services.AddRealtimeConfiguration(builder.Configuration, registration => registration
    .UseSqlite((_, connectionString) => new SqliteConnection(connectionString))
    .UsePostgreSql((_, connectionString) => new NpgsqlConnection(connectionString))
    .UseCockroachDb((_, connectionString) => new NpgsqlConnection(connectionString)));

var app = builder.Build();
app.UseRealtimeGateway();
app.UseSession();
app.MapRealtimeGateway();
if (app.Services.GetRequiredService<RealtimeSetupOptions>().HttpFallbackEnabled) app.MapRealtimeHttpFallback();
app.MapRealtimePersistence(builder.Configuration);
app.MapRealtimeDiagnostics();
app.MapGet("/", () => Results.Text("Realtime ASP.NET Core integration example"));
app.Run();
