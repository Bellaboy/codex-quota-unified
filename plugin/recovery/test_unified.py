"""统一额度快照的针对性回归，不连接真实会话、不发送消息。"""
import copy
import unittest
from recovery_monitor import Engine, quota_ready
from test_recovery import STAMP, Q, FAIL

class UnifiedQuotaTests(unittest.TestCase):
    def test_thirty_second_snapshot_is_usable(self):
        self.assertTrue(quota_ready(Q, STAMP + 30))
        self.assertFalse(quota_ready(Q, STAMP + 45))

    def test_failed_refresh_or_stale_snapshot_cannot_resume(self):
        self.assertFalse(quota_ready(dict(Q, error='query failed'), STAMP))
        self.assertFalse(quota_ready(dict(Q, stale=True), STAMP))

    def test_migrated_dispatch_ledger_blocks_same_turn(self):
        engine = Engine()
        engine.observe('a', True, {'id':'turn1'}, STAMP-40)
        engine.observe('a', False, FAIL, STAMP-35)
        engine.claim('a')
        migrated = Engine({'dispatched':copy.deepcopy(engine.state['dispatched'])})
        migrated.observe('a', True, {'id':'turn1'}, STAMP-40)
        migrated.observe('a', False, FAIL, STAMP-35)
        self.assertFalse(migrated.eligible('a', FAIL, False, Q, STAMP))

if __name__ == '__main__': unittest.main(verbosity=2)
