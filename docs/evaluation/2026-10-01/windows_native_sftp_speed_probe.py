"""Windows-only SFTP benchmark with a loopback Paramiko server and owned D-drive data.

Server API: https://docs.paramiko.org/en/stable/api/sftp.html
"""
import base64
import datetime
import errno
import hashlib
import json
import os
from pathlib import Path
import platform
import posixpath
import shutil
import socket
import statistics
import subprocess
import tempfile
import threading
import time
import paramiko

REPO=Path(__file__).resolve().parents[3]
ROOT=REPO/".audit-results/evaluation-rerun"
ROOT.mkdir(parents=True,exist_ok=True)
OUT=ROOT/'speed-native'
OUT.mkdir(exist_ok=True)
DLL=REPO/'FtpTransferAgent/bin/Release/net10.0/FtpTransferAgent.dll'

class Auth(paramiko.ServerInterface):
    def check_auth_password(self,username,password):
        return paramiko.AUTH_SUCCESSFUL if (username,password)==('audit','audit-password') else paramiko.AUTH_FAILED
    def get_allowed_auths(self,username):return 'password'
    def check_channel_request(self,kind,channel_id):
        return paramiko.OPEN_SUCCEEDED if kind=='session' else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

class Handle(paramiko.SFTPHandle):
    def stat(self):
        try:return paramiko.SFTPAttributes.from_stat(os.fstat(self.file.fileno()))
        except OSError as ex:return paramiko.SFTPServer.convert_errno(ex.errno)

class Files(paramiko.SFTPServerInterface):
    def __init__(self,server,*args,root,**kwargs):
        super().__init__(server,*args,**kwargs);self.root=Path(root).resolve()
    def physical(self,path):
        normalized=posixpath.normpath('/'+path.lstrip('/'))
        target=(self.root/normalized.lstrip('/')).resolve()
        if not target.is_relative_to(self.root):raise OSError(errno.EACCES,'Outside fixture')
        return target
    def canonicalize(self,path):return posixpath.normpath('/'+path.lstrip('/'))
    def stat(self,path):
        try:return paramiko.SFTPAttributes.from_stat(self.physical(path).stat())
        except OSError as ex:return paramiko.SFTPServer.convert_errno(ex.errno)
    lstat=stat
    def list_folder(self,path):
        try:
            result=[]
            for file in self.physical(path).iterdir():
                attr=paramiko.SFTPAttributes.from_stat(file.stat());attr.filename=file.name;result.append(attr)
            return result
        except OSError as ex:return paramiko.SFTPServer.convert_errno(ex.errno)
    def open(self,path,flags,attr):
        try:
            fd=os.open(self.physical(path),flags|os.O_BINARY,0o666)
            mode='r+b' if flags&os.O_RDWR else ('wb' if flags&os.O_WRONLY else 'rb')
            stream=os.fdopen(fd,mode,buffering=0)
            handle=Handle(flags);handle.file=stream
            if not flags&os.O_WRONLY:handle.readfile=stream
            if flags&(os.O_WRONLY|os.O_RDWR):handle.writefile=stream
            return handle
        except OSError as ex:return paramiko.SFTPServer.convert_errno(ex.errno)
    def remove(self,path):
        try:self.physical(path).unlink();return paramiko.SFTP_OK
        except OSError as ex:return paramiko.SFTPServer.convert_errno(ex.errno)
    def rename(self,old,new):
        try:os.rename(self.physical(old),self.physical(new));return paramiko.SFTP_OK
        except OSError as ex:return paramiko.SFTPServer.convert_errno(ex.errno)
    def posix_rename(self,old,new):
        try:os.replace(self.physical(old),self.physical(new));return paramiko.SFTP_OK
        except OSError as ex:return paramiko.SFTPServer.convert_errno(ex.errno)
    def mkdir(self,path,attr):
        try:self.physical(path).mkdir();return paramiko.SFTP_OK
        except OSError as ex:return paramiko.SFTPServer.convert_errno(ex.errno)
    def rmdir(self,path):
        try:self.physical(path).rmdir();return paramiko.SFTP_OK
        except OSError as ex:return paramiko.SFTPServer.convert_errno(ex.errno)
    def chattr(self,path,attr):
        try:paramiko.SFTPServer.set_file_attr(str(self.physical(path)),attr);return paramiko.SFTP_OK
        except OSError as ex:return paramiko.SFTPServer.convert_errno(ex.errno)

