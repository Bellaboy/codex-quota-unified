"""只读获取当前任务轮次；额度统一由原生顶栏每30秒读取。"""
from pathlib import Path
import atexit,datetime,json,os,queue,shutil,subprocess,threading,time
class TurnReader:
    def __init__(self):
        self.process=None;self.messages=None;self.sequence=0;self.cache=None;self.cache_at=0
        atexit.register(self.close)
    def close(self):
        p=self.process;self.process=None
        if p:
            try:p.terminate();p.wait(timeout=3)
            except (OSError,subprocess.TimeoutExpired):
                try:p.kill()
                except OSError:pass
    def _open(self):
        executable=shutil.which('codex')
        if not executable or not str(executable).lower().endswith('.exe'):
            root=Path(os.environ['LOCALAPPDATA'])/'OpenAI/Codex/bin'
            candidates=list(root.glob('*/codex.exe'))
            if not candidates:raise RuntimeError('未找到官方Codex，请先打开并登录Codex。')
            executable=str(max(candidates,key=lambda p:p.stat().st_mtime))
        self.process=subprocess.Popen([executable,'-c','plugins."codex-quota-unified@codex-unified-tools".enabled=false','app-server','--stdio'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,encoding='utf-8',creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        self.messages=queue.Queue();p=self.process;messages=self.messages
        def read():
            for line in p.stdout:
                try:messages.put(json.loads(line))
                except ValueError:pass
        threading.Thread(target=read,daemon=True).start()
        self._request('initialize',{'clientInfo':{'name':'quota_battery_plugin','version':'1.1.0'}})
        p.stdin.write('{"method":"initialized","params":{}}\n');p.stdin.flush()
    def _request(self,method,params=None):
        self.sequence+=1;request_id=self.sequence
        self.process.stdin.write(json.dumps({'id':request_id,'method':method,'params':params or {}})+'\n');self.process.stdin.flush()
        deadline=time.monotonic()+20
        while time.monotonic()<deadline:
            try:response=self.messages.get(timeout=1)
            except queue.Empty:
                if self.process.poll() is not None:raise RuntimeError('额度连接已关闭')
                continue
            if response.get('id')!=request_id:continue
            if response.get('error'):raise RuntimeError('暂时无法读取额度，请检查Codex登录状态。')
            return response.get('result') or {}
        raise RuntimeError('额度读取超时')
    def latest(self, tid):
        if not self.process or self.process.poll() is not None:self._open()
        return self._request('thread/turns/list', {'threadId':tid,'limit':1,'itemsView':'notLoaded','sortDirection':'desc'})
