param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $UxPlayPath
)

$ErrorActionPreference = 'Stop'
$resolvedUxPlayPath = (Resolve-Path -LiteralPath $UxPlayPath).Path
$ruleNames = @(
    'Orchard Mirror - UxPlay TCP',
    'Orchard Mirror - UxPlay UDP'
)

foreach ($ruleName in $ruleNames) {
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule
}

New-NetFirewallRule `
    -DisplayName $ruleNames[0] `
    -Description 'Allows the Orchard Mirror UxPlay receiver from local devices only.' `
    -Direction Inbound `
    -Program $resolvedUxPlayPath `
    -Protocol TCP `
    -LocalPort 7100-7102 `
    -RemoteAddress LocalSubnet `
    -Profile Public,Private `
    -Action Allow | Out-Null

New-NetFirewallRule `
    -DisplayName $ruleNames[1] `
    -Description 'Allows Orchard Mirror discovery and streaming from local devices only.' `
    -Direction Inbound `
    -Program $resolvedUxPlayPath `
    -Protocol UDP `
    -LocalPort 5353,7100-7102 `
    -RemoteAddress LocalSubnet `
    -Profile Public,Private `
    -Action Allow | Out-Null
