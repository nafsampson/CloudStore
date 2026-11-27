# //summary//
# Checks hosted HTTPS liveness without asserting database or worker readiness.
# //summary//
# //-------------------Start Of File------------------//
param([Parameter(Mandatory)][string]$WebAppName)
$ErrorActionPreference = 'Stop'
if ($WebAppName -notmatch '^[a-zA-Z0-9-]+$') { throw 'Use the web app name, not a URL or connection string.' }
$uri = 'https://' + $WebAppName + '.azurewebsites.net/health/live'
$response = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 60
if ($response.StatusCode -ne 200 -or $response.Content.Trim() -ne 'Healthy') { throw 'Liveness check failed.' }
Write-Output 'Liveness passed. SQL, storage, login and the queue worker still require the acceptance walkthrough.'
# //-------------------End Of File------------------//
