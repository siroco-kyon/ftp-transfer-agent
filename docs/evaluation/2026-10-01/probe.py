"""Run isolated black-box audit cases; never reads the user's appsettings."""
import hashlib
import json
import pathlib
import subprocess
import tempfile
import threading
import os
import shutil
from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler
from pyftpdlib.servers import FTPServer

REPO = pathlib.Path(__file__).resolve().parents[3]
OUT = REPO / ".audit-results/evaluation-rerun"
OUT.mkdir(parents=True, exist_ok=True)
DLL = REPO / 'FtpTransferAgent/bin/Release/net10.0/FtpTransferAgent.dll'
results = []
with tempfile.TemporaryDirectory(prefix='ftp-agent-audit-') as temp:
    root = pathlib.Path(temp)
    remote = root / 'server'
    remote.mkdir()
    (remote / 'report.txt').write_text('verified payload', encoding='utf-8')
    authorizer = DummyAuthorizer()
    authorizer.add_user('audit', 'audit-password', str(remote), perm='elradfmwMT')
    handler = type('AuditHandler', (FTPHandler,), {'authorizer': authorizer})
    server = FTPServer(('127.0.0.1', 0), handler)
    port = server.socket.getsockname()[1]
    thread = threading.Thread(target=server.serve_forever, kwargs={'timeout': 0.1}, daemon=True)
    thread.start()

    def run_case(name, trailing=False, state_failure=False, preserve=False):
        case = root / name
        watch = case / 'watch'
        watch.mkdir(parents=True)
        settings = {
            'Watch': {'Path': str(watch) + (os.sep if trailing else ''), 'AllowedExtensions': ['.txt'], 'IncludeSubfolders': preserve},
            'Transfer': {'Mode': 'ftp', 'Direction': 'get', 'Host': '127.0.0.1', 'Port': port,
                         'Username': 'audit', 'Password': 'audit-password', 'RemotePath': '/'},
            'Retry': {'MaxAttempts': 0, 'DelaySeconds': 1},
            'Hash': {'Enabled': True, 'Algorithm': 'SHA256'},
            'Cleanup': {'DeleteAfterVerify': False, 'DeleteRemoteAfterDownload': False},
            'Smtp': {'Enabled': False},
            'Logging': {'Level': 'Information', 'RollingFilePath': ''},
            'App': {'LockFilePath': str(case / 'agent.lock')},
        }
        settings['Transfer']['PreserveFolderStructure'] = preserve
        if state_failure:
            (watch / 'report.txt').write_text('source payload', encoding='utf-8')
            state = case / 'state'
            state.write_text('blocks directory creation', encoding='utf-8')
            settings['Transfer'] = {'Name': 'local', 'Mode': 'local', 'Direction': 'put',
                'RemotePath': str(case / 'dest'), 'PerDestinationDeliveryTracking': True,
                'StateDirectory': str(state), 'RetryDirectory': str(case / 'retry')}
        (case / 'appsettings.json').write_text(json.dumps(settings), encoding='utf-8')
        p = subprocess.run(['dotnet', str(DLL)], cwd=case, capture_output=True, text=True, timeout=30)
        output = p.stdout + p.stderr
        (OUT / (name + '.log')).write_text(output, encoding='utf-8')
        result = {'case': name, 'exit': p.returncode, 'download_exists': (watch / 'report.txt').exists(),
                  'destination_exists': (case / 'dest/report.txt').exists(),
                  'background_failure': 'BackgroundService failed' in output,
                  'validation_failure': 'validation failed' in output}
        results.append(result)
        print(json.dumps(result))

    try:
        run_case('download-normal')
        run_case('download-trailing-separator', trailing=True)
        run_case('download-normal-preserve', preserve=True)
        run_case('download-trailing-preserve', trailing=True, preserve=True)
        run_case('state-initialization-failure', state_failure=True)
        # A real partial fanout failure followed by a harmless Watch.Path spelling change.
        case = root / 'fanout-restart'
        watch = case / 'watch'
        watch.mkdir(parents=True)
        (watch / 'report.txt').write_text('must reach both destinations', encoding='utf-8')
        blocked = case / 'dest-b'
        blocked.write_text('not a directory', encoding='utf-8')
        settings = {
            'Watch': {'Path': str(watch), 'AllowedExtensions': ['.txt']},
            'Transfer': {'Name': 'a', 'Mode': 'local', 'RemotePath': str(case / 'dest-a'),
                'AdditionalDestinations': [{'Name': 'b', 'Mode': 'local', 'RemotePath': str(blocked)}]},
            'Retry': {'MaxAttempts': 0}, 'Hash': {'Enabled': True},
            'Cleanup': {'DeleteAfterVerify': False}, 'Smtp': {'Enabled': False},
            'Logging': {'RollingFilePath': ''}, 'App': {'LockFilePath': str(case / 'agent.lock')},
        }
        # Record only unique audit-owned default directories for cleanup.
        owned = []
        for suffix in ('', os.sep):
            key = hashlib.sha256((str(watch) + suffix).lower().encode()).hexdigest()[:16]
            for leaf in ('delivery-state', 'delivery-retry'):
                parent = pathlib.Path(os.environ['LOCALAPPDATA']) / 'FtpTransferAgent' / leaf
                path = parent / key
                assert not path.exists()
                owned.append((parent.resolve(), path.resolve()))
        try:
            for phase in ('failure', 'changed-spelling', 'original-spelling'):
                if phase == 'changed-spelling':
                    blocked.unlink()
                    blocked.mkdir()
                    settings['Watch']['Path'] = str(watch) + os.sep
                if phase == 'original-spelling':
                    settings['Watch']['Path'] = str(watch)
                (case / 'appsettings.json').write_text(json.dumps(settings), encoding='utf-8')
                p = subprocess.run(['dotnet', str(DLL)], cwd=case, capture_output=True, text=True, timeout=30)
                (OUT / ('fanout-' + phase + '.log')).write_text(p.stdout + p.stderr, encoding='utf-8')
                result = {'case': 'fanout-' + phase, 'exit': p.returncode,
                          'source_exists': (watch / 'report.txt').exists(),
                          'dest_a_exists': (case / 'dest-a/report.txt').exists(),
                          'dest_b_exists': (blocked / 'report.txt').exists()}
                results.append(result)
                print(json.dumps(result))
        finally:
            for parent, path in owned:
                assert path.parent == parent and len(path.name) == 16
                if path.exists():
                    shutil.rmtree(path)
    finally:
        server.close_all()
        thread.join(timeout=5)
(OUT / 'probe-results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')

for result in results:
    name = result['case']
    if name.startswith('download-'):
        assert result['exit'] == 0 and result['download_exists'], result
    elif name == 'state-initialization-failure':
        assert result['exit'] == 1 and not result['destination_exists'], result
    elif name == 'fanout-failure':
        assert result['exit'] == 1 and not result['dest_b_exists'], result
    else:
        assert result['exit'] == 0 and result['dest_b_exists'] and result['dest_a_exists'], result
print('All 8 path and startup regression cases passed.')
