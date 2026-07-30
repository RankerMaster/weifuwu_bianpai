#!/bin/sh
set -eu

python /app/apiManage.py --addr 0.0.0.0 --port 8000 &
API_PID=$!

python /app/platManage.py --addr 0.0.0.0 --port 8050 &
PLAT_PID=$!

shutdown() {
  kill "$API_PID" "$PLAT_PID" 2>/dev/null || true
}

trap shutdown INT TERM

wait -n "$API_PID" "$PLAT_PID"
shutdown
