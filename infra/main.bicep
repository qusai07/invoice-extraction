// InvoiceFlow on Azure — one resource group, UAE North by default (data residency).
//
//   az deployment group create -g rg-invoiceflow -f infra/main.bicep -p infra/main.parameters.json \
//     -p sessionSigningKey=<base64 32 bytes> mfaEncryptionKey=<base64 32 bytes>
//   (generate each with: openssl rand -base64 32; store them in your secret store, they are needed for redeploys)
//
// Identity model: ONE user-assigned managed identity for API + worker. No keys or passwords anywhere:
//   SQL (Entra-only auth), Blob (RBAC), Azure OpenAI (RBAC, local auth disabled), ACR pull (RBAC).
// After the first deployment run infra/post-deploy.sql against the database (creates the DB user for the identity).

targetScope = 'resourceGroup'

@description('Short name used in resource names, e.g. "invflow". Lowercase letters/digits, 3-12 chars.')
@minLength(3)
@maxLength(12)
param prefix string

@description('Azure region for everything. UAE North keeps data in the UAE.')
param location string = 'uaenorth'

@description('Entra ID tenant of the organisation (sign-in for users, admin for SQL).')
param tenantId string = subscription().tenantId

@description('Entra ID group that administers Azure SQL (display name and object id).')
param sqlAdminGroupName string
param sqlAdminGroupObjectId string

@description('Signs session tokens: 32+ random bytes, base64. Pass at deploy time (never in the parameters file); rotating it signs everyone out.')
@secure()
param sessionSigningKey string

@description('Encrypts users\' two-step sign-in secrets: exactly 32 random bytes, base64. Pass at deploy time; keep it stable (losing it means every user sets up two-step sign-in again).')
@secure()
param mfaEncryptionKey string

@description('Container images, e.g. <acr>.azurecr.io/invoiceflow-api:1.0.0. Build and push before deploying the apps.')
param apiImage string
param workerImage string
param webImage string

@description('Azure OpenAI model deployment (vision + structured outputs).')
param openAiModelName string = 'gpt-4.1'
param openAiModelVersion string = '2025-04-14'
@description('Regional (data stays in region) = Standard. GlobalStandard routes globally.')
@allowed(['Standard', 'DataZoneStandard', 'GlobalStandard'])
param openAiDeploymentSku string = 'Standard'
param openAiCapacity int = 30

@description('Our own company TRN(s): invoices addressed to another TRN are stopped (CUS-001).')
param companyTrns array = []

@description('Invoices at or above this total need a second approver.')
param approvalThreshold int = 100000

param tags object = {
  app: 'invoiceflow'
}

var suffix = uniqueString(resourceGroup().id)
var names = {
  identity: 'id-${prefix}'
  logs: 'log-${prefix}-${suffix}'
  insights: 'appi-${prefix}'
  acr: take('acr${prefix}${suffix}', 50)
  storage: take('st${prefix}${suffix}', 24)
  sql: 'sql-${prefix}-${suffix}'
  openai: 'oai-${prefix}-${suffix}'
  env: 'cae-${prefix}'
  api: '${prefix}-api'
  worker: '${prefix}-worker'
  web: '${prefix}-web'
}

// Built-in role definition ids
var roles = {
  acrPull: '7f951dca-d520-4ae9-a2e7-55a1f1aef4b2'
  // Owner (not Contributor) on the container only: reading blob index tags (Defender verdict) needs blobs/tags/read.
  blobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  openAiUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
}

// ---------------------------------------------------------------- identity & monitoring

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.identity
  location: location
  tags: tags
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logs
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 90
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: names.insights
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    DisableLocalAuth: false
  }
}

// ---------------------------------------------------------------- container registry

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: names.acr
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: {
    adminUserEnabled: false
  }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, identity.id, roles.acrPull)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------- storage (original invoices)

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: names.storage
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_ZRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false // RBAC only
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    // Originals are evidence for VAT audits: keep deleted blobs recoverable.
    deleteRetentionPolicy: { enabled: true, days: 30 }
    containerDeleteRetentionPolicy: { enabled: true, days: 30 }
    isVersioningEnabled: true
  }
}

resource invoicesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'invoices'
  properties: { publicAccess: 'None' }
}

resource blobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(invoicesContainer.id, identity.id, roles.blobDataOwner)
  scope: invoicesContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataOwner)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Malware scanning on upload. Verdicts are written as blob index tags; the app reads nothing without a clean verdict.
resource defender 'Microsoft.Security/defenderForStorageSettings@2022-12-01-preview' = {
  name: 'current'
  scope: storage
  properties: {
    isEnabled: true
    overrideSubscriptionLevelSettings: true
    malwareScanning: {
      onUpload: { isEnabled: true, capGBPerMonth: 100 }
    }
    sensitiveDataDiscovery: { isEnabled: false }
  }
}

