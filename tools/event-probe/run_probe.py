"""Isolated browser/Windows focus experiment. No browser API patching or event suppression."""
import argparse, ctypes, functools, html, http.server, json, os, platform, queue
import subprocess, sys, threading, time
from pathlib import Path
from playwright.sync_api import sync_playwright

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent

class StaticHandler(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
        if self.path.split('?')[0] not in ('/', '/index.html', '/style.css', '/probe.js'):
            self.send_error(404); return
        super().do_GET()
    def log_message(self, *args): pass

class Fixture:
    def __init__(self):
        dll = ROOT / 'tests/Superb.WindowsTests/bin/Release/net10.0-windows/Superb.WindowsTests.dll'
        self.process = subprocess.Popen(['dotnet', str(dll), '--focus-host'], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8')
        self.lines = queue.Queue(); self.seq = 0; self.history = []
        def read():
            for line in self.process.stdout: self.lines.put(line)
        threading.Thread(target=read, daemon=True).start()
        self.ready = self.receive()
        if not self.ready.get('ready'): raise RuntimeError('Windows fixture not ready')
    def receive(self):
        try: return json.loads(self.lines.get(timeout=15))
        except queue.Empty: raise RuntimeError('Windows fixture timed out') from None
    def command(self, command):
        self.seq += 1
        self.process.stdin.write(json.dumps({'id': self.seq, 'command': command})+'\n'); self.process.stdin.flush()
        response = self.receive()
        if response.get('id') != self.seq or 'error' in response: raise RuntimeError('Windows fixture command failed: '+command)
        self.history.append({'command':command,'state':response['state']})
        return response['state']
    def close(self):
        try: self.command('quit'); self.process.wait(timeout=5)
        except Exception: self.process.kill(); self.process.wait()

class NativeInput:
    def __init__(self):
        from ctypes import wintypes as w
        self.w = w; self.u = ctypes.WinDLL('user32', use_last_error=True)
        class Mouse(ctypes.Structure): _fields_ = [('dx',w.LONG),('dy',w.LONG),('data',w.DWORD),('flags',w.DWORD),('time',w.DWORD),('extra',ctypes.c_size_t)]
        class Key(ctypes.Structure): _fields_ = [('vk',w.WORD),('scan',w.WORD),('flags',w.DWORD),('time',w.DWORD),('extra',ctypes.c_size_t)]
        class Body(ctypes.Union): _fields_ = [('mouse',Mouse),('key',Key)]
        class Input(ctypes.Structure): _fields_ = [('type',w.DWORD),('body',Body)]
        self.Input = Input
        self.u.SendInput.argtypes = [w.UINT, ctypes.POINTER(Input), ctypes.c_int]; self.u.SendInput.restype = w.UINT
        self.u.GetForegroundWindow.restype = w.HWND
        self.u.SetForegroundWindow.argtypes = [w.HWND]
        self.u.GetWindowRect.argtypes = [w.HWND,ctypes.POINTER(w.RECT)]
        self.u.GetWindowTextW.argtypes = [w.HWND,w.LPWSTR,ctypes.c_int]
        self.u.IsWindowVisible.argtypes = [w.HWND]
        self.sent = 0
    def foreground(self): return int(self.u.GetForegroundWindow() or 0)
    def browser_handle(self):
        found = []
        cb = ctypes.WINFUNCTYPE(self.w.BOOL,self.w.HWND,self.w.LPARAM)
        def each(hwnd, _):
            title = ctypes.create_unicode_buffer(512); self.u.GetWindowTextW(hwnd,title,512)
            if 'Superb Event Probe' in title.value and self.u.IsWindowVisible(hwnd): found.append(int(hwnd))
            return True
        self.u.EnumWindows(cb(each),0)
        if len(found) != 1: raise RuntimeError('Could not identify exactly one test browser window')
        return found[0]
    def send(self, event):
        if self.u.SendInput(1,ctypes.byref(event),ctypes.sizeof(event)) != 1: raise RuntimeError('SendInput failed')
        self.sent += 1
    def key(self,vk,up=False):
        e=self.Input(); e.type=1; e.body.key.vk=vk; e.body.key.flags=2 if up else 0; self.send(e); time.sleep(.045)
    def chord(self, *keys):
        try:
            for k in keys: self.key(k)
        finally:
            for k in reversed(keys): self.key(k,True)
    def mouse(self, flags, data=0):
        e=self.Input(); e.type=0; e.body.mouse.flags=flags; e.body.mouse.data=data & 0xffffffff; self.send(e)
    def point(self,p):
        if p['x'] < 0 or p['y'] < 0: raise RuntimeError('Fixture click target is not visible')
        if not self.u.SetCursorPos(round(p['x']),round(p['y'])): raise RuntimeError('SetCursorPos failed')
        time.sleep(.1)
    def click(self,p): self.point(p); self.mouse(2); time.sleep(.07); self.mouse(4)
    def wheel(self,p): self.point(p); self.mouse(0x0800,-360)
    def focus_browser(self, hwnd):
        rect=self.w.RECT()
        if not self.u.GetWindowRect(hwnd,ctypes.byref(rect)): raise RuntimeError('Browser rectangle missing')
        self.u.SetForegroundWindow(hwnd)
        self.click({'x':rect.left+150,'y':rect.top+15})


def page_checks(page):
    """Recorder tests only; these do not establish native window behavior."""
    page.locator('#pad').click()
    page.evaluate("eventProbe.start('page-recorder-selftest')")
    page.keyboard.press('a')
    page.evaluate("document.querySelector('#pad').dispatchEvent(new KeyboardEvent('keydown',{code:'KeyZ',bubbles:true}))")
    page.evaluate("document.querySelector('#pad').dispatchEvent(new FocusEvent('blur',{bubbles:true}))")
    data=page.evaluate('eventProbe.stop()')['trials'][-1]
    assert any(e['code']=='KeyA' and e['isTrusted'] for e in data['events'])
    assert any(e['code']=='KeyZ' and not e['isTrusted'] for e in data['events'])
    assert data['counts']['windowBlur']==0, 'Element blur misclassified as window blur'
    before=len(data['events']); page.keyboard.press('b')
    assert len(page.evaluate('eventProbe.snapshot()')['trials'][-1]['events'])==before
    page.evaluate("eventProbe.start('<script>label</script>')"); page.evaluate('eventProbe.stop()')
    report=page.evaluate('eventProbe.report()')
    assert '<script>label</script>' not in report and '&lt;script&gt;' in report
    assert page.evaluate('localStorage.length') == 0
    return {'passed':True,'checks':['trusted and synthetic key differentiation','element blur is not window blur','stop excludes later input','export escapes labels','no localStorage writes']}

SCENARIOS = [
 ('idle','静置对照'), ('plain_key','普通按键 A（记录器正对照）'),
 ('activate_control','普通窗口激活（失焦正对照）'), ('tab_control','切换标签（隐藏正对照）'),
 ('show','程序显示阅读浮窗'), ('click_body','点击浮窗正文'), ('wheel','浮窗滚轮向下'),
 ('page_method','程序调用向下翻页'), ('hotkey_show','Ctrl+Alt+3 显示浮窗'),
 ('hotkey_hide','Ctrl+Alt+3 收起浮窗'), ('hotkey_down','Ctrl+Alt+8 向下翻页'),
 ('hotkey_up','Ctrl+Alt+9 向上翻页'), ('click_hide','点击浮窗收起按钮'),
 ('click_settings','点击浮窗设置按钮（激活对照窗口）')]

def native_trials(page, context, fixture, native, repeats, results):
    hwnd=native.browser_handle()
    for scenario,label in SCENARIOS:
        for repeat in range(1,repeats+1):
            fixture.command('closeControl'); fixture.command('show'); fixture.command('render')
            if scenario in ('idle','plain_key','activate_control','tab_control','show','hotkey_show'): fixture.command('hide')
            if scenario == 'hotkey_up': fixture.command('pageDown')
            page.bring_to_front(); native.focus_browser(hwnd); page.locator('#pad').click(); page.wait_for_timeout(350)
            initial=page.evaluate("({hasFocus:document.hasFocus(),visibility:document.visibilityState})")
            before=fixture.command('state'); sent=native.sent
            if not initial['hasFocus'] or initial['visibility']!='visible' or before['foreground']!=hwnd:
                raise RuntimeError('Desktop prerequisite failed: browser is not foreground/focused/visible')
            if not before['protection'] or before['registrationFailures']:
                raise RuntimeError('Desktop prerequisite failed: reader protection or hotkey registration')
            page.evaluate('(label)=>eventProbe.start(label)',f'{label} / {repeat}')
            extra=None
            if scenario=='plain_key': native.chord(0x41)
            elif scenario=='activate_control': fixture.command('control'); native.click({'x':100,'y':75})
            elif scenario=='tab_control': extra=context.new_page(); extra.goto('about:blank'); extra.bring_to_front()
            elif scenario=='show': fixture.command('show')
            elif scenario=='click_body': native.click(before['points']['body'])
            elif scenario=='wheel': native.wheel(before['points']['body'])
            elif scenario=='page_method': fixture.command('pageDown')
            elif scenario in ('hotkey_show','hotkey_hide'): native.chord(0x11,0x12,0x33)
            elif scenario=='hotkey_down': native.chord(0x11,0x12,0x38)
            elif scenario=='hotkey_up': native.chord(0x11,0x12,0x39)
            elif scenario=='click_hide': native.click(before['points']['hide'])
            elif scenario=='click_settings': native.click(before['points']['settings'])
            page.wait_for_timeout(650)
            after=fixture.command('state')
            run=page.evaluate('eventProbe.stop()')['trials'][-1]
            if extra: extra.close()
            new_actions=after['actions'][len(before['actions']):]
            confirmed = {
                'idle': True,
                'plain_key': any(e['code']=='KeyA' and e['type']=='keydown' and e['isTrusted'] for e in run['events']),
                'activate_control':after['foreground']!=hwnd and run['counts']['windowBlur']>0,
                'tab_control':run['counts']['hidden']>0,
                'show':after['visible'],
                'click_body':after['mouseDown']>before['mouseDown'],
                'wheel':after['wheel']>before['wheel'] and after['scrollOffset']>before['scrollOffset'],
                'page_method':after['scrollOffset']>before['scrollOffset'],
                'hotkey_show':'toggle' in new_actions and after['visible'],
                'hotkey_hide':'toggle' in new_actions and not after['visible'],
                'hotkey_down':'pageDown' in new_actions and after['scrollOffset']>before['scrollOffset'],
                'hotkey_up':'pageUp' in new_actions and after['scrollOffset']<before['scrollOffset'],
                'click_hide':'toggle' in new_actions and not after['visible'],
                'click_settings':'settings' in new_actions and after['controlVisible']
            }[scenario]
            result={'scenario':scenario,'label':label,'repeat':repeat,'actionConfirmed':confirmed,
                'nativeInputEvents':native.sent-sent,'before':before,'after':after,'trial':run}
            results.append(result)
            print(json.dumps({'scenario':scenario,'repeat':repeat,'confirmed':confirmed,'counts':run['counts']},ensure_ascii=False),flush=True)
    return results


def write_report(data, output):
    rows=[]
    for item in data.get('trials',[]):
        c=item['trial']['counts']; codes=sorted({e['code'] for e in item['trial']['events'] if e['type'] in ('keydown','keyup') and e['code']})
        rows.append('<tr>'+''.join(f'<td>{html.escape(str(x))}</td>' for x in [item['label'],item['repeat'],item['actionConfirmed'],c['windowBlur'],c['hidden'],f"{c['keydown']}/{c['keyup']}",', '.join(codes) or '—',item['after']['foreground']==item['before']['foreground']])+'</tr>')
    details=html.escape(json.dumps(data,ensure_ascii=False,indent=2))
    text=f'''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>Superb 浮窗事件实测报告</title><style>body{{font:15px/1.7 system-ui;max-width:1200px;margin:40px auto;padding:20px;color:#17251e}}h1{{font-size:30px}}table{{border-collapse:collapse;font-size:13px;width:100%}}td,th{{border:1px solid #ccd6cf;padding:9px;text-align:left}}th{{background:#eaf2ec}}pre{{white-space:pre-wrap;overflow-wrap:anywhere}}.note{{background:#eef5ef;padding:18px;border-left:4px solid #477f58}}</style>
<h1>Superb 浮窗事件实测报告</h1><p>生成时间：{html.escape(data['time'])} · 浏览器：{html.escape(data['browser'])} · 版本：{html.escape(data.get('browserVersion','unknown'))}</p>
<div class="note"><b>执行状态：{html.escape(data['status'])}</b><p>{html.escape(data.get('error',''))}</p><p>执行环境：{html.escape(data['platform'])}。本报告使用真实生产 ReaderWindow、Protection、HotkeyManager 的隔离测试宿主。鼠标和快捷键由 Windows SendInput 合成，非真人硬件输入；不加载用户配置、不调用模型。</p><p>“设置”按钮打开的是普通 WPF 对照窗口；未验证完整 Superb 主程序、用户本机、第三方网站、浏览器扩展或系统监控。窗口显示、滚轮和快捷键须同时有原生动作证据，否则不能用零事件宣称无影响。</p></div>
<h2>观察矩阵</h2><p>window.blur 与页面内部 focusout 分开。每轮采样前确认浏览器 HWND 处于前台、document.hasFocus() 为真且页面可见。isTrusted 不代表真人输入。最后一列只比较前后台 HWND，瞬时失焦由事件列记录。</p>
<table><tr><th>场景</th><th>轮次</th><th>动作确认</th><th>窗口失焦</th><th>隐藏</th><th>按下/松开</th><th>网页收到的 code</th><th>前台 HWND 保持</th></tr>{''.join(rows)}</table>
<h2>适用边界</h2><p>结果只说明这一环境下本地页面观察到什么。未观察到事件不等于不可检测；网页以外的观察机制不在本实验内。未屏蔽任何事件或修改焦点/可见性 API。原始记录与报告一并保存。</p><details><summary>环境、前台窗口、动作计数、滚动偏移及事件明细</summary><pre>{details}</pre></details></html>'''
    output.mkdir(parents=True,exist_ok=True)
    (output/'results.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    (output/'report.html').write_text(text,encoding='utf-8')


def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--browser',default='msedge',choices=['msedge','chrome','chromium'])
    parser.add_argument('--page-only',action='store_true'); parser.add_argument('--repeats',type=int,default=3)
    parser.add_argument('--output',type=Path,default=ROOT/'artifacts/event-probe'); args=parser.parse_args()
    if not 1<=args.repeats<=10: parser.error('repeats must be 1..10')
    data={'time':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),'platform':platform.platform(),
          'browser':args.browser,'mode':'recorder-only' if args.page_only else 'headed-native','status':'BLOCKED','trials':[]}
    data['commit']=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(StaticHandler,directory=str(HERE)))
    threading.Thread(target=server.serve_forever,daemon=True).start()
    fixture=None
    try:
        with sync_playwright() as pw:
            launch={'headless':args.page_only,'args':['--window-position=0,0','--window-size=1180,900'],
                'ignore_default_args':['--disable-backgrounding-occluded-windows','--disable-renderer-backgrounding','--disable-background-timer-throttling']}
            if args.browser!='chromium': launch['channel']=args.browser
            data['launchOptions']=launch
            browser=pw.chromium.launch(**launch); data['browserVersion']=browser.version
            context=browser.new_context(no_viewport=True); page=context.new_page()
            errors=[]; page.on('pageerror',lambda error:errors.append(str(error)))
            page.goto(f'http://127.0.0.1:{server.server_port}/'); page.wait_for_function('!!window.eventProbe')
            data['recorderChecks']=page_checks(page)
            if not args.page_only:
                if sys.platform!='win32': raise RuntimeError('Native WPF trials require Windows')
                fixture=Fixture(); data['fixture']=fixture.ready
                native_trials(page,context,fixture,NativeInput(),args.repeats,data['trials'])
                if not all(x['actionConfirmed'] for x in data['trials']): raise RuntimeError('One or more native actions were not confirmed; inspect per-trial evidence')
            if errors: raise RuntimeError('Recorder JavaScript errors')
            data['status']='RECORDER_ONLY_PASS' if args.page_only else 'MEASURED'
            page.bring_to_front(); args.output.mkdir(parents=True,exist_ok=True)
            page.screenshot(path=str(args.output/'test-page.png'),full_page=True)
            browser.close()
    except Exception as error:
        data['error']=type(error).__name__+': '+str(error)
    finally:
        if fixture:
            data['fixtureHistory']=fixture.history
            fixture.close()
        server.shutdown(); server.server_close(); write_report(data,args.output)
    print('REPORT_STATUS='+data['status'],flush=True)
    return 0 if data['status'] in ('MEASURED','RECORDER_ONLY_PASS') else 1

if __name__=='__main__': raise SystemExit(main())
