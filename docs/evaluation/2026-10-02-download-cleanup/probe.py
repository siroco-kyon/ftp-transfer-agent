"""Windows SFTP checks for log-only cleanup failures and remote read volume.

Uses a loopback Paramiko server, tiny owned files on D, and optional previous executable.
"""
from pathlib import Path
import argparse
import ast
import hashlib
import json
import logging
import os
import re
import subprocess
import threading

REPO = Path(__file__).resolve().parents[3]
SOURCE = REPO/'docs/evaluation/2026-10-01/windows_native_sftp_speed_probe.py'
tree = ast.parse(SOURCE.read_text(encoding='utf-8'))
definitions = [node for node in tree.body if isinstance(node, (ast.Import, ast.ImportFrom, ast.ClassDef))]
ns = {}
exec(compile(ast.Module(body=definitions, type_ignores=[]), str(SOURCE), 'exec'), ns)
BaseFiles, BaseHandle, Server = ns['Files'], ns['Handle'], ns['Server']
counter_lock = threading.Lock()
stats = dict(read_bytes={}, reads={}, deletes={}, reject=None)


class CountingHandle(BaseHandle):
    def read(self, offset, length):
        result = super().read(offset, length)
        if isinstance(result, bytes):
            with counter_lock:
                stats['read_bytes'][self.remote_path] = stats['read_bytes'].get(self.remote_path, 0) + len(result)
                stats['reads'][self.remote_path] = stats['reads'].get(self.remote_path, 0) + 1
        return result


class CountingFiles(BaseFiles):
    def open(self, path, flags, attr):
        result = super().open(path, flags, attr)
        if isinstance(result, BaseHandle):
            result.__class__ = CountingHandle
            result.remote_path = path
        return result

    def remove(self, path):
        with counter_lock:
            stats['deletes'][path] = stats['deletes'].get(path, 0) + 1
            reject = stats['reject']
        if path == reject:
            return ns['paramiko'].SFTP_PERMISSION_DENIED
        return super().remove(path)


