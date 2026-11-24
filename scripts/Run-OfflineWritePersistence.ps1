[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$HostExecutable,

    [Parameter(Mandatory)]
    [string]$Profile,

    [Parameter(Mandatory)]
    [string]$RunDirectory,

    [ValidateSet('21.0', '21.1', 'All')]
    [string]$Version = 'All',

    [int]$Revision,

    [string]$HmiTag,

    [string]$Python = 'python'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$runner = Join-Path $PSScriptRoot 'offline_write_persistence.py'
$hostPath = [IO.Path]::GetFullPath($HostExecutable)
$profilePath = [IO.Path]::GetFullPath($Profile)
$runPath = [IO.Path]::GetFullPath($RunDirectory)

if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) {
    throw "Host executable not found: $hostPath"
}
if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) {
    throw "Inspection profile not found: $profilePath"
}
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw "Python runner not found: $runner"
}

New-Item -ItemType Directory -Force -Path $runPath | Out-Null

$defaults = @{
    '21.0' = @{ Revision = 21025; HmiTag = 'McpVerify_21025' }
    '21.1' = @{ Revision = 21125; HmiTag = 'McpVerify_21125' }
}

$versions = if ($Version -eq 'All') { @('21.0', '21.1') } else { @($Version) }
$reports = @()

foreach ($entry in $versions) {
    $args = @(
        $runner,
        '--host', $hostPath,
        '--profile', $profilePath,
        '--run-directory', $runPath,
        '--version', $entry
    )

    $revisionValue = if ($PSBoundParameters.ContainsKey('Revision')) { $Revision } else { $defaults[$entry].Revision }
    $tagValue = if ($PSBoundParameters.ContainsKey('HmiTag')) { $HmiTag } else { $defaults[$entry].HmiTag }
    $args += @('--revision', "$revisionValue", '--hmi-tag', $tagValue)

    Write-Host "Running offline write persistence for TIA $entry (revision $revisionValue, tag $tagValue)..."
    & $Python @args
    if ($LASTEXITCODE -ne 0) {
        throw "Offline write persistence failed for TIA $entry (exit $LASTEXITCODE)."
    }

    $folder = Join-Path $runPath ("V" + $entry.Replace('.', ''))
    $report = Join-Path $folder 'write-persistence-report.json'
    if (-not (Test-Path -LiteralPath $report -PathType Leaf)) {
        throw "Expected report was not written: $report"
    }
    $reports += $report
}

$reports
