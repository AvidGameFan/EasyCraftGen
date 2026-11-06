#Requires -Version 5.1
param(
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64")]
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.0"
)
$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') { throw "Invalid version: $Version" }
$project = Join-Path $PSScriptRoot "EasyCraftGen\EasyCraftGen.csproj"
$dist = Join-Path $PSScriptRoot "dist"
$publish = Join-Path $dist ("publish-" + $Runtime + "-" + [guid]::NewGuid().ToString("N"))
$archive = Join-Path $dist "EasyCraftGen-$Runtime-v$Version.zip"
New-Item -ItemType Directory -Force -Path $publish | Out-Null
try {
    dotnet publish $project -c Release -r $Runtime --self-contained true "-p:Version=$Version" -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $archive -Force
    Write-Host "Created $archive"
    if ($Runtime.StartsWith("linux-")) { Write-Host "After extracting on Linux, run chmod +x EasyCraftGen." }
}
finally {
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
}
