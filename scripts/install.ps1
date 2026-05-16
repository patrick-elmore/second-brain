#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the Second Brain HTTP MCP server as a Windows service.

.DESCRIPTION
    Verifies .NET 10 prerequisites, builds the server from source, copies the
    published output to a local install directory, registers the Windows service,
    and configures the Claude Code MCP entry. Requires admin privileges and .NET 10 SDK.

    After installation, set ANTHROPIC_API_KEY in the service user's environment,
    then run: SecondBrain.IndexBuilder.exe <sources.json path> <fts.db path>
    before starting the service.
#>

$ErrorActionPreference = "Stop"

# Verify .NET 10 prerequisites
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Error "dotnet CLI not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
}

$sdks = dotnet --list-sdks 2>&1
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    Write-Error ".NET 10 SDK not found.`nInstall from https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
}

$runtimes = dotnet --list-runtimes 2>&1
if (-not ($runtimes | Where-Object { $_ -match 'Microsoft\.AspNetCore\.App 10\.' })) {
    Write-Error "ASP.NET Core 10 runtime not found.`nInstall from https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
}

Write-Host ".NET 10 SDK and ASP.NET Core runtime verified."

$ScriptDir   = $PSScriptRoot
$RepoRoot    = Split-Path $ScriptDir -Parent
$McpProj     = Join-Path (Join-Path (Join-Path $RepoRoot "src") "SecondBrain.Mcp") "SecondBrain.Mcp.csproj"
$BuilderProj = Join-Path (Join-Path (Join-Path $RepoRoot "src") "SecondBrain.IndexBuilder") "SecondBrain.IndexBuilder.csproj"
$ConfigDir   = Join-Path $RepoRoot "config"
$InstallDir  = Join-Path $env:LOCALAPPDATA "SecondBrainMcpServer"
$IndexDir    = Join-Path $InstallDir "index"

# Create directories
foreach ($dir in @($InstallDir, $IndexDir)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-Host "Created: $dir"
    }
}

# Build and publish MCP server
Write-Host "Building MCP server..."
dotnet publish $McpProj --configuration Release --runtime win-x64 --self-contained false --output $InstallDir
if ($LASTEXITCODE -ne 0) { Write-Error "MCP server build failed."; exit 1 }

# Build and publish IndexBuilder
Write-Host "Building IndexBuilder..."
dotnet publish $BuilderProj --configuration Release --runtime win-x64 --self-contained false --output $InstallDir
if ($LASTEXITCODE -ne 0) { Write-Error "IndexBuilder build failed."; exit 1 }

Write-Host "Build complete."

# Copy mcp_config.json on first install only — prefer real one from repo config,
# fall back to template. Subsequent installs preserve any local edits in the
# install dir so production tuning isn't clobbered.
$ConfigFile = Join-Path $InstallDir "mcp_config.json"
$RepoMcpConfig    = Join-Path $ConfigDir "mcp_config.json"
$RepoMcpTemplate  = Join-Path $ConfigDir "mcp_config-template.json"
if (-not (Test-Path $ConfigFile)) {
    if (Test-Path $RepoMcpConfig) {
        Copy-Item $RepoMcpConfig $ConfigFile
        Write-Host "mcp_config.json copied from repo to $ConfigFile"
    } elseif (Test-Path $RepoMcpTemplate) {
        Copy-Item $RepoMcpTemplate $ConfigFile
        Write-Host "mcp_config-template.json copied to $ConfigFile — review before starting the service"
    } else {
        Write-Error "No mcp_config.json or mcp_config-template.json found in $ConfigDir."
        exit 1
    }
} else {
    Write-Host "Existing config preserved at $ConfigFile"
}

# Copy pricing.json (always overwrite — this is reference data, not user config)
$InstallConfigDir = Join-Path $InstallDir "config"
if (-not (Test-Path $InstallConfigDir)) {
    New-Item -ItemType Directory -Path $InstallConfigDir -Force | Out-Null
}
Copy-Item (Join-Path $ConfigDir "pricing.json") (Join-Path $InstallConfigDir "pricing.json") -Force
Write-Host "pricing.json copied to install directory"

# Copy sources.json — prefer real one from repo config, fall back to template.
# The template gets copied as sources.json so the install dir always has a
# starter file the user can edit. Subsequent installs preserve any local edits.
$RepoSourcesJson    = Join-Path $ConfigDir "sources.json"
$RepoSourcesTemplate = Join-Path $ConfigDir "sources-template.json"
$InstallSourcesDir   = Join-Path $InstallDir "config"
if (-not (Test-Path $InstallSourcesDir)) {
    New-Item -ItemType Directory -Path $InstallSourcesDir -Force | Out-Null
}
$InstallSourcesJson = Join-Path $InstallSourcesDir "sources.json"
if (-not (Test-Path $InstallSourcesJson)) {
    if (Test-Path $RepoSourcesJson) {
        Copy-Item $RepoSourcesJson $InstallSourcesJson
        Write-Host "sources.json copied to $InstallSourcesJson"
    } elseif (Test-Path $RepoSourcesTemplate) {
        Copy-Item $RepoSourcesTemplate $InstallSourcesJson
        Write-Host "sources-template.json copied to $InstallSourcesJson — edit before building the index"
    } else {
        Write-Warning "No sources.json or sources-template.json found in $ConfigDir; skipping. You'll need to create $InstallSourcesJson manually."
    }
}

