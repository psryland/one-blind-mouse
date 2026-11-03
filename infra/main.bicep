// One-Blind-Mouse — Azure infrastructure.
//
// Deploys:
//   - Azure Container Registry (Basic, admin user disabled) for the relay image
//   - Log Analytics workspace for container logs
//   - Container Apps managed environment (Consumption)
//   - Container App running the relay (WebSockets at wss://<fqdn>/v1)
//   - Azure Static Web App (Free) for the landing page and /j/<CODE> join links
//
// Deployed in two phases by deploy.ps1, because the relay image must be pushed to the registry before the Container App can use it:
//   1. deploy_app=false: registry, pull identity, logs, environment and static web app
//   2. deploy_app=true:  the above plus the Container App, using relay_image

targetScope = 'resourceGroup'

@description('Short prefix for resource names')
@minLength(2)
@maxLength(10)
param name_prefix string = 'obm'

@description('Azure region for the registry, logs and container app')
param location string = resourceGroup().location

@description('Azure region for the static web app (only some regions offer Static Web Apps)')
@allowed(['centralus', 'eastus2', 'eastasia', 'westeurope', 'westus2'])
param swa_location string = 'eastasia'

@description('Deploy the relay Container App. Requires relay_image to exist in the registry.')
param deploy_app bool = false

@description('Relay image reference in the registry, e.g. "obm-relay:20240101-1200"')
param relay_image string = 'obm-relay:latest'

// Registry names must be globally unique and alphanumeric only
var suffix = uniqueString(resourceGroup().id)
var acr_name = toLower(replace('${name_prefix}${suffix}', '-', ''))

// Built-in AcrPull role
var acr_pull_role_id = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
	name: acr_name
	location: location
	sku: {
		name: 'Basic'
	}
	properties: {
		adminUserEnabled: false
	}
}

// The relay pulls its image with this identity. It is created and granted AcrPull in the base phase, so the permission
// has propagated before the Container App first pulls (a system-assigned identity would only exist once the app does).
resource pull_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
	name: '${name_prefix}-relay-pull'
	location: location
}

resource pull_identity_acr_pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
	name: guid(registry.id, pull_identity.id, acr_pull_role_id)
	scope: registry
	properties: {
		roleDefinitionId: acr_pull_role_id
		principalId: pull_identity.properties.principalId
		principalType: 'ServicePrincipal'
	}
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
	name: '${name_prefix}-logs'
	location: location
	properties: {
		sku: {
			name: 'PerGB2018'
		}
		retentionInDays: 30
	}
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
	name: '${name_prefix}-env'
	location: location
	properties: {
		appLogsConfiguration: {
			destination: 'log-analytics'
			logAnalyticsConfiguration: {
				customerId: logs.properties.customerId
				sharedKey: logs.listKeys().primarySharedKey
			}
		}
		workloadProfiles: [
			{
				name: 'Consumption'
				workloadProfileType: 'Consumption'
			}
		]
	}
}

resource relay 'Microsoft.App/containerApps@2024-03-01' = if (deploy_app) {
	name: '${name_prefix}-relay'
	location: location
	identity: {
		type: 'UserAssigned'
		userAssignedIdentities: {
			'${pull_identity.id}': {}
		}
	}
	properties: {
		environmentId: environment.id
		workloadProfileName: 'Consumption'
		configuration: {
			// HTTP ingress carries WebSocket upgrades, so 'auto' transport is enough
			ingress: {
				external: true
				targetPort: 8080
				transport: 'auto'
				allowInsecure: false
			}

			// Pull from the registry using the pull identity (no registry passwords)
			registries: [
				{
					server: registry.properties.loginServer
					identity: pull_identity.id
				}
			]
		}
		template: {
			containers: [
				{
					name: 'relay'
					image: '${registry.properties.loginServer}/${relay_image}'
					resources: {
						cpu: json('0.25')
						memory: '0.5Gi'
					}
					probes: [
						{
							type: 'Liveness'
							httpGet: {
								path: '/healthz'
								port: 8080
							}
							periodSeconds: 30
						}
						{
							type: 'Readiness'
							httpGet: {
								path: '/healthz'
								port: 8080
							}
							periodSeconds: 10
						}
					]
				}
			]

			// Rooms live in the relay's memory, so exactly one replica must run.
			// More replicas would split peers of the same room across processes that cannot see each other.
			scale: {
				minReplicas: 1
				maxReplicas: 1
			}
		}
	}
}

resource site 'Microsoft.Web/staticSites@2023-12-01' = {
	name: '${name_prefix}-site'
	location: swa_location
	sku: {
		name: 'Free'
		tier: 'Free'
	}
	properties: {}
}

@description('Registry name, for "az acr build"')
output registry_name string = registry.name

@description('Registry login server')
output registry_login_server string = registry.properties.loginServer

@description('Relay WebSocket URL to compile into the Windows app (empty until the app phase)')
output relay_url string = deploy_app ? 'wss://${relay!.properties.configuration.ingress.fqdn}/v1' : ''

@description('Static web app name')
output site_name string = site.name

@description('Static web app default host name')
output site_hostname string = site.properties.defaultHostname
