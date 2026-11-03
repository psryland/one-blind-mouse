#!/usr/bin/env pwsh
# One-Blind-Mouse — infrastructure and relay deployment.
#
# Prerequisites:
#   - Azure CLI, logged in (az login) with access to the target subscription
#   - Node.js (npx), for uploading the landing page with the Static Web Apps CLI
#
# Usage:
#   ./infra/deploy.ps1 -Subscription <name or id> -ResourceGroup rg-one-blind-mouse
#   ./infra/deploy.ps1 -Subscription <name or id> -ResourceGroup rg-one-blind-mouse -Location australiaeast -NamePrefix obm
#
# Steps:
#   1. Create the resource group
#   2. Deploy the base resources (registry, logs, Container Apps environment, static web app)
#   3. Build the relay image in the registry with 'az acr build' (no local Docker needed)
#   4. Deploy the relay Container App using that image
#   5. Upload web/ to the static web app
#   6. Print the relay URL to compile into the Windows app

param(
	[Parameter(Mandatory = $true)]
	[string]$Subscription,
	[Parameter(Mandatory = $true)]
	[string]$ResourceGroup,
	[string]$Location = "australiaeast",
	[string]$NamePrefix = "obm"
)

$ErrorActionPreference = "Stop"
$repo_root = Split-Path -Parent $PSScriptRoot
$template = Join-Path $PSScriptRoot "main.bicep"

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

# 1. Resource group
Write-Host "`n=== One-Blind-Mouse deployment ===" -ForegroundColor Cyan
Write-Host "Creating resource group '$ResourceGroup' in $Location..." -ForegroundColor Yellow
Invoke-Az group create --name $ResourceGroup --location $Location --output none | Out-Null

# 2. Base resources. Registering providers is idempotent and avoids first-use failures in new subscriptions.
Write-Host "Registering resource providers..." -ForegroundColor Yellow
foreach ($provider in @("Microsoft.App", "Microsoft.ContainerRegistry", "Microsoft.OperationalInsights", "Microsoft.Web"))
{
	Invoke-Az provider register --namespace $provider --wait --output none | Out-Null
}
Write-Host "Deploying base resources..." -ForegroundColor Yellow
$base = Deploy-Template -Name "obm-base" -Parameters @("deploy_app=false")
$registry_name = $base.registry_name.value
$site_name = $base.site_name.value

# 3. Relay image, built in the registry from the repository root so Core is in the build context.
# A unique tag per deployment makes the Container App start a new revision.
$image = "obm-relay:$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Write-Host "Building relay image '$image' in registry '$registry_name'..." -ForegroundColor Yellow
Invoke-Az acr build `
	--registry $registry_name `
	--image $image `
	--file (Join-Path $repo_root "src/OneBlindMouse.Relay/Dockerfile") `
	$repo_root | Out-Host

# 4. Relay Container App. On the first deployment the app's identity is created in the same deployment as its AcrPull role,
# so the first image pull can fail before the role propagates. One retry after a short wait covers that.
Write-Host "Deploying relay Container App..." -ForegroundColor Yellow
try
{
	$app = Deploy-Template -Name "obm-app" -Parameters @("deploy_app=true", "relay_image=$image")
}
catch
{
	Write-Host "App deployment failed (often the registry role is still propagating). Retrying in 60 s..." -ForegroundColor DarkYellow
	Start-Sleep -Seconds 60
	$app = Deploy-Template -Name "obm-app" -Parameters @("deploy_app=true", "relay_image=$image")
}
$relay_url = $app.relay_url.value
$site_hostname = $app.site_hostname.value

# 5. Landing page. Stage only the site files, so stray local folders under web/ (e.g. node_modules) are never uploaded.
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

# 6. Summary
Write-Host "`n=== Done ===" -ForegroundColor Green
Write-Host "Landing page: https://$site_hostname"
Write-Host "Relay URL:    $relay_url"
Write-Host "`nPaste the relay URL into the Windows app's compiled-in relay constant." -ForegroundColor Cyan
