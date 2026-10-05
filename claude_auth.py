"""Renew the existing Team login. Token data stays in this process and Keychain.

The endpoint and public client ID match the official Claude Code client.
No model request, API key, new OAuth scope or plaintext token file is created.
"""
import contextlib
import ctypes
import json
import math
import os
from pathlib import Path
import threading
import urllib.error
import urllib.request

TOKEN_URL = 'https://platform.claude.com/v1/oauth/token'
CLIENT_ID = '9d1c250a-e61b-44d9-88ed-5944d1962f5e'
_keychain_ui_allowed = False

class AuthError(Exception):
    def __init__(self, code, reason=None):
        self.reason = reason
        self.code = code
        super().__init__(code)

def keychain_ui(allowed=False):
    # Process-local policy, not an ACL change or an unlock. Applies to reads and
    # refresh writes; background children must fail rather than display a dialog.
    global _keychain_ui_allowed
    sec = ctypes.CDLL('/System/Library/Frameworks/Security.framework/Security')
    sec.SecKeychainSetUserInteractionAllowed.argtypes = [ctypes.c_bool]
    sec.SecKeychainSetUserInteractionAllowed.restype = ctypes.c_int32
    if sec.SecKeychainSetUserInteractionAllowed(bool(allowed)) != 0:
        raise AuthError('keychain_unavailable')
    _keychain_ui_allowed = bool(allowed)
    return sec

def read_keychain(account, allow_ui=False):
    sec = keychain_ui(allow_ui)
    cf = ctypes.CDLL('/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation')
    ptr = ctypes.c_void_p
    cf.CFStringCreateWithCString.argtypes = [ptr, ctypes.c_char_p, ctypes.c_uint32]
    cf.CFStringCreateWithCString.restype = ptr
    cf.CFDictionaryCreate.argtypes = [ptr, ptr, ptr, ctypes.c_long, ptr, ptr]
    cf.CFDictionaryCreate.restype = ptr
    cf.CFDataGetLength.argtypes = [ptr]; cf.CFDataGetLength.restype = ctypes.c_long
    cf.CFDataGetBytePtr.argtypes = [ptr]; cf.CFDataGetBytePtr.restype = ptr
    cf.CFRelease.argtypes = [ptr]
    sec.SecItemCopyMatching.argtypes = [ptr, ctypes.POINTER(ptr)]
    sec.SecItemCopyMatching.restype = ctypes.c_int32
    owned = []
    result = ptr()
    def constant(name): return ptr.in_dll(sec, name).value
    def string(value):
        p = cf.CFStringCreateWithCString(None, value.encode(), 0x08000100)
        owned.append(p); return p
    try:
        items = [('kSecClass', constant('kSecClassGenericPassword')),
                 ('kSecAttrService', string('Claude Code-credentials')),
                 ('kSecAttrAccount', string(account)),
                 ('kSecReturnData', ptr.in_dll(cf, 'kCFBooleanTrue').value),
                 ('kSecMatchLimit', constant('kSecMatchLimitOne'))]
        keys = (ptr * len(items))(*[constant(k) for k, _ in items])
        values = (ptr * len(items))(*[v for _, v in items])
        query = cf.CFDictionaryCreate(None, keys, values, len(items),
            ctypes.addressof(ctypes.c_byte.in_dll(cf, 'kCFTypeDictionaryKeyCallBacks')),
            ctypes.addressof(ctypes.c_byte.in_dll(cf, 'kCFTypeDictionaryValueCallBacks')))
        owned.append(query)
        status = sec.SecItemCopyMatching(query, ctypes.byref(result))
        if result.value: owned.append(result.value)
        if status == -25300: return None  # Item absent, not a revoked login.
        if status in (-25308, -25293, -128): raise AuthError('keychain_interaction_required')
        if status != 0: raise AuthError('keychain_unavailable')
        length = cf.CFDataGetLength(result)
        if length <= 0 or length > 65536: raise AuthError('keychain_unavailable')
        try: document = json.loads(ctypes.string_at(cf.CFDataGetBytePtr(result), length))
        except ValueError: raise AuthError('keychain_unavailable') from None
        return document if isinstance(document, dict) else None
    finally:
        for p in reversed(owned):
            if p: cf.CFRelease(p)

