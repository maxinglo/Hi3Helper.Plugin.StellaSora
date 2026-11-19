[CmdletBinding()]
param([switch] $Package)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'src/Hi3Helper.Plugin.StellaSora/Hi3Helper.Plugin.StellaSora.csproj'
$output = Join-Path $root 'artifacts/publish/win-x64'
& dotnet publish $project -c Release -r win-x64 -p:Platform=x64 -p:IlcOptimizationPreference=Size -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'NativeAOT publish failed.' }
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $output 'LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $root 'vendor/Hi3Helper.Plugin.Core/LICENSE') -Destination (Join-Path $output 'Hi3Helper.Plugin.Core.LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $root 'vendor/licenses/Microsoft.NET.LICENSE.txt') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $root 'vendor/licenses/Microsoft.NET.THIRD-PARTY-NOTICES.txt') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-StellaSoraMaintenance.ps1') -Destination $output -Force
[xml] $xml = Get-Content -LiteralPath $project -Raw
$version = [version]$xml.Project.PropertyGroup.Version
$versionText = '{0}.{1}.{2}.0' -f $version.Major, $version.Minor, $version.Build
$names = @('StellaSora.dll', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md', 'Hi3Helper.Plugin.Core.LICENSE.txt', 'Microsoft.NET.LICENSE.txt', 'Microsoft.NET.THIRD-PARTY-NOTICES.txt', 'Invoke-StellaSoraMaintenance.ps1')
$assets = foreach ($name in $names) {
    $file = Get-Item -LiteralPath (Join-Path $output $name)
    [ordered]@{ FilePath = $name; Size = $file.Length; FileHash = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $file.FullName -Algorithm MD5).Hash)) }
}
$manifest = [ordered]@{
    PluginVersion = $versionText
    PluginStandardVersion = '0.1.6.0'
    PluginCreationDate = '2026-11-10T00:00:00Z'
    ManifestDate = [DateTimeOffset]::UtcNow.ToString('o')
    MainLibraryName = 'StellaSora.dll'
    MainPluginName = 'Stella Sora'
    MainPluginAuthor = 'Maxing'
    MainPluginDescription = 'Install, update, and launch Stella Sora with Collapse Launcher.'
    Assets = @($assets)
}
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8NoBOM
& (Join-Path $PSScriptRoot 'Test-NativePlugin.ps1') -Path (Join-Path $output 'StellaSora.dll') -ExpectedVersion $versionText
if ($Package) {
    $packageRoot = Join-Path $root 'artifacts/packages'
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    $archive = Join-Path $packageRoot ("StellaSora_{0}_win-x64.zip" -f $versionText)
    $stream = [IO.File]::Open($archive, [IO.FileMode]::Create)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($name in @($names) + @('manifest.json')) {
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $output $name), $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    & (Join-Path $PSScriptRoot 'Test-Package.ps1') -Path $archive
    [pscustomobject]@{ Package = $archive; SHA256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash }
}