ns['Files'] = CountingFiles
logging.getLogger('paramiko').setLevel(logging.CRITICAL)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, default=REPO/'.audit-results/download-cleanup-rerun')
    parser.add_argument('--baseline-executable', type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    assert output.is_relative_to((REPO/'.audit-results').resolve()), output
    output.mkdir(parents=True, exist_ok=False)
    data_root = output/'data'
    remote = data_root/'server'
    remote.mkdir(parents=True)
    server = Server(remote)
    payload = bytes(range(256))*128
    current = ['dotnet', str(REPO/'FtpTransferAgent/bin/Release/net10.0/FtpTransferAgent.dll')]
    current_hash = hashlib.sha256(Path(current[1]).read_bytes()).hexdigest()
    results = []

    def launch(command, label, config):
        case = data_root/label
        case.mkdir(parents=True, exist_ok=True)
        watch = case/'watch'
        watch.mkdir(exist_ok=True)
        config['Watch']['Path'] = str(watch)
        config['Transfer']['StateDirectory'] = str(case/'state')
        config['App'] = {'LockFilePath': str(case/'batch.lock')}
        (case/'appsettings.json').write_text(json.dumps(config), encoding='utf-8')
        with counter_lock:
            stats['read_bytes'] = {}; stats['reads'] = {}; stats['deletes'] = {}
        process = subprocess.run(command, cwd=case, capture_output=True, text=True, encoding='utf-8', timeout=30)
        log = process.stdout+process.stderr
        (output/(label+'.txt')).write_text(log, encoding='utf-8')
        with counter_lock:
            measured = dict(read_bytes=dict(stats['read_bytes']), reads=dict(stats['reads']), deletes=dict(stats['deletes']))
        record = dict(case=label, exit_code=process.returncode, **measured,
                      local_matches=(watch/'payload.bin').exists() and (watch/'payload.bin').read_bytes() == payload,
                      remote_data_exists=(remote/'payload.bin').exists(), remote_end_exists=(remote/'payload.bin.END').exists(),
                      state_json_count=len(list((case/'state').rglob('*.json'))),
                      retry_logs=len(re.findall(r'\bRetry \d+/\d+ for ', log)),
                      cleanup_warning='Could not delete remote file' in log)
        results.append(record)
        return record

    try:
        for verify in (False, True):
            for transfer_end in (False, True):
                for failure in ('none', 'data', 'end'):
                    (remote/'payload.bin').write_bytes(payload)
                    (remote/'payload.bin.END').write_bytes(b'ready')
                    stats['reject'] = '/payload.bin' if failure == 'data' else '/payload.bin.END' if failure == 'end' else None
                    label = f'new-hash{verify}-end{transfer_end}-fail{failure}'
                    config = {'Watch': {'AllowedExtensions': ['.bin'], 'RequireEndFile': True, 'TransferEndFiles': transfer_end},
                              'Transfer': {'Mode': 'sftp', 'Direction': 'get', 'Host': '127.0.0.1', 'Port': server.port,
                                           'Username': 'audit', 'Password': 'audit-password', 'HostKeyFingerprint': server.fingerprint,
                                           'RemotePath': '/', 'Concurrency': 1},
                              'Hash': {'Enabled': verify, 'Algorithm': 'SHA256'},
                              'Cleanup': {'DeleteRemoteAfterDownload': True, 'DeleteRemoteEndFiles': True},
                              'Retry': {'MaxAttempts': 3, 'DelaySeconds': 0}, 'Smtp': {'Enabled': False},
                              'Logging': {'Level': 'Information', 'RollingFilePath': ''}}
                    record = launch(current, label, config)
                    expected_reads = len(payload)*(2 if verify else 1)
                    assert record['exit_code'] == 0 and record['local_matches'] and record['state_json_count'] == 0, record
                    assert record['read_bytes']['/payload.bin'] == expected_reads and record['retry_logs'] == 0, record
                    assert record['remote_data_exists'] == (failure != 'none'), record
                    assert record['remote_end_exists'] == (failure == 'end'), record
                    assert record['cleanup_warning'] == (failure != 'none'), record
                    assert record['deletes'].get(stats['reject'], 0) == (1 if failure != 'none' else 0), record
                    stats['reject'] = None
                    if failure == 'data':
                        again = launch(current, label+'-next', config)
                        assert again['exit_code'] == 0 and again['read_bytes'] == {} and again['deletes'] == {}, again
                        assert again['remote_data_exists'] and not again['remote_end_exists'], again
                    if args.baseline_executable and failure == 'none' and not transfer_end:
                        (remote/'payload.bin').write_bytes(payload)
                        (remote/'payload.bin.END').write_bytes(b'ready')
                        baseline = launch([str(args.baseline_executable.resolve())], f'previous-hash{verify}', config)
                        assert baseline['exit_code'] == 0 and baseline['local_matches'], baseline
                        assert baseline['read_bytes']['/payload.bin'] == len(payload)*(4 if verify else 3), baseline
        (output/'results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
        metadata = dict(payload_bytes=len(payload), server='Paramiko '+ns['paramiko'].__version__+' on Windows',
                        host_key=server.fingerprint, current_dll_sha256=current_hash,
                        baseline_exe_sha256=hashlib.sha256(args.baseline_executable.read_bytes()).hexdigest() if args.baseline_executable else None,
                        baseline='2026-10-01 published executable, automatic deletion recovery enabled',
                        current_source_sha256_lf=hashlib.sha256((REPO/'FtpTransferAgent/Worker.cs').read_bytes().replace(b'\r\n', b'\n')).hexdigest())
        (output/'conditions.json').write_text(json.dumps(metadata, indent=2), encoding='utf-8')
    finally:
        server.close()
        # 対象が試験専用の絶対パス配下であることを全件確認してから個別に削除する。
        paths = list(data_root.rglob('*'))
        assert all(path.resolve().is_relative_to(data_root.resolve()) and not path.is_symlink() for path in paths)
        for path in paths:
            if path.is_file(): path.unlink()
        for path in sorted((p for p in paths if p.is_dir()), key=lambda p: len(p.parts), reverse=True): path.rmdir()
        data_root.rmdir()
        cleanup = dict(data_directory_remaining=data_root.exists(), server_socket_closed=server.socket.fileno() == -1,
                       active_transports=sum(t.is_active() for t in server.transports))
        (output/'cleanup.json').write_text(json.dumps(cleanup, indent=2), encoding='utf-8')
    print(json.dumps(dict(checks=len(results), all_passed=True, data_removed=True)))


if __name__ == '__main__':
    main()