class Server:
    def __init__(self,root):
        self.root=root;self.key=paramiko.RSAKey.generate(2048);self.transports=[];self.threads=[];self.stopping=False
        self.socket=socket.socket();self.socket.bind(('127.0.0.1',0));self.socket.listen(32);self.socket.settimeout(.2)
        self.port=self.socket.getsockname()[1]
        self.thread=threading.Thread(target=self.accept,daemon=True);self.thread.start()
    @property
    def fingerprint(self):return 'SHA256:'+base64.b64encode(hashlib.sha256(self.key.asbytes()).digest()).decode().rstrip('=')
    def client(self,sock):
        transport=paramiko.Transport(sock);self.transports.append(transport)
        transport.add_server_key(self.key)
        transport.set_subsystem_handler('sftp',paramiko.SFTPServer,Files,root=self.root)
        try:
            transport.start_server(server=Auth())
            while not self.stopping and transport.is_active():time.sleep(.05)
        except Exception as ex:
            if not self.stopping:print(json.dumps({'server_connection_error':str(ex)}),flush=True)
        finally:transport.close()
    def accept(self):
        while not self.stopping:
            try:sock,_=self.socket.accept()
            except socket.timeout:continue
            except OSError:break
            sock.setsockopt(socket.IPPROTO_TCP,socket.TCP_NODELAY,1)
            thread=threading.Thread(target=self.client,args=(sock,),daemon=True);self.threads.append(thread);thread.start()
    def close(self):
        self.stopping=True;self.socket.close()
        for transport in self.transports:transport.close()
        self.thread.join(timeout=2)
        for thread in self.threads:thread.join(timeout=1)

