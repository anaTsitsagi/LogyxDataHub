<#
.SYNOPSIS
  Builds the four DataHub images (web, api, worker, migrator) from deploy/docker/Dockerfile.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\build-images.ps1 -Version 1.0.0
#>
param(
    [string]$Version = "0.0.0-local",
    [string]$Registry = "datahub"   # image names become <Registry>-<app>:<Version>, e.g. datahub-web:1.0.0
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$apps = "web", "api", "worker", "migrator"

foreach ($app in $apps) {
    $tag = "$Registry-${app}:$Version"
    Write-Host "==> $tag"
    docker build --file "$root\deploy\docker\Dockerfile" --target $app --build-arg "VERSION=$Version" --tag $tag $root
    if ($LASTEXITCODE -ne 0) { throw "docker build failed for $app" }
}

docker image ls --filter "reference=$Registry-*:$Version"
