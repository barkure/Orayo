param(
    [Parameter(Mandatory)][string] $Expected,
    [Parameter(Mandatory)][string] $Actual
)

$ErrorActionPreference = 'Stop'

# ZIP compression and timestamps can differ after patching; compare file contents.
function Get-PackageManifest([string] $Path) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        $manifest = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $stream = $entry.Open()
            try {
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
                $manifest.Add($entry.FullName, $hash)
            } finally {
                $stream.Dispose()
            }
        }
        return ,$manifest
    } finally {
        $archive.Dispose()
    }
}

$expectedFiles = Get-PackageManifest $Expected
$actualFiles = Get-PackageManifest $Actual
if ($expectedFiles.Count -ne $actualFiles.Count) {
    throw "Package file counts differ: $($expectedFiles.Count) vs $($actualFiles.Count)."
}
foreach ($name in $expectedFiles.Keys) {
    if (-not $actualFiles.ContainsKey($name) -or $actualFiles[$name] -ne $expectedFiles[$name]) {
        throw "Reconstructed package differs: $name"
    }
}
Write-Output "Verified $($expectedFiles.Count) package files after delta reconstruction."
