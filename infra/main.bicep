// The Azure deployment, declared: a Container Apps environment with its Log Analytics workspace, the app
// itself (scale to zero, one replica max — it is a demo), an Azure OpenAI account with the embedding
// deployment, and a Flexible Server for Postgres with pgvector allow-listed. Secrets arrive as parameters
// and land in the app's secret store; the embedding call uses the app's managed identity, not a key.
//
//   az group create -n rg-support-rag -l eastus2
//   az deployment group create -g rg-support-rag -f infra/main.bicep \
//       -p image=ghcr.io/twwalsh/dotnet-rag:latest anthropicApiKey=... pgPassword=... adminKey=...
targetScope = 'resourceGroup'

@description('Short name used as a prefix for every resource.')
param name string = 'support-rag'
param location string = resourceGroup().location
@description('Container image to run, e.g. ghcr.io/twwalsh/dotnet-rag:sha-abc123')
param image string
@secure()
param anthropicApiKey string
@secure()
param pgPassword string
@secure()
param adminKey string
param pgAdminUser string = 'rag'
param embeddingDeployment string = 'text-embedding-3-small'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-logs'
  location: location
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30 }
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${name}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: { customerId: logs.properties.customerId, sharedKey: logs.listKeys().primarySharedKey }
    }
  }
}

resource openai 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: '${name}-aoai'
  location: location
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: { customSubDomainName: '${name}-aoai', publicNetworkAccess: 'Enabled' }
}

resource embedding 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openai
  name: embeddingDeployment
  sku: { name: 'Standard', capacity: 50 }
  properties: { model: { format: 'OpenAI', name: 'text-embedding-3-small', version: '1' } }
}

resource pg 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: '${name}-pg'
  location: location
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: '17'
    administratorLogin: pgAdminUser
    administratorLoginPassword: pgPassword
    storage: { storageSizeGB: 32 }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
  }
}

// pgvector has to be allow-listed before CREATE EXTENSION works on Flexible Server.
resource pgExtensions 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: pg
  name: 'azure.extensions'
  properties: { value: 'VECTOR', source: 'user-override' }
}

resource pgDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: pg
  name: 'support_rag'
}

resource pgAllowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: pg
  name: 'allow-azure-services'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      ingress: { external: true, targetPort: 8080, transport: 'http' }
      secrets: [
        { name: 'anthropic-api-key', value: anthropicApiKey }
        { name: 'admin-key', value: adminKey }
        { name: 'pg-connection', value: 'Host=${pg.properties.fullyQualifiedDomainName};Database=support_rag;Username=${pgAdminUser};Password=${pgPassword};SSL Mode=Require' }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: image
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'ANTHROPIC_API_KEY', secretRef: 'anthropic-api-key' }
            { name: 'RAG_ADMIN_KEY', secretRef: 'admin-key' }
            { name: 'ConnectionStrings__Rag', secretRef: 'pg-connection' }
            { name: 'Rag__Embedder', value: 'AzureOpenAI' }
            { name: 'Rag__AzureOpenAI__Endpoint', value: openai.properties.endpoint }
            { name: 'Rag__AzureOpenAI__Deployment', value: embeddingDeployment }
            { name: 'Rag__AzureOpenAI__UseManagedIdentity', value: 'true' }
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          ]
          probes: [
            { type: 'Readiness', httpGet: { path: '/healthz', port: 8080 }, initialDelaySeconds: 5, periodSeconds: 10 }
            { type: 'Liveness', httpGet: { path: '/healthz', port: 8080 }, initialDelaySeconds: 15, periodSeconds: 30 }
          ]
        }
      ]
      scale: { minReplicas: 0, maxReplicas: 1 }
    }
  }
  dependsOn: [pgDb, pgExtensions, embedding]
}

// Cognitive Services OpenAI User: lets the app's identity call the embedding deployment without a key.
resource openaiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openai.id, app.id, 'openai-user')
  scope: openai
  properties: {
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  }
}

output url string = 'https://${app.properties.configuration.ingress.fqdn}'
output openaiEndpoint string = openai.properties.endpoint
