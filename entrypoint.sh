#!/bin/bash
set -e

# Start nginx in background
nginx &

# Start .NET server
cd /app
exec dotnet Server.dll
