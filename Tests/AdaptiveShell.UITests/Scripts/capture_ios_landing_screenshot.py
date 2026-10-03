#!/usr/bin/env python3
"""Create one complete owned iOS18 landing PNG; preserve WDA comparison bytes.

The source selection is the actual iOS18 runtime, never an image-width guess.
Capture evidence is independent of the unchanged landing/UI acceptance gates.
"""
import datetime,hashlib,importlib.util,json,os,re,signal,subprocess,sys,time
from pathlib import Path

ROOT=Path(__file__).resolve().parents[3]
RESULTS=ROOT/'TestResults'
CAPTURES={40:'landing-theme-light',41:'landing-theme-dark',42:'landing-theme-light-restored',
          43:'landing-dark-music-child-clicked',44:'landing-dark-music-returned',
          45:'landing-dark-photos-child-clicked',46:'landing-dark-photos-returned'}
BUNDLE='com.companyname.exampleashellapp'
def require(value,message):
    if not value:raise ValueError(message)
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def now():return datetime.datetime.now(datetime.timezone.utc).isoformat()
def version(value):
    require(isinstance(value,str) and re.fullmatch(r'\d+(?:\.\d+){0,2}',value),'A real numeric runtime version is required')
    parts=tuple(map(int,value.split('.')));return parts+(0,)*(3-len(parts))
def load(path):return json.loads(path.read_text())
def png_reader():
    path=Path(__file__).with_name('verify_mac_button_colors.py')
    spec=importlib.util.spec_from_file_location('landing_native_original_png',path)
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module.PNG

def screen_contract(response,request,png=None):
    require(isinstance(response.get('sessionId'),str) and re.fullmatch(r'[0-9A-Fa-f-]{36}',response['sessionId']),'Actual WDA session identity is required')
    rows=response.get('value');require(isinstance(rows,list) and rows,'Actual native screenInfo is required')
    ids=[];mains=[]
    for row in rows:
        require(type(row.get('displayId')) is int and type(row.get('isMain')) is bool,'Native display identities are malformed')
        require(type(row.get('traits')) is int and type(row.get('scale')) in (int,float) and row['scale']>0,'Native screen scale/traits are malformed')
        ids.append(row['displayId'])
        if row['isMain']:mains.append(row)
    require(len(ids)==len(set(ids)) and len(mains)==1,'Exactly one unique native main display is required')
    main=mains[0];bounds=main['bounds'];size=request['window_size']
    require(bounds['x']==0 and bounds['y']==0 and bounds['width']>0 and bounds['height']>0,'Only a full original main display with zero origin is accepted')
    require(size['width']*main['scale']==bounds['width'] and size['height']*main['scale']==bounds['height'],'Actual Appium window points must exactly match independently reported native screen pixels')
    if png is not None:
        matches=[row for row in rows if row['bounds']['width']==png.width and row['bounds']['height']==png.height]
        require(len(matches)==1 and matches[0]['displayId']==main['displayId'],'The unmodified PNG must uniquely and exactly match the real main display')
    return main

