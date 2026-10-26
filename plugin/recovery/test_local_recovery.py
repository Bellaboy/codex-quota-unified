"""本机回归：验证提示、并发监控互斥、异常退出释放和同轮次仅发送一次。
输入为临时目录内的合成任务状态；不连接 Codex、不发送真实消息，不覆盖运行状态。
运行：python -m unittest test_local_recovery。
"""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import uuid
from contextlib import nullcontext
from unittest.mock import patch
import recovery_monitor as monitor
from test_recovery import STAMP, Q, FAIL

LOCK_CHILD = r'''
import sys
from recovery_monitor import monitor_lock
with monitor_lock(sys.argv[1]) as owned:
    print(int(owned), flush=True)
    if owned:sys.stdin.readline()
'''
SEND_CHILD = r'''
import sys,time
from pathlib import Path
from recovery_monitor import monitor_lock,Engine,atomic,read_json
from test_recovery import STAMP,Q,FAIL
root=Path(sys.argv[2])
with monitor_lock(sys.argv[1]) as owned:
    if not owned:sys.exit(0)
    engine=Engine(read_json(root/'state.json'))
    if engine.eligible('test-thread',FAIL,False,Q,STAMP):
        engine.claim('test-thread')
        atomic(root/'state.json',engine.state)
        with (root/'deliveries.txt').open('a',encoding='utf-8') as out:out.write('mock-send\n')
    time.sleep(.3)
'''

@unittest.skipUnless(os.name == 'nt', 'Windows 本机互斥测试')
class LocalRecoveryTests(unittest.TestCase):
    def name(self):return 'Local\\CodexQuotaBattery.Test.' + uuid.uuid4().hex
    def child(self, name):
        return subprocess.Popen([sys.executable,'-c',LOCK_CHILD,name],stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,creationflags=subprocess.CREATE_NO_WINDOW)
    def test_prompt_matches_requested_text(self):
        self.assertEqual(monitor.PROMPT,'本轮工作因 Codex 额度耗尽而中断，现在额度已恢复。请从该中断的已有进度继续用户原先要求的工作验证和交付')
    def test_concurrent_monitor_cannot_own_same_lock(self):
        name=self.name();child=self.child(name)
        try:
            self.assertEqual(child.stdout.readline().strip(),'1')
            with monitor.monitor_lock(name) as owned:self.assertFalse(owned)
            child.stdin.close();child.wait(timeout=5)
            with monitor.monitor_lock(name) as owned:self.assertTrue(owned)
        finally:
            if child.poll() is None:child.kill();child.wait(timeout=5)
            for stream in (child.stdin,child.stdout,child.stderr):
                if stream is not None:stream.close()
    def test_crashed_worker_releases_lock(self):
        name=self.name();child=self.child(name)
        try:
            self.assertEqual(child.stdout.readline().strip(),'1')
            child.kill();child.wait(timeout=5)
            with monitor.monitor_lock(name) as owned:self.assertTrue(owned)
        finally:
            if child.poll() is None:child.kill();child.wait(timeout=5)
            for stream in (child.stdin,child.stdout,child.stderr):
                if stream is not None:stream.close()
    def test_unowned_run_never_loads_or_writes_state(self):
        with patch.object(monitor,'monitor_lock',return_value=nullcontext(False)),patch.object(monitor,'_run_owned') as run:
            monitor.run(os.getpid(),once=True)
            run.assert_not_called()
    def test_two_workers_and_restart_produce_one_mock_send(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);name=self.name();engine=monitor.Engine()
            engine.observe('test-thread',True,{'id':'turn1'},STAMP-40)
            engine.observe('test-thread',False,FAIL,STAMP-35)
            monitor.atomic(root/'state.json',engine.state)
            workers=[subprocess.Popen([sys.executable,'-c',SEND_CHILD,name,td],creationflags=subprocess.CREATE_NO_WINDOW) for _ in range(2)]
            try:
                for child in workers:self.assertEqual(child.wait(timeout=8),0)
                again=subprocess.run([sys.executable,'-c',SEND_CHILD,name,td],timeout=8,creationflags=subprocess.CREATE_NO_WINDOW)
                self.assertEqual(again.returncode,0)
                self.assertEqual((root/'deliveries.txt').read_text().splitlines(),['mock-send'])
            finally:
                for child in workers:
                    if child.poll() is None:child.kill();child.wait(timeout=5)

if __name__=='__main__':unittest.main(verbosity=2)