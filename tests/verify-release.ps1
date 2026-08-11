[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $AssemblyPath = (Join-Path $PSScriptRoot '..\source\bin\Release\GroundRoads.dll'),

    [string] $ArchivePath = (Join-Path $PSScriptRoot "..\dist\GroundRoads-$Version-COI.zip")
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

function Get-StreamSha256 {
    param([Parameter(Mandatory = $true)] [System.IO.Stream] $Stream)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha256.ComputeHash($Stream))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-NormalizedTextSha256 {
    param([Parameter(Mandatory = $true)] [string] $Path)

    $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
    $textContent = [System.IO.File]::ReadAllText($Path, $strictUtf8)
    $normalizedText = $textContent.Replace("`r`n", "`n").Replace("`r", "`n")
    $normalizedBytes = $strictUtf8.GetBytes($normalizedText)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString(
            $sha256.ComputeHash($normalizedBytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-FullPath {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assemblyFullPath = Get-FullPath -Path $AssemblyPath -BasePath $repoRoot
$archiveFullPath = Get-FullPath -Path $ArchivePath -BasePath $repoRoot

foreach ($requiredPath in @($assemblyFullPath, $archiveFullPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required release artifact is missing: $requiredPath"
    }
}

$manifestPath = Join-Path $repoRoot 'manifest.json'
$changelogPath = Join-Path $repoRoot 'changelog.txt'
$readmePath = Join-Path $repoRoot 'readme.txt'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-Equal -Label 'manifest version' -Actual $manifest.version -Expected $Version
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

$expectedFiles = @(
    [pscustomobject] @{ Source = $manifestPath; Archive = 'GroundRoads/manifest.json'; NormalizeText = $true }
    [pscustomobject] @{ Source = $changelogPath; Archive = 'GroundRoads/changelog.txt'; NormalizeText = $true }
    [pscustomobject] @{ Source = $readmePath; Archive = 'GroundRoads/readme.txt'; NormalizeText = $true }
    [pscustomobject] @{ Source = $assemblyFullPath; Archive = 'GroundRoads/GroundRoads.dll'; NormalizeText = $false }
)

$languageFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'lang') -Filter '*.json' -File | Sort-Object Name)
Assert-Equal -Label 'language catalog count' -Actual $languageFiles.Count -Expected 22
foreach ($languageFile in $languageFiles) {
    Get-Content -LiteralPath $languageFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json | Out-Null
    $expectedFiles += [pscustomobject] @{
        Source = $languageFile.FullName
        Archive = "GroundRoads/lang/$($languageFile.Name)"
        NormalizeText = $true
    }
}
$expectedFiles = @($expectedFiles | Sort-Object Archive)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$archiveStream = [System.IO.File]::OpenRead($archiveFullPath)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($archiveStream, [System.IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        $entries = @($archive.Entries)
        Assert-Equal -Label 'archive entry count' -Actual $entries.Count -Expected $expectedFiles.Count
        Assert-Equal -Label 'archive entry list' -Actual (($entries | ForEach-Object FullName) -join "`n") -Expected (($expectedFiles | ForEach-Object Archive) -join "`n")

        foreach ($entry in $entries) {
            if (-not $entry.FullName.StartsWith('GroundRoads/', [System.StringComparison]::Ordinal) -or
                $entry.FullName.StartsWith('/') -or
                $entry.FullName.Contains('\') -or
                @($entry.FullName.Split('/')) -contains '..') {
                throw "Unsafe archive path: $($entry.FullName)"
            }
            if ($entry.LastWriteTime.DateTime -ne [datetime]::new(2000, 1, 1, 0, 0, 0)) {
                throw "Unexpected timestamp on '$($entry.FullName)': $($entry.LastWriteTime)."
            }
        }

        foreach ($expectedFile in $expectedFiles) {
            $entry = $archive.GetEntry($expectedFile.Archive)
            if ($null -eq $entry) {
                throw "Archive entry is missing: $($expectedFile.Archive)"
            }

            $entryStream = $entry.Open()
            try {
                $archiveHash = Get-StreamSha256 -Stream $entryStream
            }
            finally {
                $entryStream.Dispose()
            }
            $sourceHash = if ($expectedFile.NormalizeText) {
                Get-NormalizedTextSha256 -Path $expectedFile.Source
            }
            else {
                (Get-FileHash -LiteralPath $expectedFile.Source -Algorithm SHA256).Hash
            }
            Assert-Equal -Label "content hash for $($expectedFile.Archive)" -Actual $archiveHash -Expected $sourceHash
        }

        if ($null -ne $archive.GetEntry('GroundRoads/MultiLangLib.dll')) {
            throw 'MultiLangLib.dll must remain an external dependency and must not be bundled.'
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $archiveStream.Dispose()
}

$archiveHash = (Get-FileHash -LiteralPath $archiveFullPath -Algorithm SHA256).Hash
Write-Host "Release verification passed: $($expectedFiles.Count) entries"
Write-Host "SHA256: $archiveHash"
