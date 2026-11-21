[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $repository 'artifacts/windows-x64'
$release = Join-Path $repository 'artifacts/release'

Push-Location -LiteralPath $repository
try {
    $publishArgs = @(
        'publish', 'src/Locara.Desktop', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=embedded',
        '-p:NuGetLockFilePath=packages.windows-x64.lock.json', '-p:RestoreLockedMode=true', '-o', $publish
    )
    & $Dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    New-Item -ItemType Directory -Force -Path $release | Out-Null
    $name = 'Locara-windows-x64.zip'
    $id = [guid]::NewGuid().ToString('N')
    $build = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + $id.Substring(0, 8)
    $stage = (New-Item -ItemType Directory -Path (Join-Path $release ('.staging-' + $id))).FullName
    $destination = [IO.Path]::GetFullPath((Join-Path $release $build))
    if ([IO.Path]::GetDirectoryName($stage) -ne [IO.Path]::GetFullPath($release) -or
        [IO.Path]::GetDirectoryName($destination) -ne [IO.Path]::GetFullPath($release)) {
        throw 'Invalid release directory.'
    }
    $archive = Join-Path $stage $name
    try {
        $contents = @(
            (Join-Path $publish 'Locara.Desktop.exe'),
            (Join-Path $repository 'LICENSE'),
            (Join-Path $repository 'THIRD-PARTY-LICENSES.txt'),
            (Join-Path $repository 'docs/Windows-Release.md')
        )
        Compress-Archive -LiteralPath $contents -DestinationPath $archive
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            if ($zip.Entries.Count -ne $contents.Count) { throw 'Unexpected release contents.' }
            foreach ($file in $contents) {
                $entry = $zip.GetEntry([IO.Path]::GetFileName($file))
                if ($null -eq $entry) { throw "Missing release file: $file" }
                $stream = $entry.Open()
                try { $packed = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
                finally { $stream.Dispose() }
                if ($packed -ne (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash) {
                    throw "Release file hash mismatch: $file"
                }
            }
        }
        finally { $zip.Dispose() }
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $name" -Encoding Ascii
        if ((Get-Content -LiteralPath "$archive.sha256" -Raw).TrimEnd() -cne "$hash  $name") {
            throw 'Release checksum verification failed.'
        }
        [IO.Directory]::Move($stage, $destination)
        Get-Item -LiteralPath (Join-Path $destination $name), (Join-Path $destination "$name.sha256")
    }
    finally {
        if (Test-Path -LiteralPath $stage) {
            foreach ($file in @($archive, "$archive.sha256")) {
                if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
            }
            [IO.Directory]::Delete($stage)
        }
    }
}
finally {
    Pop-Location
}
