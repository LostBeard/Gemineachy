#!/usr/bin/env bash
# End-to-end compliance runs on a chat site: for each run open a NEW chat, send the message file (repro-csharp.sh,
# which goes through the extension's real first-send path), then report whether the file landed and how.
# Usage: bash <abs>/e2e-runs.sh <site> <runs> <outDir> <relPathPrefix> [messageFile]
# Always invoke by ABSOLUTE path: it cd's to its own folder, so the caller's working directory never matters.
cd "$(dirname "$0")"
export PYTHONIOENCODING=utf-8
SITE="$1"; RUNS="$2"; OUT="$3"; PREFIX="$4"; MSG="${5:-repro-csharp.txt}"
mkdir -p "$OUT"
bash reload-all.sh "$SITE" > /dev/null
for n in $(seq 1 "$RUNS"); do
  dotnet run cdp.cs "$SITE" file:site-table.js+new-chat-any.js > /dev/null 2>&1
  sleep 2
  dotnet run cdp.cs "$SITE" file:site-table.js+wait-ready.js 60000 > /dev/null 2>&1
  bash repro-csharp.sh "$OUT/run$n" "fstest/${PREFIX}$n.cs" "$MSG" "$SITE" > /dev/null 2>&1
  python -c "
import json,sys
o=sys.argv[1]
raw=open(o+'/file.json',encoding='utf-8').read().strip(); d=json.loads(raw) if raw.startswith('{') else {}
t=d.get('text') or ''; c=open(o+'/console.txt',encoding='utf-8').read(); tu=json.load(open(o+'/turns.json',encoding='utf-8'))
warm=[l for l in c.splitlines() if 'warm-up' in l]
print('run',sys.argv[2],'size',d.get('size'),'| lines',t.count(chr(10)),'| List<int>',('List<int>' in t),'| turns',len(tu),'|',warm[-1][-40:] if warm else 'no warm-up log')
" "$OUT/run$n" "$n"
done
