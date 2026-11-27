# //summary//
# Uploads and verifies the six sample catalogue photos in a private Azure Blob container.
# Backs up existing product records, then conditionally updates only their image references.
# Preserves existing blobs, account credentials, product details, orders, carts and contracts.
# //summary//
# //-------------------Start Of File------------------//
param([switch]$Apply, [string]$DataDirectory = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$DataDirectory) {
    $DataDirectory = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'manifest.json')) { $PSScriptRoot } else { Join-Path $PSScriptRoot '../sample-data/catalog' }
}
$manifest = Get-Content -LiteralPath (Join-Path $DataDirectory 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
if ($manifest.Version -ne 1 -or $manifest.Products.Count -ne 6) { throw 'Expected the six-image catalogue manifest.' }
$seen = @{}
foreach ($item in $manifest.Products) {
    if ($item.ProductId -notmatch '\Ademo-(headphones|keyboard|mouse|speaker|webcam|hub)\z' -or $seen.ContainsKey($item.ProductId)) { throw 'Invalid or duplicate product reference.' }
    if ($item.File -cne ('electronics-' + $item.ProductId.Substring(5) + '.png')) { throw 'Invalid catalogue image path.' }
    $path = Join-Path $DataDirectory $item.File
    $file = Get-Item -LiteralPath $path
    if ($file.Length -ne $item.Length -or $file.Length -gt 4 * 1024 * 1024) { throw 'Image length is invalid.' }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne $item.Sha256 -or $item.BlobName -cne ($hash.Substring(0, 32) + '.png')) { throw 'Image checksum is invalid.' }
    $seen[$item.ProductId] = $true
}
Write-Host 'Validated six original catalogue photos. Target: cloudstoredemo / images and Products.ImageName.'
if (!$Apply) { Write-Host 'Validation only; use -Apply to upload and link these images.'; return }

