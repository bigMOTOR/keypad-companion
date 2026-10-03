#!/usr/bin/python3
"""Opt-in read-only usage bridge. Credentials never leave this child process.

Reads the existing Claude Code credential from macOS Keychain and sends its
access token only to Anthropic's own usage endpoint. Does not refresh, persist,
log, or return credentials. The parent receives allowance numbers only.
"""
import datetime
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


def read_credential(now):
    # Early Companion logins omitted USER; Bun stored them under "unknown".
    # Read only these two exact Claude Code accounts, never enumerate secrets.
    candidates = []
    for account in dict.fromkeys([getpass.getuser(), 'unknown']):
        stored = subprocess.run(['/usr/bin/security', 'find-generic-password',
                                 '-s', 'Claude Code-credentials', '-a', account, '-w'],
                                capture_output=True, timeout=8, check=False)
        if not stored.returncode:
            try:
                credential = json.loads(stored.stdout).get('claudeAiOauth', {})
                if isinstance(credential, dict):
                    candidates.append(credential)
            except (ValueError, AttributeError):
                pass
    if not candidates:
        return {'error': 'no_authorized_credential'}
    team = [c for c in candidates if c.get('subscriptionType') == 'team']
    if not team:
        return {'error': 'team_account_required'}
    valid = [c for c in team if isinstance(c.get('accessToken'), str) and c['accessToken']
             and isinstance(c.get('expiresAt'), (int, float))
             and not isinstance(c['expiresAt'], bool) and math.isfinite(c['expiresAt'])
             and c['expiresAt'] > now * 1000]
    if not valid:
        return {'error': 'expired_credential'}
    return max(valid, key=lambda c: c['expiresAt'])


def read_usage():
    credential = read_credential(time.time())
    if 'error' in credential:
        return credential
    token = credential['accessToken']
    request = urllib.request.Request(USAGE_URL, headers={
        'Authorization': 'Bearer ' + token,
        'anthropic-beta': 'oauth-2025-04-20', 'Accept': 'application/json'})
    with urllib.request.build_opener(NoRedirect).open(request, timeout=10) as reply:
        payload = json.load(reply)
    return allowance(payload, time.time())


if __name__ == '__main__':
    try:
        result = read_usage()
    except urllib.error.HTTPError as e:
        result = {'error': 'sign_in_required' if e.code == 401 else 'access_denied' if e.code == 403 else 'usage_unavailable'}
    except Exception:
        # Exception messages can contain request/credential data: never print them.
        result = {'error': 'usage_unavailable'}
    print(json.dumps(result))
