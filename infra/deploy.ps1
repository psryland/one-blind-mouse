#!/usr/bin/env pwsh
# One-Blind-Mouse — infrastructure and relay deployment.
#
# Prerequisites:
#   - Azure CLI, logged in (az login) with access to the target subscription
#   - Node.js (npx), for uploading the landing page with the Static Web Apps CLI
#
# Usage:
#   ./infra/deploy.ps1 -Subscription <name or id> -ResourceGroup rg-one-blind-mouse
#   ./infra/deploy.ps1 -Subscription <name or id> -ResourceGroup rg-one-blind-mouse -Location australiaeast -NamePrefix obm -CustomDomain one-blind-mouse.rylogic.co.nz -RelayTag latest
#
# Steps:
#   1. Create the resource group
#   2. Resolve the relay image's digest in GitHub Container Registry. The image is built and pushed by .github/workflows/relay-image.yml.
#   3. Deploy the resources (logs, Container Apps environment, relay Container App, static web app)
#   4. Upload web/ to the static web app, and add the custom domain if one is given
#   5. Print the relay URL to compile into the Windows app
#
# A custom domain needs a CNAME record pointing at the static web app's default host name before this runs; Azure validates it and issues a free certificate.

param(
	[Parameter(Mandatory = $true)]
	[string]$Subscription,
	[Parameter(Mandatory = $true)]
	[string]$ResourceGroup,
	[string]$Location = "australiaeast",
	[string]$NamePrefix = "obm",
	[string]$CustomDomain = "",
	[string]$RelayTag = "latest"
)

$ErrorActionPreference = "Stop"
$repo_root = Split-Path -Parent $PSScriptRoot
$template = Join-Path $PSScriptRoot "main.bicep"
$relay_repository = "psryland/one-blind-mouse-relay"

# Run an az command against the chosen subscription and stop the script if it fails ($ErrorActionPreference does not cover native commands).
# The subscription is passed explicitly so the machine-wide az default is never used or changed.
function Invoke-Az
{
	# Pass the arguments through and check the exit code
	$output = & az @args --subscription $Subscription
	if ($LASTEXITCODE -ne 0)
	{
		throw "az $($args -join ' ') failed with exit code $LASTEXITCODE"
	}
	return $output
}

# Deploy main.bicep with the given extra parameters and return its outputs
function Deploy-Template([string]$Name, [string[]]$Parameters)
{
	# Deployment outputs come back as JSON
	$json = Invoke-Az deployment group create `
		--resource-group $ResourceGroup `
		--name $Name `
		--template-file $template `
		--parameters name_prefix=$NamePrefix location=$Location @Parameters `
		--query properties.outputs `
		--output json
	return ($json | Out-String | ConvertFrom-Json)
}

# Return the relay image reference pinned to the digest that $RelayTag currently names.
# Pinning by digest means a redeploy starts a new revision exactly when the image has changed.
function Resolve-RelayImage
{
	# GHCR issues anonymous pull tokens for public packages, so this needs no GitHub login
	$token = (Invoke-RestMethod -Uri "https://ghcr.io/token?scope=repository:$($relay_repository):pull").token
	$headers = @{
		Authorization = "Bearer $token"
		Accept = "application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json"
	}
	try
	{
		# The manifest's digest is returned in a response header
		$response = Invoke-WebRequest -Method Head -Uri "https://ghcr.io/v2/$relay_repository/manifests/$RelayTag" -Headers $headers
	}
	catch
	{
		throw "Relay image 'ghcr.io/$($relay_repository):$RelayTag' was not found. Run the 'Relay image' GitHub workflow, and make sure the package is public. ($_)"
	}
	$digest = @($response.Headers["Docker-Content-Digest"])[0]
	return "ghcr.io/$relay_repository@$digest"
}

# 1. Resource group
Write-Host "`n=== One-Blind-Mouse deployment ===" -ForegroundColor Cyan
Write-Host "Creating resource group '$ResourceGroup' in $Location..." -ForegroundColor Yellow
Invoke-Az group create --name $ResourceGroup --location $Location --output none | Out-Null

# 2. Relay image
$image = Resolve-RelayImage
Write-Host "Relay image: $image" -ForegroundColor Yellow

# 3. Resources. Registering providers is idempotent and avoids first-use failures in new subscriptions.
Write-Host "Registering resource providers..." -ForegroundColor Yellow
foreach ($provider in @("Microsoft.App", "Microsoft.OperationalInsights", "Microsoft.Web"))
{
	Invoke-Az provider register --namespace $provider --wait --output none | Out-Null
}
Write-Host "Deploying resources..." -ForegroundColor Yellow
$app = Deploy-Template -Name "obm" -Parameters @("relay_image=$image")
$site_name = $app.site_name.value
$relay_url = $app.relay_url.value
$site_hostname = $app.site_hostname.value

# 4. Landing page. Stage only the site files, so stray local folders under web/ (e.g. node_modules) are never uploaded.
Write-Host "Uploading landing page to static web app '$site_name'..." -ForegroundColor Yellow
$token = Invoke-Az staticwebapp secrets list --name $site_name --resource-group $ResourceGroup --query "properties.apiKey" --output tsv
$staging = Join-Path $repo_root ".deploy-web"
try
{
	# Copy the site into a clean folder and upload it
	if (Test-Path $staging)
	{
		Remove-Item $staging -Recurse -Force
	}
	New-Item -ItemType Directory -Path $staging | Out-Null
	$web = Join-Path $repo_root "web"
	Copy-Item (Join-Path $web "*.html"), (Join-Path $web "*.css"), (Join-Path $web "staticwebapp.config.json") -Destination $staging
	Copy-Item (Join-Path $web "js") -Destination $staging -Recurse
	& npx --yes @azure/static-web-apps-cli deploy $staging --deployment-token $token --env production
	if ($LASTEXITCODE -ne 0)
	{
		throw "Static web app upload failed with exit code $LASTEXITCODE"
	}
}
finally
{
	# Never leave the staging copy behind
	if (Test-Path $staging)
	{
		Remove-Item $staging -Recurse -Force
	}
}

# Custom domain. Adding an existing domain again is harmless, so redeployments keep it.
$site_url = "https://$site_hostname"
if ($CustomDomain)
{
	Write-Host "Adding custom domain '$CustomDomain' (validates the CNAME record)..." -ForegroundColor Yellow
	Invoke-Az staticwebapp hostname set --name $site_name --resource-group $ResourceGroup --hostname $CustomDomain --output none | Out-Null
	$site_url = "https://$CustomDomain"
}

# 5. Summary
Write-Host "`n=== Done ===" -ForegroundColor Green
Write-Host "Landing page: $site_url"
Write-Host "Relay URL:    $relay_url"
Write-Host "`nCompile the relay URL and landing page into the Windows app's AppConfig." -ForegroundColor Cyan
