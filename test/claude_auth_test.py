import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
import urllib.error
from unittest.mock import patch, MagicMock
from types import SimpleNamespace
sys.path.insert(0, str(Path(__file__).parents[1]))
import claude_auth as auth

@contextlib.contextmanager
def unlocked(): yield lambda: True

class Reply(io.BytesIO):
    status = 200
class Opener:
    def __init__(self, response): self.response=response; self.requests=[]
    def open(self, request, **options):
        self.requests.append(request)
        if isinstance(self.response, Exception): raise self.response
        return Reply(json.dumps(self.response).encode())

class AuthTests(unittest.TestCase):
    def setUp(self):
        self.token={'accessToken':'old-fixture','refreshToken':'refresh-fixture','expiresAt':1,'subscriptionType':'team','scopes':['user:profile','user:inference']}
        self.document={'claudeAiOauth':self.token,'unrelated':{'keep':True}}
        self.response={'access_token':'new-fixture','refresh_token':'rotated-fixture','expires_in':3600,'scope':'user:profile user:inference'}
        self.writes=[]
    def renew(self, response=None, read=None, **options):
        return auth.renew('owner',self.token,read or (lambda _:self.document),Opener(response or self.response),lambda:100,write=lambda a,d:self.writes.append((a,d)),lock=unlocked,**options)
    def test_native_policy_is_process_local_and_background_fails_closed(self):
        api=MagicMock(return_value=0)
        with patch.object(auth.ctypes,'CDLL',return_value=SimpleNamespace(SecKeychainSetUserInteractionAllowed=api)),patch.object(auth,'_keychain_ui_allowed',False):
            auth.keychain_ui();self.assertFalse(auth._keychain_ui_allowed);api.assert_called_with(False)
            auth.keychain_ui(True);self.assertTrue(auth._keychain_ui_allowed);api.assert_called_with(True)
            api.return_value=-1
            with self.assertRaisesRegex(auth.AuthError,'keychain_unavailable'):auth.keychain_ui(False)
    def test_rotation_and_other_keychain_fields_are_preserved(self):
        fresh=self.renew();self.assertEqual(fresh['accessToken'],'new-fixture');self.assertEqual(fresh['refreshToken'],'rotated-fixture');self.assertEqual(fresh['expiresAt'],3700000)
        self.assertEqual(self.writes[0][1]['unrelated'],{'keep':True})
    def test_no_refresh_when_login_is_fresh(self):
        self.token['expiresAt']=1000000
        self.assertEqual(self.renew()['accessToken'],'old-fixture');self.assertEqual(self.writes,[])
    def test_account_change_never_overwrites_a_new_login(self):
        reads=iter([self.document,{'claudeAiOauth':{**self.token,'accessToken':'other-login'}}])
        with self.assertRaisesRegex(auth.AuthError,'refresh_in_progress'):self.renew(read=lambda _:next(reads))
        self.assertEqual(self.writes,[])
    def test_refresh_requests_only_original_scopes_at_the_fixed_endpoint(self):
        opener=Opener(self.response)
        auth.renew('owner',self.token,lambda _:self.document,opener,lambda:100,write=lambda *_:None,lock=unlocked)
        request=opener.requests[0];body=json.loads(request.data)
        self.assertEqual(request.get_header('User-agent'),'KeypadCompanion/1.0');self.assertEqual(request.full_url,auth.TOKEN_URL);self.assertEqual(body['grant_type'],'refresh_token');self.assertEqual(body['scope'],'user:profile user:inference')
    def test_bad_or_expanded_reply_is_never_written(self):
        for update in [{'access_token':''},{'expires_in':True},{'expires_in':float('inf')},{'scope':'user:profile new:permission'},{'scope':3}]:
            with self.assertRaises(auth.AuthError):self.renew({**self.response,**update})
        self.assertEqual(self.writes,[])
    def test_revocation_and_transient_failure_are_distinct(self):
        for status,code in [(400,'refresh_unavailable'),(401,'sign_in_required'),(403,'refresh_unavailable'),(429,'refresh_unavailable'),(503,'refresh_unavailable'),(302,'refresh_unavailable')]:
            error=urllib.error.HTTPError(auth.TOKEN_URL,status,'fixture',{},io.BytesIO(b'{}'))
            with self.assertRaisesRegex(auth.AuthError,code):self.renew(error)
        self.assertEqual(self.writes,[])
    def test_cli_lock_contention_is_respected_and_only_own_locks_are_removed(self):
        with tempfile.TemporaryDirectory() as root:
            folder=Path(root)/'.claude';folder.mkdir()
            foreign=folder/'.oauth_refresh.lock';foreign.mkdir()
            with self.assertRaisesRegex(auth.AuthError,'refresh_in_progress'):
                with auth.refresh_lock(folder):pass
            self.assertTrue(foreign.exists());foreign.rmdir()
            with auth.refresh_lock(folder) as healthy:
                self.assertTrue(healthy());self.assertTrue(foreign.exists())
                self.assertTrue(Path(str(folder)+'.lock').exists())
            self.assertFalse(foreign.exists());self.assertFalse(Path(str(folder)+'.lock').exists())
    def test_failure_releases_both_locks(self):
        with tempfile.TemporaryDirectory() as root:
            folder=Path(root)/'.claude';folder.mkdir()
            with self.assertRaises(RuntimeError):
                with auth.refresh_lock(folder):raise RuntimeError('fixture')
            self.assertFalse((folder/'.oauth_refresh.lock').exists());self.assertFalse(Path(str(folder)+'.lock').exists())

if __name__=='__main__':unittest.main()
