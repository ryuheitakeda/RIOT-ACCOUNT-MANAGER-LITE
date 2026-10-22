$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts/RiotAccountsLite-win-x64'
$archive = Join-Path $root 'artifacts/RiotAccountsLite-win-x64.zip'
Push-Location $root
try {
    dotnet test tests/RiotAccountsLite.Core.Tests -c Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    dotnet publish src/RiotAccountsLite.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $output --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item README.md, LICENSE $output
    Copy-Item docs $output -Recurse -Force
    Compress-Archive -Path "$output/*" -DestinationPath $archive -Force
    Get-FileHash $archive -Algorithm SHA256
}
finally { Pop-Location }
