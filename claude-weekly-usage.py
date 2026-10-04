#!/usr/bin/python3
"""Opt-in read-only usage bridge. Credentials never leave this child process.

Reads the existing Claude Code credential from macOS Keychain and sends its
access token only to Anthropic's own usage endpoint. Expiring credentials renew
through the fixed official OAuth endpoint and update the existing Keychain item.
No credentials are logged, returned or written to files. The parent receives allowance numbers only.
"""
import datetime
import signal
from claude_auth import AuthError, renew
import json
import getpass
import re
import math
import subprocess
import time
import urllib.error
import urllib.request

USAGE_URL = 'https://api.anthropic.com/api/oauth/usage'


def allowance(payload, now):
    week = payload.get('seven_day')
    if not isinstance(week, dict):
        raise ValueError('missing weekly window')
    used = week.get('utilization')
    reset = week.get('resets_at')
    if isinstance(used, bool) or not isinstance(used, (int, float)) or not math.isfinite(used) or not 0 <= used <= 100:
        raise ValueError('invalid utilization')
    if isinstance(reset, str):
        reset = re.sub(r'\.(\d+)(?=[+-]|Z|$)', lambda m: '.' + m.group(1)[:6].ljust(6, '0'), reset)
        reset = datetime.datetime.fromisoformat(reset.replace('Z', '+00:00')).timestamp()
    if isinstance(reset, bool) or not isinstance(reset, (int, float)) or not math.isfinite(reset) or reset <= now or reset > now + 8 * 86400:
        raise ValueError('expired window')
    return {'remaining': int(100 - used + 0.5), 'resetsAt': int(reset),
            'updatedAt': datetime.datetime.fromtimestamp(now, datetime.timezone.utc).isoformat()}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        # Never forward a bearer to a redirected host.
        raise urllib.error.HTTPError(req.full_url, code, 'redirect rejected', headers, fp)


def read_record(account):
    stored = subprocess.run(['/usr/bin/security', 'find-generic-password',
                             '-s', 'Claude Code-credentials', '-a', account, '-w'],
                            capture_output=True, timeout=8, check=False)
    if stored.returncode: return None
    try:
        record = json.loads(stored.stdout)
        return record if isinstance(record, dict) else None
    except ValueError: return None


def read_credential(now, force=False):
    candidates = []
    for account in dict.fromkeys([getpass.getuser(), 'unknown']):
        record = read_record(account)
        credential = (record or {}).get('claudeAiOauth', {})
        if isinstance(credential, dict) and credential:
            candidates.append((account, credential))
    if not candidates: return {'error': 'no_authorized_credential'}
    team = [(a,c) for a,c in candidates if c.get('subscriptionType') == 'team']
    if not team: return {'error': 'team_account_required'}
    usable = [(a,c) for a,c in team if isinstance(c.get('accessToken'), str) and c['accessToken']
              and isinstance(c.get('expiresAt'), (int, float)) and not isinstance(c['expiresAt'], bool)
              and math.isfinite(c['expiresAt'])]
    usable.sort(key=lambda pair: pair[1]['expiresAt'], reverse=True)
    valid = [(a,c) for a,c in usable if c['expiresAt'] > now * 1000]
    choices = valid or usable
    last_error = 'expired_credential'
    last_reason = None
    for account, credential in choices:
        if not force and credential['expiresAt'] > now * 1000 and (credential['expiresAt'] > (now + 300) * 1000 or not credential.get('refreshToken')):
            return credential
        if not credential.get('refreshToken'): continue
        try:
            return renew(account, credential, read_record, urllib.request.build_opener(NoRedirect), time.time, force)
        except AuthError as error:
            last_error = error.code
            last_reason = error.reason
            if error.code != 'sign_in_required': break
    return {'error': last_error, **({'reason': last_reason} if last_reason else {})}


def read_usage():
    credential = read_credential(time.time())
    if 'error' in credential:
        return credential
    token = credential['accessToken']
    request = urllib.request.Request(USAGE_URL, headers={
        'Authorization': 'Bearer ' + token,
        'anthropic-beta': 'oauth-2025-04-20', 'Accept': 'application/json'})
    try:
        with urllib.request.build_opener(NoRedirect).open(request, timeout=10) as reply:
            payload = json.load(reply)
    except urllib.error.HTTPError as error:
        if error.code != 401: raise
        error.close()
        credential = read_credential(time.time(), force=True)
        if 'error' in credential: return credential
        request.add_header('Authorization', 'Bearer ' + credential['accessToken'])
        with urllib.request.build_opener(NoRedirect).open(request, timeout=10) as reply:
            payload = json.load(reply)
    return allowance(payload, time.time())


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(SystemExit(0)))
    try:
        result = read_usage()
    except AuthError as e:
        result = {'error': e.code}
    except urllib.error.HTTPError as e:
        result = {'error': 'sign_in_required' if e.code == 401 else 'access_denied' if e.code == 403 else 'usage_unavailable'}
    except Exception:
        # Exception messages can contain request/credential data: never print them.
        result = {'error': 'usage_unavailable'}
    print(json.dumps(result))
