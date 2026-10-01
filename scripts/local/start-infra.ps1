<#
.SYNOPSIS
    Starts the local stand-ins for DataHub's infrastructure, without Docker:
      - SeaweedFS  S3 API on http://localhost:9000 (bucket datahub-uploads, keys minioadmin/minioadmin)
      - RabbitMQ   AMQP on localhost:5672, management UI on http://localhost:15672 (guest/guest)
      - Mailpit    SMTP on localhost:1025, web inbox on http://localhost:8025
    These match appsettings.Local.json. Binaries live in .local\tools, data and logs in .local\infra.
    Stop everything with scripts\local\stop-infra.ps1.
#>
$ErrorActionPreference = 'Stop'
$root  = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$tools = Join-Path $root '.local\tools'
$infra = Join-Path $root '.local\infra'
$logs  = Join-Path $infra 'logs'
New-Item -ItemType Directory -Force $logs, "$infra\seaweedfs", "$infra\rabbitmq" | Out-Null

function Test-Port([int]$port) { [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) }

function Wait-Port([int]$port, [string]$name, [int]$seconds = 90) {
    for ($i = 0; $i -lt $seconds; $i++) {
        if (Test-Port $port) { Write-Host "  $name is listening on $port"; return }
        Start-Sleep 1
    }
    throw "$name did not start on port $port within $seconds s; see $logs"
}

# --- Mailpit -------------------------------------------------------------------
if (Test-Port 1025) { Write-Host 'Mailpit: already running' }
else {
    Write-Host 'Starting Mailpit...'
    Start-Process "$tools\mailpit.exe" -ArgumentList '--smtp', '127.0.0.1:1025', '--listen', '127.0.0.1:8025', '--database', "`"$infra\mailpit.db`"" `
        -WindowStyle Hidden -RedirectStandardOutput "$logs\mailpit.log" -RedirectStandardError "$logs\mailpit.err.log"
    Wait-Port 1025 'Mailpit SMTP'
}

# --- SeaweedFS (S3) ------------------------------------------------------------
if (Test-Port 9000) { Write-Host 'SeaweedFS S3: already running' }
else {
    Write-Host 'Starting SeaweedFS...'
    $s3Config = Join-Path $infra 's3.json'
    @'
{
  "identities": [
    {
      "name": "local",
      "credentials": [ { "accessKey": "minioadmin", "secretKey": "minioadmin" } ],
      "actions": [ "Admin", "Read", "List", "Tagging", "Write" ]
    }
  ]
}
'@ | Set-Content -Encoding ascii $s3Config
    # DataHub uploads with SSE AES256, which SeaweedFS only accepts once it has an encryption key.
    # The key is generated once and kept, so stored objects stay readable across restarts.
    $sseKeyFile = Join-Path $infra 'sse.key'
    if (-not (Test-Path $sseKeyFile)) {
        $bytes = New-Object byte[] 32
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        [Convert]::ToBase64String($bytes) | Set-Content -Encoding ascii $sseKeyFile
    }
    $env:WEED_S3_SSE_KEY = (Get-Content $sseKeyFile -Raw).Trim()
    Start-Process "$tools\weed.exe" -ArgumentList 'server', "-dir=`"$infra\seaweedfs`"", '-ip=127.0.0.1', '-ip.bind=127.0.0.1',
        '-s3', '-s3.port=9000', "-s3.config=`"$s3Config`"", '-s3.port.iceberg=0', '-s3.port.lance=0',
        '-master.volumeSizeLimitMB=1024', '-volume.max=0' `
        -WindowStyle Hidden -RedirectStandardOutput "$logs\seaweedfs.log" -RedirectStandardError "$logs\seaweedfs.err.log"
    Wait-Port 9000 'SeaweedFS S3'
}

# In SeaweedFS an S3 bucket is a folder under /buckets on the filer, so it is created over the
# filer's HTTP API (409 means it already exists).
Wait-Port 8888 'SeaweedFS filer'
Write-Host 'Ensuring bucket datahub-uploads...'
for ($i = 0; $i -lt 30; $i++) {
    $status = & curl.exe -s -o NUL -w '%{http_code}' -X POST 'http://127.0.0.1:8888/buckets/datahub-uploads/'
    if ($status -match '^(2\d\d|409)$') { Write-Host '  bucket datahub-uploads ready'; break }
    Start-Sleep 2
}
if ($status -notmatch '^(2\d\d|409)$') { throw "Could not create bucket datahub-uploads (filer answered $status)" }

# --- RabbitMQ ------------------------------------------------------------------
if (Test-Port 5672) { Write-Host 'RabbitMQ: already running' }
else {
    Write-Host 'Starting RabbitMQ...'
    $env:ERLANG_HOME   = Join-Path $tools 'erlang'
    $env:RABBITMQ_BASE = Join-Path $infra 'rabbitmq'
    $rabbit = Get-ChildItem $tools -Directory -Filter 'rabbitmq_server-*' | Select-Object -Last 1
    & "$($rabbit.FullName)\sbin\rabbitmq-plugins.bat" enable rabbitmq_management --offline | Out-Null
    Start-Process "$($rabbit.FullName)\sbin\rabbitmq-server.bat" -WindowStyle Hidden `
        -RedirectStandardOutput "$logs\rabbitmq.log" -RedirectStandardError "$logs\rabbitmq.err.log"
    Wait-Port 5672 'RabbitMQ AMQP' 120
}

Write-Host ''
Write-Host 'Local infrastructure is up:'
Write-Host '  S3 (SeaweedFS)  http://localhost:9000   bucket datahub-uploads'
Write-Host '  RabbitMQ UI     http://localhost:15672  guest / guest'
Write-Host '  Mailpit inbox   http://localhost:8025'
