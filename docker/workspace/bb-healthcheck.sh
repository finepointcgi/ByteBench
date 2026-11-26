#!/bin/sh
# Healthy once OpenVSCode Server answers HTTP on its port (any status below 500; it answers 403 without the token).
code=$(curl -s -o /dev/null -m 2 -w '%{http_code}' "http://127.0.0.1:${BB_IDE_PORT:-3000}${BB_IDE_BASE_PATH:-}/version" 2>/dev/null || echo 000)
[ "$code" != "000" ] && [ "$code" -lt 500 ]
