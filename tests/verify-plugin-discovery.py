"""通过官方 Codex 加载器验证注册与自动启动，不直接启动插件二进制、不运行模型。
输入：已注册的本地市场目录。输出：只含验证结论的 JSON；不记录会话或桥接身份。
运行：python tests/verify-plugin-discovery.py --marketplace <目录> --report <报告文件>
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import threading
import time


class Client:
    def __init__(self):
        self.sequence = 0
        self.messages = queue.Queue()
        self.process = subprocess.Popen(
            [shutil.which('codex'), 'app-server', '--stdio'],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
        def read():
            for line in self.process.stdout:
                try: self.messages.put(json.loads(line))
                except ValueError: pass
        threading.Thread(target=read, daemon=True).start()
        self.call('initialize', {'clientInfo': {'name': 'quota_plugin_discovery_test', 'version': '1'},
                                 'capabilities': {'experimentalApi': True}})
        self.process.stdin.write('{"method":"initialized","params":{}}\n')
        self.process.stdin.flush()

    def call(self, method, params):
        self.sequence += 1
        self.process.stdin.write(json.dumps({'id': self.sequence, 'method': method, 'params': params})+'\n')
        self.process.stdin.flush()
        deadline = time.monotonic()+50
        while time.monotonic() < deadline:
            try: message = self.messages.get(timeout=1)
            except queue.Empty: continue
            if message.get('id') != self.sequence: continue
            if 'error' in message: raise RuntimeError(str(message['error']))
            return message['result']
        raise TimeoutError(method)

    def close(self):
        self.process.stdin.close()
        self.process.wait(timeout=10)
        self.process.stdout.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--marketplace', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    started = datetime.datetime.now(datetime.timezone.utc)
    client = Client()
    report = {}
    try:
        details = client.call('plugin/read', {'pluginName': 'codex-quota-unified',
            'marketplacePath': str(args.marketplace/'.agents/plugins/marketplace.json')})['plugin']
        report['declaredServers'] = details['mcpServers']
        assert report['declaredServers'] == ['quota-unified'], '官方加载器未识别额度服务'
        # 无会话的状态枚举会在返回后关闭临时 MCP 连接，无法验证持续运行。
        # 临时会话只持有 MCP 生命周期；不创建模型轮次、不向用户侧栏保存任务。
        test_thread = client.call('thread/start', {'ephemeral': True})['thread']['id']
        servers = client.call('mcpServerStatus/list', {'limit': 100,
            'threadId': test_thread, 'serverName': 'quota-unified'})['data']
        quota = next((server for server in servers if server['name'] == 'quota-unified'), None)
        report['serverListed'] = quota is not None
        report['toolNames'] = list((quota or {}).get('tools', {}))
        print(json.dumps(report, ensure_ascii=False), flush=True)
        assert any(name.endswith('get_quota_status') for name in report['toolNames']), 'MCP 状态工具未完成加载'
        status_path = Path(os.environ['LOCALAPPDATA'])/'CodexQuotaUnified/status.json'
        deadline = time.monotonic()+45
        while time.monotonic() < deadline:
            status = json.loads(status_path.read_text('utf-8-sig'))
            fresh = datetime.datetime.fromisoformat(status['lastUpdate'].replace('Z', '+00:00')) >= started
            if fresh and status.get('mcpHosted') and status.get('recovery', {}).get('status') == 'monitoring': break
            time.sleep(1)
        report.update(mcpHosted=status.get('mcpHosted'), recoveryStatus=status.get('recovery', {}).get('status'),
                      quotaPollingSeconds=status.get('quotaPollingSeconds'), bounds=status.get('bounds'))
        report['freshQuotaRead'] = fresh
        assert report['mcpHosted'] is True, '实际所有者没有经 MCP 托管启动'
        assert report['freshQuotaRead'], '仅发现旧状态文件，没有本次启动后的新额度读数'
        assert report['recoveryStatus'] == 'monitoring', '恢复监控尚未就绪'
        report['passed'] = True
    finally:
        client.close()
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), 'utf-8')
    print(json.dumps(report, ensure_ascii=False))


if __name__ == '__main__': main()
