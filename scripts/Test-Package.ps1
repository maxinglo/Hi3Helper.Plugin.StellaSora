[CmdletBinding()]
param([Parameter(Mandatory)][string] $Path)
$ErrorActionPreference = 'Stop'
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
try {
    $manifestEntry = $zip.GetEntry('manifest.json')
    if ($null -eq $manifestEntry) { throw 'Package manifest is missing.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.MainLibraryName -ne 'StellaSora.dll' -or $manifest.PluginStandardVersion -ne '0.1.6.0') { throw 'Unexpected plugin metadata.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($asset in $manifest.Assets) {
        $name = [string]$asset.FilePath
        $segments = $name.Replace([char]92, [char]47).Split([char]47)
        if ([IO.Path]::IsPathRooted($name) -or ($segments -contains "..") -or -not $seen.Add($name)) { throw "Invalid manifest path: $name" }
        $entry = $zip.GetEntry($name)
        if ($null -eq $entry -or $entry.Length -ne $asset.Size) { throw "Missing or invalid asset: $name" }
        $inputStream = $entry.Open()
        try { $hash = [Convert]::ToBase64String([Security.Cryptography.MD5]::HashData($inputStream)) } finally { $inputStream.Dispose() }
        if ($hash -ne $asset.FileHash) { throw "Checksum mismatch: $name" }
    }
    if ($zip.Entries.Count -ne $seen.Count + 1) { throw 'Package contains undeclared entries.' }
    Write-Output "PASS: package manifest, paths, sizes, and $($seen.Count) asset checksums."
} finally { $zip.Dispose() }