// ---------------------------------------------------------------- database

resource sql 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: names.sql
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled' // v1: firewall "Azure services" only. Phase 5: private endpoint + VNet.
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true // no SQL logins/passwords
      login: sqlAdminGroupName
      sid: sqlAdminGroupObjectId
      tenantId: tenantId
      principalType: 'Group'
    }
  }
}

resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sql
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource db 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sql
  name: 'InvoiceFlow'
  location: location
  tags: tags
  sku: { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: 2 }
  properties: {
    autoPauseDelay: 60
    minCapacity: json('0.5')
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Zone'
  }
}

// ---------------------------------------------------------------- Azure OpenAI

resource openai 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: names.openai
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: names.openai
    disableLocalAuth: true // Entra ID only, no API keys
    publicNetworkAccess: 'Enabled'
  }
}

resource model 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openai
  name: 'invoice-extraction'
  sku: { name: openAiDeploymentSku, capacity: openAiCapacity }
  properties: {
    model: { format: 'OpenAI', name: openAiModelName, version: openAiModelVersion }
  }
}

resource openAiAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openai.id, identity.id, roles.openAiUser)
  scope: openai
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.openAiUser)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------- container apps

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: names.env
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

var sqlConnection = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=${db.name};Authentication=Active Directory Managed Identity;User Id=${identity.properties.clientId};Encrypt=True;'

var companyTrnEnv = [for (trn, i) in companyTrns: { name: 'Validation__CompanyTrns__${i}', value: trn }]

var commonEnv = concat([
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId } // DefaultAzureCredential → this identity
  { name: 'ConnectionStrings__InvoiceFlow', value: sqlConnection }
  { name: 'Storage__Provider', value: 'AzureBlob' }
  { name: 'Storage__AzureBlob__ServiceUri', value: storage.properties.primaryEndpoints.blob }
  { name: 'Storage__AzureBlob__Container', value: invoicesContainer.name }
  { name: 'Security__MalwareScan__Provider', value: 'DefenderForStorage' }
  { name: 'Extraction__AzureOpenAI__Endpoint', value: openai.properties.endpoint }
  { name: 'Extraction__AzureOpenAI__Deployment', value: model.name }
  { name: 'Validation__ApprovalThreshold', value: string(approvalThreshold) }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
], companyTrnEnv)

var registries = [
  { server: acr.properties.loginServer, identity: identity.id }
]

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: names.api
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  dependsOn: [acrPull]
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      // Internal only: reached by the web app through the environment, never from the internet.
      ingress: { external: false, targetPort: 8080, transport: 'http', allowInsecure: true }
      registries: registries
      secrets: [
        { name: 'session-signing-key', value: sessionSigningKey }
        { name: 'mfa-encryption-key', value: mfaEncryptionKey }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat(commonEnv, [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            // InvoiceFlow's own sign-in (ADR-0009); keys come from Container Apps secrets, never plain env values.
            { name: 'Auth__Mode', value: 'Session' }
            { name: 'Auth__Session__SigningKey', secretRef: 'session-signing-key' }
            { name: 'Auth__Session__MfaEncryptionKey', secretRef: 'mfa-encryption-key' }
            { name: 'Database__MigrateOnStartup', value: 'true' }
            { name: 'ForwardedHeaders__Enabled', value: 'true' }
          ])
          probes: [
            { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 30 }
            { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 }, periodSeconds: 15 }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 4 }
    }
  }
}

resource worker 'Microsoft.App/containerApps@2024-03-01' = {
  name: names.worker
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  dependsOn: [acrPull, api] // API applies migrations first
  properties: {
    managedEnvironmentId: env.id
    configuration: { registries: registries }
    template: {
      containers: [
        {
          name: 'worker'
          image: workerImage
          resources: { cpu: json('1.0'), memory: '2Gi' } // PDF rendering
          env: concat(commonEnv, [
            { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
            { name: 'Pipeline__Concurrency', value: '4' }
          ])
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

resource web 'Microsoft.App/containerApps@2024-03-01' = {
  name: names.web
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  dependsOn: [acrPull]
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      ingress: { external: true, targetPort: 3000, transport: 'auto', allowInsecure: false }
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage // built with --build-arg API_BASE_URL=http://${names.api}
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

output webUrl string = 'https://${web.properties.configuration.ingress.fqdn}'
output apiInternalName string = names.api
output acrLoginServer string = acr.properties.loginServer
output identityClientId string = identity.properties.clientId
output identityName string = identity.name
output sqlServer string = sql.properties.fullyQualifiedDomainName
