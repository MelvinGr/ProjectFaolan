#!/usr/bin/env bash
# Automated client integration smoke test: start the server, capture loopback, launch the retail client
# unattended (auto-login), and assert a battery of login -> EnterWorld markers. See docs/client-launcher.md.
# Writes a PASS/FAIL report to $REPORT. Run from anywhere; needs the launcher+server already built.
set -u

REPO="/home/melvin/Development/C#/Project Faolan"
GD="/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan"
SRVLOG=/tmp/faolan-srv.log
LAUNLOG=/tmp/faolan-laun.log
PCAP=/tmp/faolan-session.pcap
CLIENTLOG="$GD/AgeOfConan.log"
REPORT=/tmp/faolan-test-report.txt
cd "$REPO" || exit 1
: > "$REPORT"

pass=0; fail=0
check() { # name ; condition already evaluated via $?
  if [ "$1" -eq 0 ]; then echo "PASS  $2" | tee -a "$REPORT"; pass=$((pass+1));
  else echo "FAIL  $2" | tee -a "$REPORT"; fail=$((fail+1)); fi
}

LAUNCHER_DLL="$REPO/Faolan.ClientLauncher/bin/Debug/Faolan.ClientLauncher.dll"

cleanup() {
  pkill -9 -f "AgeOfConan" 2>/dev/null
  pkill -f "proton run.*AgeOfConan" 2>/dev/null
  pkill -f "Faolan.ClientLauncher" 2>/dev/null
  pkill -f "dotnet.*Faolan.dll" 2>/dev/null
  pkill -f "tshark.*portrange" 2>/dev/null
  sleep 1
  # heal any UniverseAddr left redirected (sidecar-based), as a backstop
  dotnet "$LAUNCHER_DLL" --restore > /dev/null 2>&1
}
trap cleanup EXIT
cleanup

echo "=== Faolan client smoke test $(date -u +%H:%M:%S) ===" | tee -a "$REPORT"

# 1. server
( cd Faolan/bin/Debug && nohup dotnet Faolan.dll > "$SRVLOG" 2>&1 & )
for i in $(seq 1 30); do ss -tln 2>/dev/null | grep -q ":7010" && break; sleep 1; done
ss -tln 2>/dev/null | grep -qE ":7010" ; check $? "server listening on 7010 (UniverseAgent)"
# (GameServer 7014/7015 come up lazily once a player reaches that stage - validated via the pcap later.)

# 2. loopback capture (dumpcap caps -> no root)
nohup tshark -i lo -f "tcp portrange 7001-7015" -w "$PCAP" > /tmp/tshark.log 2>&1 &
sleep 2

# 3. launch client unattended, redirect UniverseAddr -> localhost (run the dll directly so SIGTERM reverts cleanly)
nohup dotnet "$LAUNCHER_DLL" \
  --universeaddr 127.0.0.1:7010 -u faolan -p aa -c tester > "$LAUNLOG" 2>&1 &

# 4. wait for the success marker (EnterWorld), up to 120s (Proton cold start)
entered=1
for i in $(seq 1 120); do grep -q "EnterWorld" "$SRVLOG" 2>/dev/null && { entered=0; break; }; sleep 1; done
sleep 6  # let in-world traffic accrue

# 5. assertions on the server log
grep -q "UniverseAddr: dm09-nj4.ageofconan.com:7010 -> 127.0.0.1:7010" "$LAUNLOG"; check $? "launcher redirected UniverseAddr -> localhost"
grep -q "UniverseAgent login accepted for user faolan" "$SRVLOG"; check $? "UniverseAgent: login accepted (faolan)"
grep -qi "RequestAuth" "$SRVLOG"; check $? "PlayerAgent: RequestAuth"
check "$entered" "PlayerAgent: EnterWorld (0x20B6) reached"
grep -q "CsPlayerAgent.*Authenticate" "$SRVLOG"; check $? "CSPlayerAgent: Authenticate"
grep -q "GameServerListener" "$SRVLOG"; check $? "GameServer: in-world traffic"

# 6. client-log markers
grep -qi "Applying .* gametweaks" "$CLIENTLOG" 2>/dev/null; check $? "client: ApplyDynamicGameTweaks"
! grep -qi "Dispatcher call .* failed" "$CLIENTLOG" 2>/dev/null; check $? "client: no 'Dispatcher call failed' (login-blocking)"

# 7. pcap
sleep 1; pkill -f "tshark.*portrange" 2>/dev/null; sleep 1
PKTS=$(tshark -r "$PCAP" 2>/dev/null | wc -l)
[ "${PKTS:-0}" -gt 0 ]; check $? "loopback pcap captured packets (${PKTS})"
tshark -r "$PCAP" -Y "tcp.port==7010" 2>/dev/null | grep -q .; check $? "pcap has UniverseAgent (7010) traffic"
tshark -r "$PCAP" -Y "tcp.port==7014" 2>/dev/null | grep -q .; check $? "pcap has GameServer (7014) traffic"

# 8. stop the client so the launcher's WaitForExit returns and reverts UniverseAddr cleanly
pkill -9 AgeOfConan.exe 2>/dev/null; pkill -f "proton run.*AgeOfConan" 2>/dev/null; sleep 5
grep -q "<UniverseAddr>dm09-nj4.ageofconan.com:7010</UniverseAddr>" "$GD/LocalConfig.xml"; check $? "UniverseAddr reverted after client exit"
[ ! -f "$GD/LocalConfig.xml.faolan-revert" ]; check $? "revert sidecar cleaned up"

echo "=== RESULT: $pass passed, $fail failed ===" | tee -a "$REPORT"
echo "(server log: $SRVLOG | launcher: $LAUNLOG | pcap: $PCAP | client log: $CLIENTLOG)" | tee -a "$REPORT"