results=json.loads((OUT/'windows-speed-results.json').read_text(encoding='utf-8')) if (OUT/'windows-speed-results.json').exists() else []
completed={row['case']:row for row in [json.loads(line) for line in (OUT/'run-progress.jsonl').read_text(encoding='utf-8').splitlines()]} if (OUT/'run-progress.jsonl').exists() else {}
previous_metadata=json.loads((OUT/'measurement-conditions.json').read_text(encoding='utf-8')) if (OUT/'measurement-conditions.json').exists() else {}
with tempfile.TemporaryDirectory(prefix='native-sftp-',dir=ROOT) as temporary:
    temp=Path(temporary);remote_root=temp/'server';remote_root.mkdir();(remote_root/'upload').mkdir()
    server=Server(remote_root)
    metadata={'client_os':platform.platform(),'server_os':platform.platform(),'server':'Paramiko '+paramiko.__version__+' on Windows; loopback only',
        'started_at':datetime.datetime.now().astimezone().isoformat(),'files_per_run':1000,'bytes_per_file':4*1024**2,
        'total_bytes_per_run':1000*4*1024**2,'concurrency':[1,4,8,16],'repeats':3,'product_sha256_enabled':True,
        'verify_uploaded_file_exists':True,'retry_count_limit':1,'delay_seconds':0,'client_and_server_storage':'D drive, unique temporary directories',
        'measurement':'process start to exit, includes product verification and input cleanup; independent destination verification excluded',
        'artificial_delay':False,'host_key_verification':True,'binary_sha256':hashlib.sha256(DLL.read_bytes()).hexdigest()}
    if previous_metadata:
        metadata['started_at']=previous_metadata['started_at']
        metadata['resumed_at']=datetime.datetime.now().astimezone().isoformat()
        metadata['resume_note']='Initial runner interrupted after completed 1/4 measurements. Remaining measurements retain the Windows software, volume, settings and binary.'
    (OUT/'measurement-conditions.json').write_text(json.dumps(metadata,ensure_ascii=False,indent=2),encoding='utf-8')
    try:
        # A small protocol smoke check before generating the official large workload.
        with paramiko.SSHClient() as ssh:
            ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
            ssh.connect('127.0.0.1',server.port,'audit','audit-password',look_for_keys=False,allow_agent=False)
            with ssh.open_sftp() as ftp:
                with ftp.open('/upload/smoke.bin','wb') as f:f.write(b'SFTP smoke')
                ftp.rename('/upload/smoke.bin','/upload/smoke-renamed.bin')
                with ftp.open('/upload/smoke-renamed.bin','rb') as f:assert f.read()==b'SFTP smoke'
                ftp.remove('/upload/smoke-renamed.bin')
        for concurrency in (c for c in (1,4,8,16) if c not in {r['concurrency'] for r in results}):
            times=[]
            for repeat in range(3):
                count,size=1000,4*1024**2
                label=f'bulk-c{concurrency}-hashTrue-existsTrue-r{repeat}'
                if label in completed:
                    assert (OUT/(label+'-sha256.txt')).exists()
                    times.append(completed[label]['seconds'])
                    continue
                case=temp/label;watch=case/'watch';watch.mkdir(parents=True);expected={}
                for index in range(count):
                    file=watch/f'file-{index:04}.bin';chunk=hashlib.sha256(str(index).encode()).digest()*32768
                    with file.open('wb') as f:
                        for _ in range(4):f.write(chunk)
                    with file.open('rb') as f:expected[file.name]=hashlib.file_digest(f,'sha256').hexdigest()
                (OUT/'expected-sha256.json').write_text(json.dumps(expected,indent=2),encoding='utf-8')
                config={'Watch':{'Path':str(watch),'AllowedExtensions':['.bin']},
                    'Transfer':{'Mode':'sftp','Direction':'put','Host':'127.0.0.1','Port':server.port,'Username':'audit','Password':'audit-password',
                    'RemotePath':'/upload/'+label,'Concurrency':concurrency,'HostKeyFingerprint':server.fingerprint,'VerifyUploadedFileExists':True},
                    'Hash':{'Enabled':True,'Algorithm':'SHA256'},'Retry':{'MaxAttempts':1,'DelaySeconds':0},
                    'Cleanup':{'DeleteAfterVerify':True},'Smtp':{'Enabled':False},'App':{'LockFilePath':str(case/'agent.lock')},
                    'Logging':{'RollingFilePath':'','Level':'Warning'}}
                (case/'appsettings.json').write_text(json.dumps(config),encoding='utf-8')
                print(json.dumps({'starting':label,'files':count,'total_gib':count*size/1024**3}),flush=True)
                start=time.perf_counter()
                process=subprocess.run(['dotnet',str(DLL)],cwd=case,capture_output=True,timeout=600)
                seconds=round(time.perf_counter()-start,3)
                (OUT/(label+'.log')).write_bytes(process.stdout+process.stderr)
                assert process.returncode==0,(label,process.returncode)
                assert not list(watch.iterdir()),label
                delivered=remote_root/'upload'/label
                assert {f.name for f in delivered.iterdir()}==set(expected),label
                lines=[]
                for file in delivered.iterdir():
                    with file.open('rb') as f:digest=hashlib.file_digest(f,'sha256').hexdigest()
                    assert digest==expected[file.name],(label,file.name)
                    lines.append(f'{digest}  /upload/{label}/{file.name}')
                (OUT/(label+'-sha256.txt')).write_text('\n'.join(sorted(lines))+'\n',encoding='utf-8')
                assert delivered.resolve().is_relative_to(temp.resolve())
                shutil.rmtree(delivered)
                row={'case':label,'seconds':seconds,'verified_files':count}
                with (OUT/'run-progress.jsonl').open('a',encoding='utf-8') as f:f.write(json.dumps(row)+'\n')
                print(json.dumps(row),flush=True);times.append(seconds)
            results.append({'workload':'bulk','files':count,'bytes_per_file':size,'total_bytes':count*size,'concurrency':concurrency,
                'hash_enabled':True,'verify_uploaded_file_exists':True,'seconds':times,'median_seconds':statistics.median(times),
                'all_file_hashes_match':True,'exit_codes':[0,0,0],'source_remaining_files':[0,0,0]})
            (OUT/'windows-speed-results.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
    finally:
        server.close()
        metadata['finished_at']=datetime.datetime.now().astimezone().isoformat()
        (OUT/'measurement-conditions.json').write_text(json.dumps(metadata,ensure_ascii=False,indent=2),encoding='utf-8')
(OUT/'cleanup.json').write_text(json.dumps({'temporary_directory':str(temp),'exists_after_cleanup':temp.exists(),'server_socket_closed':True,'transports_active':sum(t.is_active() for t in server.transports)},indent=2),encoding='utf-8')
assert not temp.exists()
