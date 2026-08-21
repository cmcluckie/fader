#!/usr/bin/env bash
# Find where this app's output actually lands on the console.
#
# Walks a 1 kHz tone across the Apollo's outputs one at a time, holding each for
# a few seconds and printing which one it is. Watch the X32 meters: whichever
# channel lights up tells us the real return path. If NOTHING ever lights up,
# the app's audio is not reaching the console at all.
#
#   ./scripts/find-return.sh            # sweep outputs 0-11
#   ./scripts/find-return.sh 2 9        # sweep just outputs 2..9
set -euo pipefail
FROM="${1:-0}"; TO="${2:-11}"; HOLD="${3:-4}"

send() {  # send() <address> <int args...>
  python3 - "$@" <<'PY'
import socket, struct, sys
addr = sys.argv[1]; args = [int(a) for a in sys.argv[2:]]
def pad(b): return b + b'\0' * (4 - len(b) % 4)
msg = pad(addr.encode()) + pad((',' + 'i' * len(args)).encode()) + b''.join(struct.pack('>i', a) for a in args)
socket.socket(socket.AF_INET, socket.SOCK_DGRAM).sendto(msg, ('127.0.0.1', 10024))
PY
}

NAMES=("MON L" "MON R" "ADAT 1" "ADAT 2" "ADAT 3" "ADAT 4" "ADAT 5" "ADAT 6" \
       "ADAT 7" "ADAT 8" "VIRTUAL 1" "VIRTUAL 2")

echo "Watch the X32 meters. Tone moves every ${HOLD}s."
echo
for (( i=FROM; i<=TO; i++ )); do
  send /fk/outputs "$i"
  send /fk/testtone 1000
  printf "  --> output %-2s  %s\n" "$i" "${NAMES[$i]:-}"
  sleep "$HOLD"
done

send /fk/testtone 0
echo
echo "Done - tone off. Which X32 channel lit up, and on which output?"
