"""Measure SFTP authentication limits using the Windows product client and OpenSSH.

Owns only its uniquely named container and files under .audit-results/sftp-auth-rerun.
Server credentials are fictional, and the port is bound to loopback only.
"""
from pathlib import Path
import argparse
import hashlib
import json
import platform
import re
import socket
import statistics
import subprocess
import time
import uuid

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]


def run(*args, timeout=60):
    result = subprocess.run(args, check=True, capture_output=True, text=True, encoding='utf-8', timeout=timeout)
    return (result.stdout + result.stderr).strip()


def summarize(trials):
    rows = []
    for held, cap in sorted({(r['HeldUnauthenticated'], r['Limit']) for r in trials}):
        group = [r for r in trials if r['HeldUnauthenticated'] == held and r['Limit'] == cap]
        times = sorted(r['AllAuthenticatedMilliseconds'] for r in group if r['AllAuthenticatedMilliseconds'] is not None)
        failures = [c for r in group for c in r['Connections'] if not c['Success']]
        rows.append(dict(held_unauthenticated=held, limit=cap, trials=len(group),
                         connection_attempts=len(group)*16, failures=len(failures),
                         successful_trials=sum(all(c['Success'] for c in r['Connections']) for r in group),
                         successful_hash_checks=sum(c['HashMatches'] for r in group for c in r['Connections']),
                         all_authenticated_median_ms=statistics.median(times) if times else None,
                         all_authenticated_p95_ms=times[min(len(times)-1, int(len(times)*.95))] if times else None,
                         failure_stages=sorted({c['FailureStage'] for c in failures}),
                         error_types=sorted({c['ErrorType'] for c in failures})))
    return rows


