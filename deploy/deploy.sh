#!/bin/bash
set -e

export NVM_DIR="/home/shenxianovo/.nvm"
source "$NVM_DIR/nvm.sh"
nvm use 20

# ==== 配置区域 ====
APP_NAME="heartbeat"
APP_DIR="/srv/heartbeat"
DOTNET_PROJECT="server/Heartbeat.Server/Heartbeat.Server.csproj"
DOTNET_ENV="Production"
VUE_PROJECT="frontend"
LOG_FILE="$APP_DIR/$APP_NAME.log"
PID_FILE="$APP_DIR/$APP_NAME.pid"

cd "$APP_DIR"

# ==== 拉取最新代码 ====
echo "Pulling latest code..."
git fetch origin main
OLD_HEAD=$(git rev-parse HEAD)
git reset --hard origin/main

# ==== 检查变更 ====
SERVER_CHANGED=$(git diff --name-only $OLD_HEAD HEAD | grep '^server/' || true)
FRONTEND_CHANGED=$(git diff --name-only $OLD_HEAD HEAD | grep '^frontend/' || true)

# ==== 构建前端（如果有变动） ====
if [ -n "$FRONTEND_CHANGED" ]; then
    echo "Frontend changes detected. Building frontend..."
    npm ci --prefix "$VUE_PROJECT"
    npm run build --prefix "$VUE_PROJECT"
else
    echo "No frontend changes detected. Skipping frontend build."
fi

# ==== 停止服务（如果 server 有变动） ====
if [ -n "$SERVER_CHANGED" ] && [ -f "$PID_FILE" ]; then
    PID=$(cat "$PID_FILE")
    if ps -p $PID > /dev/null; then
        echo "Stopping existing backend service (PID $PID)..."
        kill $PID
        sleep 2
    fi
    rm -f "$PID_FILE"
fi

# ==== 启动服务（如果 server 有变动） ====
if [ -n "$SERVER_CHANGED" ]; then
    echo "Starting backend service..."
    nohup dotnet run --project "$DOTNET_PROJECT" --environment $DOTNET_ENV > "$LOG_FILE" 2>&1 &
    echo $! > "$PID_FILE"
    echo "Backend service started (PID $(cat $PID_FILE)), logs: $LOG_FILE"
else
    echo "No backend changes detected. Skipping backend restart."
fi