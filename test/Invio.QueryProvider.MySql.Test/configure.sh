#! /bin/bash
source="$(dirname "${BASH_SOURCE[0]}")"
appsettings_path="$source/config/appsettings.json"
if [ ! -f "$appsettings_path" ]; then
  sed "s/<<YOUR_MYSQL_PASSWORD_HERE>>/$mysql_password/" "$appsettings_path.template" > $appsettings_path
fi
