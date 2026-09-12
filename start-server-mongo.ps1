[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$composeFile = Join-Path $projectRoot "docker.yml"
$envFile = Join-Path $projectRoot ".env"
$serverProject = Join-Path $projectRoot "src\AIBot.Server\AIBot.Server.csproj"
$containerName = "ai-npc-mongo"

function Read-DotEnv {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    # 显式按 UTF-8 读取：Windows PowerShell 5.1 默认按 ANSI(GBK) 解析无 BOM 的 UTF-8 文件时，
    # 中文注释行末会吞掉换行，导致紧随其后的 KEY=VALUE 被并入注释而丢失。
    foreach ($rawLine in Get-Content -LiteralPath $Path -Encoding UTF8) {
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
$database = Get-RequiredValue -Values $settings -Name "AIBOT_MONGO_DATABASE"
$user = Get-RequiredValue -Values $settings -Name "AIBOT_MONGO_USER"
$password = Get-RequiredValue -Values $settings -Name "AIBOT_MONGO_PASSWORD"
$portText = Get-RequiredValue -Values $settings -Name "AIBOT_MONGO_PORT"

# 可选：从根目录 .env 注入模型 API Key。NPC 配置中的非空 apiKey 仍具有更高优先级。
if ($settings.ContainsKey("AIBOT_LLM_KEY") -and -not [string]::IsNullOrWhiteSpace([string]$settings["AIBOT_LLM_KEY"])) {
    $env:AIBOT_LLM_KEY = [string]$settings["AIBOT_LLM_KEY"]
}

$port = 0
if (-not [int]::TryParse($portText, [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
    throw "AIBOT_MONGO_PORT must be a valid TCP port: $portText"
}

Push-Location $projectRoot
try {
    Write-Host "Starting Docker MongoDB..."
    & docker compose --env-file $envFile -f $composeFile up -d mongo
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose failed with exit code $LASTEXITCODE"
    }

    Write-Host "Waiting for MongoDB health check..."
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
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
            & docker compose --env-file $envFile -f $composeFile logs --tail 80 mongo
            throw "MongoDB container state is '$health'."
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($health -ne "healthy" -and $health -ne "running") {
        & docker compose --env-file $envFile -f $composeFile logs --tail 80 mongo
        throw "Timed out waiting for MongoDB to become healthy."
    }

    # 密码可能含 @ : / ? # 等字符，必须 URL 编码后再拼进 URI 的 userinfo 段。
    $encodedUser = [Uri]::EscapeDataString($user)
    $encodedPassword = [Uri]::EscapeDataString($password)
    $env:AIBOT_STORAGE_PROVIDER = "Mongo"
    $env:AIBOT_MONGO_CONNECTION_STRING = "mongodb://${encodedUser}:${encodedPassword}@127.0.0.1:${port}/?authSource=admin"
    $env:AIBOT_MONGO_DATABASE = $database
    # Mongo 模式必须自动初始化（Validate 强制），未显式配置时默认开启
    if ($settings.ContainsKey("AIBOT_MONGO_AUTOMIGRATE")) {
        $env:AIBOT_MONGO_AUTOMIGRATE = [string]$settings["AIBOT_MONGO_AUTOMIGRATE"]
    }
    else {
        $env:AIBOT_MONGO_AUTOMIGRATE = "true"
    }

    Write-Host "MongoDB is healthy at 127.0.0.1:$port (database=$database, user=$user)."
    Write-Host "Starting AIBot.Server in Mongo mode. Press Ctrl+C to stop the Server."
    & dotnet run --project $serverProject
    if ($LASTEXITCODE -ne 0) {
        throw "AIBot.Server exited with code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}
