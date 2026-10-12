# Summarise a repro-csharp.sh output folder: each turn's user/model text, and the file as written to OPFS.
import json, sys, os
out = sys.argv[1]
turns = json.load(open(os.path.join(out, 'turns.json'), encoding='utf-8'))
for t in turns:
    print('---', t['i'], 'USER:', (t['user'] or '')[:200].replace('\n', ' | '))
    print('MODEL:', repr((t['text'] or '')[:int(sys.argv[2]) if len(sys.argv) > 2 else 400]))
raw = open(os.path.join(out, 'file.json'), encoding='utf-8').read().strip()
f = json.loads(raw) if raw.startswith('{') else {}
print('=== FILE size', f.get('size'))
print(f.get('text'))
