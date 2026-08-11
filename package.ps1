[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $AssemblyPath = (Join-Path $PSScriptRoot 'source\bin\Release\GroundRoads.dll'),

    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'dist'),

    [switch] $Force,

    [switch] $SkipGitChecks
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)] [string] $Label,
        [AllowNull()] $Actual,
        [AllowNull()] $Expected
    )

    if ([string] $Actual -cne [string] $Expected) {
        throw "$Label mismatch: expected '$Expected', got '$Actual'."
    }
}

function Get-FullPath {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [string] $BasePath = (Get-Location).Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

$repoRoot = Get-FullPath -Path $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'manifest.json'
$projectPath = Join-Path $repoRoot 'source\GroundRoads.csproj'
$changelogPath = Join-Path $repoRoot 'changelog.txt'
$readmePath = Join-Path $repoRoot 'readme.txt'
$assemblyFullPath = Get-FullPath -Path $AssemblyPath -BasePath $repoRoot
$activeAssemblyPath = Get-FullPath -Path (Join-Path $repoRoot 'GroundRoads.dll')

foreach ($requiredPath in @($manifestPath, $projectPath, $changelogPath, $readmePath, $assemblyFullPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required release input is missing: $requiredPath"
    }
}

if ([string]::Equals($assemblyFullPath, $activeAssemblyPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to package the active mod-root GroundRoads.dll. Build and package source\bin\Release\GroundRoads.dll instead.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
[xml] $project = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
$projectVersion = [string] ($project.Project.PropertyGroup.Version | Select-Object -First 1)
$projectFileVersion = [string] ($project.Project.PropertyGroup.FileVersion | Select-Object -First 1)

Assert-Equal -Label 'manifest version' -Actual $manifest.version -Expected $Version
Assert-Equal -Label 'project version' -Actual $projectVersion -Expected $Version
Assert-Equal -Label 'project file version' -Actual $projectFileVersion -Expected "$Version.0"

if (@($manifest.primary_dlls) -notcontains 'GroundRoads.dll') {
    throw "manifest.json does not declare GroundRoads.dll in primary_dlls."
}

$changelogFirstLine = Get-Content -LiteralPath $changelogPath -Encoding UTF8 -TotalCount 1
$changelogHeaderPattern = '^v' + [regex]::Escape($Version) + ' \| \d{4}-\d{2}-\d{2}$'
if ($changelogFirstLine -notmatch $changelogHeaderPattern) {
    throw "Changelog header '$changelogFirstLine' does not match 'v$Version | YYYY-MM-DD'."
}

$readmeVersionLine = Get-Content -LiteralPath $readmePath -Encoding UTF8 |
    Where-Object { $_ -match '^Version:\s+' } |
    Select-Object -First 1
Assert-Equal -Label 'readme version' -Actual $readmeVersionLine -Expected "Version: $Version"

$assemblyInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyFullPath)
Assert-Equal -Label 'assembly file version' -Actual $assemblyInfo.FileVersion -Expected "$Version.0"
if (-not ([string] $assemblyInfo.ProductVersion).StartsWith($Version, [System.StringComparison]::Ordinal)) {
    throw "Assembly product version '$($assemblyInfo.ProductVersion)' does not start with '$Version'."
}

if (-not $SkipGitChecks) {
    $gitRoot = (& git -C $repoRoot rev-parse --show-toplevel 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw 'Git repository check failed.'
    }

    Assert-Equal -Label 'Git repository root' -Actual (Get-FullPath -Path ([string] $gitRoot).Trim()) -Expected $repoRoot

    $gitStatus = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to read Git status.'
    }
    if ($gitStatus.Count -ne 0) {
        throw "The working tree is not clean. Commit the release sources before packaging.`n$($gitStatus -join "`n")"
    }

    $headRevision = ([string] (& git -C $repoRoot rev-parse HEAD)).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to resolve the Git HEAD revision.'
    }

    if ([string] $assemblyInfo.ProductVersion -match '\+([0-9a-fA-F]{7,40})$') {
        $assemblyRevision = $Matches[1]
        if (-not $headRevision.StartsWith($assemblyRevision, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Assembly revision '$assemblyRevision' does not match Git HEAD '$headRevision'. Rebuild after committing."
        }
    }
}

$payload = @(
    [pscustomobject] @{ Source = $manifestPath; Archive = 'GroundRoads/manifest.json' }
    [pscustomobject] @{ Source = $changelogPath; Archive = 'GroundRoads/changelog.txt' }
    [pscustomobject] @{ Source = $readmePath; Archive = 'GroundRoads/readme.txt' }
    [pscustomobject] @{ Source = $assemblyFullPath; Archive = 'GroundRoads/GroundRoads.dll' }
)

$languageFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'lang') -Filter '*.json' -File | Sort-Object Name)
if ($languageFiles.Count -ne 22) {
    throw "Expected 22 language catalogs, found $($languageFiles.Count)."
}

foreach ($languageFile in $languageFiles) {
    $payload += [pscustomobject] @{
        Source = $languageFile.FullName
        Archive = "GroundRoads/lang/$($languageFile.Name)"
    }
}

$payload = @($payload | Sort-Object Archive)
$duplicateNames = @($payload | Group-Object Archive | Where-Object Count -gt 1)
if ($duplicateNames.Count -ne 0) {
    throw "Duplicate archive paths: $($duplicateNames.Name -join ', ')"
}

foreach ($item in $payload) {
    if (-not (Test-Path -LiteralPath $item.Source -PathType Leaf)) {
        throw "Release input is missing: $($item.Source)"
    }
    if (-not $item.Archive.StartsWith('GroundRoads/', [System.StringComparison]::Ordinal) -or
        $item.Archive.StartsWith('/') -or
        $item.Archive.Contains('\') -or
        @($item.Archive.Split('/')) -contains '..') {
        throw "Unsafe archive path: $($item.Archive)"
    }
}

$outputFullPath = Get-FullPath -Path $OutputDirectory -BasePath $repoRoot
$archivePath = Join-Path $outputFullPath "GroundRoads-$Version-COI.zip"
if ((Test-Path -LiteralPath $archivePath) -and -not $Force) {
    throw "Release archive already exists: $archivePath. Pass -Force to replace it."
}

[System.IO.Directory]::CreateDirectory($outputFullPath) | Out-Null
$temporaryPath = Join-Path $outputFullPath (".GroundRoads-$Version-COI.zip.{0}.tmp" -f [guid]::NewGuid().ToString('N'))
$releaseTimestamp = [datetimeoffset]::new(2000, 1, 1, 0, 0, 0, [timespan]::Zero)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

try {
    $fileStream = [System.IO.File]::Open($temporaryPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($fileStream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($item in $payload) {
                $entry = $archive.CreateEntry($item.Archive, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $releaseTimestamp
                $entry.ExternalAttributes = 0

                $inputStream = [System.IO.File]::OpenRead($item.Source)
                try {
                    $entryStream = $entry.Open()
                    try {
                        $inputStream.CopyTo($entryStream)
                    }
                    finally {
                        $entryStream.Dispose()
                    }
                }
                finally {
                    $inputStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $fileStream.Dispose()
    }

    $readStream = [System.IO.File]::OpenRead($temporaryPath)
    try {
        $createdArchive = [System.IO.Compression.ZipArchive]::new($readStream, [System.IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            $actualNames = @($createdArchive.Entries | ForEach-Object FullName)
            $expectedNames = @($payload | ForEach-Object Archive)
            Assert-Equal -Label 'archive entry count' -Actual $actualNames.Count -Expected $expectedNames.Count
            Assert-Equal -Label 'archive entry list' -Actual ($actualNames -join "`n") -Expected ($expectedNames -join "`n")

            foreach ($entry in $createdArchive.Entries) {
                if ($entry.LastWriteTime.DateTime -ne $releaseTimestamp.DateTime) {
                    throw "Unexpected timestamp on '$($entry.FullName)': $($entry.LastWriteTime)."
                }
            }
        }
        finally {
            $createdArchive.Dispose()
        }
    }
    finally {
        $readStream.Dispose()
    }

    if (Test-Path -LiteralPath $archivePath) {
        [System.IO.File]::Replace($temporaryPath, $archivePath, $null)
    }
    else {
        [System.IO.File]::Move($temporaryPath, $archivePath)
    }
}
catch {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
    throw
}

$archiveFile = Get-Item -LiteralPath $archivePath
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
Write-Host "Created $archivePath"
Write-Host "Entries: $($payload.Count)"
Write-Host "Bytes: $($archiveFile.Length)"
Write-Host "SHA256: $archiveHash"
