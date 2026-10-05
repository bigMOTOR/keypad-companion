import importlib.util
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parents[1]))
from types import SimpleNamespace
import unittest
from unittest.mock import patch
spec = importlib.util.spec_from_file_location('usage', Path(__file__).parents[1] / 'claude-weekly-usage.py')
usage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(usage)

class UsageTests(unittest.TestCase):
    def test_new_login_fallback_and_exact_accounts(self):
        records={'owner':{'accessToken':'fixture-old','expiresAt':1,'subscriptionType':'team'},'unknown':{'accessToken':'fixture-new','expiresAt':200000,'subscriptionType':'team'}}
        calls=[]
        def read(account,allow_ui=False):
            calls.append((account,allow_ui))
            return {'claudeAiOauth':records[account]}
        with patch.object(usage.getpass,'getuser',return_value='owner'),patch.object(usage,'read_keychain',side_effect=read):
            self.assertEqual(usage.read_credential(100)['accessToken'],'fixture-new')
        self.assertEqual(calls,[('owner',False),('unknown',False)])
    def test_expired_is_distinct_from_denied(self):
        record={'claudeAiOauth':{'accessToken':'fixture','expiresAt':1,'subscriptionType':'team'}}
        with patch.object(usage,'read_keychain',return_value=record):
            self.assertEqual(usage.read_credential(100),{'error':'expired_credential'})
    def test_keychain_prompt_is_opt_in_and_not_mislabeled_as_missing_login(self):
        with patch.object(usage,'read_keychain',side_effect=usage.AuthError('keychain_interaction_required')) as reader:
            self.assertEqual(usage.read_credential(100),{'error':'keychain_interaction_required'})
            self.assertTrue(all(call.kwargs.get('allow_ui') is False for call in reader.call_args_list))
        with patch.object(usage,'read_keychain',return_value=None) as reader:
            usage.read_credential(100,allow_ui=True)
            self.assertTrue(all(call.kwargs.get('allow_ui') is True for call in reader.call_args_list))
    def test_blocked_old_account_does_not_hide_a_readable_new_login(self):
        def read(account,allow_ui=False):
            if account=='owner':raise usage.AuthError('keychain_interaction_required')
            return {'claudeAiOauth':{'accessToken':'fixture-new','expiresAt':1000000,'subscriptionType':'team'}}
        with patch.object(usage.getpass,'getuser',return_value='owner'),patch.object(usage,'read_keychain',side_effect=read):
            self.assertEqual(usage.read_credential(100)['accessToken'],'fixture-new')
    def test_reset_fraction_and_timezone(self):
        for fraction in ['1','12','1234','123456','123456789']:
            result=usage.allowance({'seven_day':{'utilization':82,'resets_at':'2030-01-02T00:00:00.'+fraction+'+00:00'}},1893456000)
            self.assertEqual(result['remaining'],18)
    def test_bad_reset_and_milliseconds_are_rejected(self):
        for reset in [None,True,1893456000,1893542400000]:
            with self.assertRaises(ValueError):usage.allowance({'seven_day':{'utilization':82,'resets_at':reset}},1893456000)
    def test_temporary_http_failure_preserves_provider_cooldown_only(self):
        for code in [429, 503]:
            error=usage.urllib.error.HTTPError(usage.USAGE_URL, code, 'private body', {'Retry-After':'600'}, None)
            self.assertEqual(usage.usage_error(error), {'error':'usage_unavailable','retryAfterSeconds':600})
        error=usage.urllib.error.HTTPError(usage.USAGE_URL, 401, 'private body', {}, None)
        self.assertEqual(usage.usage_error(error), {'error':'sign_in_required'})
        error=usage.urllib.error.HTTPError(usage.USAGE_URL, 429, 'private body', {'Retry-After':'private'}, None)
        self.assertEqual(usage.usage_error(error), {'error':'usage_unavailable'})
    def test_bearer_is_never_redirected(self):
        with self.assertRaises(usage.urllib.error.HTTPError):
            usage.NoRedirect().redirect_request(SimpleNamespace(full_url=usage.USAGE_URL),None,302,'redirect',{},'https://other.example')

if __name__=='__main__':unittest.main()
