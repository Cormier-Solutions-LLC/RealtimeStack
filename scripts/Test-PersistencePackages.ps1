[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageSource,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$UpstreamPackageSource
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$feed = (Resolve-Path -LiteralPath $PackageSource).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("realtime-persistence-consumer-" + [guid]::NewGuid().ToString('N'))
$names = @('Persistence', 'PostgreSql', 'MySql', 'MariaDb', 'SqlServer', 'AzureSql', 'Oracle', 'Sqlite', 'Db2', 'CockroachDb', 'Snowflake')
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $references = "<PackageReference Include=`"Realtime.Config`" Version=`"$([Security.SecurityElement]::Escape($Version))`" />" + ($names | ForEach-Object { "<PackageReference Include=`"Realtime.Plugins.$_`" Version=`"$([Security.SecurityElement]::Escape($Version))`" />" }) -join "`n"
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
  <ItemGroup>$references</ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $scratch 'Consumer.csproj')
    $constructors = ($names | Where-Object { $_ -ne 'Persistence' } | ForEach-Object {
        "new Cormier.Realtime.Plugins.$_.${_}PersistenceStore(() => throw new InvalidOperationException()),"
    }) -join "`n"
    @"
using Cormier.Realtime.Plugins.Persistence;
using Cormier.Realtime.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
RelationalPersistenceStore[] stores = [$constructors];
foreach (var store in stores) {
    if (!store.CreateTableSql.Contains("PRIMARY KEY")) throw new InvalidOperationException();
}
using var services = new ServiceCollection().AddRealtimeConfiguration(new ConfigurationBuilder().Build()).BuildServiceProvider();
if ((await services.GetRequiredService<RealtimeSetupManager>().InspectAsync()).Count != 0) throw new InvalidOperationException();
Console.WriteLine("Config and all ten packaged database adapters loaded.");
"@ | Set-Content -LiteralPath (Join-Path $scratch 'Program.cs')
    $escapedFeed = [Security.SecurityElement]::Escape($feed)
    $escapedUpstream = [Security.SecurityElement]::Escape($UpstreamPackageSource)
    $escapedPackages = [Security.SecurityElement]::Escape((Join-Path $scratch 'packages'))
    @"
<configuration>
  <config><add key="globalPackagesFolder" value="$escapedPackages" /></config>
  <packageSources><clear /><add key="local" value="$escapedFeed" /><add key="upstream" value="$escapedUpstream" /></packageSources>
  <packageSourceMapping><packageSource key="local"><package pattern="Realtime.*" /></packageSource><packageSource key="upstream"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $scratch 'NuGet.Config')
    dotnet restore (Join-Path $scratch 'Consumer.csproj') --configfile (Join-Path $scratch 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Persistence package consumer restore failed.' }
    dotnet run --project (Join-Path $scratch 'Consumer.csproj') --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Persistence package consumer execution failed.' }
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        $resolvedScratch = (Resolve-Path -LiteralPath $scratch).Path
        $temp = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedScratch.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary cleanup path.' }
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
