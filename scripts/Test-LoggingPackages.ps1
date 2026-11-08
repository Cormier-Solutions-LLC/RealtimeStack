[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageSource,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')][string]$Version,
    [Parameter(Mandatory)][string]$UpstreamPackageSource
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$feed = (Resolve-Path -LiteralPath $PackageSource).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("realtimestack-logging-consumer-" + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $references = (@('Logging', 'Bugsnag', 'Serilog', 'Log4net') | ForEach-Object {
        "<PackageReference Include=`"RealtimeStack.Plugins.$_`" Version=`"$([Security.SecurityElement]::Escape($Version))`" />"
    }) -join "`n"
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.14" />$references</ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $scratch 'Consumer.csproj')
    @'
using Microsoft.Extensions.DependencyInjection;
using RealtimeStack.Plugins.Logging;
using RealtimeStack.Plugins.Bugsnag;
using RealtimeStack.Plugins.Serilog;
using RealtimeStack.Plugins.Log4net;
var adapters = new[] { typeof(BugsnagErrorHandler), typeof(SerilogErrorHandler), typeof(Log4netErrorHandler) };
if (adapters.Any(type => !typeof(IRealtimeStackErrorHandler).IsAssignableFrom(type))) throw new InvalidOperationException();
using var provider = new ServiceCollection().AddRealtimeStackErrorHandler<RecordingHandler>().BuildServiceProvider();
var result = await provider.GetRequiredService<IRealtimeStackErrorReporter>().ReportAsync(new RealtimeStackError(new InvalidOperationException("offline consumer check")));
if (!result.Succeeded || result.AcceptedCount != 1) throw new InvalidOperationException();
Console.WriteLine("All four packaged logging integrations loaded; custom handler dispatch passed.");
public sealed class RecordingHandler : IRealtimeStackErrorHandler
{
    public Task HandleAsync(RealtimeStackError errorReport, CancellationToken cancellationToken) => Task.CompletedTask;
}
'@ | Set-Content -LiteralPath (Join-Path $scratch 'Program.cs')
    $escapedFeed = [Security.SecurityElement]::Escape($feed)
    $escapedUpstream = [Security.SecurityElement]::Escape($UpstreamPackageSource)
    $escapedPackages = [Security.SecurityElement]::Escape((Join-Path $scratch 'packages'))
    @"
<configuration>
  <config><add key="globalPackagesFolder" value="$escapedPackages" /></config>
  <packageSources><clear /><add key="local" value="$escapedFeed" /><add key="upstream" value="$escapedUpstream" /></packageSources>
  <packageSourceMapping><packageSource key="local"><package pattern="RealtimeStack.*" /></packageSource><packageSource key="upstream"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $scratch 'NuGet.Config')
    dotnet restore (Join-Path $scratch 'Consumer.csproj') --configfile (Join-Path $scratch 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Logging package consumer restore failed.' }
    dotnet run --project (Join-Path $scratch 'Consumer.csproj') --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Logging package consumer execution failed.' }
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        $resolvedScratch = (Resolve-Path -LiteralPath $scratch).Path
        $temp = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedScratch.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary cleanup path.' }
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
