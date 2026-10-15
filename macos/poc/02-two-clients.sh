#!/usr/bin/env bash
# Opens two Roblox clients from two roblox:// launch URLs and checks that both stay alive for 60 seconds.
# This script never kills or changes any process. Close the clients yourself afterwards (see the end of the output).
# Bash 3.2 compatible (the version macOS ships).
#
# Usage: 02-two-clients.sh "<launch URL 1>" "<launch URL 2>"
#
# Real launch URLs contain one-time auth tickets and expire quickly. Get a fresh pair right before running.
set -u

if [ $# -lt 2 ]; then
  cat <<'USAGE'
Usage: 02-two-clients.sh "<launch URL 1>" "<launch URL 2>"

  Both arguments are roblox:// launch URLs, for example copied from the Roblox join button.
  Real launch URLs contain one-time auth tickets and expire quickly, so copy a fresh pair
  right before running this script. An old pair will be refused or will open nothing.

  Placeholders such as "roblox://placeholder1" "roblox://placeholder2" are accepted for a
  dry run of the process listing and sampling, but they will not start real clients.
USAGE
  exit 1
fi

URL1="$1"
URL2="$2"

echo "== RobloxPlayer processes before =="
ps -axo pid,etime,command | grep -i roblox | grep -v -e grep -e 02-two-clients || echo "(none)"
# Processes that were already running count as baseline, so they cannot make the check pass.
BASE=$(ps -axo command | grep -c '[R]obloxPlayer')
echo "baseline RobloxPlayer processes before launch: $BASE"
echo

echo "== launching client 1 =="
open -n "$URL1"
echo "open exit code: $?"
sleep 10

echo "== launching client 2 =="
open -n "$URL2"
echo "open exit code: $?"
echo

EXPECTED=$((BASE + 2))
echo "== sampling RobloxPlayer processes (every 10 s for 60 s, expected at least $EXPECTED) =="
ALIVE_ALL=yes
T=0
while [ "$T" -lt 60 ]; do
  # Sample only after the first 10 s, so client 2 has time to start.
  sleep 10
  T=$((T + 10))
  COUNT=$(ps -axo command | grep -c '[R]obloxPlayer')
  echo "t=${T}s RobloxPlayer processes: $COUNT"
  if [ "$COUNT" -lt "$EXPECTED" ]; then
    ALIVE_ALL=no
  fi
done
echo

if [ "$ALIVE_ALL" = yes ]; then
  echo "PASS  both clients alive for 60s: yes"
else
  echo "FAIL  both clients alive for 60s: no"
fi
echo

echo "== RobloxPlayer processes after =="
ps -axo pid,etime,command | grep -i roblox | grep -v -e grep -e 02-two-clients || echo "(none)"
echo

cat <<'CLOSE'
Nothing was closed by this script. To close the clients yourself:
  - Click each Roblox window and use the menu bar: Roblox > Quit Roblox (or Cmd-Q).
  - Or open Activity Monitor, select the RobloxPlayer row, and use the X toolbar button (Quit).
  - Use "Force Quit" only if a client does not respond.
CLOSE