# Read service name from config
$config      = Get-Content $ConfigFile -Raw | ConvertFrom-Json
$ServiceName = $config.service_name
$InstalledExe = Join-Path $InstallDir "SecondBrain.Mcp.exe"

# Register/replace service
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host ""
    Write-Host "  '$ServiceName' is already installed." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Proceeding will:" -ForegroundColor Yellow
    Write-Host "    - Stop and remove the running service" -ForegroundColor Yellow
    Write-Host "    - Redeploy all binaries" -ForegroundColor Yellow
    Write-Host "    - Reset mcp_config.json (your configured paths and settings may be lost)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  If you only want to update the binaries, use update.ps1 instead." -ForegroundColor Cyan
    Write-Host "  update.ps1 preserves your mcp_config.json and does not reset configuration." -ForegroundColor Cyan
    Write-Host ""
    $confirm = Read-Host "  Type 'yes' to proceed with full reinstall, or press Enter to cancel"
    if ($confirm -ne 'yes') {
        Write-Host ""
        Write-Host "Cancelled. Run update.ps1 to rebuild and redeploy without touching your configuration." -ForegroundColor Green
        exit 0
    }
    Write-Host ""

    if ($existingService.Status -eq 'Running') {
        Write-Host "Stopping existing service '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Removing existing service '$ServiceName'..."
    sc.exe delete $ServiceName | Out-Null

    $timeout = 15
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and $timeout -gt 0) {
        Start-Sleep -Seconds 1
        $timeout--
    }
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Error "Service '$ServiceName' is still present after delete (may be marked for deletion). Close any open handles or reboot, then re-run."
        exit 1
    }
}

Write-Host "Registering service '$ServiceName'..."
sc.exe create $ServiceName `
    binPath= "`"$InstalledExe`"" `
    DisplayName= "$($config.display_name)" `
    start= auto

if ($LASTEXITCODE -ne 0) { Write-Error "sc.exe create failed."; exit 1 }
sc.exe description $ServiceName "$($config.description)"

# Configure Claude Code MCP entry
$port   = $config.http_port
$mcpUrl = "http://localhost:${port}/mcp"
$mcpName = "second-brain"

$claudeJsonPaths = @()
$windowsPath = Join-Path $env:USERPROFILE ".claude.json"
$claudeJsonPaths += $windowsPath

try {
    $wslHome = (wsl.exe -e bash -c 'echo $HOME' 2>$null)?.Trim()
    if ($wslHome) {
        $wslDistro = (wsl.exe -l -q 2>$null | ForEach-Object { $_ -replace '\x00','' } | Where-Object { $_ -match '\S' } | Select-Object -First 1).Trim()
        if ($wslDistro) {
            $wslPath = "\\wsl$\$wslDistro$($wslHome -replace '/', '\')\.claude.json"
            if ($wslPath -ne $windowsPath) { $claudeJsonPaths += $wslPath }
        }
    }
} catch { }

$foundAny = $false
foreach ($claudeJsonPath in $claudeJsonPaths) {
    if (Test-Path $claudeJsonPath) {
        $claudeJson = Get-Content $claudeJsonPath -Raw | ConvertFrom-Json
        if (-not $claudeJson.mcpServers) {
            $claudeJson | Add-Member -NotePropertyName "mcpServers" -NotePropertyValue @{}
        }
        if (-not $claudeJson.mcpServers.$mcpName) {
            $claudeJson.mcpServers | Add-Member -NotePropertyName $mcpName -NotePropertyValue @{
                type = "http"; url = $mcpUrl
            }
            $claudeJson | ConvertTo-Json -Depth 10 | Set-Content $claudeJsonPath -Encoding UTF8
            Write-Host "Added '$mcpName' MCP entry to $claudeJsonPath"
        } else {
            Write-Host "'$mcpName' MCP entry already exists in $claudeJsonPath"
        }
        $foundAny = $true
    }
}

if (-not $foundAny) {
    Write-Host "No .claude.json found. Add manually: $mcpName`: { type: 'http', url: '$mcpUrl' }"
}

# Post-install instructions
$apiKey = [System.Environment]::GetEnvironmentVariable("ANTHROPIC_API_KEY", "User")
if (-not $apiKey) {
    Write-Host ""
    Write-Warning "ANTHROPIC_API_KEY not set in user environment."
    Write-Warning "Set it before starting the service: [System.Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY', 'your-key', 'User')"
    Write-Host ""
    Write-Host "Service registered but NOT started. After setting the API key:"
    Write-Host "  1. Run the index builder: & '$InstallDir\SecondBrain.IndexBuilder.exe' '$InstallSourcesJson' '$IndexDir\fts.db'"
    Write-Host "  2. Start the service: net start $ServiceName"
} else {
    Write-Host ""
    Write-Host "ANTHROPIC_API_KEY found."
    Write-Host "Next step: build the index before starting the service:"
    Write-Host "  & '$InstallDir\SecondBrain.IndexBuilder.exe' '$InstallSourcesJson' '$IndexDir\fts.db'"
    Write-Host "Then start: net start $ServiceName"
}

Write-Host ""
Write-Host "Installation complete. Install directory: $InstallDir"