def main(request_path):
    started=time.monotonic();deadline=started+30
    request_path=Path(request_path);require(not request_path.is_symlink(),'Request cannot be a symlink');request_path=request_path.resolve()
    require(request_path.parent==RESULTS.resolve(),'Request must belong to the checked-out TestResults')
    request=load(request_path);sequence=request.get('sequence');label=request.get('label')
    require(type(sequence) is int and CAPTURES.get(sequence)==label,'Only the seven original landing captures are accepted')
    final=RESULTS/f'landing-{sequence:02d}-screenshot-provider.json';require(not final.exists(),'Original provider proof must not be overwritten')
    evidence=RESULTS/'shots/ios-compact/provider-evidence';evidence.mkdir(parents=True,exist_ok=True)
    prefix=evidence/f'{sequence:02d}-{label}'
    proof={'schema':1,'status':'capture_error','provider':'simctl_internal','provider_switch':'actual_iOS18_landing_seven_captures',
           'ui_acceptance':False,'started_utc':now(),'request':request,'commands':[]}
    def remaining():
        seconds=deadline-time.monotonic();require(seconds>0,'Shared 30-second capture deadline expired');return min(10,seconds)
    def native(argv,label):
        budget=remaining();begin=time.monotonic();record={'argv':argv,'started_utc':now(),'allotted_timeout_seconds':budget};proof['commands'].append(record)
        stdout=Path(str(prefix)+'-'+label+'.stdout');stderr=Path(str(prefix)+'-'+label+'.stderr')
        with stdout.open('xb') as out,stderr.open('xb') as err:
            child=subprocess.Popen(argv,cwd=ROOT,stdout=out,stderr=err,start_new_session=True);record.update(pid=child.pid,pgid=os.getpgid(child.pid))
            try:child.wait(timeout=remaining());record['exit_code']=child.returncode
            except Exception as error:
                record['timed_out']=isinstance(error,subprocess.TimeoutExpired)
                if child.poll() is None and os.getpgid(child.pid)==child.pid:os.killpg(child.pid,signal.SIGKILL)
                try:child.wait(timeout=2)
                except subprocess.TimeoutExpired:pass
                raise
            finally:record.update(finished_utc=now(),elapsed_seconds=time.monotonic()-begin,
                stdout_file=str(stdout.relative_to(RESULTS)),stderr_file=str(stderr.relative_to(RESULTS)),stdout_sha256=digest(stdout),stderr_sha256=digest(stderr))
        require(child.returncode==0,'Native command failed: '+label);return stdout.read_bytes()
    def http(route,label):
        url='http://127.0.0.1:8100'+route
        raw=native(['curl','--fail','--silent','--show-error','--max-time',str(remaining()),url],label)
        require(len(raw)<=2*1024*1024,'Unexpectedly large native response');return json.loads(raw)
    try:
        scope={'GITHUB_ACTIONS':'true','RUNNER_ENVIRONMENT':'github-hosted','GITHUB_REPOSITORY':'EVNII/AdaptiveShell.maui','UITEST_PLATFORM':'ios','UITEST_FORM':'compact'}
        require(sys.platform=='darwin' and all(os.environ.get(k)==v for k,v in scope.items()) and os.environ.get('GITHUB_JOB') in ('uitest-ios','uitest-ios-18'),'Provider is restricted to the owned hosted iOS18 job')
        require(Path(os.environ['GITHUB_WORKSPACE']).resolve()==ROOT,'The exact checked-out workspace is required')
        for field,key in [('run_id','GITHUB_RUN_ID'),('run_attempt','GITHUB_RUN_ATTEMPT'),('head_sha','GITHUB_SHA'),('actual_udid','UITEST_DEVICE_UDID'),('workflow_ref','GITHUB_WORKFLOW_REF')]:
            require(request.get(field)==os.environ.get(key) and request.get(field),'Actual session/source identity mismatch: '+field)
        require(version(request['actual_platform_version'])[0]==18,'Only the actual iOS18 runtime uses this provider')
        require(re.fullmatch(r'[0-9A-Fa-f-]{36}',request['actual_udid']) and re.fullmatch(r'[0-9A-Fa-f-]{36}',request['appium_session_id']),'Malformed actual session identity')
        require(request['test_full_name']=='AdaptiveShell.UITests.LandingPageDarkModeTests.DarkMode_GroupLandingPage','Original landing test only')
        require(request['active_app'].get('bundleId')==BUNDLE,'The tested sample must be the real active native application')
        require(request['workflow_path'] in ('.github/workflows/uitest.yml','.github/workflows/release-uitest.yml') and request['workflow_ref'].split('@')[0]=='EVNII/AdaptiveShell.maui/'+request['workflow_path'],'Exact registered workflow required')
        require(digest(ROOT/request['workflow_path'])==request['workflow_sha256'],'Workflow bytes changed')
        assembly=Path(request['assembly_path']).resolve();require(assembly.is_relative_to(ROOT/'Tests/AdaptiveShell.UITests/bin') and digest(assembly)==request['assembly_sha256'],'Actual test assembly changed')
        primary=RESULTS/f'shots/ios-compact/{sequence:02d}-{label}.png';comparison=Path(str(prefix)+'-wda.png')
        require(request['primary_file']==str(primary.relative_to(RESULTS)) and request['comparison_file']==str(comparison.relative_to(RESULTS)),'Only the original uniquely named capture paths are allowed')
        require(not primary.exists() and not primary.is_symlink() and not comparison.is_symlink(),'Primary must be created exactly once; comparison must be an original file')
        require(digest(comparison)==request['comparison_sha256'],'Original WDA comparison changed')
        reader=png_reader();reader(comparison,decode_pixels=False)
        require(native(['git','rev-parse','HEAD'],'source-head').decode().strip()==request['head_sha'],'Actual checked-out source differs from the request')
        require(not native(['git','status','--porcelain','--untracked-files=no'],'source-clean').strip(),'Tracked source must remain immutable')
        status_before=http('/status','wda-status-before');require(version(status_before['value']['os']['version'])==version(request['actual_platform_version']),'Actual WDA runtime differs from Appium')
        screens_before=http('/wda/screens','wda-screens-before');main_screen=screen_contract(screens_before,request)
        wda_session=screens_before['sessionId'];proof['wda_session_id']=wda_session
        settings_before=http('/session/'+wda_session+'/appium/settings','wda-settings-before')
        require(settings_before.get('sessionId')==wda_session and type(settings_before['value'].get('screenshotQuality')) is int,'Actual WDA screenshot settings must be recorded')
        devices=json.loads(native(['xcrun','simctl','list','devices','available','-j'],'devices'))
        matches=[(runtime,d) for runtime,rows in devices.get('devices',{}).items() for d in rows if d.get('udid')==request['actual_udid']]
        require(len(matches)==1,'Actual session UDID must identify exactly one native device');runtime,device=matches[0]
        require(device.get('state')=='Booted' and device.get('isAvailable') is True and device.get('name')==request['actual_device_name'],'The exact native session device must be available and Booted')
        runtimes=json.loads(native(['xcrun','simctl','list','runtimes','-j'],'runtimes'));entries=[x for x in runtimes.get('runtimes',[]) if x.get('identifier')==runtime]
        require(len(entries)==1 and entries[0].get('isAvailable') is True and version(entries[0]['version'])==version(request['actual_platform_version']),'The actual native runtime must exactly match the session')
        proof.update(native_device=device,native_runtime=entries[0],native_screen=main_screen)
        usage=native(['xcrun','simctl','help','io'],'simctl-io-help').decode()+Path(str(prefix)+'-simctl-io-help.stderr').read_text()
        require('--display' in usage and 'internal' in usage and '--type' in usage,'Installed simctl must support exact internal PNG capture')
        native(['xcrun','simctl','io',request['actual_udid'],'screenshot','--type=png','--display=internal',str(primary)],'internal-screenshot')
        png=reader(primary,decode_pixels=False);screen_contract(screens_before,request,png)
        proof['native_original_PNG']={'file':request['primary_file'],'width':png.width,'height':png.height,'sha256':digest(primary),'size_bytes':primary.stat().st_size}
        screens_after=http('/wda/screens','wda-screens-after');require(screens_after==screens_before,'Actual native displays/session changed during capture')
        status_after=http('/status','wda-status-after');require(version(status_after['value']['os']['version'])==version(request['actual_platform_version']),'Actual native runtime changed')
        settings_after=http('/session/'+wda_session+'/appium/settings','wda-settings-after')
        require(settings_after==settings_before,'Original WDA settings/session changed; provider does not set quality')
        require(digest(comparison)==request['comparison_sha256'],'Original WDA comparison must remain byte-identical')
        remaining();proof.update(status='verified_capture',capture_script_sha256=digest(Path(__file__)),PNG_reader_sha256=digest(Path(__file__).with_name('verify_mac_button_colors.py')));code=0
    except Exception as error:proof['error']={'type':type(error).__name__,'message':str(error)};code=1
    finally:
        proof.update(finished_utc=now(),elapsed_seconds=time.monotonic()-started)
        with final.open('x') as f:json.dump(proof,f,ensure_ascii=False,indent=2);f.write('\n')
        print(json.dumps({'status':proof['status'],'provider':proof['provider'],'ui_acceptance':False,'proof':str(final)}))
    return code


