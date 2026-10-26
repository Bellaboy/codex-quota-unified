"""Quota-only recovery. Reads official MCP metadata; never launches a CLI work turn.

Only the bundled desktop send_message_to_thread tool can resume work. No automatic
approval responses, API credits, model overrides, or historical idle-task replay.
"""
from pathlib import Path
from contextlib import contextmanager
import argparse, ctypes, datetime, json, os, queue, re, subprocess, threading, time
from turn_reader import TurnReader

ROOT = Path(__file__).resolve().parent
DATA = Path(os.environ['LOCALAPPDATA']) / 'CodexQuotaUnified'
PROMPT = '本轮工作因 Codex 额度耗尽而中断，现在额度已恢复。请从该中断的已有进度继续用户原先要求的工作验证和交付'


def resolve_runtime(config, refresh_identity=False):
    """Refresh vendor runtime files after desktop upgrades; never discover pipes."""
    import shutil
    result = dict(config)
    local = Path(os.environ.get('LOCALAPPDATA', '.')) / 'OpenAI/Codex'
    node = os.environ.get('CODEX_MCP_NODE_PATH')
    if refresh_identity and node and Path(node).is_file():
        result['node'] = node
    elif not Path(result.get('node') or '').is_file():
        choices = list((local / 'runtimes/cua_node').glob('*/bin/node.exe'))
        candidate = max(choices, key=lambda p: p.stat().st_mtime) if choices else None
        result['node'] = str(candidate) if candidate else shutil.which('node')
    home = Path(os.environ['CODEX_HOME']) if os.environ.get('CODEX_HOME') else Path.home() / '.codex'
    servers = list((home / 'plugins/cache/openai-bundled/codex-app-tools').glob('*/server.mjs'))
    if servers:
        result['server'] = str(max(servers, key=lambda p: p.stat().st_mtime))
    # Only a current invocation may supply an endpoint and caller identity.
    for key, env in [('pipe', 'CODEX_APP_TOOLS_PIPE_PATH'), ('callerThreadId', 'CODEX_THREAD_ID')]:
        if refresh_identity and os.environ.get(env): result[key] = os.environ[env]
    return result

def load_runtime(root=ROOT, data=DATA):
    """All plugin cache versions rendezvous through one per-user bridge file."""
    config = {}
    shared = read_json(data / 'bridge-runtime.json', {})
    if shared.get('pipe') and shared.get('callerThreadId'):
        config.update({k: shared[k] for k in ('pipe', 'callerThreadId', 'node', 'server') if shared.get(k)})
    return resolve_runtime(config)


def publish_runtime(config, data=DATA):
    # Called only by a current official plugin invocation, never by a worker
    # inheriting the old desktop environment. No endpoint enumeration.
    refreshed = resolve_runtime(config, refresh_identity=True)
    if os.environ.get('CODEX_APP_TOOLS_PIPE_PATH') and os.environ.get('CODEX_THREAD_ID'):
        data.mkdir(parents=True, exist_ok=True)
        atomic(data / 'bridge-runtime.json', {k: refreshed[k] for k in ('pipe', 'callerThreadId', 'node', 'server') if refreshed.get(k)})
    return refreshed

def atomic(path, value):
    temp = path.with_name(path.name + '.' + str(os.getpid()) + '.tmp')
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2), 'utf-8')
    os.replace(temp, path)

def read_json(path, default=None):
    try: return json.loads(path.read_text('utf-8-sig'))
    except (OSError, ValueError): return default

def now(): return datetime.datetime.now(datetime.timezone.utc).isoformat()

def quota_ready(q, stamp):
    try:
        age = stamp - datetime.datetime.fromisoformat(q['updatedUtc'].replace('Z', '+00:00')).timestamp()
        return 0 <= age < 45 and q.get('error') is None and not q.get('stale', False) and all(isinstance(q.get(k), (int, float)) and q[k] > 0 for k in ('shortRemaining', 'weekRemaining'))
    except (KeyError, TypeError, ValueError): return False

