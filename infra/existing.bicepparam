//summary//
// Selects existing data resources and explicit hosting, alert and budget parameters.
//summary//
//-------------------Start Of File------------------//
using './main.bicep'
param provisionCore = false
param prefix = 'cloudstore'
param location = 'southafricanorth'
param functionsLocation = 'southafricanorth'
param businessStorageName = 'cloudstoredemo'
param sqlServerName = 'cloudstore-sql-1234'
param databaseName = 'cloudstore-accounts'
param webOutboundIps = []
param alertEmail = ''
param enableAlerts = false
param monthlyBudget = 10
param budgetStartDate = '2026-11-01'
param budgetEndDate = '2027-11-01'
//-------------------End Of File------------------//
