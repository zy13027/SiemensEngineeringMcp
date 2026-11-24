[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Tia21ApiDirectory = 'F:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48',
    [string]$Tia211ApiDirectory = 'F:\Program Files\Siemens\Automation\Portal V21.1\PublicAPI\V21.1\net48',
    [string]$PlcsimApiPath = 'C:\Program Files (x86)\Common Files\Siemens\PLCSIMADV\API\9.0\Siemens.Simatic.Simulation.Runtime.Api.x64.dll',
    [string]$PackageSource
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bundle = Join-Path $root "artifacts\$Configuration\win-x64"

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

Build-Project Host
Build-Project Engineering @('-p:TiaApiVersion=21.0', "-p:TiaApiDirectory=$Tia21ApiDirectory")
Build-Project Engineering @('-p:TiaApiVersion=21.1', "-p:TiaApiDirectory=$Tia211ApiDirectory")
Build-Project Simulation @("-p:PlcsimApiPath=$PlcsimApiPath")
Build-Project Runtime

Copy-Output "src\Host\bin\$Configuration\net10.0" $bundle
Copy-Output "src\Engineering\bin\$Configuration\21.0\net48" (Join-Path $bundle 'workers\Engineering\21.0')
Copy-Output "src\Engineering\bin\$Configuration\21.1\net48" (Join-Path $bundle 'workers\Engineering\21.1')
Copy-Output "src\Simulation\bin\$Configuration\net10.0" (Join-Path $bundle 'workers\Simulation')
Copy-Output "src\Runtime\bin\$Configuration\net10.0" (Join-Path $bundle 'workers\Runtime')
Write-Output (Join-Path $bundle 'SiemensEngineeringMcp.exe')
