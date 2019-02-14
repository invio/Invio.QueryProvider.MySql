$appsettings_path = "$(Split-Path -parent $PSCommandPath)/config/appsettings.json"
if (-not (Test-Path $appsettings_path)) {
  Get-Content "$($appsettings_path).template" |
    ForEach-Object { $_.Replace("<<YOUR_MYSQL_PASSWORD_HERE>>", $env:mysql_password) } > `
    $appsettings_path
}
