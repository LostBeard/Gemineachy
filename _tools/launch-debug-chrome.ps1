# Launch the Gemineachy debug Chrome: CDP on 9222, the persistent gem-debug-profile (extension installed
# unpacked from bin\PublishRelease\chrome, OPFS mounts, accepted site terms), one tab per supported chat site.
#
# --disable-features=CalculateNativeWinOcclusion / --disable-backgrounding-occluded-windows:
#   Windows window-occlusion tracking marks a COVERED Chrome window "hidden", and Chrome then throttles it
#   (timers ~1/s, no animation frames). With TJ working in front of the debug window that made every probe
#   flaky - sends sat composed and unsent for minutes (2026-10-12). These flags only stop occlusion from
#   hiding the window; switching TABS still hides a tab, so real hidden-tab behaviour remains testable.
#
# Refuses to start if port 9222 is already in use (it may be another agent's Chrome).
$busy = $false
try { Invoke-WebRequest -UseBasicParsing http://127.0.0.1:9222/json/version -TimeoutSec 2 | Out-Null; $busy = $true } catch { }
if ($busy) { Write-Output "port 9222 is already in use - not launching"; exit 1 }
$chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe"
$p = Start-Process -FilePath $chrome -PassThru -ArgumentList @(
    "--remote-debugging-port=9222",
    "--user-data-dir=`"C:\Users\TJ\gem-debug-profile`"",
    "--disable-features=CalculateNativeWinOcclusion",
    "--disable-backgrounding-occluded-windows",
    "https://gemini.google.com/app",
    "https://duck.ai/")
Write-Output "launched debug Chrome PID $($p.Id) - stop it by that PID when done"
