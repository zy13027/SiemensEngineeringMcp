[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Tia21ApiDirectory,
    [string]$Tia211ApiDirectory,
    [string]$PlcsimApiPath = 'C:\Program Files (x86)\Common Files\Siemens\PLCSIMADV\API\9.0\Siemens.Simatic.Simulation.Runtime.Api.x64.dll',
    [string]$PackageSource,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$defaultBundle = Join-Path $root "artifacts\$Configuration\win-x64"
$useCustomOutput = -not [string]::IsNullOrWhiteSpace($OutputDirectory)
$staging = $null
$failedStep = 'Initialize'

function Resolve-TiaApiDirectory([string]$RelativePath) {
    $candidates = @(
        ([IO.Path]::Combine($env:ProgramFiles, $RelativePath)),
        ([IO.Path]::Combine('F:\Program Files', $RelativePath)),
        ([IO.Path]::Combine('D:\Program Files', $RelativePath)),
        ([IO.Path]::Combine('E:\Program Files', $RelativePath))
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath ([IO.Path]::Combine($candidate, 'Siemens.Engineering.Base.dll')) -PathType Leaf) {
            return $candidate
        }
    }

    return $candidates[0]
}

function Build-Project([string]$Project, [string[]]$BuildProperties) {
    $arguments = @('build', (Join-Path $root "src\$Project\$Project.csproj"), '-c', $Configuration)
    if ($PackageSource) {
        $arguments += @('--source', $PackageSource)
    }
    & dotnet @arguments @BuildProperties
    if ($LASTEXITCODE -ne 0) {
        throw "$Project build failed with exit code $LASTEXITCODE."
    }
}

function Copy-Output([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $root $Source) -File |
        Copy-Item -Destination $Destination -Force
}

function Assert-Bundle([string]$BundleRoot) {
    $required = @(
        (Join-Path $BundleRoot 'SiemensEngineeringMcp.exe'),
        (Join-Path $BundleRoot 'workers\Engineering\21.0\Engineering.exe'),
        (Join-Path $BundleRoot 'workers\Engineering\21.1\Engineering.exe'),
        (Join-Path $BundleRoot 'workers\Simulation\Simulation.exe'),
        (Join-Path $BundleRoot 'workers\Runtime\Runtime.exe')
    )
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($missing.Count -gt 0) {
        throw ("Bundle is missing required executables:`n - " + ($missing -join "`n - "))
    }
}

if (-not $Tia21ApiDirectory) {
    $Tia21ApiDirectory = Resolve-TiaApiDirectory 'Siemens\Automation\Portal V21\PublicAPI\V21\net48'
}
if (-not $Tia211ApiDirectory) {
    $Tia211ApiDirectory = Resolve-TiaApiDirectory 'Siemens\Automation\Portal V21.1\PublicAPI\V21.1\net48'
}

if ($useCustomOutput) {
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $destination) {
        throw "OutputDirectory already exists and was rejected before copying: $destination"
    }

    $parent = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        throw "OutputDirectory parent does not exist: $parent"
    }

    $staging = Join-Path $parent ("{0}.staging-{1}" -f (Split-Path -Leaf $destination), [guid]::NewGuid().ToString('N'))
    $bundle = $staging
}
else {
    $destination = $defaultBundle
    $bundle = $defaultBundle
}

try {
    $failedStep = 'Host'
    Build-Project Host

    $failedStep = 'Engineering-21.0'
    Build-Project Engineering @('-p:TiaApiVersion=21.0', "-p:TiaApiDirectory=$Tia21ApiDirectory")

    $failedStep = 'Engineering-21.1'
    Build-Project Engineering @('-p:TiaApiVersion=21.1', "-p:TiaApiDirectory=$Tia211ApiDirectory")

    $failedStep = 'Simulation'
    Build-Project Simulation @("-p:PlcsimApiPath=$PlcsimApiPath")

    $failedStep = 'Runtime'
    Build-Project Runtime

    if ($useCustomOutput) {
        New-Item -ItemType Directory -Force -Path $staging | Out-Null
    }

    $failedStep = 'Copy-Host'
    Copy-Output "src\Host\bin\$Configuration\net10.0" $bundle

    $failedStep = 'Copy-Engineering-21.0'
    Copy-Output "src\Engineering\bin\$Configuration\21.0\net48" (Join-Path $bundle 'workers\Engineering\21.0')

    $failedStep = 'Copy-Engineering-21.1'
    Copy-Output "src\Engineering\bin\$Configuration\21.1\net48" (Join-Path $bundle 'workers\Engineering\21.1')

    $failedStep = 'Copy-Simulation'
    Copy-Output "src\Simulation\bin\$Configuration\net10.0" (Join-Path $bundle 'workers\Simulation')

    $failedStep = 'Copy-Runtime'
    Copy-Output "src\Runtime\bin\$Configuration\net10.0" (Join-Path $bundle 'workers\Runtime')

    $failedStep = 'Verify-Bundle'
    Assert-Bundle $bundle

    if ($useCustomOutput) {
        $failedStep = 'Publish'
        Move-Item -LiteralPath $staging -Destination $destination
        $staging = $null
        $bundle = $destination
    }
}
catch {
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw "Build-Workstation failed at step '$failedStep': $($_.Exception.Message)"
}

Write-Output (Join-Path $bundle 'SiemensEngineeringMcp.exe')