$azureExecutable = Get-Command az -CommandType Application | Select-Object -First 1 -ExpandProperty Source
function Invoke-ImageAzure {
    $result = & $azureExecutable @args --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) { throw 'Azure image migration command failed. No credential values were printed.' }
    if ($result) { ($result -join "`n") | ConvertFrom-Json -AsHashtable }
}
function Invoke-ProductEntity([string]$ProductId, [string]$Method = 'GET', [string]$ImageName = '', [string]$ETag = '') {
    # Table Shared Key Lite signs the date and canonical resource, without exposing an account key.
    $resourcePath = "Products(PartitionKey='Product',RowKey='$ProductId')"
    $date = [DateTime]::UtcNow.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
    $canonical = "/cloudstoredemo/$resourcePath"
    $hmac = [Security.Cryptography.HMACSHA256]::new([Convert]::FromBase64String($env:AZURE_STORAGE_KEY))
    try { $signature = [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes("$date`n$canonical"))) } finally { $hmac.Dispose() }
    $headers = @{
        Authorization = "SharedKeyLite cloudstoredemo:$signature"
        'x-ms-date' = $date
        'x-ms-version' = '2019-02-02'
        Accept = 'application/json;odata=nometadata'
        DataServiceVersion = '3.0'
        MaxDataServiceVersion = '3.0'
    }
    $request = @{ Uri = "https://cloudstoredemo.table.core.windows.net/$resourcePath"; Method = $Method; Headers = $headers }
    if ($Method -eq 'MERGE') {
        if (!$ETag -or $ETag -eq '*') { throw 'A captured product ETag is required.' }
        $headers['If-Match'] = $ETag
        $request['ContentType'] = 'application/json'
        $request['Body'] = [Text.Encoding]::UTF8.GetBytes((@{ PartitionKey = 'Product'; RowKey = $ProductId; ImageName = $ImageName } | ConvertTo-Json -Compress))
    }
    $response = Invoke-WebRequest @request
    if ($Method -eq 'GET') {
        $entity = $response.Content | ConvertFrom-Json -AsHashtable
        if ($entity['PartitionKey'] -cne 'Product' -or $entity['RowKey'] -cne $ProductId) { throw 'Unexpected Azure product record.' }
        $entityETag = [string]($response.Headers['ETag'] | Select-Object -First 1)
        if (!$entityETag) { throw 'Azure did not return a product ETag.' }
        return @{ Entity = $entity; ETag = $entityETag }
    }
}
function Test-BlobMatches([string]$Name, [hashtable]$Item) {
    $exists = Invoke-ImageAzure storage blob exists --container-name images --name $Name
    if (!$exists['exists']) { return $false }
    $properties = Invoke-ImageAzure storage blob show --container-name images --name $Name
    if ($properties['properties']['contentLength'] -ne $Item.Length) { return $false }
    $download = Join-Path $workingDirectory ([Guid]::NewGuid().ToString('N') + '.png')
    $null = Invoke-ImageAzure storage blob download --container-name images --name $Name --file $download --no-progress
    return (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $Item.Sha256
}

$savedEnvironment = @{}
foreach ($name in @('AZURE_STORAGE_ACCOUNT', 'AZURE_STORAGE_KEY', 'AZURE_STORAGE_AUTH_MODE', 'AZURE_STORAGE_CONNECTION_STRING', 'AZURE_STORAGE_SAS_TOKEN')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$workingDirectory = Join-Path ([IO.Path]::GetTempPath()) ('cloudstore-images-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $workingDirectory
$backupName = 'catalog-images/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '/products-before.json'
try {
    $account = Invoke-ImageAzure account show
    if ($account['id'] -ne 'f7e02b2c-326f-4c55-8927-b5368f81d666') { throw 'Select the expected Azure for Students subscription first.' }
    $env:AZURE_STORAGE_ACCOUNT = 'cloudstoredemo'
    $env:AZURE_STORAGE_AUTH_MODE = 'key'
    [Environment]::SetEnvironmentVariable('AZURE_STORAGE_CONNECTION_STRING', $null, 'Process')
    [Environment]::SetEnvironmentVariable('AZURE_STORAGE_SAS_TOKEN', $null, 'Process')
    $keys = @(Invoke-ImageAzure storage account keys list --resource-group rg-cloudstore-demo --account-name cloudstoredemo)
    if (!$keys.Count -or !$keys[0]['value']) { throw 'Storage authorization was not available.' }
    $env:AZURE_STORAGE_KEY = $keys[0]['value']; $keys = $null

    $original = @{}
    foreach ($item in $manifest.Products) { $original[$item.ProductId] = Invoke-ProductEntity $item.ProductId }
    $backupPath = Join-Path $workingDirectory 'products-before.json'
    $original | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $backupPath -Encoding utf8
    $null = Invoke-ImageAzure storage container create --name cloudstore-catalog-backups --public-access off
    $null = Invoke-ImageAzure storage blob upload --container-name cloudstore-catalog-backups --name $backupName --file $backupPath --content-type application/json --overwrite false --no-progress
    $backup = Invoke-ImageAzure storage blob show --container-name cloudstore-catalog-backups --name $backupName
    if ($backup['properties']['contentLength'] -ne (Get-Item -LiteralPath $backupPath).Length) { throw 'Backup verification failed.' }
    Write-Host "Product record backup: cloudstore-catalog-backups / $backupName"

    $resolved = @{}
    foreach ($item in $manifest.Products) {
        $currentName = [string]$original[$item.ProductId]['Entity']['ImageName']
        if ($currentName -match '\A[a-f0-9]{32}\.png\z' -and (Test-BlobMatches $currentName $item)) {
            $resolved[$item.ProductId] = $currentName
            continue
        }
        $exists = Invoke-ImageAzure storage blob exists --container-name images --name $item.BlobName
        if (!$exists['exists']) {
            $null = Invoke-ImageAzure storage blob upload --container-name images --name $item.BlobName --file (Join-Path $DataDirectory $item.File) --type block --content-type image/png --content-disposition attachment --overwrite false --no-progress
        }
        if (!(Test-BlobMatches $item.BlobName $item)) { throw 'An Azure image differs from the original file; no existing blob was overwritten.' }
        $resolved[$item.ProductId] = $item.BlobName
    }
    # Every new image exists and is verified before any product is linked to it.
    foreach ($item in $manifest.Products) {
        $record = $original[$item.ProductId]
        if ([string]$record['Entity']['ImageName'] -cne $resolved[$item.ProductId]) {
            Invoke-ProductEntity -ProductId $item.ProductId -Method MERGE -ImageName $resolved[$item.ProductId] -ETag $record.ETag
        }
        $updated = Invoke-ProductEntity $item.ProductId
        if ($updated['Entity']['ImageName'] -cne $resolved[$item.ProductId]) { throw 'Product image reference verification failed.' }
        Write-Host "$($item.ProductId) -> $($resolved[$item.ProductId])"
    }
    Write-Host 'Verified: all six catalogue products reference private Azure Blob images. Existing images were reused when identical.'
    Write-Host 'No accounts, product descriptions/prices, orders, carts or contracts were reset or deleted.'
} finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
}
# //-------------------End Of File------------------//
