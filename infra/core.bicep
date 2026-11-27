//summary//
// Defines business storage and the account database for a new environment.
//summary//
//-------------------Start Of File------------------//
targetScope = 'resourceGroup'
param location string
param storageName string
param sqlServerName string
param databaseName string
param entraAdminObjectId string
param entraAdminLogin string
param tags object

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true // Azure Files REST compatibility; key is held in Key Vault.
    publicNetworkAccess: 'Enabled'
  }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}
resource images 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'images'
  properties: { publicAccess: 'None' }
}
resource queues 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = { parent: storage, name: 'default' }
resource queue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = [
  for name in ['orders-queue', 'operations']: {
    parent: queues
    name: name
  }
]
resource tables 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = { parent: storage, name: 'default' }
resource table 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = [
  for name in ['Products', 'Orders', 'Cart']: {
    parent: tables
    name: name
  }
]
resource files 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = { parent: storage, name: 'default' }
resource contracts 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: files
  name: 'contracts'
  properties: { shareQuota: 1, accessTier: 'TransactionOptimized', enabledProtocols: 'SMB' }
}
resource sql 'Microsoft.Sql/servers@2023-08-01' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      principalType: 'User'
      login: entraAdminLogin
      sid: entraAdminObjectId
      tenantId: tenant().tenantId
    }
  }
}
resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sql
  name: databaseName
  location: location
  tags: tags
  sku: { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: 2 }
  properties: {
    maxSizeBytes: 34359738368
    minCapacity: json('0.5')
    autoPauseDelay: 60
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    requestedBackupStorageRedundancy: 'Local'
    zoneRedundant: false
  }
}
//-------------------End Of File------------------//
