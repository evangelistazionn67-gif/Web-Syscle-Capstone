#!/usr/bin/env bash
# Runs once when the codespace is created: installs the MySQL client, loads the tables, restores packages.
set -e
cd "$(dirname "$0")/.."
sudo apt-get update -y > /dev/null
sudo apt-get install -y default-mysql-client > /dev/null
echo "Waiting for MySQL..."
for i in $(seq 1 40); do
  if mysqladmin ping -h db -uroot -psyscle --silent 2>/dev/null; then break; fi
  sleep 2
done
mysql -h db -uroot -psyscle < Database/Syscle_MySQL.sql
dotnet restore Syscle/Syscle.Web/Syscle.Web.csproj
echo ""
echo "READY. To start the website run:"
echo "  cd Syscle/Syscle.Web && dotnet run --no-launch-profile --urls http://0.0.0.0:5080"
