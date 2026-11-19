[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string] $Configuration = 'Release', [switch] $Test)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $root
try {
    & dotnet build (Join-Path $root 'Hi3Helper.Plugin.StellaSora.slnx') -c $Configuration -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    if ($Test) {
        & dotnet run --project (Join-Path $root 'tests/Hi3Helper.Plugin.StellaSora.Tests/Hi3Helper.Plugin.StellaSora.Tests.csproj') -c $Configuration -p:Platform=x64 --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
    }
} finally { Pop-Location }
