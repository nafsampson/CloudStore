//summary//
// Defines application hosting, identity, secrets and monitoring around configured data resources.
//summary//
//-------------------Start Of File------------------//
targetScope = 'resourceGroup'
@description('Reuse existing core resources by default. Enable only in a new, reviewed resource group.')
param provisionCore bool = false
param location string = 'southafricanorth'
@description('Flex Consumption must be available in this region for your subscription.')
param functionsLocation string = location
@minLength(3)
@maxLength(12)
param prefix string = 'cloudstore'
param businessStorageName string = 'cloudstoredemo'
param sqlServerName string = 'cloudstore-sql-1234'
param databaseName string = 'cloudstore-accounts'
param entraAdminObjectId string = ''
param entraAdminLogin string = ''
@description('Public outbound IPv4 addresses from the web app. Obtain after initial deployment, then redeploy.')
param webOutboundIps array = []
@description('Optional email for alerts and budget notifications. Leave empty to omit email recipients.')
param alertEmail string = ''
@minValue(1)
param monthlyBudget int = 10
@description('Budget start must be the first day of the current month; end a future date.')
param budgetStartDate string
param budgetEndDate string
@description('Alerts have their own charges. Leave off until telemetry ingestion and costs are reviewed.')
param enableAlerts bool = false
var suffix = uniqueString(resourceGroup().id, prefix)
var tags = { application: 'CloudStore', environment: 'demo', managedBy: 'Bicep' }
var webName = '${prefix}-web-${suffix}'
var workerName = '${prefix}-worker-${suffix}'
// Keep the full deterministic suffix while respecting Key Vault's 24-character limit.
var vaultName = 'cs-kv-${suffix}'
var hostName = 'cs${suffix}host'

module core './core.bicep' = if (provisionCore) {
  name: 'core'
  params: {
    location: location
    storageName: businessStorageName
    sqlServerName: sqlServerName
    databaseName: databaseName
    entraAdminObjectId: entraAdminObjectId
    entraAdminLogin: entraAdminLogin
    tags: tags
  }
}
resource business 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: businessStorageName }
resource sql 'Microsoft.Sql/servers@2023-08-01' existing = { name: sqlServerName }
resource firewall 'Microsoft.Sql/servers/firewallRules@2023-08-01' = [
  for ip in webOutboundIps: {
    parent: sql
    name: 'web-${replace(ip, '.', '-')}'
    properties: { startIpAddress: ip, endIpAddress: ip }
    dependsOn: [core]
  }
]
resource webIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-web-identity'
  location: location
  tags: tags
}
resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-worker-identity'
  location: functionsLocation
  tags: tags
}
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
    accessPolicies: []
  }
}
resource storageSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'business-storage'
  properties: {
    value: 'DefaultEndpointsProtocol=https;AccountName=${business.name};AccountKey=${business.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
  }
  dependsOn: [core]
}
resource secretReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for index in range(0, 2): {
    scope: vault
    name: guid(vault.id, string(index), 'secret-reader')
    properties: {
      roleDefinitionId: subscriptionResourceId(
        'Microsoft.Authorization/roleDefinitions',
        '4633458b-17de-408a-b874-0445c86b69e6'
      )
      principalId: index == 0 ? webIdentity.properties.principalId : workerIdentity.properties.principalId
      principalType: 'ServicePrincipal'
    }
  }
]
resource hostStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: hostName
  location: functionsLocation
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
  }
}
resource hostBlobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: hostStorage
  name: 'default'
}
resource deployments 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: hostBlobs
  name: 'deployments'
  properties: { publicAccess: 'None' }
}
resource hostRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: hostStorage
  name: guid(hostStorage.id, workerIdentity.id, 'blob-owner')
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
    )
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs-${suffix}'
  location: location
  tags: tags
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30, workspaceCapping: { dailyQuotaGb: json('0.1') } }
}
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-insights-${suffix}'
  location: location
  kind: 'web'
  tags: tags
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id }
}
resource webPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${prefix}-web-plan'
  location: location
  tags: tags
  sku: { name: 'F1', tier: 'Free' }
  properties: { reserved: false }
}
resource web 'Microsoft.Web/sites@2023-12-01' = {
  name: webName
  location: location
  tags: tags
  kind: 'app'
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${webIdentity.id}': {} } }
  properties: {
    serverFarmId: webPlan.id
    httpsOnly: true
    keyVaultReferenceIdentity: webIdentity.id
    siteConfig: {
      netFrameworkVersion: 'v10.0'
      use32BitWorkerProcess: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'Storage__Mode', value: 'Azure' }
        {
          name: 'AzureStorage__ConnectionString'
          value: '@Microsoft.KeyVault(SecretUri=${vault.properties.vaultUri}secrets/${storageSecret.name})'
        }
        {
          name: 'ConnectionStrings__DefaultConnection'
          value: 'Server=tcp:${sqlServerName}${environment().suffixes.sqlServerHostname},1433;Database=${databaseName};Authentication=Active Directory Managed Identity;User Id=${webIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False;'
        }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'APPLICATIONINSIGHTS_ROLE_NAME', value: 'cloudstore-web' }
        { name: 'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command', value: 'Warning' }
      ]
    }
  }
  dependsOn: [secretReaders]
}
resource flexPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${prefix}-flex-plan'
  location: functionsLocation
  kind: 'functionapp'
  tags: tags
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}
resource worker 'Microsoft.Web/sites@2024-04-01' = {
  name: workerName
  location: functionsLocation
  kind: 'functionapp,linux'
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${workerIdentity.id}': {} } }
  properties: {
    serverFarmId: flexPlan.id
    httpsOnly: true
    keyVaultReferenceIdentity: workerIdentity.id
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${hostStorage.properties.primaryEndpoints.blob}${deployments.name}'
          authentication: { type: 'UserAssignedIdentity', userAssignedIdentityResourceId: workerIdentity.id }
        }
      }
      runtime: { name: 'dotnet-isolated', version: '10.0' }
      scaleAndConcurrency: { maximumInstanceCount: 40, instanceMemoryMB: 512 }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: hostStorage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: workerIdentity.properties.clientId }
        {
          name: 'StorageConnection'
          value: '@Microsoft.KeyVault(SecretUri=${vault.properties.vaultUri}secrets/${storageSecret.name})'
        }
        { name: 'OrdersQueueName', value: 'orders-queue' }
        { name: 'OrderTable', value: 'Orders' }
        { name: 'FileShareName', value: 'contracts' }
        { name: 'EnableStorageHttpWrites', value: 'false' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'Logging__LogLevel__Default', value: 'Information' }
      ]
    }
  }
  dependsOn: [hostRole, secretReaders]
}
module monitoring './monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    prefix: prefix
    workspaceId: logs.id
    insightsId: insights.id
    workerName: worker.name
    alertEmail: alertEmail
    monthlyBudget: monthlyBudget
    budgetStartDate: budgetStartDate
    budgetEndDate: budgetEndDate
    enableAlerts: enableAlerts
  }
}
output webAppName string = web.name
output webUrl string = 'https://${web.properties.defaultHostName}'
output functionAppName string = worker.name
output webIdentityObjectId string = webIdentity.properties.principalId
output webIdentityClientId string = webIdentity.properties.clientId
output keyVaultName string = vault.name
output workspaceName string = logs.name
output applicationInsightsName string = insights.name
output webOutboundIpAddresses string = web.properties.possibleOutboundIpAddresses
//-------------------End Of File------------------//
