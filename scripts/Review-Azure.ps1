# //summary//
# Compiles infrastructure and previews resource changes without applying a deployment.
# //summary//
# //-------------------Start Of File------------------//
param([string]$ResourceGroup = 'rg-cloudstore-demo')
$ErrorActionPreference = 'Stop'
if (!(Get-Command az -ErrorAction SilentlyContinue)) { throw 'Use Azure Cloud Shell, or install Azure CLI only with your approval.' }
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & az bicep build --file infra/main.bicep --outfile (Join-Path ([IO.Path]::GetTempPath()) 'cloudstore-template.json')
    if ($LASTEXITCODE -ne 0) { throw 'Bicep compilation failed.' }
    # ResourceIdOnly avoids printing calculated settings or secret values in the diff.
    & az deployment group what-if --resource-group $ResourceGroup --parameters infra/existing.bicepparam --result-format ResourceIdOnly
    if ($LASTEXITCODE -ne 0) { throw 'Azure preview failed; do not deploy.' }
} finally { Pop-Location }
# //-------------------End Of File------------------//