def quota_failure(turn):
    # Interrupted includes an intentional stop: NEVER recover it, even with an old error.
    if turn.get('status') != 'failed':return False
    error=turn.get('error') or {}
    info=error.get('codexErrorInfo')
    if info is not None:return info == 'usageLimitExceeded'
    # Desktop read_thread currently omits codexErrorInfo in some genuine terminal
    # errors. Match ONLY its known official error prefix in turn.error.message,
    # never conversation text, command output, or a generic mention of quota.
    message=error.get('message','')
    return isinstance(message,str) and bool(re.match(
        r"^You(?:['\u2019]|\ufffd{1,3}|\u00e2\u20ac\u2122)ve hit your usage limit[.!]",message))

def failure_evidence(turn):
    return 'official_error_code' if (turn.get('error') or {}).get('codexErrorInfo')=='usageLimitExceeded' else 'official_terminal_error_message'

class Engine:
    def __init__(self, state=None):
        self.state = state or {'watched': {}, 'pending': {}, 'dispatched': {}}
        for k in ('watched', 'pending', 'dispatched'): self.state.setdefault(k, {})
        self.state.setdefault('events',[])

    def event(self,tid,turn,reason):
        self.state['events'].append({'at':now(),'threadId':tid,'turnId':turn.get('id'),'reason':reason})
        self.state['events']=self.state['events'][-100:]

    def observe(self, tid, active, turn, stamp):
        if active:
            if turn and turn.get('id'):
                self.state['watched'][tid] = turn['id']
            self.state['pending'].pop(tid, None)
            return
        watched = self.state['watched'].get(tid)
        if not turn or not watched: return
        if turn.get('id') != watched or not quota_failure(turn):
            self.event(tid,turn,'new_turn' if turn.get('id') != watched else 'ignored_'+str(turn.get('status')))
            self.state['pending'].pop(tid, None)
            self.state['watched'].pop(tid, None)
            return
        key = tid + ':' + watched
        if key not in self.state['dispatched']:
            if tid not in self.state['pending']:self.event(tid,turn,'queued_'+failure_evidence(turn))
            self.state['pending'].setdefault(tid, {'turnId': watched, 'since': stamp,'evidence':failure_evidence(turn)})

    def eligible(self, tid, turn, active, q, stamp):
        p = self.state['pending'].get(tid)
        return bool(p and not active and stamp-p['since'] >= 30 and turn.get('id') == p['turnId']
                    and quota_failure(turn) and quota_ready(q, stamp)
                    and tid+':'+p['turnId'] not in self.state['dispatched'])

    def claim(self, tid):
        p = self.state['pending'].pop(tid)
        key = tid + ':' + p['turnId']
        # Persist BEFORE sending: uncertain delivery must not cause duplicate work.
        self.state['dispatched'][key] = {'at': now(), 'delivery': 'claimed'}
        self.event(tid,{'id':p['turnId']},'dispatch_claimed')
        self.state['watched'].pop(tid, None)
        return key

