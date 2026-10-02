"""Isolated real-process recovery tests; no production configuration is used."""
import json
import pathlib
import subprocess
import tempfile
import threading
from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler
from pyftpdlib.servers import FTPServer

REPO = pathlib.Path(__file__).resolve().parents[3]
OUT = REPO / ".audit-results/evaluation-rerun"
OUT.mkdir(parents=True, exist_ok=True)
DLL = REPO / 'FtpTransferAgent/bin/Release/net10.0/FtpTransferAgent.dll'
results = []

def base(case):
    watch = case / 'watch'
    watch.mkdir(parents=True)
    return {'Watch': {'Path': str(watch), 'AllowedExtensions': ['.txt']},
            'Transfer': {}, 'Retry': {'MaxAttempts': 1, 'DelaySeconds': 0},
            'Hash': {'Enabled': True, 'Algorithm': 'SHA256'},
            'Cleanup': {'DeleteAfterVerify': False}, 'Smtp': {'Enabled': False},
            'Logging': {'Level': 'Debug', 'RollingFilePath': ''},
            'App': {'LockFilePath': str(case / 'agent.lock')}}

def run(case, settings, label):
    (case / 'appsettings.json').write_text(json.dumps(settings), encoding='utf-8')
    p = subprocess.run(['dotnet', str(DLL)], cwd=case, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=30)
    (OUT / (label + '.log')).write_text(p.stdout + p.stderr, encoding='utf-8')
    return p.returncode

def record(**kw):
    label = kw['case']
    if 'fanout' in label:
        if label.endswith('outage'):
            assert kw['exit'] == 1 and kw['retry'] == 'old generation'
        elif label.endswith('recovery'):
            assert kw['exit'] == 0 and kw['dest_a'] == kw['dest_b'] == 'old generation'
            assert kw['retry'] is None and kw['source'].startswith('NEW generation')
        else:
            assert kw['exit'] == 0 and kw['dest_a'] == kw['dest_b'] == 'NEW generation that must eventually be delivered'
    else:
        assert kw['local_data'] == 'download payload'
        if label.endswith('delete-failure'):
            assert kw['exit'] == 1 and kw['remote_data'] == 'download payload'
        else:
            assert kw['exit'] == 0 and kw['remote_data'] is None and kw['remote_end'] is None
    kw['passed'] = True
    results.append(kw)
    print(json.dumps(kw), flush=True)

def read(path):
    return path.read_text(encoding='utf-8') if path.is_file() else None

with tempfile.TemporaryDirectory(prefix='ftp-agent-deep-audit-') as temp:
    root = pathlib.Path(temp)
    # Partially delivered old generation + new generation at the same source name.
    for delete_source in (False, True):
        case = root / ('fanout-delete-' + str(delete_source))
        settings = base(case)
        a, b, retry = case / 'a', case / 'b', case / 'retry'
        b.write_text('destination unavailable')
        settings['Transfer'] = {'Name': 'a', 'Mode': 'local', 'RemotePath': str(a),
            'StateDirectory': str(case / 'state'), 'RetryDirectory': str(retry),
            'AdditionalDestinations': [{'Name': 'b', 'Mode': 'local', 'RemotePath': str(b)}]}
        settings['Cleanup']['DeleteAfterVerify'] = delete_source
        source = case / 'watch/report.txt'
        source.write_text('old generation')
        for phase in ('outage', 'recovery', 'next-run', 'third-run'):
            if phase == 'recovery':
                b.unlink()
                b.mkdir()
                source.write_text('NEW generation that must eventually be delivered')
            label = f'deep-fanout-{delete_source}-{phase}'
            code = run(case, settings, label)
            record(case=label, exit=code, source=read(source), retry=read(retry / 'report.txt'),
                   dest_a=read(a / 'report.txt'), dest_b=read(b / 'report.txt'))

    # Refuse deletion of data while allowing END deletion; then restore permissions.
    remote = root / 'ftp'
    remote.mkdir()
    auth = DummyAuthorizer()
    auth.add_user('audit', 'audit-password', str(remote), perm='elradfmwMT')
    class Handler(FTPHandler):
        authorizer = auth
        reject_data_delete = True
        def ftp_DELE(self, path):
            if type(self).reject_data_delete and path.endswith('report.txt'):
                self.respond('450 injected temporary deletion failure')
                return
            return super().ftp_DELE(path)
    server = FTPServer(('127.0.0.1', 0), Handler)
    port = server.socket.getsockname()[1]
    thread = threading.Thread(target=server.serve_forever, kwargs={'timeout': 0.1}, daemon=True)
    thread.start()
    try:
        for transfer_end in (False, True):
            folder = remote / str(transfer_end)
            folder.mkdir()
            (folder / 'report.txt').write_text('download payload')
            (folder / 'report.txt.END').write_text('ready')
            case = root / ('download-end-' + str(transfer_end))
            settings = base(case)
            settings['Watch'].update(RequireEndFile=True, TransferEndFiles=transfer_end)
            settings['Transfer'] = {'Mode': 'ftp', 'Direction': 'get', 'Host': '127.0.0.1',
                'Port': port, 'Username': 'audit', 'Password': 'audit-password', 'RemotePath': '/' + str(transfer_end), 'StateDirectory': str(case / 'state')}
            settings['Cleanup'].update(DeleteRemoteAfterDownload=True, DeleteRemoteEndFiles=True)
            for phase in ('delete-failure', 'recovered', 'next-run'):
                Handler.reject_data_delete = phase == 'delete-failure'
                label = f'deep-end-{transfer_end}-{phase}'
                code = run(case, settings, label)
                record(case=label, exit=code, local_data=read(case / 'watch/report.txt'),
                       remote_data=read(folder / 'report.txt'), remote_end=read(folder / 'report.txt.END'))
    finally:
        server.close_all()
        thread.join(timeout=5)
(OUT / 'recovery-results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
