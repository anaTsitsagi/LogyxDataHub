<#
.SYNOPSIS
    Starts the whole local test environment on Rancher Desktop with one command:
      1. checks that the local cluster answers
      2. builds the four images from the working tree if they are missing (or with -Rebuild)
      3. deploys infrastructure and DataHub (scripts\local\k8s-up.ps1)
      4. restarts the app pods after a rebuild (same tag, so Helm sees no change)
      5. waits until the portal and the API are ready
      6. optionally runs the end-to-end smoke test (scripts\local\smoke-test.ps1)
    The hand test that follows is in docs/PROJECT.md 6.7. Stop with scripts\local\k8s-down.ps1.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\local\start-all.ps1 -Rebuild -SmokeZip .local\test-data\HIRO.zip
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\local\start-all.ps1     # reuse the existing images
#>
param(
    [string]$Version = "0.1.0",
    # Build the images from the working tree even if they exist (needed after code changes).
    [switch]$Rebuild,
    # An ORIS company folder zipped outside the repository, e.g. .local\test-data\HIRO.zip.
    [string]$SmokeZip,
    [string]$Namespace = "datahub-local",
    [string]$Context = "rancher-desktop"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$apps = "web", "api", "worker", "migrator"

function Invoke-Native([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Write-Step([string]$text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }

# --- 1. Preflight ---------------------------------------------------------------
Write-Step "Checking the local cluster"
if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) { throw "kubectl not found. Install and start Rancher Desktop (docs/PROJECT.md 6.1)." }
$current = (& kubectl config current-context).Trim()
if ($current -ne $Context) { throw "kubectl context is '$current', expected '$Context'. Switch with: kubectl config use-context $Context" }
& {
    # PowerShell 5.1 turns a native command's stderr into an error under "Stop"; a failure is handled here.
    $ErrorActionPreference = "Continue"
    & kubectl get nodes --request-timeout=10s *> $null
}
if ($LASTEXITCODE -ne 0) { throw "The cluster doesn't answer. Start Rancher Desktop and wait until Kubernetes is running, then try again." }
Write-Host "  cluster '$Context' answers"

if ($Rebuild -and (& git -C $root status --porcelain)) {
    Write-Warning "The working tree has uncommitted changes; the images will include them."
}
if ($SmokeZip -and -not (Test-Path $SmokeZip)) { throw "Smoke-test ZIP not found: $SmokeZip" }

# --- 2. Images ------------------------------------------------------------------
# Same check as k8s-up.ps1: the Windows docker CLI first, then the Rancher VM.
function Test-Image([string]$image) {
    $ErrorActionPreference = "Continue"
    & docker image inspect $image *> $null
    if ($LASTEXITCODE -eq 0) { return $true }
    & wsl -d rancher-desktop sh -c "PATH=/usr/sbin:/usr/bin:/sbin:/bin DOCKER_CONFIG=/tmp/dh-dockercfg docker image inspect $image" *> $null
    return ($LASTEXITCODE -eq 0)
}

# On this PC the Windows docker CLI can hang ("timed out dialing Hyper-V socket"), so give it 15 s.
function Test-WindowsDocker {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { return $false }
    $p = Start-Process docker -ArgumentList "version" -WindowStyle Hidden -PassThru
    if (-not $p.WaitForExit(15000)) { $p.Kill(); return $false }
    return ($p.ExitCode -eq 0)
}

$built = $false
$missing = $apps | Where-Object { -not (Test-Image "datahub-${_}:$Version") }
if ($Rebuild -or $missing) {
    if ($Rebuild) { Write-Step "Building the images (version $Version) from the working tree" }
    else { Write-Step "Building the missing images ($($missing -join ', '); version $Version)" }
    if (Test-WindowsDocker) {
        Invoke-Native powershell @("-ExecutionPolicy", "Bypass", "-File", (Join-Path $root "scripts\build-images.ps1"), "-Version", $Version)
    }
    else {
        # Build inside the Rancher VM (docs/PROJECT.md 6.4): public DNS until Rancher restarts, a Linux-only PATH
        # and an empty docker config, because the VM's credential helpers call back to Windows and hang.
        Write-Host "  the Windows docker CLI doesn't answer; building inside the Rancher VM"
        $repo = "/mnt/" + $root.Substring(0, 1).ToLower() + ($root.Substring(2) -replace "\\", "/")
        Invoke-Native wsl @("-d", "rancher-desktop", "sh", "-c", "printf 'nameserver 8.8.8.8\nnameserver 1.1.1.1\n' > /etc/resolv.conf")
        $sh = "export PATH=/usr/sbin:/usr/bin:/sbin:/bin DOCKER_CONFIG=/tmp/dh-dockercfg; mkdir -p `$DOCKER_CONFIG; echo {} > `$DOCKER_CONFIG/config.json; " +
              "cd '$repo' || exit 1; for a in $($apps -join ' '); do docker build -f deploy/docker/Dockerfile --target `$a --build-arg VERSION=$Version -t datahub-`$a:$Version . || exit 1; done"
        # --exec: without it wsl runs the command through the VM's login shell, which expands $DOCKER_CONFIG and $a to "".
        Invoke-Native wsl @("-d", "rancher-desktop", "--exec", "sh", "-c", $sh)
    }
    $built = $true
}
else { Write-Step "Images datahub-*:$Version exist (use -Rebuild after code changes)" }

# --- 3. Deploy ------------------------------------------------------------------
Write-Step "Deploying infrastructure and DataHub"
Invoke-Native powershell @("-ExecutionPolicy", "Bypass", "-File", (Join-Path $root "scripts\local\k8s-up.ps1"),
    "-Version", $Version, "-Namespace", $Namespace, "-Context", $Context)

# --- 4. Restart after a rebuild -------------------------------------------------
if ($built) {
    Write-Step "Restarting the app pods so they run the new images"
    Invoke-Native kubectl @("rollout", "restart", "deployment", "-n", $Namespace, "-l", "app.kubernetes.io/instance=datahub")
    foreach ($app in "web", "api", "worker") {
        Invoke-Native kubectl @("rollout", "status", "deployment/datahub-$app", "-n", $Namespace, "--timeout=300s")
    }
}

# --- 5. Health ------------------------------------------------------------------
Write-Step "Waiting for the portal and the API"
foreach ($url in "https://datahub.localtest.me/health/ready", "https://api.datahub.localtest.me/health/ready") {
    $status = ""
    for ($i = 0; $i -lt 60; $i++) {
        # -k: the ingress certificate is self-signed.
        $status = & curl.exe -k -s -o NUL -w "%{http_code}" --max-time 5 $url
        if ($status -eq "200") { break }
        Start-Sleep 2
    }
    if ($status -ne "200") { throw "$url answered '$status' after 2 minutes. Check: kubectl get pods -n $Namespace" }
    Write-Host "  $url  200"
}

# --- 6. Smoke test --------------------------------------------------------------
$smokeExit = 0
if ($SmokeZip) {
    Write-Step "Running the smoke test with $SmokeZip"
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root "scripts\local\smoke-test.ps1") -ZipPath $SmokeZip
    $smokeExit = $LASTEXITCODE
}

# --- 7. Summary -----------------------------------------------------------------
Write-Host ""
Write-Host "DataHub is ready in namespace ${Namespace}:" -ForegroundColor Green
Write-Host "  Staff     https://api.datahub.localtest.me/staff     (bank staff portal: send invitations, open reports)"
Write-Host "  Portal    https://datahub.localtest.me"
Write-Host "  API       https://api.datahub.localtest.me/swagger   (definition 'dev (local token)' -> POST /dev/token)"
Write-Host "  Seq       http://seq.localtest.me"
Write-Host "  Mailpit   http://mailpit.localtest.me"
Write-Host "  RabbitMQ  http://rabbitmq.localtest.me   (user datahub; password in .local\k8s\secrets.json)"
Write-Host "  SMS       .local\sms-outbox-k8s"
if ($SmokeZip) {
    if ($smokeExit -eq 0) { Write-Host "Smoke test: all checks passed." -ForegroundColor Green }
    else { Write-Host "Smoke test: FAILED (see the FAIL lines above)." -ForegroundColor Red }
}
Write-Host "Next: the hand test in docs/PROJECT.md 6.7. Stop with: powershell -ExecutionPolicy Bypass -File scripts\local\k8s-down.ps1"
exit $smokeExit
