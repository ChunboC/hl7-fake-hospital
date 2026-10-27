<#
.SYNOPSIS
Builds Hopital for the Raspberry Pi (linux-arm64, self-contained) and installs it there.

.DESCRIPTION
1. dotnet publish for linux-arm64 as one self-contained file (no .NET needed on the Pi).
2. Copies it, the scenarios and samples to the Pi with scp.
3. Runs install.sh on the Pi over ssh: puts everything in ~/hopital, adds 'hopital' to the
   PATH and (unless -NoService) installs and restarts the hopital-receiver systemd service.

Each run asks for the Pi password twice (scp, then ssh) unless you have set up an SSH key.

.EXAMPLE
.\deploy\deploy.ps1 -User pi

.EXAMPLE
.\deploy\deploy.ps1 -User pi -NoService      # copy and install only; start Hopital yourself

.EXAMPLE
.\deploy\deploy.ps1 -BuildOnly                  # just build, into artifacts\deploy\hopital
#>
[CmdletBinding()]
param(
    # Pi user name. Defaults to the HOPITAL_PI_USER environment variable.
    [string]$User = $env:HOPITAL_PI_USER,

    # Pi host name or IP address.
    [string]$PiHost = 'hopital.local',

    # Install without setting up or restarting the receiver service.
    [switch]$NoService,

    # Build and stage the files, but do not copy them to the Pi.
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $repo 'artifacts\publish\linux-arm64'
$stage = Join-Path $repo 'artifacts\deploy\hopital'

if (-not $BuildOnly -and -not $User) {
    throw 'Give the Pi user name: -User <name> (or set HOPITAL_PI_USER).'
}

Write-Host 'Building for linux-arm64...' -ForegroundColor Cyan
dotnet publish (Join-Path $repo 'src\Hopital\Hopital.csproj') `
    --configuration Release --runtime linux-arm64 --self-contained `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Stage exactly what goes to the Pi.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $publish 'Hopital') $stage
Copy-Item (Join-Path $repo 'scenarios'), (Join-Path $repo 'samples') $stage -Recurse
Copy-Item (Join-Path $PSScriptRoot 'install.sh'), (Join-Path $PSScriptRoot 'hopital-receiver.service') $stage

# Scripts must reach the Pi with Linux line endings, whatever git did on checkout.
foreach ($name in 'install.sh', 'hopital-receiver.service') {
    $path = Join-Path $stage $name
    $text = [IO.File]::ReadAllText($path) -replace "`r`n", "`n"
    [IO.File]::WriteAllText($path, $text)
}

$size = (Get-Item (Join-Path $stage 'Hopital')).Length / 1MB
Write-Host ("Staged in {0} (Hopital is {1:N0} MB)" -f $stage, $size) -ForegroundColor Cyan
if ($BuildOnly) { return }

$target = "$User@$PiHost"
$upload = "hopital-upload-$(Get-Date -Format yyyyMMddHHmmss)"

Write-Host "Copying to ${target}:~/$upload ..." -ForegroundColor Cyan
scp -r $stage "${target}:$upload"
if ($LASTEXITCODE -ne 0) { throw "scp to $target failed." }

Write-Host "Installing on $target ..." -ForegroundColor Cyan
$installArgs = if ($NoService) { '--no-service' } else { '' }
# -t gives sudo a terminal in case it asks for a password.
ssh -t $target "bash ~/$upload/install.sh $installArgs; status=`$?; rm -rf ~/$upload; exit `$status"
if ($LASTEXITCODE -ne 0) { throw "Install on $target failed." }

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
if (-not $NoService) {
    Write-Host "Receiver: MLLP on ${PiHost}:6662, control on http://${PiHost}:6680/"
    Write-Host "Log:      ssh $target journalctl -u hopital-receiver -f"
}
Write-Host "Sender:   ssh $target hopital run hopital/scenarios/smoke-test.yaml --host <OIE host> --port 6661"
