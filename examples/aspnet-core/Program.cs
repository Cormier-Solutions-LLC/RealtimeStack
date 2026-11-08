using RealtimeStack.Config;
using Microsoft.Data.Sqlite;
using Npgsql;
using RealtimeStack.AspNetCore;
using RealtimeStack.HttpFallback;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromMinutes(20);
});
builder.Services.AddRealtimeStackConfiguration(builder.Configuration, registration => registration
    .UseSqlite((_, connectionString) => new SqliteConnection(connectionString))
    .UsePostgreSql((_, connectionString) => new NpgsqlConnection(connectionString))
    .UseCockroachDb((_, connectionString) => new NpgsqlConnection(connectionString)));

var app = builder.Build();
app.UseRealtimeStackGateway();
app.UseSession();
app.MapRealtimeStackGateway();
if (app.Services.GetRequiredService<RealtimeStackSetupOptions>().HttpFallbackEnabled) app.MapRealtimeStackHttpFallback();
app.MapRealtimeStackPersistence(builder.Configuration);
app.MapRealtimeStackDiagnostics();
app.MapGet("/", () => Results.Text("RealtimeStack ASP.NET Core integration example"));
app.Run();
