# Call one One-Blind-Mouse tool through its MCP server, for sessions where the MCP server is not registered or not loaded yet.
# Usage: & obm.ps1 <tool> [@{ arg = value; ... }] [-Exe <path>] [-Profile <name>]
# Prints the tool's text result. Exits 1 if the tool reports an error.
param(
	[Parameter(Mandatory)][string]$Tool,
	[hashtable]$Arguments = @{},
	[string]$Exe = '',
	[string]$Profile = ''
)
$ErrorActionPreference = 'Stop'

# Find the exe: an explicit path, the standard install location, then a running copy
if (-not $Exe)
{
	$Exe = Join-Path $env:LOCALAPPDATA 'OneBlindMouse\OneBlindMouse.exe'
	if (-not (Test-Path $Exe))
	{
		$Exe = (Get-Process OneBlindMouse -ErrorAction SilentlyContinue | Select-Object -First 1).Path
	}
}
if (-not $Exe -or -not (Test-Path $Exe))
{
	throw 'OneBlindMouse.exe not found. See the one-blind-mouse skill for how to obtain it.'
}

# Start the MCP server on stdio; it starts the tray app if needed
$psi = [System.Diagnostics.ProcessStartInfo]::new($Exe)
$psi.ArgumentList.Add('mcp')
if ($Profile)
{
	$psi.ArgumentList.Add('--profile')
	$psi.ArgumentList.Add($Profile)
}
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false
$proc = [System.Diagnostics.Process]::Start($psi)
try
{
	# Send one JSON-RPC message per line, reading the reply when the message is a request
	function Send($message, [bool]$reply)
	{
		$proc.StandardInput.WriteLine(($message | ConvertTo-Json -Compress -Depth 10))
		$proc.StandardInput.Flush()
		if ($reply)
		{
			return $proc.StandardOutput.ReadLine() | ConvertFrom-Json
		}
	}

	# The MCP handshake, then the tool call
	$null = Send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'obm.ps1'; version = '1' } } } $true
	Send @{ jsonrpc = '2.0'; method = 'notifications/initialized' } $false
	$res = Send @{ jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = @{ name = $Tool; arguments = $Arguments } } $true
	if ($res.error)
	{
		Write-Output $res.error.message
		exit 1
	}
	$res.result.content | ForEach-Object { $_.text }
	if ($res.result.isError)
	{
		exit 1
	}
}
finally
{
	$proc.StandardInput.Close()
	if (-not $proc.WaitForExit(3000))
	{
		$proc.Kill()
	}
}