@contextlib.contextmanager
def refresh_lock(directory=None):
    directory = Path(directory or Path.home() / '.claude').resolve()
    if not directory.is_dir():
        raise AuthError('refresh_unavailable')
    locks = [directory / '.oauth_refresh.lock', Path(str(directory) + '.lock')]
    held = []
    stop = threading.Event()
    compromised = threading.Event()
    def healthy():
        for path, inode in held:
            try:
                if path.stat().st_ino != inode: return False
            except OSError: return False
        return True
    def heartbeat():
        while not stop.wait(5):
            if not healthy():
                compromised.set(); return
            for path, _ in held:
                try: os.utime(path, None)
                except OSError: compromised.set(); return
    thread = None
    try:
        for path in locks:
            try: path.mkdir(mode=0o700)
            except FileExistsError: raise AuthError('refresh_in_progress')
            held.append((path, path.stat().st_ino))
        thread = threading.Thread(target=heartbeat, daemon=True)
        thread.start()
        yield lambda: healthy() and not compromised.is_set()
    finally:
        stop.set()
        if thread: thread.join(timeout=1)
        for path, inode in reversed(held):
            try:
                if path.stat().st_ino == inode: path.rmdir()
            except OSError: pass  # Never remove another process's lock or contents.

def update_keychain(account, document):
    # SecItemUpdate avoids putting credential JSON into process arguments/stdin.
    cf = ctypes.CDLL('/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation')
    sec = keychain_ui(_keychain_ui_allowed)
    ptr = ctypes.c_void_p
    cf.CFStringCreateWithCString.argtypes = [ptr, ctypes.c_char_p, ctypes.c_uint32]
    cf.CFStringCreateWithCString.restype = ptr
    cf.CFDataCreate.argtypes = [ptr, ptr, ctypes.c_long]; cf.CFDataCreate.restype = ptr
    cf.CFDictionaryCreate.argtypes = [ptr, ptr, ptr, ctypes.c_long, ptr, ptr]
    cf.CFDictionaryCreate.restype = ptr
    cf.CFRelease.argtypes = [ptr]
    sec.SecItemUpdate.argtypes = [ptr, ptr]; sec.SecItemUpdate.restype = ctypes.c_int32
    owned = []
    def constant(name): return ptr.in_dll(sec, name).value
    def string(value):
        p = cf.CFStringCreateWithCString(None, value.encode(), 0x08000100); owned.append(p); return p
    def dictionary(items):
        keys = (ptr * len(items))(*[constant(k) for k, _ in items])
        values = (ptr * len(items))(*[v for _, v in items])
        p = cf.CFDictionaryCreate(None, keys, values, len(items),
            ctypes.addressof(ctypes.c_byte.in_dll(cf, 'kCFTypeDictionaryKeyCallBacks')),
            ctypes.addressof(ctypes.c_byte.in_dll(cf, 'kCFTypeDictionaryValueCallBacks')))
        owned.append(p); return p
    try:
        query = dictionary([('kSecClass', constant('kSecClassGenericPassword')),
                            ('kSecAttrService', string('Claude Code-credentials')),
                            ('kSecAttrAccount', string(account))])
        data = json.dumps(document, separators=(',', ':')).encode()
        buffer = ctypes.create_string_buffer(data)
        value = cf.CFDataCreate(None, buffer, len(data)); owned.append(value)
        changes = dictionary([('kSecValueData', value)])
        if sec.SecItemUpdate(query, changes) != 0: raise AuthError('keychain_unavailable')
    finally:
        for p in reversed(owned):
            if p: cf.CFRelease(p)

