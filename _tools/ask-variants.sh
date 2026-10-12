#!/usr/bin/env bash
# Send several prompts one after another in the CURRENT chat and report, for each, whether the reply was a tool
# call, a web-results fallback, or prose. Usage: bash <abs>/ask-variants.sh <site> <promptsFile> <outDir>
# promptsFile: one prompt per line.
cd "$(dirname "$0")"
export PYTHONIOENCODING=utf-8
SITE="$1"; PROMPTS="$2"; OUT="$3"; mkdir -p "$OUT"
n=0
while IFS= read -r line || [ -n "$line" ]; do
  [ -z "$line" ] && continue
  n=$((n+1))
  python -c "import json,sys; open(sys.argv[2],'w',encoding='utf-8').write('window.__geminiMsg='+json.dumps(sys.argv[1])+'; \"ok\"')" "$line" "$OUT/p$n.js"
  dotnet run cdp.cs "$SITE" "file:$OUT/p$n.js" > /dev/null 2>&1
  dotnet run cdp.cs "$SITE" file:site-table.js+chat-send.js > /dev/null 2>&1
  sleep 4
  dotnet run cdp.cs "$SITE" file:site-table.js+wait-idle.js 200000 > /dev/null 2>&1
  dotnet run cdp.cs "$SITE" file:site-table.js+dump-turns-html.js 2>/dev/null > "$OUT/turns$n.json"
  python -c "
import json,sys
tu=json.load(open(sys.argv[1],encoding='utf-8')); p=sys.argv[2]
i=max(k for k,t in enumerate(tu) if (t['user'] or '').startswith(p[:30]))
r=' '.join((t['text'] or '') for t in tu[i:])
kind='TOOL_CALL' if '«TOOL_CALL' in r else ('WEB-RESULTS' if 'top web results' in r else 'prose')
print(kind.ljust(11),'|',p[:70],'|',repr((tu[i]['text'] or '')[:80]))
" "$OUT/turns$n.json" "$line"
done < "$PROMPTS"
