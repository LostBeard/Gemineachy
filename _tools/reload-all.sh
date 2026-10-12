#!/usr/bin/env bash
# Reload the unpacked Gemineachy extension in the debug Chrome (port 9222) and then reload every chat tab
# it runs in, so the page picks up the freshly published build. Usage: bash reload-all.sh [tabUrlSubstr]
# Opens a chrome://extensions tab if none is open (chrome.developerPrivate only exists there).
set -e
cd "$(dirname "$0")"
TAB="${1:-gemini.google.com}"
if ! curl -s http://127.0.0.1:9222/json | grep -q '"url": "chrome://extensions/'; then
  curl -s -X PUT "http://127.0.0.1:9222/json/new?chrome://extensions/" > /dev/null
  sleep 2
fi
dotnet run cdp.cs "chrome://extensions" "(async()=>{const l=await chrome.developerPrivate.getExtensionsInfo();const e=l.find(x=>x.name==='Gemineachy');if(!e)return 'Gemineachy not installed';await chrome.developerPrivate.reload(e.id,{failQuietly:true});return 'reloaded '+e.id+' v'+e.version+' from '+e.prettifiedPath})()" 2>/dev/null | tail -1
sleep 2
dotnet run cdp.cs "$TAB" "location.reload(); 'tab reloaded'" 2>/dev/null | tail -1
# Bring the chat tab to the front: opening chrome://extensions above made it a HIDDEN background tab, and
# Chrome throttles hidden tabs (timers ~1/s, no animation frames) - every probe of a hidden chat tab ran
# slow or stalled, which cost a morning of misread "send not accepted" results (2026-10-12).
ID=$(curl -s http://127.0.0.1:9222/json | python -c "import json,sys; print(next(t['id'] for t in json.load(sys.stdin) if t['type']=='page' and sys.argv[1] in t['url']))" "$TAB")
curl -s "http://127.0.0.1:9222/json/activate/$ID" > /dev/null
sleep 6
