#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Rebuilds and redeploys the Second Brain MCP server, preserving config and data.
#>

$ErrorActionPreference = "Stop"

$ScriptDir   = $PSScriptRoot
$RepoRoot    = Split-Path $ScriptDir -Parent
$McpProj     = Join-Path (Join-Path (Join-Path $RepoRoot "src") "SecondBrain.Mcp") "SecondBrain.Mcp.csproj"
$BuilderProj = Join-Path (Join-Path (Join-Path $RepoRoot "src") "SecondBrain.IndexBuilder") "SecondBrain.IndexBuilder.csproj"
$MinerProj   = Join-Path (Join-Path (Join-Path $RepoRoot "src") "SecondBrain.AliasMiner") "SecondBrain.AliasMiner.csproj"
$InstallDir  = Join-Path $env:LOCALAPPDATA "SecondBrainMcpServer"
$ConfigFile  = Join-Path $InstallDir "mcp_config.json"

if (-not (Test-Path $ConfigFile)) {
    Write-Error "Install directory not found. Run install.ps1 first."
    exit 1
}

$config = Get-Content $ConfigFile -Raw | ConvertFrom-Json
$ServiceName = $config.service_name

Write-Host "Stopping service '$ServiceName'..."
net stop $ServiceName 2>$null

Write-Host "Building MCP server..."
dotnet publish $McpProj --configuration Release --runtime win-x64 --self-contained false --output $InstallDir
if ($LASTEXITCODE -ne 0) { Write-Error "MCP server build failed."; exit 1 }

Write-Host "Building IndexBuilder..."
dotnet publish $BuilderProj --configuration Release --runtime win-x64 --self-contained false --output $InstallDir
if ($LASTEXITCODE -ne 0) { Write-Error "IndexBuilder build failed."; exit 1 }

Write-Host "Building AliasMiner..."
dotnet publish $MinerProj --configuration Release --runtime win-x64 --self-contained false --output $InstallDir
if ($LASTEXITCODE -ne 0) { Write-Error "AliasMiner build failed."; exit 1 }

# Refresh pricing.json from repo (reference data, always overwrite)
$RepoPricingJson = Join-Path $RepoRoot "config" "pricing.json"
$InstallConfigDir = Join-Path $InstallDir "config"
if (-not (Test-Path $InstallConfigDir)) { New-Item -ItemType Directory -Path $InstallConfigDir -Force | Out-Null }
Copy-Item $RepoPricingJson (Join-Path $InstallConfigDir "pricing.json") -Force
Write-Host "pricing.json refreshed"

# Promote system_prompt.md from repo (the tuned production prompt; always overwrite)
$RepoPromptsLocal = Join-Path $RepoRoot "Prompts.local"
$RepoSystemPrompt = Join-Path $RepoPromptsLocal "system_prompt.md"
if (Test-Path $RepoSystemPrompt) {
    $InstallPromptsLocal = Join-Path $InstallDir "Prompts.local"
    if (-not (Test-Path $InstallPromptsLocal)) { New-Item -ItemType Directory -Path $InstallPromptsLocal -Force | Out-Null }
    Copy-Item $RepoSystemPrompt (Join-Path $InstallPromptsLocal "system_prompt.md") -Force
    Write-Host "system_prompt.md promoted from repo"
} else {
    Write-Host "No repo system_prompt.md found; deployed prompt unchanged"
}

Write-Host "Starting service '$ServiceName'..."
net start $ServiceName

Write-Host "Update complete."
