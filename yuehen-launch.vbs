' yuehen-launch.vbs — 登录自启（启动文件夹，零提权）
Dim ws, pyDir
Set ws = CreateObject("WScript.Shell")
pyDir = ws.ExpandEnvironmentStrings("%LOCALAPPDATA%\Programs\Python\Python312\pythonw.exe")
ws.Run pyDir & " -B -u C:\yx\yuehen-core.py", 0, False
ws.Run pyDir & " -B -u C:\yx\yuehen-watchdog.py", 0, False
