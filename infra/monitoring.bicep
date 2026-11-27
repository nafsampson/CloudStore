//summary//
// Defines the operations workbook, budget notifications and optional failure alerts.
//summary//
//-------------------Start Of File------------------//
targetScope = 'resourceGroup'
param location string
param prefix string
param workspaceId string
param insightsId string
param workerName string
param alertEmail string
param monthlyBudget int
param budgetStartDate string
param budgetEndDate string
param enableAlerts bool

resource group 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${prefix}-operators'
  location: 'global'
  properties: {
    groupShortName: 'CloudStore'
    enabled: true
    emailReceivers: empty(alertEmail)
      ? []
      : [{ name: 'operator', emailAddress: alertEmail, useCommonAlertSchema: true }]
  }
}
resource failures 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = if (enableAlerts) {
  name: '${prefix}-order-failures'
  location: location
  properties: {
    displayName: 'CloudStore order processing needs investigation'
    description: 'Failed worker invocations or explicit order-delivery failures in the last 15 minutes.'
    enabled: true
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [workspaceId]
    criteria: {
      allOf: [
        {
          query: 'union (AppRequests | where AppRoleName == "${workerName}" and Success == false), (AppTraces | where Message startswith "CloudStore order delivery failed")'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: { actionGroups: [group.id] }
    autoMitigate: true
  }
}
resource webErrors 'Microsoft.Insights/metricAlerts@2018-03-01' = if (enableAlerts) {
  name: '${prefix}-web-failures'
  location: 'global'
  properties: {
    description: 'More than five failed requests in five minutes. Investigate dependencies and deployments.'
    enabled: true
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [insightsId]
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'failedRequests'
          metricNamespace: 'microsoft.insights/components'
          metricName: 'requests/failed'
          operator: 'GreaterThan'
          threshold: 5
          timeAggregation: 'Count'
        }
      ]
    }
    actions: [{ actionGroupId: group.id }]
  }
}
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: '${prefix}-monthly'
  properties: {
    category: 'Cost'
    amount: monthlyBudget
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate, endDate: budgetEndDate }
    notifications: empty(alertEmail)
      ? {}
      : {
          actual80: {
            enabled: true
            operator: 'GreaterThanOrEqualTo'
            threshold: 80
            thresholdType: 'Actual'
            contactEmails: [alertEmail]
          }
          forecast100: {
            enabled: true
            operator: 'GreaterThanOrEqualTo'
            threshold: 100
            thresholdType: 'Forecasted'
            contactEmails: [alertEmail]
          }
        }
  }
}
resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  name: guid(resourceGroup().id, prefix, 'operations')
  location: location
  kind: 'shared'
  properties: {
    displayName: 'CloudStore operations'
    sourceId: insightsId
    category: 'workbook'
    serializedData: loadTextContent('../monitoring/workbook.json')
  }
}
//-------------------End Of File------------------//
