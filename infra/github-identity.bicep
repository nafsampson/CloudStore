//summary//
// Defines environment-scoped GitHub federation and separate deployment identities.
//summary//
//-------------------Start Of File------------------//
targetScope = 'resourceGroup'
param location string = resourceGroup().location
param repository string // owner/repository, exact case from GitHub
param webAppName string
param functionAppName string
param prefix string = 'cloudstore'
resource web 'Microsoft.Web/sites@2023-12-01' existing = { name: webAppName }
resource worker 'Microsoft.Web/sites@2023-12-01' existing = { name: functionAppName }
resource publisher 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-github-publisher'
  location: location
}
resource publishingTrust 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: publisher
  name: 'github-production'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${repository}:environment:azure-production'
    audiences: ['api://AzureADTokenExchange']
  }
}
resource webPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: web
  name: guid(web.id, publisher.id, 'website-contributor')
  properties: {
    principalId: publisher.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'de139f84-1756-47ae-9be6-808fbbe84772'
    )
  }
}
resource workerPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: worker
  name: guid(worker.id, publisher.id, 'website-contributor')
  properties: {
    principalId: publisher.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'de139f84-1756-47ae-9be6-808fbbe84772'
    )
  }
}
resource operator 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-github-infrastructure'
  location: location
}
resource infrastructureTrust 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = [
  for environmentName in ['azure-preview', 'azure-infrastructure']: {
    parent: operator
    name: 'github-${environmentName}'
    properties: {
      issuer: 'https://token.actions.githubusercontent.com'
      subject: 'repo:${repository}:environment:${environmentName}'
      audiences: ['api://AzureADTokenExchange']
    }
  }
]
resource infrastructureRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleId in [
    'b24988ac-6180-42a0-ab88-20f7382dd24c' // Contributor: infrastructure in this resource group only.
    'f58310d9-a9f6-439a-9e8d-f62e7b41a168' // RBAC Administrator: assign managed identity roles here.
  ]: {
    name: guid(resourceGroup().id, operator.id, roleId)
    properties: {
      principalId: operator.properties.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    }
  }
]
output publisherClientId string = publisher.properties.clientId
output infrastructureClientId string = operator.properties.clientId
output tenantId string = tenant().tenantId
output subscriptionId string = subscription().subscriptionId
//-------------------End Of File------------------//