def renew(account, expected, read_record, opener, now, force=False, write=update_keychain, lock=refresh_lock):
    with lock() as healthy:
        current = read_record(account)
        token = (current or {}).get('claudeAiOauth', {})
        if token.get('refreshToken') != expected.get('refreshToken') or token.get('accessToken') != expected.get('accessToken'):
            # A login or another refresh won the race. Re-read on the next poll.
            raise AuthError('refresh_in_progress')
        if not force and token.get('expiresAt', 0) > (now() + 300) * 1000: return token
        refresh = token.get('refreshToken')
        if not isinstance(refresh, str) or not refresh: raise AuthError('expired_credential')
        scopes = token.get('scopes')
        if not isinstance(scopes, list) or not scopes or not all(isinstance(s, str) for s in scopes):
            raise AuthError('refresh_unavailable')
        client = token.get('clientId') or CLIENT_ID
        if client != CLIENT_ID: raise AuthError('refresh_unavailable')
        request = urllib.request.Request(TOKEN_URL, data=json.dumps({
            'grant_type': 'refresh_token', 'refresh_token': refresh,
            'client_id': client, 'scope': ' '.join(scopes)}).encode(), headers={'Content-Type': 'application/json', 'Accept': 'application/json', 'User-Agent': 'KeypadCompanion/1.0'}, method='POST')
        try:
            with opener.open(request, timeout=12) as reply:
                if reply.status != 200: raise AuthError('refresh_unavailable')
                raw = reply.read(65537)
                if len(raw) > 65536: raise AuthError('refresh_unavailable')
                response = json.loads(raw)
        except urllib.error.HTTPError as error:
            reason = 'oauth_http_' + str(error.code) if error.code in (401, 403, 429, 503) else None
            try:
                raw = error.read(4096)
                payload = json.loads(raw)
                value = payload.get('error') if isinstance(payload, dict) else None
                if value in ('invalid_grant', 'invalid_scope', 'invalid_client', 'invalid_request', 'unauthorized_client', 'unsupported_grant_type'): reason = value
                elif isinstance(value, dict) and value.get('type') in ('permission_error', 'authentication_error', 'invalid_request_error'):
                    reason = value['type']
            except Exception:
                if error.code == 403 and 'text/html' in error.headers.get('Content-Type', ''): reason = 'gateway_forbidden'
            code = 'sign_in_required' if error.code == 401 or reason == 'invalid_grant' else 'refresh_unavailable'
            error.close(); raise AuthError(code, reason) from None
        except AuthError: raise
        except Exception: raise AuthError('refresh_unavailable') from None
        if not isinstance(response, dict): raise AuthError('refresh_unavailable')
        access = response.get('access_token'); rotated = response.get('refresh_token', refresh)
        ttl = response.get('expires_in')
        scope = response.get('scope', ' '.join(scopes))
        if not isinstance(scope, str): raise AuthError('refresh_unavailable')
        granted = scope.split()
        if not granted: raise AuthError('refresh_unavailable')
        if not isinstance(access, str) or not access or not isinstance(rotated, str) or not rotated or isinstance(ttl, bool) or not isinstance(ttl, (int, float)) or not math.isfinite(ttl) or not 0 < ttl <= 86400 * 30 or not set(granted).issubset(scopes):
            raise AuthError('refresh_unavailable')
        latest = read_record(account)
        check = (latest or {}).get('claudeAiOauth', {})
        if not healthy() or check.get('refreshToken') != refresh or check.get('accessToken') != token.get('accessToken'):
            raise AuthError('refresh_in_progress')
        updated = {**check, 'accessToken': access, 'refreshToken': rotated, 'expiresAt': int((now() + ttl) * 1000), 'scopes': granted}
        if isinstance(response.get('refresh_token_expires_in'), (int, float)) and not isinstance(response.get('refresh_token_expires_in'), bool):
            updated['refreshTokenExpiresAt'] = int((now() + response['refresh_token_expires_in']) * 1000)
        write(account, {**latest, 'claudeAiOauth': updated})
        return updated
