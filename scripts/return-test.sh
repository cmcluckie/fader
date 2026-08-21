#!/usr/bin/env bash
# Return-path test: is the console actually hearing THIS app's output?
#
# Drives the running engine's diagnostic override on every armed channel:
#   tone    - 1 kHz sine on the return  -> X32 channel should show a steady tone
#   silence - hard mute on the return   -> X32 channel should go DEAD quiet
#   off     - back to normal audio
#
# If "silence" does NOT mute the X32 channel, the mic is reaching the console by
# some other path as well, and no amount of notching here can ever cut it.
set -euo pipefail
MODE="${1:-}"
case "$MODE" in
  tone)    HZ=1000 ;;
  silence) HZ=-1 ;;
  off)     HZ=0 ;;
  *) echo "usage: $0 {tone|silence|off}" >&2; exit 2 ;;
esac
python3 - "$HZ" <<'PY'
import socket, struct, sys
hz = float(sys.argv[1])
def pad(b): return b + b'\0' * (4 - len(b) % 4)
msg = pad(b'/fk/testtone') + pad(b',f') + struct.pack('>f', hz)
socket.socket(socket.AF_INET, socket.SOCK_DGRAM).sendto(msg, ('127.0.0.1', 10024))
PY
echo "sent: $MODE"
