"""Windows client timing against an isolated SFTP server; verify every delivered file."""
import hashlib
import json
import pathlib
import socket
import statistics
import subprocess
import tempfile
import time
import uuid
import paramiko

REPO = pathlib.Path(__file__).resolve().parents[3]
ROOT = REPO / ".audit-results/evaluation-rerun"
ROOT.mkdir(parents=True, exist_ok=True)
OUT = ROOT / 'speed-final'
OUT.mkdir(exist_ok=True)
DLL = REPO / 'FtpTransferAgent/bin/Release/net10.0/FtpTransferAgent.dll'
name = 'ftp-agent-speed-' + uuid.uuid4().hex
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    port = sock.getsockname()[1]
results = []
try:
    subprocess.run(['docker', 'run', '-d', '--rm', '-p', f'127.0.0.1:{port}:22', '--name', name,
                    'atmoz/sftp:alpine', 'audit:audit-password:1001:1001:upload'], check=True, capture_output=True, timeout=60)
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    deadline = time.monotonic() + 30
    while True:
        try:
            ssh.connect('127.0.0.1', port, 'audit', 'audit-password', look_for_keys=False, allow_agent=False, timeout=3)
            break
        except Exception:
            if time.monotonic() > deadline:
                raise
            time.sleep(.3)
    (OUT / 'sshd-effective-config.log').write_bytes(subprocess.run(['docker', 'exec', name, '/usr/sbin/sshd', '-T'], check=True, capture_output=True).stdout)
    with tempfile.TemporaryDirectory(prefix='agent-speed-') as temp:
        root = pathlib.Path(temp)
        workloads = [("small", 400, 8192, c, False, verify) for c in (1, 4, 16) for verify in (True, False)]
        workloads += [("medium", 200, 512 * 1024, 4, False, True), ("large", 1, 256 * 1024 * 1024, 1, True, True)]
        for workload, count, size, concurrency, hash_enabled, verify_exists in workloads:
            times = []
            for repeat in range(3):
                label = f'{workload}-c{concurrency}-hash{hash_enabled}-exists{verify_exists}-r{repeat}'
                case = root / label
                watch = case / 'watch'
                watch.mkdir(parents=True)
                expected = {}
                for index in range(count):
                    file = watch / f'file-{index:04}.bin'
                    seed = hashlib.sha256(str(index).encode()).digest()
                    with file.open('wb') as stream:
                        remaining = size
                        chunk = seed * 32768
                        while remaining:
                            part = chunk[:min(remaining, len(chunk))]
                            stream.write(part)
                            remaining -= len(part)
                    with file.open('rb') as stream:
                        expected[file.name] = hashlib.file_digest(stream, 'sha256').hexdigest()
                remote = '/upload/' + label
                config = {'Watch': {'Path': str(watch), 'AllowedExtensions': ['.bin']},
                    'Transfer': {'Mode': 'sftp', 'Direction': 'put', 'Host': '127.0.0.1', 'Port': port,
                        'Username': 'audit', 'Password': 'audit-password', 'RemotePath': remote,
                        'Concurrency': concurrency, 'VerifyUploadedFileExists': verify_exists},
                    'Hash': {'Enabled': hash_enabled, 'Algorithm': 'SHA256'}, 'Retry': {'MaxAttempts': 1, 'DelaySeconds': 0},
                    'Cleanup': {'DeleteAfterVerify': True}, 'Smtp': {'Enabled': False},
                    'App': {'LockFilePath': str(case / 'agent.lock')}, 'Logging': {'RollingFilePath': '', 'Level': 'Warning'}}
                (case / 'appsettings.json').write_text(json.dumps(config), encoding='utf-8')
                start = time.perf_counter()
                process = subprocess.run(['dotnet', str(DLL)], cwd=case, capture_output=True, timeout=120)
                seconds = time.perf_counter() - start
                (OUT / (label + '.log')).write_bytes(process.stdout + process.stderr)
                assert process.returncode == 0, label
                assert not list(watch.iterdir()), label
                with ssh.open_sftp() as sftp:
                    assert len(sftp.listdir(remote)) == count, label
                    for filename, digest in expected.items():
                        checksum = hashlib.sha256()
                        with sftp.open(remote + '/' + filename, 'rb') as stream:
                            while chunk := stream.read(1024 * 1024):
                                checksum.update(chunk)
                        assert checksum.hexdigest() == digest, (label, filename)
                        sftp.remove(remote + '/' + filename)
                    sftp.rmdir(remote)
                times.append(round(seconds, 3))
                print(json.dumps({'case': label, 'seconds': round(seconds, 3), 'verified_files': count}), flush=True)
            results.append({'workload': workload, 'files': count, 'bytes_per_file': size,
                'concurrency': concurrency, 'hash_enabled': hash_enabled, 'verify_uploaded_file_exists': verify_exists,
                'seconds': times, 'median_seconds': statistics.median(times), 'all_file_hashes_match': True})
            (OUT / 'windows-speed-results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
    ssh.close()
finally:
    (OUT / 'sftp-server.log').write_bytes(subprocess.run(['docker', 'logs', name], capture_output=True).stderr)
    subprocess.run(['docker', 'rm', '-f', name], capture_output=True, timeout=30)
