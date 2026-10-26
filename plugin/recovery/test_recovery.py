import copy, datetime, unittest, tempfile, os
from pathlib import Path
from unittest.mock import patch
from recovery_monitor import resolve_runtime
from recovery_monitor import Engine, quota_ready, AppTools,quota_failure

STAMP=1800000000
Q={'shortRemaining':2,'weekRemaining':10,'updatedUtc':datetime.datetime.fromtimestamp(STAMP,datetime.timezone.utc).isoformat()}
FAIL={'id':'turn1','status':'failed','error':{'codexErrorInfo':'usageLimitExceeded'}}

class RecoveryTests(unittest.TestCase):
    def armed(self):
        e=Engine();e.observe('a',True,{'id':'turn1'},STAMP-40);e.observe('a',False,FAIL,STAMP-35);return e
    def test_running_quota_failure_recovers(self):
        self.assertTrue(self.armed().eligible('a',FAIL,False,Q,STAMP))
    def test_old_idle_failure_never_armed(self):
        e=Engine();e.observe('a',False,FAIL,STAMP-35);self.assertFalse(e.eligible('a',FAIL,False,Q,STAMP))
    def test_manual_interrupt_overrides_quota_error(self):
        e=self.armed();t=dict(FAIL,status='interrupted');e.observe('a',False,t,STAMP);self.assertFalse(e.state['pending'])
    def test_normal_completion_cancels(self):
        e=self.armed();e.observe('a',False,dict(FAIL,status='completed'),STAMP);self.assertFalse(e.state['pending'])
    def test_network_and_rate_limit_not_usage_quota(self):
        for error in ['rateLimitExceeded','unauthorized','internalServerError','sessionBudgetExceeded',None]:
            e=self.armed();t=dict(FAIL,error={'codexErrorInfo':error});e.observe('a',False,t,STAMP);self.assertFalse(e.state['pending'])
    def test_missing_error_not_retry(self):
        e=self.armed();e.observe('a',False,dict(FAIL,error=None),STAMP);self.assertFalse(e.state['pending'])
    def test_new_user_turn_cancels_old_failure(self):
        e=self.armed();e.observe('a',False,dict(FAIL,id='turn2'),STAMP);self.assertFalse(e.state['pending'])
    def test_running_never_interrupted(self):
        self.assertFalse(self.armed().eligible('a',FAIL,True,Q,STAMP))
    def test_grace_period(self):
        e=Engine();e.observe('a',True,{'id':'turn1'},STAMP);e.observe('a',False,FAIL,STAMP);self.assertFalse(e.eligible('a',FAIL,False,Q,STAMP))
    def test_both_windows_must_have_quota(self):
        for a,b in [(0,90),(90,0),(None,90),(90,None)]:
            self.assertFalse(self.armed().eligible('a',FAIL,False,dict(Q,shortRemaining=a,weekRemaining=b),STAMP))
    def test_stale_or_future_quota_rejected(self):
        self.assertFalse(quota_ready(Q,STAMP+46));self.assertFalse(quota_ready(Q,STAMP-1))
    def test_claim_persisted_before_send_and_never_duplicate_after_restart(self):
        e=self.armed();e.claim('a');e=Engine(copy.deepcopy(e.state));e.observe('a',True,{'id':'turn1'},STAMP-40);e.observe('a',False,FAIL,STAMP-35)
        self.assertFalse(e.eligible('a',FAIL,False,Q,STAMP))
    def test_new_quota_failure_can_recover_next_turn(self):
        e=self.armed();e.claim('a');t=dict(FAIL,id='turn2');e.observe('a',True,t,STAMP-40);e.observe('a',False,t,STAMP-35)
        self.assertTrue(e.eligible('a',t,False,Q,STAMP))
    def test_active_again_removes_pending(self):
        e=self.armed();e.observe('a',True,{'id':'turn2'},STAMP);self.assertFalse(e.state['pending'])
    def test_send_uses_original_desktop_thread_without_model_override(self):
        a=AppTools({'callerThreadId':'caller'});a.schemas={'send_message_to_thread':{}}
        captured=[]
        a.rpc=lambda method,params: captured.append((method,params)) or {'content':[]}
        a.send('original')
        args=captured[0][1]['arguments']
        self.assertEqual(args['threadId'],'original');self.assertEqual(args['hostId'],'local')
        self.assertNotIn('model',args);self.assertNotIn('thinking',args)
    def test_actual_desktop_message_without_error_code_is_recognized(self):
        for apostrophe in ["'",'\u2019','\ufffd\ufffd']:
            t={'id':'turn1','status':'failed','error':{'message':'You'+apostrophe+'ve hit your usage limit. Upgrade to Pro or try again at 2:32 AM.','additionalDetails':None}}
            self.assertTrue(quota_failure(t))
            e=self.armed();e.observe('a',False,t,STAMP-35)
            self.assertTrue(e.eligible('a',t,False,Q,STAMP))
    def test_message_fallback_still_never_resumes_manual_interrupt(self):
        t={'status':'interrupted','error':{'message':"You've hit your usage limit. Try again later."}}
        self.assertFalse(quota_failure(t))
    def test_arbitrary_quota_mentions_never_match(self):
        for message in ["The user said: You've hit your usage limit.",'Quota exceeded','429 Too many requests','usage limit',"You've hit your usage limit for a local tool"]:
            self.assertFalse(quota_failure({'status':'failed','error':{'message':message}}))
    def test_nonquota_structured_code_blocks_text_fallback(self):
        self.assertFalse(quota_failure({'status':'failed','error':{'codexErrorInfo':'rateLimitExceeded','message':"You've hit your usage limit."}}))

