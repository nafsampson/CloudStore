# //summary//
# Runs hidden-password Identity resets against Azure SQL using the operator's Entra session.
# Temporarily allows this Cloud Shell IP, then removes its own firewall rule and token settings.
# Does not deploy the website, alter roles or touch retail data.
# //summary//
# //-------------------Start Of File------------------//
param(
    [string]$ArchivePath = "$HOME/CloudStore-Password-Reset.zip",
    [string]$ResourceGroup = 'rg-cloudstore-demo',
    [string]$SqlServer = 'cloudstore-sql-1234',
    [string]$Database = 'cloudstore-accounts'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsLinux) { throw 'Run this helper in Azure Cloud Shell PowerShell (Linux).' }
if (!(Test-Path -LiteralPath $ArchivePath)) { throw 'Upload CloudStore-Password-Reset.zip first.' }
if ($SqlServer -notmatch '\A[a-z0-9][a-z0-9-]{0,62}\z' -or $Database -notmatch '\A[a-zA-Z0-9_-]{1,128}\z') { throw 'Invalid SQL server or database name.' }
$azureExecutable = Get-Command az -CommandType Application | Select-Object -First 1 -ExpandProperty Source
function Invoke-ResetAzure {
    $result = & $azureExecutable @args --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) { throw 'Azure authorization or resource operation failed. No password was printed.' }
    if ($result) { ($result -join "`n") | ConvertFrom-Json -AsHashtable }
}
$null = Invoke-ResetAzure sql db show --resource-group $ResourceGroup --server $SqlServer --name $Database
$packageDirectory = Join-Path ([IO.Path]::GetTempPath()) ('cloudstore-account-reset-' + [Guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $ArchivePath -DestinationPath $packageDirectory
$application = Join-Path $packageDirectory 'CloudStore.Web.dll'
if (!(Test-Path -LiteralPath $application)) { throw 'Expected a published website package, not the source ZIP.' }
$cloudStoreDotnet = "$HOME/.dotnet10/dotnet"
$runtimeInstalled = $false
if (Test-Path -LiteralPath $cloudStoreDotnet) {
    $runtimes = & $cloudStoreDotnet --list-runtimes
    $runtimeInstalled = $LASTEXITCODE -eq 0 -and @($runtimes | Where-Object { $_ -match '^Microsoft.AspNetCore.App 10\.' }).Count -gt 0
}
if (!$runtimeInstalled) {
    Write-Host 'Preparing .NET 10 in this Cloud Shell session...'
    $installer = Join-Path $packageDirectory 'dotnet-install.sh'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.sh' -OutFile $installer
    & bash $installer --channel 10.0 --install-dir "$HOME/.dotnet10"
    if ($LASTEXITCODE -ne 0) { throw '.NET installation failed; no password was changed.' }
}
$clientAddress = ([string](Invoke-RestMethod -Uri 'https://api.ipify.org')).Trim()
$parsedAddress = $null
if (![Net.IPAddress]::TryParse($clientAddress, [ref]$parsedAddress) -or $parsedAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'Could not determine this Cloud Shell IPv4 address.' }
$ruleName = 'cloudstore-account-reset-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$ruleCreated = $false
$savedSettings = @{}
foreach ($name in @('DOTNET_ROOT', 'Storage__Mode', 'ConnectionStrings__DefaultConnection', 'AccountSetup__SqlAccessToken')) {
    $savedSettings[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    $null = Invoke-ResetAzure sql server firewall-rule create --resource-group $ResourceGroup --server $SqlServer --name $ruleName --start-ip-address $clientAddress --end-ip-address $clientAddress
    $ruleCreated = $true
    $env:DOTNET_ROOT = "$HOME/.dotnet10"
    $env:Storage__Mode = 'Azure'
    $env:ConnectionStrings__DefaultConnection = "Server=tcp:$SqlServer.database.windows.net,1433;Database=$Database;Encrypt=True;TrustServerCertificate=False;Connect Timeout=60;"
    foreach ($accountName in @('store-admin', 'store-customer')) {
        # A new short-lived SQL access token is obtained for each interactive reset.
        $token = Invoke-ResetAzure account get-access-token --resource 'https://database.windows.net/'
        if (!$token['accessToken']) { throw 'SQL access token was not available.' }
        $env:AccountSetup__SqlAccessToken = $token['accessToken']
        $token = $null
        Write-Host "Reset $accountName next. Enter your new password twice at the hidden prompts."
        Write-Host 'Passwords need 12-256 characters, uppercase, lowercase, a digit and punctuation. Use different passwords.'
        & $cloudStoreDotnet $application reset-password --username $accountName
        if ($LASTEXITCODE -ne 0) { throw "Password reset did not complete for this step. Any earlier successful reset remains applied; rerunning is safe." }
        [Environment]::SetEnvironmentVariable('AccountSetup__SqlAccessToken', $null, 'Process')
    }
    Write-Host 'Both reset commands completed. Sign in to the live website with the new passwords.'
} finally {
    foreach ($name in $savedSettings.Keys) { [Environment]::SetEnvironmentVariable($name, $savedSettings[$name], 'Process') }
    if ($ruleCreated) {
        & $azureExecutable sql server firewall-rule delete --resource-group $ResourceGroup --server $SqlServer --name $ruleName --only-show-errors --output none
        if ($LASTEXITCODE -ne 0) { Write-Warning "Remove temporary SQL firewall rule $ruleName from server $SqlServer manually." }
    }
}
# //-------------------End Of File------------------//
