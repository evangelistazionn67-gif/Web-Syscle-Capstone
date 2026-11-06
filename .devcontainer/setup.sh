#!/usr/bin/env bash
# Runs once when the codespace is created. The app creates the database and loads the tables and starter data by itself on first start.
set -e
cd "$(dirname "$0")/.."
dotnet restore Syscle/Syscle.Web/Syscle.Web.csproj
echo ""
echo "READY. The website starts automatically on port 5080 (log: /tmp/dotnet.log)."
