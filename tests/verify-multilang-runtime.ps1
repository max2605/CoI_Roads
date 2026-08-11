[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [Parameter(Mandatory = $true)]
    [string]$CoiRoot,
    [string]$MultiLangRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$CoiRoot = [System.IO.Path]::GetFullPath($CoiRoot)
if ([string]::IsNullOrWhiteSpace($MultiLangRoot)) {
    $MultiLangRoot = Join-Path (Split-Path $RepositoryRoot -Parent) `
        'MultiLangLib'
}
$MultiLangRoot = [System.IO.Path]::GetFullPath($MultiLangRoot)

function Assert-Condition {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "GroundRoads MultiLang verification failed: $Message"
    }
}

function Read-TranslationCatalog {
    param([string]$Path)

    Assert-Condition `
        (Test-Path -LiteralPath $Path -PathType Leaf) `
        "translation catalog is missing: $Path"
    $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
    $json = $strictUtf8.GetString(
        [System.IO.File]::ReadAllBytes($Path))
    $catalog = $json | ConvertFrom-Json
    Assert-Condition `
        ($null -ne $catalog -and
         $catalog -is [System.Management.Automation.PSCustomObject]) `
        "translation catalog is not a JSON object: $Path"
    return $catalog
}

$manifestPath = Join-Path $RepositoryRoot 'manifest.json'
$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath |
    ConvertFrom-Json
Assert-Condition `
    (@($manifest.mod_dependencies) -contains 'MultiLangLib>=0.1.0') `
    'manifest no longer requires MultiLangLib 0.1.0 or newer.'

$projectPath = Join-Path $RepositoryRoot 'source\GroundRoads.csproj'
[xml]$project = Get-Content -Raw -Encoding UTF8 -LiteralPath $projectPath
$multiLangReference = @(
    $project.SelectNodes('/Project/ItemGroup/Reference') |
        Where-Object { [string]$_.Include -eq 'MultiLangLib' }
) | Select-Object -First 1
Assert-Condition `
    ($null -ne $multiLangReference -and
     [string]$multiLangReference.HintPath -eq
        '$(APPDATA)\Captain of Industry\Mods\MultiLangLib\MultiLangLib.dll' -and
     [string]$multiLangReference.Private -eq 'false') `
    'project must reference the shared, non-copied MultiLangLib assembly.'
$catalogCopyItem = @(
    $project.SelectNodes('/Project/ItemGroup/None') |
        Where-Object {
            [string]$_.Include -eq '..\lang\**\*.json' -and
            [string]$_.CopyToOutputDirectory -eq 'PreserveNewest'
        }
) | Select-Object -First 1
Assert-Condition `
    ($null -ne $catalogCopyItem) `
    'build no longer copies language catalogs to its output.'

$modSource = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $RepositoryRoot 'source\GroundRoadsMod.cs')
Assert-Condition `
    ($modSource -match
        'Lang\.RegisterMod\(manifest\.Id,\s*manifest\.RootDirectoryPath\)') `
    'mod no longer registers its root directory with MultiLangLib.'

$managedRoot = Join-Path $CoiRoot 'Captain of Industry_Data\Managed'
foreach ($assemblyName in @('Mafi.dll', 'Mafi.Core.dll', 'Mafi.Unity.dll')) {
    $assemblyPath = Join-Path $managedRoot $assemblyName
    Assert-Condition `
        (Test-Path -LiteralPath $assemblyPath -PathType Leaf) `
        "required game assembly is missing: $assemblyPath"
    [System.Reflection.Assembly]::LoadFrom($assemblyPath) | Out-Null
}

$multiLangAssemblyPath = Join-Path $MultiLangRoot 'MultiLangLib.dll'
Assert-Condition `
    (Test-Path -LiteralPath $multiLangAssemblyPath -PathType Leaf) `
    "installed MultiLangLib.dll is missing: $multiLangAssemblyPath"
[System.Reflection.Assembly]::LoadFrom($multiLangAssemblyPath) | Out-Null

$languageCatalogs = @(
    'ca',
    'cs',
    'de',
    'en',
    'es',
    'et',
    'fr',
    'hu',
    'it',
    'ja',
    'ko',
    'nb_NO',
    'nl',
    'pl',
    'pt',
    'pt_BR',
    'ru',
    'sv',
    'tr',
    'uk',
    'zh_Hans',
    'zh_Hant'
)

$langRoot = Join-Path $RepositoryRoot 'lang'
$englishCatalog = Read-TranslationCatalog (
    Join-Path $langRoot 'en.json')
$expectedKeys = @(
    $englishCatalog.PSObject.Properties.Name | Sort-Object)
Assert-Condition `
    ($expectedKeys.Count -eq 56) `
    "English reference catalog must contain exactly 56 keys, found $($expectedKeys.Count)."

$englishValues = @{}
foreach ($property in $englishCatalog.PSObject.Properties) {
    Assert-Condition `
        (-not [string]::IsNullOrWhiteSpace([string]$property.Value)) `
        "English translation '$($property.Name)' is empty."
    $englishValues[$property.Name] = [string]$property.Value
}

$sourceText = (
    Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'source') `
        -Filter '*.cs' |
        ForEach-Object {
            [System.IO.File]::ReadAllText($_.FullName)
        }) -join "`n"
$requiredKeys = @(
    [regex]::Matches(
        $sourceText,
        'GroundRoadTexts\.(?:Get|Localized)\(\s*"([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value })
$requiredKeys += @(
    'prototype.highway-segment.name',
    'prototype.highway-segment.description',
    'prototype.highway-segment-t3.name',
    'prototype.highway-segment-t3.description',
    'prototype.highway-segment-t4.name',
    'prototype.highway-segment-t4.description',
    'tool.highway.name',
    'tool.highway.description',
    'tool.highway-t3.name',
    'tool.highway-t3.description',
    'tool.highway-t4.name',
    'tool.highway-t4.description',
    'tool.t-intersection.name',
    'tool.t-intersection.description',
    'tool.cross-intersection.name',
    'tool.cross-intersection.description',
    'tool.roundabout.name',
    'tool.roundabout.description'
)
$requiredKeys = @($requiredKeys | Sort-Object -Unique)
Assert-Condition `
    (-not (Compare-Object $expectedKeys $requiredKeys)) `
    'English catalog keys no longer exactly match source lookups.'

$modsRoot = Split-Path $RepositoryRoot -Parent
foreach ($language in $languageCatalogs) {
    $catalogPath = Join-Path $langRoot ($language + '.json')
    $catalog = Read-TranslationCatalog $catalogPath
    $actualKeys = @($catalog.PSObject.Properties.Name | Sort-Object)
    Assert-Condition `
        ($actualKeys.Count -eq $expectedKeys.Count) `
        "$language has $($actualKeys.Count) keys; expected $($expectedKeys.Count)."
    Assert-Condition `
        (-not (Compare-Object $expectedKeys $actualKeys)) `
        "$language does not have the exact English key set."

    $values = @{}
    foreach ($property in $catalog.PSObject.Properties) {
        $value = [string]$property.Value
        Assert-Condition `
            (-not [string]::IsNullOrWhiteSpace($value)) `
            "$language translation '$($property.Name)' is empty."
        $values[$property.Name] = $value

        $expectedPlaceholders = @(
            [regex]::Matches(
                $englishValues[$property.Name],
                '\{([0-9]+)(?:[^}]*)\}') |
                ForEach-Object { $_.Groups[1].Value } |
                Sort-Object -Unique)
        $actualPlaceholders = @(
            [regex]::Matches(
                $value,
                '\{([0-9]+)(?:[^}]*)\}') |
                ForEach-Object { $_.Groups[1].Value } |
                Sort-Object -Unique)
        Assert-Condition `
            (-not (Compare-Object $expectedPlaceholders $actualPlaceholders)) `
            "$language translation '$($property.Name)' changed its placeholders."
    }

    $options = New-Object MultiLangLib.LangOptions -ArgumentList @(
        $MultiLangRoot,
        $language,
        'zz_missing_fallback',
        $modsRoot,
        $false,
        [System.Globalization.CultureInfo]::InvariantCulture,
        $null)
    [MultiLangLib.Lang]::Configure($options)
    [MultiLangLib.Lang]::RegisterMod('GroundRoads', $RepositoryRoot)

    foreach ($key in $expectedKeys) {
        $resolved = [MultiLangLib.Lang]::Get('GroundRoads', $key)
        Assert-Condition `
            ($resolved -eq $values[$key]) `
            "$language did not resolve '$key' from its own catalog."
        $localized = [MultiLangLib.Lang]::Localized(
            'GroundRoads',
            $key)
        Assert-Condition `
            ($localized.Value -eq $values[$key]) `
            "$language Localized('$key') differs from Get()."
    }
}

Write-Host (
    'GroundRoads MultiLang verification passed: all 22 catalogs contain ' +
    'the same 56 non-empty keys and resolve through the installed ' +
    'MultiLangLib without fallback.')