def verify_saved_capture(root,sequence,label,identity,png):
    """Recheck immutable provider provenance during the existing strict raw gate."""
    paths=list(root.rglob(f'landing-{sequence:02d}-screenshot-provider.json'));require(len(paths)==1,'One native provider proof is required for each original capture')
    final=paths[0];results=final.parent;proof=load(final);request=proof['request']
    request_file=results/f'landing-{sequence:02d}-screenshot-provider-request.json'
    require(request_file.is_file() and not request_file.is_symlink() and load(request_file)==request,'The original C# session request is missing or differs')
    require(type(proof.get('elapsed_seconds')) in (int,float) and 0<=proof['elapsed_seconds']<30,'Original successful capture exceeded its shared clock')
    require(proof.get('schema')==1 and proof.get('status')=='verified_capture' and proof.get('provider')=='simctl_internal' and proof.get('ui_acceptance') is False,'Native capture proof is incomplete; it is not a UI pass')
    require(CAPTURES.get(sequence)==label and request.get('sequence')==sequence and request.get('label')==label,'Wrong original capture identity')
    for field in ('run_id','run_attempt','head_sha','actual_udid','appium_session_id'):
        require(request.get(field)==identity.get(field) and request.get(field),'Provider source/session identity changed: '+field)
    require(version(request['actual_platform_version'])==version(identity['actual_platform_version']) and version(request['actual_platform_version'])[0]==18,'Wrong actual iOS18 runtime')
    require(request['active_app'].get('bundleId')==BUNDLE and request['test_full_name']=='AdaptiveShell.UITests.LandingPageDarkModeTests.DarkMode_GroupLandingPage','Wrong native application or original test')
    require(proof.get('capture_script_sha256')==digest(Path(__file__)) and proof.get('PNG_reader_sha256')==digest(Path(__file__).with_name('verify_mac_button_colors.py')),'Actual provider/parser source hash differs')
    require(request['workflow_path'] in ('.github/workflows/uitest.yml','.github/workflows/release-uitest.yml') and digest(ROOT/request['workflow_path'])==request['workflow_sha256'],'Actual workflow source hash differs')
    def file(relative):
        q=Path(relative);require(not q.is_absolute() and '..' not in q.parts,'Unsafe original evidence path');path=results/q
        require(path.is_file() and not path.is_symlink() and path.resolve().is_relative_to(results.resolve()),'Missing or unsafe original provider evidence');return path
    primary=f'shots/ios-compact/{sequence:02d}-{label}.png';comparison=f'shots/ios-compact/provider-evidence/{sequence:02d}-{label}-wda.png'
    require(request['primary_file']==primary and request['comparison_file']==comparison,'Wrong original PNG paths')
    original=proof['native_original_PNG'];require(original['file']==primary and original['sha256']==digest(file(primary)) and original['size_bytes']==file(primary).stat().st_size and [original['width'],original['height']]==[png.width,png.height],'Original complete native PNG differs from its proof')
    require(digest(file(comparison))==request['comparison_sha256'],'Original WDA comparison bytes changed');png_reader()(file(comparison),decode_pixels=False)
    commands=proof.get('commands');require(isinstance(commands,list) and len(commands)==12,'All original bounded native/readback commands are required')
    expected_labels=('source-head','source-clean','wda-status-before','wda-screens-before','wda-settings-before','devices','runtimes','simctl-io-help','internal-screenshot','wda-screens-after','wda-status-after','wda-settings-after')
    require(len(commands)==len(expected_labels),'Unexpected capture command count')
    raw={}
    for command,label_suffix in zip(commands,expected_labels):
        require(command.get('exit_code')==0 and not command.get('timed_out') and type(command.get('pid')) is int and command['pid']==command.get('pgid') and 0<command.get('allotted_timeout_seconds',0)<=10,'Owned bounded original command failed')
        require(command['stdout_file']==f'shots/ios-compact/provider-evidence/{sequence:02d}-{label}-{label_suffix}.stdout' and command['stderr_file']==f'shots/ios-compact/provider-evidence/{sequence:02d}-{label}-{label_suffix}.stderr','Original command records are incomplete')
        require(digest(file(command['stdout_file']))==command.get('stdout_sha256') and digest(file(command['stderr_file']))==command.get('stderr_sha256'),'Original command stdout/stderr changed')
        raw[label_suffix]=file(command['stdout_file']).read_bytes()
    expected_native={0:['git','rev-parse','HEAD'],1:['git','status','--porcelain','--untracked-files=no'],5:['xcrun','simctl','list','devices','available','-j'],6:['xcrun','simctl','list','runtimes','-j'],7:['xcrun','simctl','help','io']}
    for index,argv in expected_native.items():require(commands[index]['argv']==argv,'Original bounded native command changed')
    expected_routes={2:'/status',3:'/wda/screens',4:'/session/'+proof['wda_session_id']+'/appium/settings',9:'/wda/screens',10:'/status',11:'/session/'+proof['wda_session_id']+'/appium/settings'}
    for index,route in expected_routes.items():
        argv=commands[index]['argv'];require(len(argv)==7 and argv[:5]==['curl','--fail','--silent','--show-error','--max-time'] and 0<float(argv[5])<=10 and argv[6]=='http://127.0.0.1:8100'+route,'Original bounded native GET command changed')
    usage=raw['simctl-io-help'].decode()+file(commands[7]['stderr_file']).read_text()
    require('--display' in usage and 'internal' in usage and '--type' in usage,'Original simctl capability readback is incomplete')
    require(raw['source-head'].decode().strip()==request['head_sha'] and not raw['source-clean'].strip(),'Actual source was not the immutable requested checkout')
    screenshot=commands[8]['argv'];require(screenshot[:7]==['xcrun','simctl','io',request['actual_udid'],'screenshot','--type=png','--display=internal'] and Path(screenshot[7]).name==Path(primary).name and len(screenshot)==8,'Original source must be exact owned simctl internal PNG')
    before=json.loads(raw['wda-screens-before']);after=json.loads(raw['wda-screens-after']);require(before==after and before['sessionId']==proof['wda_session_id'],'Original WDA displays/session changed');screen=screen_contract(before,request,png);require(screen==proof['native_screen'],'Recorded native display differs from original response')
    for key in ('wda-status-before','wda-status-after'):require(version(json.loads(raw[key])['value']['os']['version'])==version(request['actual_platform_version']),'Original WDA runtime does not match')
    settings=[json.loads(raw[key]) for key in ('wda-settings-before','wda-settings-after')];require(settings[0]==settings[1] and settings[0]['sessionId']==proof['wda_session_id'] and type(settings[0]['value'].get('screenshotQuality')) is int,'Original WDA settings changed')
    devices=json.loads(raw['devices']);found=[(runtime,d) for runtime,rows in devices['devices'].items() for d in rows if d.get('udid')==request['actual_udid']];require(len(found)==1 and found[0][1]==proof['native_device'] and found[0][1].get('state')=='Booted' and found[0][1].get('isAvailable') is True and found[0][1].get('name')==request['actual_device_name'],'Original exact session native device differs')
    runtimes=json.loads(raw['runtimes']);found_r=[x for x in runtimes['runtimes'] if x.get('identifier')==found[0][0]];require(len(found_r)==1 and found_r[0]==proof['native_runtime'] and found_r[0].get('isAvailable') is True and version(found_r[0]['version'])==version(request['actual_platform_version']),'Original native runtime differs')
    return {'provider':'simctl_internal','proof':str(final),'source_head':request['head_sha'],'actual_udid':request['actual_udid'],'appium_session_id':request['appium_session_id'],'wda_session_id':proof['wda_session_id'],'screen':screen,'primary_sha256':original['sha256'],'WDA_comparison_sha256':request['comparison_sha256'],'screenshot_quality_unchanged':settings[0]['value']['screenshotQuality']}

if __name__=='__main__':
    try:raise SystemExit(main(sys.argv[1]))
    except Exception as error:print(json.dumps({'status':'capture_error','ui_acceptance':False,'error':str(error)}));raise SystemExit(1)
