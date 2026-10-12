#!/usr/bin/env bash
# Repro for TJ's report (2026-10-12): ask the chat agent to write a small C# script to a file, let the tool
# loop settle, then dump (1) the extension's console lines, (2) every turn's text+HTML, and (3) the file as it
# actually landed in OPFS.
# Usage: bash repro-csharp.sh <outDir> [opfsRelPath] [messageFile] [site]
#   messageFile: plain text; {PATH} is replaced with /opfs/<opfsRelPath>. Default = repro-csharp.txt.
#   site: CDP target substring, default gemini.google.com (also: duck.ai). The tab must be the ACTIVE tab.
set -e
cd "$(dirname "$0")"
OUT="$1"; REL="${2:-fstest/Scores.cs}"; MSGFILE="${3:-repro-csharp.txt}"; SITE="${4:-gemini.google.com}"
mkdir -p "$OUT"
# JSON-encode the message so any quote/backslash survives as a JS string literal.
python -c "import json,sys; m=open(sys.argv[1],encoding='utf-8').read().strip().replace('{PATH}','/opfs/'+sys.argv[2]); open(sys.argv[3],'w',encoding='utf-8').write('window.__geminiMsg='+json.dumps(m)+'; \"ok\"')" "$MSGFILE" "$REL" "$OUT/set-msg.js"
dotnet run cdp.cs "$SITE" "file:$OUT/set-msg.js" >/dev/null 2>&1
dotnet run cdp.cs "$SITE" file:chat-send.js 2>/dev/null | tail -1 | cut -c1-160
sleep 5
dotnet run cdp.cs "$SITE" file:wait-idle.js 200000 2>/dev/null | tail -1
dotnet run cdp.cs --console "$SITE" 3 "Gemineachy" 2>/dev/null > "$OUT/console.txt" || true
dotnet run cdp.cs "$SITE" file:dump-turns-html.js 2>/dev/null > "$OUT/turns.json"
dotnet run cdp.cs "$SITE" "window.__opfsPath='$REL'; 'ok'" >/dev/null 2>&1
dotnet run cdp.cs "$SITE" file:opfs-read.js 2>/dev/null | tail -1 > "$OUT/file.json" || true
cat "$OUT/console.txt"
