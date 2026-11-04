// One-Blind-Mouse — Azure infrastructure.
//
// Deploys:
//   - Log Analytics workspace for container logs
//   - Container Apps managed environment (Consumption)
//   - Container App running the relay (WebSockets at wss://<fqdn>/v1), scaled to zero when nobody is connected
//   - Azure Static Web App (Free) for the landing page and /j/<CODE> join links
//
// The relay image is a public GitHub Container Registry package built by .github/workflows/relay-image.yml, so no registry is deployed here.

targetScope = 'resourceGroup'

@description('Short prefix for resource names')
@minLength(2)
@maxLength(10)
param name_prefix string = 'obm'

@description('Azure region for the logs and container app')
param location string = resourceGroup().location

@description('Azure region for the static web app (only some regions offer Static Web Apps)')
@allowed(['centralus', 'eastus2', 'eastasia', 'westeurope', 'westus2'])
param swa_location string = 'eastasia'

@description('Public relay image reference, pinned by digest so each new image starts a new revision, e.g. "ghcr.io/psryland/one-blind-mouse-relay@sha256:..."')
param relay_image string

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

resource relay 'Microsoft.App/containerApps@2024-03-01' = {
	name: '${name_prefix}-relay'
	location: location
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
		}
		template: {
			containers: [
				{
					name: 'relay'
					image: relay_image
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

			// Rooms live in the relay's memory, so at most one replica may run.
			// More replicas would split peers of the same room across processes that cannot see each other.
			// The replica stops when no requests are open (each client's WebSocket is an open request) for the cool-down period, so the relay
			// costs nothing while unused. The first connection after that waits while a replica starts; nothing is lost because no client is connected.
			scale: {
				minReplicas: 0
				maxReplicas: 1
				rules: [
					{
						name: 'connections'
						http: {
							metadata: {
								concurrentRequests: '1000'
							}
						}
					}
				]
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

@description('Relay WebSocket URL to compile into the Windows app')
output relay_url string = 'wss://${relay.properties.configuration.ingress.fqdn}/v1'

@description('Static web app name')
output site_name string = site.name

@description('Static web app default host name')
output site_hostname string = site.properties.defaultHostname
