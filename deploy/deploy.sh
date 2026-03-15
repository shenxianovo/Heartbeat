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
LOCAL=$(git rev-parse HEAD)
REMOTE=$(git rev-parse origin/main)
git reset --hard origin/main

# 检查 server 目录是否有变更
CHANGED=$(git diff --name-only $LOCAL $REMOTE | grep '^server/')
if [ -z "$CHANGED" ]; then
    echo "No server changes detected. Skipping service restart."
    exit 0
fi

# ==== 停止服务 ====
if [ -f "$PID_FILE" ]; then
    PID=$(cat "$PID_FILE")
    if ps -p $PID > /dev/null; then
        echo "Stopping existing service (PID $PID)..."
        kill $PID
        sleep 2
    fi
    rm -f "$PID_FILE"
fi

# ==== 启动服务 ====
echo "Starting service..."
npm ci --prefix "$VUE_PROJECT"
npm run build --prefix "$VUE_PROJECT"

nohup dotnet run --project "$DOTNET_PROJECT" --environment $DOTNET_ENV > "$LOG_FILE" 2>&1 &

# 记录 PID
echo $! > "$PID_FILE"
echo "Service started (PID $(cat $PID_FILE)), logs: $LOG_FILE"