#!/bin/bash
set -e

export NVM_DIR="/home/shenxianovo/.nvm"
source "$NVM_DIR/nvm.sh"
nvm use 20

# ==== 配置 ====
APP_NAME="heartbeat"
APP_DIR="/srv/heartbeat"
DOTNET_PROJECT="server/Heartbeat.Server/Heartbeat.Server.csproj"
PUBLISH_DIR="$APP_DIR/publish"
DOTNET_ENV="Production"
VUE_PROJECT="frontend"
LOG_FILE="$APP_DIR/$APP_NAME.log"
PID_FILE="$APP_DIR/$APP_NAME.pid"

# ==== 函数 ====
stop_backend() {
    [ -f "$PID_FILE" ] || return 0
    local pid=$(cat "$PID_FILE")
    ps -p $pid > /dev/null 2>&1 || { rm -f "$PID_FILE"; return 0; }

    echo "Stopping backend (PID $pid)..."
    kill -15 $pid

    for i in {1..30}; do
        ps -p $pid > /dev/null 2>&1 || { echo "Stopped gracefully"; rm -f "$PID_FILE"; return 0; }
        sleep 1
    done

    echo "Force killing..."
    kill -9 $pid
    rm -f "$PID_FILE"
}

start_backend() {
    echo "Starting backend..."
    nohup dotnet "$PUBLISH_DIR/Heartbeat.Server.dll" \
        --environment $DOTNET_ENV \
        --urls http://0.0.0.0:5023 \
        > "$LOG_FILE" 2>&1 &
    echo $! > "$PID_FILE"
    echo "Started (PID $!)"
}

# ==== 拉取代码 ====
cd "$APP_DIR"
echo "Pulling latest code..."
git fetch origin main
OLD_HEAD=$(git rev-parse HEAD)
git reset --hard origin/main

# ==== 检测变更 ====
CHANGED_FILES=$(git diff --name-only $OLD_HEAD HEAD)
BUILD_SERVER=$(echo "$CHANGED_FILES" | grep -E '^(server|shared|deploy)/' || true)
BUILD_FRONTEND=$(echo "$CHANGED_FILES" | grep -E '^(frontend|deploy)/' || true)

# ==== 构建 ====
if [ -n "$BUILD_FRONTEND" ]; then
    echo "Building frontend..."
    npm ci --prefix "$VUE_PROJECT"
    npm run build --prefix "$VUE_PROJECT"
fi

if [ -n "$BUILD_SERVER" ]; then
    echo "Publishing backend..."
    rm -rf "$PUBLISH_DIR"/*
    dotnet publish "$DOTNET_PROJECT" -c Release -o "$PUBLISH_DIR"
fi

# ==== 重启后端：有后端变更，或无任何变更时（手动重启） ====
if [ -n "$BUILD_SERVER" ] || [ -z "$CHANGED_FILES" ]; then
    [ -z "$CHANGED_FILES" ] && echo "No changes detected → restarting backend"
    stop_backend
    start_backend
fi