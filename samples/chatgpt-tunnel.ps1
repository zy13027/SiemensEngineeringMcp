[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$TunnelId,
    [string]$TunnelProfile = 'siemens-engineering',
    [string]$WorkstationProfile,
    [string]$ServerExecutable
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$ServerExecutable) {
    $ServerExecutable = Join-Path $root 'artifacts\Release\win-x64\SiemensEngineeringMcp.exe'
}
if (!$WorkstationProfile) {
    $WorkstationProfile = Join-Path $PSScriptRoot 'workstation.local.json'
}

$server = (Get-Item -LiteralPath $ServerExecutable).FullName.Replace('\', '/')
$WorkstationProfile = (Get-Item -LiteralPath $WorkstationProfile).FullName.Replace('\', '/')
$mcpCommand = '"{0}" --profile "{1}"' -f $server, $WorkstationProfile
& tunnel-client init --sample sample_mcp_stdio_local --profile $TunnelProfile --tunnel-id $TunnelId --mcp-command $mcpCommand
if ($LASTEXITCODE -ne 0) {
    throw "tunnel-client init failed with exit code $LASTEXITCODE."
}
Write-Output "tunnel-client doctor --profile $TunnelProfile --explain"
Write-Output "tunnel-client run --profile $TunnelProfile"
