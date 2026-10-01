<#
.SYNOPSIS
    Deploys DataHub to the local Rancher Desktop cluster (namespace datahub-local):
      - infrastructure from deploy/local (SQL Server, RabbitMQ, SeaweedFS S3, Mailpit, Seq)
      - the DataHub Helm chart with values-local.yaml (the migrator runs first as a hook Job)
    Passwords, keys and the TLS certificate are generated once into .local\k8s (gitignored) and reused,
    because the data volumes depend on them. Images must exist first: scripts\build-images.ps1 -Version <v>.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\local\k8s-up.ps1 -Version 0.1.0
#>
param(
    [string]$Version = "0.1.0",
    [string]$Namespace = "datahub-local",
    [string]$Context = "rancher-desktop"
)

$ErrorActionPreference = "Stop"
$root  = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$state = Join-Path $root ".local\k8s"
New-Item -ItemType Directory -Force $state, (Join-Path $root ".local\sms-outbox-k8s") | Out-Null

function Invoke-Native([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

# Only ever touch the local cluster.
$current = (& kubectl config current-context).Trim()
if ($current -ne $Context) { throw "kubectl context is '$current', expected '$Context'. Switch with: kubectl config use-context $Context" }

# --- Images --------------------------------------------------------------------
# On this PC the Windows docker CLI can't reach Rancher's engine (see docs/PROJECT.md 6.4), so fall back to the VM.
function Test-Image([string]$image) {
    # PowerShell 5.1 turns a native command's stderr into an error under "Stop"; a failure is expected here.
    $ErrorActionPreference = "Continue"
    & docker image inspect $image *> $null
    if ($LASTEXITCODE -eq 0) { return $true }
    & wsl -d rancher-desktop sh -c "PATH=/usr/sbin:/usr/bin:/sbin:/bin DOCKER_CONFIG=/tmp/dh-dockercfg docker image inspect $image" *> $null
    return ($LASTEXITCODE -eq 0)
}
$missing = "web", "api", "worker", "migrator" | Where-Object { -not (Test-Image "datahub-${_}:$Version") }
if ($missing) { throw "Missing images for version ${Version}: $($missing -join ', '). Build them first (scripts\build-images.ps1 -Version $Version, or docs/PROJECT.md 6.4)." }

# --- Secrets (generated once) --------------------------------------------------
function New-RandomHex([int]$bytes) {
    $b = New-Object byte[] $bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    -join ($b | ForEach-Object { $_.ToString("x2") })
}
function New-RandomBase64([int]$bytes) {
    $b = New-Object byte[] $bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    [Convert]::ToBase64String($b)
}

$secretsFile = Join-Path $state "secrets.json"
if (-not (Test-Path $secretsFile)) {
    Write-Host "Generating local secrets into $secretsFile"
    [ordered]@{
        # SQL Server requires upper case, lower case, digits and a symbol.
        mssqlSaPassword  = "Dh-" + (New-RandomHex 16) + "-Aa1"
        rabbitmqPassword = New-RandomHex 16
        s3AccessKey      = "datahub"
        s3SecretKey      = New-RandomHex 20
        seaweedfsSseKey  = New-RandomBase64 32
        signingKey       = New-RandomBase64 32
        devSigningKey    = New-RandomBase64 32
    } | ConvertTo-Json | Set-Content -Encoding ascii $secretsFile
}
$s = Get-Content $secretsFile -Raw | ConvertFrom-Json

$s3Config = @{ identities = @(@{ name = "datahub"; credentials = @(@{ accessKey = $s.s3AccessKey; secretKey = $s.s3SecretKey }); actions = @("Admin", "Read", "List", "Tagging", "Write") }) } |
    ConvertTo-Json -Depth 5 -Compress
$s3ConfigFile = Join-Path $state "s3.json"
[IO.File]::WriteAllText($s3ConfigFile, $s3Config)

# --- TLS certificate for the ingress (generated once) --------------------------
$crt = Join-Path $state "tls.crt"; $key = Join-Path $state "tls.key"
if (-not (Test-Path $crt)) {
    # Git for Windows' openssl first; others (e.g. Anaconda's) may lack a default openssl.cnf, hence -config.
    $openssl = "C:\Program Files\Git\usr\bin\openssl.exe"
    if (-not (Test-Path $openssl)) { $openssl = (Get-Command openssl -ErrorAction SilentlyContinue).Source }
    if (-not $openssl) { throw "openssl not found (it comes with Git for Windows)." }
    $cnf = Join-Path $state "openssl.cnf"
    [IO.File]::WriteAllText($cnf, "[req]`ndistinguished_name = dn`n[dn]`n")
    Write-Host "Generating a self-signed certificate for datahub.localtest.me and api.datahub.localtest.me"
    Invoke-Native $openssl @("req", "-config", $cnf, "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "825", "-subj", "/CN=datahub.localtest.me",
        "-addext", "subjectAltName=DNS:datahub.localtest.me,DNS:api.datahub.localtest.me",
        "-keyout", $key, "-out", $crt)
}

# --- Namespace, secrets, infrastructure ----------------------------------------
function Apply-Yaml([string[]]$createArgs) {
    $yaml = & kubectl @createArgs --dry-run=client -o yaml
    if ($LASTEXITCODE -ne 0) { throw "kubectl $($createArgs -join ' ') failed" }
    $yaml | & kubectl apply -f - | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "kubectl apply failed" }
}

Apply-Yaml @("create", "namespace", $Namespace)

Apply-Yaml @("create", "secret", "generic", "datahub-local-infra", "-n", $Namespace,
    "--from-literal=mssql-sa-password=$($s.mssqlSaPassword)",
    "--from-literal=rabbitmq-user=datahub",
    "--from-literal=rabbitmq-password=$($s.rabbitmqPassword)",
    "--from-literal=seaweedfs-sse-key=$($s.seaweedfsSseKey)",
    "--from-file=seaweedfs-s3-config=$s3ConfigFile")

# Keys are the environment variables the apps read (see the chart's apps.*.secretKeys).
Apply-Yaml @("create", "secret", "generic", "datahub-secrets", "-n", $Namespace,
    "--from-literal=ConnectionStrings__DataHub=Server=mssql,1433;Database=DataHub;User Id=sa;Password=$($s.mssqlSaPassword);Encrypt=True;TrustServerCertificate=True",
    "--from-literal=Security__SigningKey=$($s.signingKey)",
    "--from-literal=Auth__DevSigningKey=$($s.devSigningKey)",
    "--from-literal=S3__AccessKey=$($s.s3AccessKey)",
    "--from-literal=S3__SecretKey=$($s.s3SecretKey)",
    "--from-literal=RabbitMq__Uri=amqp://datahub:$($s.rabbitmqPassword)@rabbitmq:5672/")

Apply-Yaml @("create", "secret", "tls", "datahub-tls", "-n", $Namespace, "--cert=$crt", "--key=$key")

Write-Host "Applying the infrastructure (deploy/local)..."
# A finished bucket Job can't be changed in place; recreate it.
& kubectl delete job seaweedfs-bucket -n $Namespace --ignore-not-found | Out-Null
Invoke-Native kubectl @("apply", "-k", (Join-Path $root "deploy\local"))
foreach ($sts in "mssql", "rabbitmq", "seaweedfs", "seq") {
    Invoke-Native kubectl @("rollout", "status", "statefulset/$sts", "-n", $Namespace, "--timeout=300s")
}
Invoke-Native kubectl @("rollout", "status", "deployment/mailpit", "-n", $Namespace, "--timeout=120s")
Invoke-Native kubectl @("wait", "--for=condition=complete", "job/seaweedfs-bucket", "-n", $Namespace, "--timeout=180s")

# --- DataHub -------------------------------------------------------------------
# The SMS outbox is a folder on this PC (the cluster node is the WSL VM, which sees C: under /mnt/c).
$outboxWin = (Join-Path $root ".local\sms-outbox-k8s")
$outbox = "/mnt/" + $outboxWin.Substring(0, 1).ToLower() + ($outboxWin.Substring(2) -replace "\\", "/")
Write-Host "Installing the DataHub chart (version $Version)..."
Invoke-Native helm @("upgrade", "--install", "datahub", (Join-Path $root "deploy\helm\datahub"), "-n", $Namespace,
    "-f", (Join-Path $root "deploy\helm\datahub\values-local.yaml"),
    "--set", "image.tag=$Version", "--set", "smsOutbox.hostPath=$outbox",
    "--wait", "--timeout", "10m")

Write-Host ""
Write-Host "DataHub is running in namespace ${Namespace}:"
Write-Host "  Portal    https://datahub.localtest.me"
Write-Host "  API       https://api.datahub.localtest.me/swagger"
Write-Host "  Seq       http://seq.localtest.me"
Write-Host "  Mailpit   http://mailpit.localtest.me"
Write-Host "  RabbitMQ  http://rabbitmq.localtest.me   (user datahub; password in .local\k8s\secrets.json)"
Write-Host "  SMS       .local\sms-outbox-k8s"
Write-Host "The certificate is self-signed (.local\k8s\tls.crt); trust it to avoid browser warnings."
