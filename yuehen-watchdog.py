# -*- coding: utf-8 -*-
"""
月痕 watchdog · 心跳守护
每 15 秒查一次 C:\yx\yuehen-hb.txt，超过 30 秒没更新就重启 core
入口：启动文件夹 VBS → pythonw -B -u C:\yx\yuehen-watchdog.py
"""
import os, sys, time, subprocess

HEARTBEAT = r"C:\yx\yuehen-hb.txt"
LOG = r"C:\yx\yuehen.log"
CORE = r"C:\yx\yuehen-core.py"
PYW = r"C:\Users\裴龙飞\AppData\Local\Programs\Python\Python312\pythonw.exe"

def log(s):
    line = f"[{time.strftime('%H:%M:%S')}] [WD] {s}"
    print(line, flush=True)
    try:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except: pass

def is_alive():
    try:
        with open(HEARTBEAT) as f:
            last = float(f.read().strip())
        return (time.time() - last) < 30
    except: return False

def start_core():
    log("心跳过期，重启 core...")
    subprocess.Popen([PYW, "-B", "-u", CORE], close_fds=True)
    log("core 重启指令已发")

if __name__ == "__main__":
    log(f"watchdog 就位 | PID={os.getpid()} | 查间隔=15s | 心跳过期=30s")
    while True:
        if not is_alive():
            start_core()
            time.sleep(5)   # 等 core 起来
        time.sleep(15)