class AppTools:
    """Standard stdio client for the installed first-party app-tools MCP server."""
    def __init__(self, config):
        self.config=resolve_runtime(config);self.seq=0;self.messages=queue.Queue();self.p=None
    def open(self):
        self.config=resolve_runtime(self.config)
        env=os.environ.copy()
        if self.config.get('pipe'):env['CODEX_APP_TOOLS_PIPE_PATH']=self.config['pipe']
        if not env.get('CODEX_APP_TOOLS_PIPE_PATH'): raise RuntimeError('desktop_bridge_unavailable')
        self.messages=queue.Queue()
        self.p=subprocess.Popen([self.config['node'], self.config['server']], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding='utf-8',
            creationflags=subprocess.CREATE_NO_WINDOW,env=env)
        source=self.p;messages=self.messages
        def reader():
            for line in source.stdout:
                try:messages.put(json.loads(line))
                except ValueError:pass
        threading.Thread(target=reader,daemon=True).start()
        self.rpc('initialize',{'protocolVersion':'2024-11-05','capabilities':{},'clientInfo':{'name':'quota-recovery','version':'2.1.0'}})
        self.p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n');self.p.stdin.flush()
        self.schemas={x['name']:x for x in self.rpc('tools/list',{}).get('tools',[])}
    def rpc(self, method, params):
        self.seq+=1;seq=self.seq
        self.p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');self.p.stdin.flush()
        deadline=time.monotonic()+40
        while time.monotonic()<deadline:
            try:message=self.messages.get(timeout=1)
            except queue.Empty:
                if self.p.poll() is not None:raise RuntimeError('desktop_bridge_closed')
                continue
            if message.get('id')!=seq:continue
            if 'error' in message:raise RuntimeError('desktop_bridge_rpc_rejected')
            return message.get('result',{})
        raise RuntimeError('desktop_bridge_timeout')
    def call(self, name, args):
        if name not in self.schemas:raise RuntimeError('desktop_tool_unavailable')
        r=self.rpc('tools/call',{'name':name,'arguments':args,'_meta':{'threadId':self.config['callerThreadId']}})
        if r.get('isError'):
            # Preserve a bounded tool rejection reason for diagnosis, without
            # saving task messages or the tool result payload.
            reason=' '.join(x.get('text','') for x in r.get('content',[]) if x.get('type')=='text')[:240]
            raise RuntimeError('desktop_tool_rejected: '+reason)
        for item in r.get('content',[]):
            if item.get('type')=='text':
                try:return json.loads(item['text'])
                except ValueError:pass
        return r
    def threads(self):
        r=self.call('list_threads',{'limit':50})
        if r.get('unavailableHosts'):raise RuntimeError('desktop_host_unavailable')
        return {t['id']:t for t in r.get('pinnedThreads',[])+r.get('threads',[])
                if t.get('kind')=='codex' and t.get('hostId')=='local'}
    def latest(self, tid):
        r=self.call('read_thread',{'threadId':tid,'hostId':'local','turnLimit':1,'includeOutputs':False,'maxOutputCharsPerItem':1})
        turns=r.get('turns',[])
        if not turns:raise RuntimeError('latest_turn_unavailable')
        # Discard messages and command output immediately. Never save project content.
        t=turns[0]
        return {k:t.get(k) for k in ('id','status','error')}, r.get('thread',{}).get('status',{}).get('type')=='active'
    def send(self,tid):return self.call('send_message_to_thread',{'threadId':tid,'hostId':'local','prompt':PROMPT})
    def close(self):
        if self.p:
            try:self.p.terminate();self.p.wait(timeout=3)
            except (OSError,subprocess.TimeoutExpired):pass
        self.p=None

def alive(pid):
    # A terminated parent may retain handles; test its exit state and use pointer-sized handles.
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_bool, ctypes.c_ulong]
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
    kernel.GetExitCodeProcess.restype = ctypes.c_bool
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.OpenProcess(0x1000, False, pid)
    if not handle:return False
    try:
        code = ctypes.c_ulong()
        return bool(kernel.GetExitCodeProcess(handle, ctypes.byref(code)) and code.value == 259)
    finally:kernel.CloseHandle(handle)


@contextmanager
def monitor_lock(name=r'Local\CodexQuotaBattery.RecoveryMonitor'):
    """同一用户会话只允许一个监控读写恢复状态并发送消息；进程退出由系统释放锁。"""
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p]
    kernel.CreateMutexW.restype = ctypes.c_void_p
    kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
    kernel.WaitForSingleObject.restype = ctypes.c_ulong
    kernel.ReleaseMutex.argtypes = [ctypes.c_void_p]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.CreateMutexW(None, False, name)
    if not handle:raise ctypes.WinError(ctypes.get_last_error())
    result = kernel.WaitForSingleObject(handle, 0)
    acquired = result in (0, 0x80)
    try:
        if result not in (0, 0x80, 0x102):raise ctypes.WinError(ctypes.get_last_error())
        yield acquired
    finally:
        if acquired:kernel.ReleaseMutex(handle)
        kernel.CloseHandle(handle)