def wait_ready(port):
    for attempt in range(60):
        try:
            with socket.create_connection(('127.0.0.1', port), timeout=1) as sock:
                if sock.recv(512).startswith(b'SSH-'):
                    return
        except OSError:
            pass
        time.sleep(.5)
    raise TimeoutError('OpenSSH did not become ready')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, default=REPO/'.audit-results/sftp-auth-rerun')
    args = parser.parse_args()
    output = args.output.resolve()
    assert output.is_relative_to((REPO/'.audit-results').resolve()), output
    output.mkdir(parents=True, exist_ok=False)
    container = 'ftp-transfer-agent-auth-' + uuid.uuid4().hex
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    created = False
    smoke_dir = output/'batch-input'
    try:
        run('dotnet', 'build', str(HERE/'Probe/Probe.csproj'), '-c', 'Release', '--nologo', timeout=120)
        run('docker', 'run', '-d', '--rm', '-p', f'127.0.0.1:{port}:22', '--name', container,
            'atmoz/sftp:alpine', 'testuser:testpass:1001:1001:upload', timeout=120)
        created = True
        wait_ready(port)
        (output/'sshd-effective-config.txt').write_text(run('docker', 'exec', container, '/usr/sbin/sshd', '-T'), encoding='utf-8')
        environment = dict(client_os=platform.platform(), server_image='atmoz/sftp:alpine',
                           image_id=run('docker', 'inspect', '--format', '{{.Image}}', container),
                           image_digest=run('docker', 'image', 'inspect', '--format', '{{json .RepoDigests}}', 'atmoz/sftp:alpine'),
                           docker_version=run('docker', 'version', '--format', '{{.Server.Version}}'),
                           sshd_version=run('docker', 'exec', container, '/usr/sbin/sshd', '-V'),
                           container=container, port=port, server_settings_changed=False,
                           measured_source_commit=run('git', '-C', str(REPO), 'rev-parse', 'HEAD'),
                           source_state='working tree with configurable limiter, see source-hashes.json')
        (output/'environment.json').write_text(json.dumps(environment, indent=2), encoding='utf-8')
        source_paths = ['FtpTransferAgent/Services/SftpConnectionLimiter.cs', 'FtpTransferAgent/Services/SftpClientWrapper.cs',
                        'FtpTransferAgent/Configuration/TransferOptions.cs', 'FtpTransferAgent/Program.cs',
                        'docs/evaluation/2026-10-02-sftp-auth/Probe/Program.cs', 'docs/evaluation/2026-10-02-sftp-auth/run_probe.py']
        manifest = []
        for path in source_paths:
            data = (REPO/path).read_bytes()
            snapshot = 'measured-sources/' + path
            saved = output/snapshot
            saved.parent.mkdir(parents=True, exist_ok=True)
            saved.write_bytes(data)
            manifest.append(dict(path=path, snapshot=snapshot, sha256_lf=hashlib.sha256(data.replace(b'\r\n', b'\n')).hexdigest()))
        (output/'source-hashes.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
        with (output/'probe-console.txt').open('w', encoding='utf-8') as log:
            subprocess.run(['dotnet', str(HERE/'Probe/bin/Release/net10.0/Probe.dll'), str(port), str(output)],
                           check=True, stdout=log, stderr=subprocess.STDOUT, timeout=900)
        trials = json.loads((output/'trials.json').read_text())
        (output/'summary.json').write_text(json.dumps(summarize(trials), indent=2), encoding='utf-8')
        # 認証前の保持接続を終了した後、所有する試験サーバーだけを再起動して制限状態をリセットする。
        run('docker', 'restart', container)
        wait_ready(port)
        run('docker', 'exec', container, 'sh', '-c',
            'ssh-keygen -t ed25519 -f /tmp/probe-key -N "" >/dev/null && '
            'mkdir -p /home/testuser/.ssh && cp /tmp/probe-key.pub /home/testuser/.ssh/authorized_keys && '
            'chown -R 1001:1001 /home/testuser/.ssh && chmod 700 /home/testuser/.ssh && '
            'chmod 600 /home/testuser/.ssh/authorized_keys')
        private_key = output/'probe-key'
        run('docker', 'cp', f'{container}:/tmp/probe-key', str(private_key))
        key_output = output/'key-auth'
        key_output.mkdir()
        with (key_output/'probe-console.txt').open('w', encoding='utf-8') as log:
            subprocess.run(['dotnet', str(HERE/'Probe/bin/Release/net10.0/Probe.dll'), str(port), str(key_output), str(private_key)],
                           check=True, stdout=log, stderr=subprocess.STDOUT, timeout=300)
        key_trials = json.loads((key_output/'trials.json').read_text())
        (key_output/'summary.json').write_text(json.dumps(summarize(key_trials), indent=2), encoding='utf-8')
        # 製品の起動・設定バインド・WorkerのDI経由でも4件/8件を使用できることを確認する。
        fingerprint = json.loads((output/'probe-environment.json').read_text())['fingerprint']
        smoke_dir.mkdir()
        smoke = []
        for cap, auth in ((4, 'password'), (8, 'password'), (8, 'key')):
            for index in range(32):
                (smoke_dir/f'{index}.bin').write_bytes(bytes([index])*1024)
            command = ['dotnet', str(REPO/'FtpTransferAgent/bin/Release/net10.0/FtpTransferAgent.dll'),
                       f'--Watch:Path={smoke_dir}', '--Watch:AllowedExtensions:0=.bin',
                       '--Watch:RequireEndFile=false', '--Transfer:Mode=sftp', '--Transfer:Direction=put',
                       '--Transfer:Host=127.0.0.1', f'--Transfer:Port={port}', '--Transfer:Username=testuser',
                       '--Transfer:Password=testpass', f'--Transfer:HostKeyFingerprint={fingerprint}',
                       '--Transfer:Concurrency=16', f'--Transfer:SftpMaxConcurrentHandshakes={cap}',
                       f'--Transfer:RemotePath=/upload/batch-{cap}-{auth}', '--Hash:Enabled=true',
                       '--Retry:MaxAttempts=1', '--Retry:DelaySeconds=0', '--Cleanup:DeleteAfterVerify=true',
                       '--Smtp:Enabled=false', '--Logging:RollingFilePath=']
            if auth == 'key':
                command.remove('--Transfer:Password=testpass')
                command += ['--Transfer:Password=', f'--Transfer:PrivateKeyPath={private_key}']
            label = f'batch-cap{cap}-{auth}'
            with (output/f'{label}.txt').open('w', encoding='utf-8') as log:
                batch = subprocess.run(command, cwd=REPO/'FtpTransferAgent', stdout=log, stderr=subprocess.STDOUT, timeout=90)
            remaining = len(list(smoke_dir.glob('*.bin')))
            text = (output/f'{label}.txt').read_text(encoding='utf-8')
            smoke.append(dict(limit=cap, authentication=auth, transfer_concurrency=16, files=32, exit_code=batch.returncode,
                              source_remaining=remaining, established_sessions=text.count('SFTP session established:'),
                              retry_log_count=len(re.findall(r'\bRetry \d+/\d+ for ', text))))
            assert batch.returncode == 0 and remaining == 0, smoke[-1]
        (output/'batch-smoke.json').write_text(json.dumps(smoke, indent=2), encoding='utf-8')
    finally:
        if created:
            (output/'server-log.txt').write_text(run('docker', 'logs', container), encoding='utf-8')
            # 所有する一意の名前のコンテナだけを終了し、その書込層を削除する。
            run('docker', 'rm', '-f', container)
        for path in [output/'payload.bin', output/'probe-key', output/'key-auth/payload.bin', *smoke_dir.glob('*')]:
            assert path.resolve().is_relative_to(output), path
            if path.is_file():
                path.unlink()
        if smoke_dir.exists():
            smoke_dir.rmdir()
        cleanup = dict(container=container, container_remaining=bool(run('docker', 'ps', '-a', '-q', '--filter', f'name=^{container}$')),
                       payload_remaining=(output/'payload.bin').exists(), private_key_remaining=(output/'probe-key').exists(),
                       key_payload_remaining=(output/'key-auth/payload.bin').exists(), source_directory_remaining=smoke_dir.exists())
        (output/'cleanup.json').write_text(json.dumps(cleanup, indent=2), encoding='utf-8')
    print(json.dumps(summarize(trials), indent=2))


if __name__ == '__main__':
    main()
