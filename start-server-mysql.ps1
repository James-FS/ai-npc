[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$composeFile = Join-Path $projectRoot "docker.yml"
$envFile = Join-Path $projectRoot ".env"
$serverProject = Join-Path $projectRoot "src\AIBot.Server\AIBot.Server.csproj"
$containerName = "ai-npc-mysql"

function Read-DotEnv {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    foreach ($rawLine in Get-Content -LiteralPath $Path) {
        $line = $rawLine.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith("#")) {
            continue
        }

        $separator = $line.IndexOf("=")
        if ($separator -le 0) {
            continue
        }

        $name = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1).Trim()
        if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
            throw "Invalid environment variable name in .env: $name"
        }

        if ($value.Length -ge 2) {
            $first = $value[0]
            $last = $value[$value.Length - 1]
            if (($first -eq '"' -and $last -eq '"') -or ($first -eq "'" -and $last -eq "'")) {
                $value = $value.Substring(1, $value.Length - 2)
            }
        }

        $values[$name] = $value
    }

    return $values
}

function Get-RequiredValue {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Values,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not $Values.ContainsKey($Name) -or [string]::IsNullOrWhiteSpace($Values[$Name])) {
        throw "Missing required setting '$Name' in $envFile"
    }
    return [string]$Values[$Name]
}

if (-not (Test-Path -LiteralPath $composeFile)) {
    throw "docker.yml was not found: $composeFile"
}
if (-not (Test-Path -LiteralPath $serverProject)) {
    throw "AIBot.Server project was not found: $serverProject"
}
if (-not (Test-Path -LiteralPath $envFile)) {
    throw ".env was not found. Run: Copy-Item .env.example .env"
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker CLI was not found. Start Docker Desktop and try again."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK was not found."
}

$settings = Read-DotEnv -Path $envFile
$database = Get-RequiredValue -Values $settings -Name "AIBOT_MYSQL_DATABASE"
$user = Get-RequiredValue -Values $settings -Name "AIBOT_MYSQL_USER"
$password = Get-RequiredValue -Values $settings -Name "AIBOT_MYSQL_PASSWORD"
$portText = Get-RequiredValue -Values $settings -Name "AIBOT_MYSQL_PORT"

# 可选：从根目录 .env 注入模型 API Key。NPC 配置中的非空 apiKey 仍具有更高优先级。
if ($settings.ContainsKey("AIBOT_LLM_KEY") -and -not [string]::IsNullOrWhiteSpace([string]$settings["AIBOT_LLM_KEY"])) {
    $env:AIBOT_LLM_KEY = [string]$settings["AIBOT_LLM_KEY"]
}

$port = 0
if (-not [int]::TryParse($portText, [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
    throw "AIBOT_MYSQL_PORT must be a valid TCP port: $portText"
}

Push-Location $projectRoot
try {
    Write-Host "Starting Docker MySQL..."
    & docker compose --env-file $envFile -f $composeFile up -d mysql
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose failed with exit code $LASTEXITCODE"
    }

    Write-Host "Waiting for MySQL health check..."
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $health = ""
    do {
        $healthOutput = & docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' $containerName 2>$null
        if ($LASTEXITCODE -eq 0 -and $null -ne $healthOutput) {
            $health = ([string]$healthOutput).Trim()
        }
        if ($health -eq "healthy" -or $health -eq "running") {
            break
        }
        if ($health -eq "unhealthy" -or $health -eq "exited" -or $health -eq "dead") {
            & docker compose --env-file $envFile -f $composeFile logs --tail 80 mysql
            throw "MySQL container state is '$health'."
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($health -ne "healthy" -and $health -ne "running") {
        & docker compose --env-file $envFile -f $composeFile logs --tail 80 mysql
        throw "Timed out waiting for MySQL to become healthy."
    }

    $env:AIBOT_STORAGE_PROVIDER = "MySql"
    $env:AIBOT_MYSQL_CONNECTION_STRING = "Server=127.0.0.1;Port=$port;Database=$database;User ID=$user;Password=$password;SslMode=None;AllowPublicKeyRetrieval=True;"
    # 可选：AIBOT_MYSQL_AUTOMIGRATE=true 时启动自动按 schema_migrations 版本补齐缺失表
    if ($settings.ContainsKey("AIBOT_MYSQL_AUTOMIGRATE")) {
        $env:AIBOT_MYSQL_AUTOMIGRATE = [string]$settings["AIBOT_MYSQL_AUTOMIGRATE"]
    }

    Write-Host "MySQL is healthy at 127.0.0.1:$port (database=$database, user=$user)."
    Write-Host "Starting AIBot.Server in MySql mode. Press Ctrl+C to stop the Server."
    & dotnet run --project $serverProject
    if ($LASTEXITCODE -ne 0) {
        throw "AIBot.Server exited with code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

