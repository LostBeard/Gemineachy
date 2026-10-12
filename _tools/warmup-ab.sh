#!/usr/bin/env bash
# A/B a warm-up wording on a chat site: N fresh chats, send the text file as-is, report whether the reply
# contained a «TOOL_CALL». Usage: bash warmup-ab.sh <textFile> <site> <runs> <outDir>
# (A [TOOL_MANIFEST]-prefixed message is plumbing to the extension: sent unmodified, any call still runs.)
cd "$(dirname "$0")"
TXT="$1"; SITE="${2:-duck.ai}"; RUNS="${3:-3}"; OUT="$4"
mkdir -p "$OUT"
python -c "import json,sys; m=open(sys.argv[1],encoding='utf-8').read().strip(); open(sys.argv[2],'w',encoding='utf-8').write('window.__geminiMsg='+json.dumps(m)+'; \"ok\"')" "$TXT" "$OUT/set-msg.js"
for n in $(seq 1 "$RUNS"); do
  dotnet run cdp.cs "$SITE" file:site-table.js+new-chat-any.js >/dev/null 2>&1
  sleep 2
  dotnet run cdp.cs "$SITE" file:site-table.js+wait-ready.js 60000 > /dev/null 2>&1
  dotnet run cdp.cs "$SITE" "file:$OUT/set-msg.js" >/dev/null 2>&1
  dotnet run cdp.cs "$SITE" file:site-table.js+chat-send.js >/dev/null 2>&1
  sleep 4
  dotnet run cdp.cs "$SITE" file:site-table.js+wait-idle.js 200000 >/dev/null 2>&1
  dotnet run cdp.cs "$SITE" file:site-table.js+dump-turns-html.js 2>/dev/null > "$OUT/run$n.json"
  python -c "import json,sys; t=json.load(open(sys.argv[1],encoding='utf-8')); r=(t[0]['text'] or '') if t else ''; print('run',sys.argv[2],'CALL' if '«TOOL_CALL' in r else 'no call','| turns',len(t),'|',repr(r[:90]))" "$OUT/run$n.json" "$n"
done
