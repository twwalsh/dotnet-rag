// The Azure deployment, declared: a Container Apps environment with its Log Analytics workspace, a
// container registry the image is built INTO (az acr build, so no local Docker is needed), a user-assigned
// identity the app uses to pull from the registry and to call Azure OpenAI (no keys for either), the Azure
// OpenAI account with the embedding deployment, a Flexible Server for Postgres with pgvector allow-listed,
// and the app itself (scale to zero, one replica max: it is a demo). Secrets arrive as parameters and land
// in the app's secret store.
//
// Two passes, because the app cannot start on an image that does not exist yet:
//   az group create -n rg-support-rag -l eastus
//   az deployment group create -g rg-support-rag -f infra/main.bicep -p anthropicApiKey=... pgPassword=... adminKey=...
//   az acr build -r <registry> -t support-rag:<sha> .
//   az deployment group create -g rg-support-rag -f infra/main.bicep -p image=<registry>.azurecr.io/support-rag:<sha> ...
targetScope = 'resourceGroup'

@description('Short name used as a prefix for every resource.')
param name string = 'support-rag'
param location string = resourceGroup().location
@description('Container image to run. The default is a public placeholder for the first pass, before the registry holds a build.')
param image string = 'mcr.microsoft.com/k8se/quickstart:latest'
@secure()
param anthropicApiKey string
@secure()
param pgPassword string
@secure()
param adminKey string
param pgAdminUser string = 'rag'
@description('Postgres Flexible Server region. Separate because East US refuses Postgres provisioning on some subscriptions ("Provisioning is restricted in this region") while serving everything else.')
param pgLocation string = 'eastus2'
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

// Basic tier is enough: one small image, rebuilt a few times a week at most.
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: replace('${name}acr${uniqueString(resourceGroup().id)}', '-', '')
  location: location
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

// One identity, created before the app so it can be granted AcrPull and OpenAI access up front.
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-id'
  location: location
}

// AcrPull
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, identity.id, 'acrpull')
  scope: acr
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
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
  location: pgLocation
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

// Cognitive Services OpenAI User: lets the identity call the embedding deployment without a key.
resource openaiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openai.id, identity.id, 'openai-user')
  scope: openai
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      ingress: { external: true, targetPort: 8080, transport: 'http' }
      registries: [
        { server: acr.properties.loginServer, identity: identity.id }
      ]
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
            { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
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
  dependsOn: [pgDb, pgExtensions, embedding, acrPull, openaiUser]
}

output url string = 'https://${app.properties.configuration.ingress.fqdn}'
output openaiEndpoint string = openai.properties.endpoint
output registry string = acr.name
output registryServer string = acr.properties.loginServer
output pgHost string = pg.properties.fullyQualifiedDomainName
