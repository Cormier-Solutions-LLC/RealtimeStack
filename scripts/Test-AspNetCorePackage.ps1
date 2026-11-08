[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string]$AspNetCoreIntegrationVersion = '2.0.3-beta',
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string]$HttpFallbackVersion = '2.0.3-beta',
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string]$ContractsVersion = '2.0.3-beta',
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string]$RedisAdapterVersion = '2.0.3-beta',
    [string]$PackageSource,
    [string]$UpstreamPackageSource = $env:NUGET_UPSTREAM_SOURCE
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($UpstreamPackageSource)) {
    throw 'UpstreamPackageSource or NUGET_UPSTREAM_SOURCE must identify the configured upstream NuGet feed.'
}
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$scratchRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("realtimestack-package-" + [guid]::NewGuid().ToString('N'))
$feedPath = Join-Path $scratchRoot 'feed'
$consumerPath = Join-Path $scratchRoot 'consumer'
$nugetConfigPath = Join-Path $scratchRoot 'NuGet.Config'
$packagesPath = Join-Path $scratchRoot 'packages'

try {
    New-Item -ItemType Directory -Path $feedPath, $consumerPath -Force | Out-Null
    $escapedFeedPath = [System.Security.SecurityElement]::Escape($feedPath)
    $escapedPackagesPath = [System.Security.SecurityElement]::Escape($packagesPath)
    @"
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$escapedPackagesPath" />
  </config>
  <packageSources>
    <clear />
    <add key="local" value="$escapedFeedPath" />
    <add key="upstream" value="$([System.Security.SecurityElement]::Escape($UpstreamPackageSource))" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $nugetConfigPath -Encoding utf8NoBOM

    if ([string]::IsNullOrWhiteSpace($PackageSource)) {
        $packableProjects = Get-ChildItem (Join-Path $repositoryRoot 'src') -Recurse -Filter '*.csproj' | Where-Object {
            [xml]$projectXml = Get-Content -LiteralPath $_.FullName -Raw
            $projectXml.SelectSingleNode('//IsPackable')?.InnerText -eq 'true'
        }
        foreach ($project in $packableProjects.FullName) {
            dotnet pack $project --configuration Release --no-build --output $feedPath `
                -p:AspNetCoreIntegrationVersion=$AspNetCoreIntegrationVersion `
                -p:HttpFallbackVersion=$HttpFallbackVersion `
                -p:ContractsVersion=$ContractsVersion `
                -p:RedisAdapterVersion=$RedisAdapterVersion
            if ($LASTEXITCODE -ne 0) { throw "Packing failed for $project." }
        }
    }
    else {
        $resolvedPackageSource = (Resolve-Path -LiteralPath $PackageSource -ErrorAction Stop).Path
        Get-ChildItem -LiteralPath $resolvedPackageSource -Filter '*.nupkg' -File | Copy-Item -Destination $feedPath

    }

    @"
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="RealtimeStack.Config" Version="$ContractsVersion" />
    <PackageReference Include="Npgsql" Version="10.0.3" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.14" />
    <PackageReference Include="RealtimeStack.AspNetCore" Version="$AspNetCoreIntegrationVersion" />
    <PackageReference Include="RealtimeStack.HttpFallback" Version="$HttpFallbackVersion" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $consumerPath 'Consumer.csproj') -Encoding utf8NoBOM

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'examples/aspnet-core/Program.cs') -Destination $consumerPath
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'examples/aspnet-core/appsettings.json') -Destination $consumerPath

    dotnet restore (Join-Path $consumerPath 'Consumer.csproj') --configfile $nugetConfigPath
    if ($LASTEXITCODE -ne 0) { throw 'Clean consumer restore failed.' }
    dotnet build (Join-Path $consumerPath 'Consumer.csproj') --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Clean consumer build failed.' }
}
finally {
    if (Test-Path -LiteralPath $scratchRoot) {
        $resolvedScratch = (Resolve-Path -LiteralPath $scratchRoot).Path
        $resolvedTemp = (Resolve-Path -LiteralPath ([System.IO.Path]::GetTempPath())).Path
        if (-not $resolvedScratch.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove temporary path outside the configured temporary directory: $resolvedScratch"
        }
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
