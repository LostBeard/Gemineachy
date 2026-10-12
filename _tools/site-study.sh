#!/usr/bin/env bash
# Study a chat site for an adapter: activate its tab, send a multi-line code prompt through stream-record.js
# (logs attribute flips + button changes until quiet), then probe the turn structure (turns-probe.js) and the
# last code block (site-result.js). Usage: bash <abs>/site-study.sh <urlSubstr> <outDir>
cd "$(dirname "$0")"
export PYTHONIOENCODING=utf-8
SITE="$1"; OUT="$2"; mkdir -p "$OUT"
ID=$(curl -s http://127.0.0.1:9222/json | python -c "import json,sys; print(next(t['id'] for t in json.load(sys.stdin) if t['type']=='page' and sys.argv[1] in t['url']))" "$SITE")
curl -s "http://127.0.0.1:9222/json/activate/$ID" > /dev/null
sleep 1
python -c "import json; m=open('site-study-msg.txt',encoding='utf-8').read().strip(); open(r'$OUT/set.js','w',encoding='utf-8').write('window.__siteMsg='+json.dumps(m)+'; window.__userNeedle=\"Study probe\"; \"ok\"')"
dotnet run cdp.cs "$SITE" "file:$OUT/set.js" > /dev/null 2>&1
dotnet run cdp.cs "$SITE" file:stream-record.js 150000 2>/dev/null | tail -n +1 > "$OUT/stream.txt"
dotnet run cdp.cs "$SITE" file:turns-probe.js 2>/dev/null > "$OUT/turns.json"
dotnet run cdp.cs "$SITE" file:site-result.js 2>/dev/null > "$OUT/result.json"
dotnet run cdp.cs --shot "$SITE" "$OUT/shot.png" > /dev/null 2>&1
echo "== stream"; cat "$OUT/stream.txt"; echo "== turns"; cat "$OUT/turns.json"; echo "== code"; python -c "
import json,sys
d=json.load(open(sys.argv[1],encoding='utf-8'))
for p in d['pres']: print('CODE:',repr(p['code'][:300])); print('CHROME:',repr(p['chrome'])); print('PATH:',p['path'][:300])
if not d['pres']: print('no <pre>; tail:',repr(d['tail'][-300:]))" "$OUT/result.json"
