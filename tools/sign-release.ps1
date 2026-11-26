<#
.SYNOPSIS
    Signs a LaunchHere release for the in-app updater.

.DESCRIPTION
    Writes LaunchHere.exe.sig (ECDSA P-256 signature of the exe's SHA-256, base64) and LaunchHere.exe.sha256
    next to the exe. Attach the exe and both files to the GitHub release. LaunchHere 0.1.4 and later only
    install updates whose signature matches the public key built into the app (Updater.cs), so a release
    uploaded by anyone who doesn't have the private key is refused.

    The private key must never be committed or uploaded. Keep it outside the repo, with a backup.

.EXAMPLE
    pwsh tools/sign-release.ps1 -Key <path to private key .pem>
    pwsh tools/sign-release.ps1 -NewKey -Key <path for the new private key .pem>
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\LaunchHere.exe'),
    [string]$Key = $env:LAUNCHHERE_SIGNING_KEY,
    [switch]$NewKey
)
$ErrorActionPreference = 'Stop'
if (-not $Key) { throw 'Pass -Key <private key .pem> or set LAUNCHHERE_SIGNING_KEY.' }

if ($NewKey) {
    if (Test-Path $Key) { throw "$Key already exists. Refusing to overwrite a signing key." }
    $ec = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    New-Item -ItemType Directory -Force (Split-Path $Key) | Out-Null
    Set-Content -Path $Key -Value $ec.ExportPkcs8PrivateKeyPem() -NoNewline
    Write-Host "Private key written to $Key. Back it up somewhere safe; never commit it."
    Write-Host "Public key (paste into Updater.cs, PublicKeyPem):"
    Write-Host $ec.ExportSubjectPublicKeyInfoPem()
    return
}

$Exe = (Resolve-Path $Exe).Path
$ec = [System.Security.Cryptography.ECDsa]::Create()
$ec.ImportFromPem((Get-Content $Key -Raw))

$fs = [System.IO.File]::OpenRead($Exe)
try { $hash = [System.Security.Cryptography.SHA256]::HashData($fs) } finally { $fs.Dispose() }
$sig = $ec.SignHash($hash)
if (-not $ec.VerifyHash($hash, $sig)) { throw 'Signature check failed.' }

$hex = [Convert]::ToHexString($hash)
$name = Split-Path $Exe -Leaf
Set-Content -Path "$Exe.sig" -Value ([Convert]::ToBase64String($sig)) -NoNewline
Set-Content -Path "$Exe.sha256" -Value "$hex  $name" -NoNewline
Write-Host "SHA-256: $hex"
Write-Host "Wrote $Exe.sig and $Exe.sha256"
