import importlib.util
import json
from pathlib import Path
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
        def read(args,**kwargs):
            calls.append(args)
            return SimpleNamespace(returncode=0,stdout=json.dumps({'claudeAiOauth':records[args[args.index('-a')+1]]}).encode())
        with patch.object(usage.getpass,'getuser',return_value='owner'),patch.object(usage.subprocess,'run',side_effect=read):
            self.assertEqual(usage.read_credential(100)['accessToken'],'fixture-new')
        self.assertEqual([c[c.index('-a')+1] for c in calls],['owner','unknown'])
        self.assertTrue(all(c[c.index('-s')+1]=='Claude Code-credentials' for c in calls))
    def test_expired_is_distinct_from_denied(self):
        record={'claudeAiOauth':{'accessToken':'fixture','expiresAt':1,'subscriptionType':'team'}}
        with patch.object(usage.subprocess,'run',return_value=SimpleNamespace(returncode=0,stdout=json.dumps(record).encode())):
            self.assertEqual(usage.read_credential(100),{'error':'expired_credential'})
    def test_reset_fraction_and_timezone(self):
        for fraction in ['1','12','1234','123456','123456789']:
            result=usage.allowance({'seven_day':{'utilization':82,'resets_at':'2030-01-02T00:00:00.'+fraction+'+00:00'}},1893456000)
            self.assertEqual(result['remaining'],18)
    def test_bad_reset_and_milliseconds_are_rejected(self):
        for reset in [None,True,1893456000,1893542400000]:
            with self.assertRaises(ValueError):usage.allowance({'seven_day':{'utilization':82,'resets_at':reset}},1893456000)
    def test_bearer_is_never_redirected(self):
        with self.assertRaises(usage.urllib.error.HTTPError):
            usage.NoRedirect().redirect_request(SimpleNamespace(full_url=usage.USAGE_URL),None,302,'redirect',{},'https://other.example')

if __name__=='__main__':unittest.main()
