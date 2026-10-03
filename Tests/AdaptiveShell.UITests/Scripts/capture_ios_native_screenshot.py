#!/usr/bin/env python3
"""Independent diagnostic evidence; never substitutes native pixels for WDA PNG."""
import datetime,hashlib,importlib.util,json,os,re,signal,struct,subprocess,sys,time
from pathlib import Path

ROOT=Path(__file__).resolve().parents[3]
RESULTS=ROOT/'TestResults'
PHASES={'light':40,'dark':41,'light-restored':42}
def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def now():return datetime.datetime.now(datetime.timezone.utc).isoformat()
def require(value,message):
    if not value:raise ValueError(message)

def main(request_path):
    started=time.monotonic();deadline=started+30
    request_path=Path(request_path)
    require(not request_path.is_symlink(),'Request must not be a symlink')
    request_path=request_path.resolve()
    require(request_path.parent==RESULTS.resolve(),'Request must be in owned TestResults')
    request=json.loads(request_path.read_text());stage=request.get('stage')
    require(stage in PHASES and request.get('sequence')==PHASES[stage],'Unexpected original theme phase')
    prefix=RESULTS/f'landing-{stage}-native'
    final=Path(str(prefix)+'-screenshot-proof.json')
    require(not final.exists(),'Original native phase proof must not be overwritten')
    proof={'schema':1,'status':'capture_error','ui_acceptance':False,'started_utc':now(),'request':request,'commands':[],'http':[]}
    def remaining():
        seconds=deadline-time.monotonic();require(seconds>0,'Shared 30-second native capture deadline expired');return min(10,seconds)
    def native(argv,label):
        budget=remaining()
        begin=time.monotonic();record={'argv':argv,'started_utc':now(),'allotted_timeout_seconds':budget};proof['commands'].append(record)
        stdout=Path(str(prefix)+'-'+label+'.stdout');stderr=Path(str(prefix)+'-'+label+'.stderr')
        with stdout.open('xb') as out,stderr.open('xb') as err:
            child=subprocess.Popen(argv,cwd=ROOT,stdout=out,stderr=err,start_new_session=True)
            record['pid']=child.pid;record['pgid']=os.getpgid(child.pid)
            try:
                child.wait(timeout=remaining());record['exit_code']=child.returncode
            except Exception as error:
                record['timed_out']=isinstance(error,subprocess.TimeoutExpired)
                if child.poll() is None and os.getpgid(child.pid)==child.pid:os.killpg(child.pid,signal.SIGKILL)
                try:child.wait(timeout=2)
                except subprocess.TimeoutExpired:pass
                raise
            finally:
                record.update({'finished_utc':now(),'elapsed_seconds':time.monotonic()-begin,'stdout_file':stdout.name,'stderr_file':stderr.name})
        require(child.returncode==0,'Native command failed: '+label)
        return stdout.read_bytes()
    def http(path,label,wda_session):
        url='http://127.0.0.1:8100/session/'+wda_session+path
        begin=time.monotonic();record={'url':url,'method':'GET','started_utc':now()};proof['http'].append(record)
        raw=native(['curl','--fail','--silent','--show-error','--max-time',str(remaining()),url],label+'-http')
        output=Path(str(prefix)+'-'+label+'.json');output.write_bytes(raw)
        record.update({'file':output.name,'sha256':digest(output),'finished_utc':now(),'elapsed_seconds':time.monotonic()-begin})
        require(len(raw)<=2*1024*1024,'Unexpectedly large native response')
        data=json.loads(raw);require(data.get('sessionId')==wda_session,'Native response session must match original WDA screens')
        return data['value']
    try:
        scope={'GITHUB_ACTIONS':'true','RUNNER_ENVIRONMENT':'github-hosted','GITHUB_REPOSITORY':'EVNII/AdaptiveShell.maui','GITHUB_JOB':'uitest-ios','GITHUB_WORKFLOW':'iOS 18 Screenshot Provider Diagnostic','GITHUB_REF':'refs/heads/codex/ios18-screenshot-provider-diagnostic','UITEST_PLATFORM':'ios','UITEST_FORM':'compact','UITEST_DEVICE_NAME':'iPhone 16','IOS_SCREENSHOT_PROVIDER_DIAGNOSTIC':'true'}
        require(sys.platform=='darwin' and all(os.environ.get(k)==v for k,v in scope.items()),'Diagnostic is restricted to the exact hosted macOS iOS job')
        require(Path(os.environ['GITHUB_WORKSPACE']).resolve()==ROOT,'Exact checked-out workspace required')
        for field,key in [('run_id','GITHUB_RUN_ID'),('run_attempt','GITHUB_RUN_ATTEMPT'),('head_sha','GITHUB_SHA'),('actual_udid','UITEST_DEVICE_UDID')]:require(request.get(field)==os.environ.get(key) and request.get(field),'Actual session/run identity mismatch: '+field)
        require(request.get('actual_platform_version')=='18.5' and request.get('actual_device_name')=='iPhone 16','Exact original native session required')
        require(re.fullmatch(r'[0-9A-Fa-f-]{36}',request['actual_udid']) and re.fullmatch(r'[0-9A-Fa-f-]{36}',request['appium_session_id']),'Malformed actual native identity')
        require(request.get('test_full_name')=='AdaptiveShell.UITests.LandingPageDarkModeTests.DarkMode_GroupLandingPage','Original landing test only')
        require(digest(ROOT/'.github/workflows/release-uitest.yml')==request.get('workflow_sha256'),'Workflow identity changed')
        assembly=Path(request['assembly_path']).resolve();require(assembly.is_relative_to(ROOT/'Tests/AdaptiveShell.UITests/bin') and digest(assembly)==request.get('assembly_sha256'),'Actual compiled assembly identity mismatch')
        require(native(['git','rev-parse','HEAD'],'source-head').decode().strip()==request['head_sha'],'Actual checkout must equal run source')
        require(not native(['git','status','--porcelain','--untracked-files=no'],'source-clean').strip(),'Tracked source must remain immutable')
        screens=json.loads((RESULTS/f'landing-{stage}-screens.json').read_text());wda_session=screens.get('sessionId');require(isinstance(wda_session,str) and re.fullmatch(r'[0-9A-Fa-f-]{36}',wda_session),'Original native WDA session ID required')
        proof['original_screens_sha256']=digest(RESULTS/f'landing-{stage}-screens.json')
        wda_png=RESULTS/'shots/ios-compact'/f'{PHASES[stage]:02d}-landing-theme-{stage}.png';proof['original_WDA_PNG_sha256']=digest(wda_png)
        before=http('/appium/settings','wda-settings-before',wda_session);require(type(before.get('screenshotQuality')) is int and before['screenshotQuality']==0,'Native WDA must read back explicit lossless PNG quality0')
        devices=json.loads(native(['xcrun','simctl','list','devices','available','-j'],'devices'))
        matches=[(runtime,d) for runtime,rows in devices.get('devices',{}).items() for d in rows if d.get('udid')==request['actual_udid']]
        require(len(matches)==1,'Actual UDID must identify one native device')
        runtime,device=matches[0];require(runtime=='com.apple.CoreSimulator.SimRuntime.iOS-18-5' and device.get('name')=='iPhone 16' and device.get('state')=='Booted' and device.get('isAvailable') is True and device.get('deviceTypeIdentifier')=='com.apple.CoreSimulator.SimDeviceType.iPhone-16','Original native Booted iPhone16/iOS18.5 required')
        runtimes=json.loads(native(['xcrun','simctl','list','runtimes','-j'],'runtimes'))
        entries=[x for x in runtimes.get('runtimes',[]) if x.get('identifier')==runtime];require(len(entries)==1 and entries[0].get('version')=='18.5' and entries[0].get('isAvailable') is True,'Exact native runtime readback required')
        proof['native_device']=device;proof['native_runtime']=entries[0]
        help_text=native(['xcrun','simctl','help','io'],'simctl-io-help').decode()+Path(str(prefix)+'-simctl-io-help.stderr').read_text();require('--display' in help_text and 'internal' in help_text and '--type' in help_text,'Installed simctl must support exact internal lossless PNG command')
        png=Path(str(prefix)+'-internal.png');require(not png.exists() and not png.is_symlink(),'Native original PNG already exists')
        native(['xcrun','simctl','io',request['actual_udid'],'screenshot','--type=png','--display=internal',str(png)],'internal-screenshot')
        raw=png.read_bytes();require(len(raw)>24 and raw[:8]==b'\x89PNG\r\n\x1a\n' and raw[12:16]==b'IHDR','Native provider must produce an original readable PNG')
        width,height=struct.unpack('>II',raw[16:24]);require(width>0 and height>0,'Native PNG must report real nonzero dimensions')
        png_helper=ROOT/'Tests/AdaptiveShell.UITests/Scripts/verify_mac_button_colors.py'
        spec=importlib.util.spec_from_file_location('native_original_png_reader',png_helper)
        reader=importlib.util.module_from_spec(spec);spec.loader.exec_module(reader)
        decoded=reader.PNG(png,decode_pixels=False)
        require((decoded.width,decoded.height)==(width,height),'Original PNG must pass unchanged CRC/IHDR/compressed-row validation')
        proof['unchanged_PNG_reader_sha256']=digest(png_helper)
        proof['native_original_PNG']={'file':png.name,'width':width,'height':height,'sha256':digest(png),'size_bytes':len(raw)}
        after=http('/appium/settings','wda-settings-after',wda_session);require(type(after.get('screenshotQuality')) is int and after['screenshotQuality']==0,'Native screenshot settings changed during capture')
        require(digest(wda_png)==proof['original_WDA_PNG_sha256'],'Original primary WDA PNG must remain byte-identical')
        proof['status']='captured';return_code=0
    except Exception as error:
        proof['error']={'type':type(error).__name__,'message':str(error)};return_code=1
    finally:
        proof['finished_utc']=now();proof['elapsed_seconds']=time.monotonic()-started
        with final.open('x') as output:json.dump(proof,output,ensure_ascii=False,indent=2);output.write('\n')
        print(json.dumps({'status':proof['status'],'ui_acceptance':False,'proof':str(final)}))
    return return_code

if __name__=='__main__':
    try:raise SystemExit(main(sys.argv[1]))
    except Exception as error:print(json.dumps({'status':'capture_error','ui_acceptance':False,'error':str(error)}));raise SystemExit(1)
