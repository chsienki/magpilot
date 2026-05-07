$env:MAGPILOT_AGENT_TOKEN = 'FurZU8JJPw2w6EXD_3lLb_VdbT3PnYhgMfPjsPKy7us'
$env:MAGPILOT_AGENT_PUBLIC_URL = 'http://192.168.4.2:5099'
$env:ASPNETCORE_URLS = 'http://0.0.0.0:5099'
# Add the WinGet packages dir for chris's copilot install directly to PATH.
# The Links shim in C:\Users\chris\AppData\Local\Microsoft\WinGet\Links\
# is a Windows symlink that's not always resolvable when launched as SYSTEM.
$env:Path = 'C:\Users\chris\AppData\Local\Microsoft\WinGet\Packages\GitHub.Copilot_Microsoft.Winget.Source_8wekyb3d8bbwe;' + $env:Path
& 'C:\tools\Magpilot.Agent\Magpilot.Agent.exe' *>> 'C:\ProgramData\magpilot-agent.log'
