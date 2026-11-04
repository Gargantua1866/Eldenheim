#!/usr/bin/env bash
# Test cycle: quit Valheim through the mod (so it saves), rebuild and deploy,
# queue a command script, relaunch windowed, wait until the script reports
# MARKER (or the game exits). Usage: tools/cycle.sh commands.txt MARKER [TIMEOUT_S]
set -u
HERE="$(cd "$(dirname "$0")/.." && pwd)"
PLUG=/c/Games/Valheim/BepInEx/plugins/Eldenheim
SCRIPT="$1"; MARKER="$2"; TIMEOUT="${3:-300}"

pid() { tasklist //FI "IMAGENAME eq valheim.exe" //FO CSV //NH 2>/dev/null | grep -i valheim | head -1 | cut -d'"' -f4; }

if [ -n "$(pid)" ]; then
  echo quit > "$PLUG/commands.txt"
  for i in $(seq 1 40); do [ -z "$(pid)" ] && break; sleep 1; done
fi
if [ -n "$(pid)" ]; then echo "Valheim still running; not deploying"; exit 1; fi

(cd "$HERE" && dotnet build src/Eldenheim -c Release 2>&1 | grep -E 'Deployed| error ') || exit 1
[ -f "$PLUG/results.log" ] && mv "$PLUG/results.log" "$PLUG/results-prev.log"
cp "$SCRIPT" "$PLUG/commands.txt"
powershell.exe -NoProfile -Command "Start-Process -FilePath 'C:\Games\Valheim\valheim.exe' -WorkingDirectory 'C:\Games\Valheim' -ArgumentList '-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-console'" >/dev/null
sleep 5
for i in $(seq 1 "$TIMEOUT"); do
  grep -q "$MARKER" "$PLUG/results.log" 2>/dev/null && break
  [ -z "$(pid)" ] && { echo "Valheim exited"; break; }
  sleep 1
done
cat "$PLUG/results.log" 2>/dev/null | cut -c1-600
grep -iE 'exception' /c/Games/Valheim/BepInEx/LogOutput.log | grep -v 'Unable to start' | sort | uniq -c | sort -rn | head -5
