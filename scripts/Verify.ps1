# //summary//
# Restores, builds and tests the solution, with optional infrastructure compilation.
# //summary//
# //-------------------Start Of File------------------//
param([string]$DotNet = 'dotnet', [string]$Bicep = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    foreach ($project in @('src/CloudStore.Web/CloudStore.Web.csproj', 'src/CloudStore.Functions/CloudStore.Functions.csproj', 'tests/CloudStore.Tests/CloudStore.Tests.csproj')) {
        & $DotNet restore $project --disable-parallel
        if ($LASTEXITCODE -ne 0) { throw ('Restore failed: ' + $project) }
    }
    & $DotNet build CloudStore.slnx --configuration Release --no-restore --maxcpucount:1 -p:UseSharedCompilation=false --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $DotNet test tests/CloudStore.Tests/CloudStore.Tests.csproj --configuration Release --no-build --no-restore --logger 'trx;LogFileName=cloudstore.trx' --results-directory TestResults
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if ($Bicep) {
        $output = Join-Path $root 'artifacts'
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        & $Bicep build infra/main.bicep --outfile (Join-Path $output 'infrastructure.json')
        if ($LASTEXITCODE -ne 0) { throw 'Infrastructure compilation failed.' }
        & $Bicep build infra/github-identity.bicep --outfile (Join-Path $output 'github-identity.json')
        if ($LASTEXITCODE -ne 0) { throw 'Identity compilation failed.' }
    }
} finally { Pop-Location }
# //-------------------End Of File------------------//
