#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and removes the Second Brain MCP Windows service and its install directory.
#>

$ErrorActionPreference = "Stop"

$InstallDir = Join-Path $env:LOCALAPPDATA "SecondBrainMcpServer"
$ConfigFile = Join-Path $InstallDir "mcp_config.json"

$ServiceName = "SecondBrainHttpMcp"
if (Test-Path $ConfigFile) {
    $config = Get-Content $ConfigFile -Raw | ConvertFrom-Json
    $ServiceName = $config.service_name
}

$existingService = sc.exe query $ServiceName 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "Stopping service '$ServiceName'..."
    net stop $ServiceName 2>$null
    sc.exe delete $ServiceName
    Write-Host "Service removed."
} else {
    Write-Host "Service '$ServiceName' not found."
}

if (Test-Path $InstallDir) {
    $confirm = Read-Host "Delete install directory '$InstallDir'? This removes the index and session state. (y/N)"
    if ($confirm -eq 'y') {
        Remove-Item $InstallDir -Recurse -Force
        Write-Host "Install directory removed."
    } else {
        Write-Host "Install directory preserved at $InstallDir"
    }
}

Write-Host "Uninstall complete."