if __name__=='__main__':unittest.main(verbosity=2)

class RuntimeRefreshTests(unittest.TestCase):
    def test_upgrade_replaces_deleted_node_and_server_without_guessing_endpoint(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); node=root/'OpenAI/Codex/runtimes/cua_node/new/bin/node.exe';node.parent.mkdir(parents=True);node.touch()
            server=root/'home/plugins/cache/openai-bundled/codex-app-tools/new/server.mjs';server.parent.mkdir(parents=True);server.touch()
            with patch.dict(os.environ, {'LOCALAPPDATA':td,'CODEX_HOME':str(root/'home')}, clear=True):
                got=resolve_runtime({'node':str(root/'removed.exe'),'server':'removed','pipe':'original','callerThreadId':'caller'})
            self.assertEqual(got['node'],str(node));self.assertEqual(got['server'],str(server));self.assertEqual(got['pipe'],'original');self.assertEqual(got['callerThreadId'],'caller')
    def test_current_invocation_refreshes_endpoint_and_identity(self):
        with patch.dict(os.environ, {'CODEX_APP_TOOLS_PIPE_PATH':'current-endpoint','CODEX_THREAD_ID':'current-caller'}):
            got=resolve_runtime({'pipe':'stale','callerThreadId':'old'}, refresh_identity=True)
        self.assertEqual(got['pipe'],'current-endpoint');self.assertEqual(got['callerThreadId'],'current-caller')

    def test_old_worker_environment_cannot_overwrite_refreshed_connection(self):
        with patch.dict(os.environ, {'CODEX_APP_TOOLS_PIPE_PATH':'stale-worker-endpoint','CODEX_THREAD_ID':'old-caller'}):
            got=resolve_runtime({'pipe':'fresh-plugin-endpoint','callerThreadId':'fresh-caller'})
        self.assertEqual(got['pipe'],'fresh-plugin-endpoint');self.assertEqual(got['callerThreadId'],'fresh-caller')

class UpgradeLifecycleTests(unittest.TestCase):
    def test_new_cache_handoff_reaches_already_running_worker(self):
        from recovery_monitor import load_runtime, publish_runtime, atomic
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);old=root/'old-cache';new=root/'new-cache';data=root/'data'
            old.mkdir();new.mkdir()
            atomic(old/'recovery-runtime.json', {'pipe':'old-endpoint','callerThreadId':'old-caller'})
            with patch.dict(os.environ, {'CODEX_APP_TOOLS_PIPE_PATH':'new-endpoint','CODEX_THREAD_ID':'new-caller'}):
                publish_runtime({},data)
            with patch.dict(os.environ, {'CODEX_APP_TOOLS_PIPE_PATH':'old-endpoint','CODEX_THREAD_ID':'old-caller'}):
                got=load_runtime(old,data)
            self.assertEqual(got['pipe'],'new-endpoint');self.assertEqual(got['callerThreadId'],'new-caller')
    def test_worker_cannot_publish_inferred_endpoint_without_current_identity(self):
        from recovery_monitor import publish_runtime
        with tempfile.TemporaryDirectory() as td:
            with patch.dict(os.environ, {}, clear=True):
                with patch('pathlib.Path.home',return_value=Path(td)):
                    publish_runtime({'pipe':'cached','callerThreadId':'cached'},Path(td))
            self.assertFalse((Path(td)/'bridge-runtime.json').exists())
    def test_stale_valid_environment_node_does_not_replace_shared_node(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);old=root/'old.exe';new=root/'new.exe';old.touch();new.touch()
            with patch.dict(os.environ, {'CODEX_MCP_NODE_PATH':str(old)}):got=resolve_runtime({'node':str(new)})
            self.assertEqual(got['node'],str(new))
