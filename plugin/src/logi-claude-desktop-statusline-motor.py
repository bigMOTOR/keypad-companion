#!/usr/bin/python3
"""Forward the existing Logitech status line; cache only numeric weekly allowance."""
import sys, json, os, pathlib, subprocess, datetime, time
raw=sys.stdin.read()
folder=pathlib.Path.home()/'Library/Application Support/MotorAI'
try:
    data=json.loads(raw)
    week=data.get('rate_limits',{}).get('seven_day',{})
    used=week.get('used_percentage'); reset=week.get('resets_at')
    # Diagnose absent provider fields without retaining conversations or account data.
    folder.mkdir(mode=0o700,parents=True,exist_ok=True)
    diagnostic={'updatedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
                'hasRateLimits':isinstance(data.get('rate_limits'),dict),
                'hasWeekly':bool(week),'hasUsedPercentage':isinstance(used,(int,float)),
                'hasReset':isinstance(reset,(int,float))}
    diagnostic_file=folder/('claude-quota-diagnostic.'+str(os.getpid())+'.tmp')
    fd=os.open(diagnostic_file,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
    with os.fdopen(fd,'w') as f: json.dump(diagnostic,f)
    os.replace(diagnostic_file,folder/'claude-quota-diagnostic.json')
    if isinstance(used,(int,float)) and isinstance(reset,(int,float)) and 0<=used<=100 and reset>time.time():
        folder.mkdir(mode=0o700,parents=True,exist_ok=True)
        value={'remaining':int(100-used+0.5),'resetsAt':int(reset),'updatedAt':datetime.datetime.now(datetime.timezone.utc).isoformat()}
        temp=folder/('claude-quota.'+str(os.getpid())+'.tmp')
        fd=os.open(temp,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
        with os.fdopen(fd,'w') as f: json.dump(value,f)
        os.replace(temp,folder/'claude-quota.json')
except Exception:
    pass
# Keep the vendor integration unchanged, including its local status-line endpoint.
try:
    forward=folder/'logi-statusline-forward.sh'
    if not forward.exists(): forward=pathlib.Path.home()/'.claude/claude-desktop/statusline.sh'
    subprocess.run(['/bin/sh',str(forward)],input=raw,text=True,timeout=3,check=False,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
except Exception:
    pass
