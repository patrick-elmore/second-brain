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
$McpProj     = Join-Path (Join-Path (Join-Path $ScriptDir "src") "SecondBrain.Mcp") "SecondBrain.Mcp.csproj"
$BuilderProj = Join-Path (Join-Path (Join-Path $ScriptDir "src") "SecondBrain.IndexBuilder") "SecondBrain.IndexBuilder.csproj"
$ConfigDir   = Join-Path $ScriptDir "config"
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

# Copy config template on first install only
$ConfigFile = Join-Path $InstallDir "mcp_config.json"
if (-not (Test-Path $ConfigFile)) {
    Copy-Item (Join-Path $ConfigDir "mcp_config.json") $ConfigFile
    Write-Host "Config template copied to $ConfigFile"
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

# Copy sources.json if it exists in the repo root config
$RepoSourcesJson = Join-Path (Split-Path $ScriptDir -Parent) "config" "sources.json"
$InstallSourcesDir = Join-Path $InstallDir "config"
if (-not (Test-Path $InstallSourcesDir)) {
    New-Item -ItemType Directory -Path $InstallSourcesDir -Force | Out-Null
}
$InstallSourcesJson = Join-Path $InstallSourcesDir "sources.json"
if ((Test-Path $RepoSourcesJson) -and (-not (Test-Path $InstallSourcesJson))) {
    Copy-Item $RepoSourcesJson $InstallSourcesJson
    Write-Host "sources.json copied to $InstallSourcesJson"
}

# Read service name from config
$config      = Get-Content $ConfigFile -Raw | ConvertFrom-Json
$ServiceName = $config.service_name
$InstalledExe = Join-Path $InstallDir "SecondBrain.Mcp.exe"

# Register/replace service
$existingService = sc.exe query $ServiceName 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "Service '$ServiceName' already exists. Stopping and removing..."
    net stop $ServiceName 2>$null
    sc.exe delete $ServiceName
    Start-Sleep -Seconds 2
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
