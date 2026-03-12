#!/bin/bash
set -e

cd "$(dirname "$0")"

git pull origin main

export GIT_COMMIT=$(git rev-parse --short HEAD)
export BUILD_DATE=$(date -u '+%Y-%m-%d %H:%M:%S UTC')

docker compose build --no-cache
docker compose up -d