def run(parent, once=False):
    # 先取得跨进程所有权再加载状态；未取得的实例不写状态、不发送指令。
    with monitor_lock() as owned:
        if owned:_run_owned(parent, once)


def _run_owned(parent, once=False):
    DATA.mkdir(parents=True,exist_ok=True)
    config=load_runtime()
    engine=Engine(read_json(DATA/'recovery-state.json'))
    app=AppTools(config);reader=TurnReader()
    def save(status, **extra):
        atomic(DATA/'recovery-state.json',engine.state)
        atomic(DATA/'recovery-status.json',{'status':status,'updatedUtc':now(),'pid':os.getpid(),
            'watchedCount':len(engine.state['watched']),'waitingCount':len(engine.state['pending']),
            'resumedCount':sum(x.get('delivery')=='sent' for x in engine.state['dispatched'].values()),**extra})
    try:
        while alive(parent):
            settings=read_json(DATA/'recovery-settings.json',{})
            generation=settings.get('generation')
            if generation!=engine.state.get('settingsGeneration'):
                engine.state['watched'].clear();engine.state['pending'].clear()
                engine.state['settingsGeneration']=generation
            if settings.get('enabled',True) is False:
                engine.state['watched'].clear();engine.state['pending'].clear();save('paused')
                if once:break
                time.sleep(3);continue
            try:
                config=load_runtime()
                if any(config.get(k)!=app.config.get(k) for k in ('pipe','node','server','callerThreadId')):app.close()
                app.config=config
                if app.p is None or app.p.poll() is not None:app.open()
                threads=app.threads();stamp=time.time()
                # Remove archived/deleted/unavailable tasks rather than resurrecting them.
                for tid in list(engine.state['watched']):
                    if tid not in threads:
                        engine.state['watched'].pop(tid,None);engine.state['pending'].pop(tid,None)
                for tid, row in threads.items():
                    active=row.get('status')=='active'
                    if active:
                        v=reader.latest(tid)
                        turns=v.get('data',[])
                        engine.observe(tid,True,turns[0] if turns else None,stamp)
                    elif tid in engine.state['watched']:
                        turn,running=app.latest(tid)
                        engine.observe(tid,running,turn,stamp)
                        q=read_json(DATA/'status.json',{})
                        if engine.eligible(tid,turn,running,q,stamp):
                            # Recheck original desktop runtime and terminal turn at dispatch time.
                            check,running=app.latest(tid)
                            if not engine.eligible(tid,check,running,read_json(DATA/'status.json',{}),time.time()):continue
                            if read_json(DATA/'recovery-settings.json',{}).get('enabled',True) is False:continue
                            key=engine.claim(tid);save('dispatching')
                            try:
                                app.send(tid);engine.state['dispatched'][key]['delivery']='sent'
                            except Exception as exc:
                                engine.state['dispatched'][key]['delivery']='uncertain_no_retry'
                                engine.state['dispatched'][key]['reason']=str(exc)[:240]
                                # Do not bypass an app rejection or retry ambiguous delivery.
                                save('needs_attention');continue
                uncertain=any(x.get('delivery')=='uncertain_no_retry' for x in engine.state['dispatched'].values())
                save('needs_attention' if uncertain else 'monitoring')
            except Exception as exc:
                reader.close();app.close();save('connection_error',errorType=type(exc).__name__,reason=str(exc)[:240])
            if once:break
            for _ in range(10):
                if not alive(parent):return
                time.sleep(1)
    finally:app.close();reader.close()

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--parent',type=int,required=True);parser.add_argument('--once',action='store_true');a=parser.parse_args()
    publish_runtime({})
    run(a.parent,a.once)
