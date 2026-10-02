"""Receive product error notifications on localhost, including limit and shutdown drain."""
import email
import json
import pathlib
import socketserver
import subprocess
import tempfile
import threading
import time

REPO = pathlib.Path(__file__).resolve().parents[3]
ROOT = REPO / ".audit-results/evaluation-rerun"
ROOT.mkdir(parents=True, exist_ok=True)
OUT = ROOT / 'smtp-fixed'
OUT.mkdir(exist_ok=True)
DLL = REPO / 'FtpTransferAgent/bin/Release/net10.0/FtpTransferAgent.dll'
messages = []
mutex = threading.Lock()

class Handler(socketserver.StreamRequestHandler):
    reject = False
    def handle(self):
        def reply(line):
            self.wfile.write(line + b'\r\n')
            self.wfile.flush()
        reply(b'220 localhost audit SMTP')
        while line := self.rfile.readline():
            command = line.split(b' ', 1)[0].strip().upper()
            if command in (b'EHLO', b'HELO'):
                reply(b'250-localhost')
                reply(b'250 SIZE 10000000')
            elif command == b'RCPT' and self.reject:
                reply(b'550 injected recipient rejection')
            elif command == b'DATA':
                reply(b'354 send message')
                body = []
                while data := self.rfile.readline():
                    if data == b'.\r\n':
                        break
                    body.append(data)
                # A slow receiver checks whether the one-shot app waits for queued sends.
                time.sleep(.15)
                parsed = email.message_from_bytes(b''.join(body))
                with mutex:
                    messages.append({'subject': str(parsed['Subject']), 'to': str(parsed['To']), 'from': str(parsed['From'])})
                reply(b'250 queued')
            elif command == b'QUIT':
                reply(b'221 bye')
                break
            else:
                reply(b'250 OK')

class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True
    request_queue_size = 128

results = []
with Server(('127.0.0.1', 0), Handler) as server, tempfile.TemporaryDirectory(prefix='agent-smtp-') as temp:
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        for label, enabled, limit, reject in [('disabled', False, 5, False), ('limited', True, 5, False),
                                               ('unlimited', True, 0, False), ('relay-rejects', True, 5, True)]:
            case = pathlib.Path(temp) / label
            watch = case / 'watch'
            watch.mkdir(parents=True)
            destination = case / 'blocked-destination'
            destination.write_text('not a directory')
            for index in range(20):
                (watch / f'{index}.txt').write_text('preserve this source')
            config = {'Watch': {'Path': str(watch), 'AllowedExtensions': ['.txt']},
                'Transfer': {'Mode': 'local', 'RemotePath': str(destination), 'Concurrency': 4},
                'Retry': {'MaxAttempts': 0}, 'Hash': {'Enabled': True}, 'Cleanup': {'DeleteAfterVerify': True},
                'Smtp': {'Enabled': enabled, 'RelayHost': '127.0.0.1', 'RelayPort': server.server_address[1],
                    'From': 'sender@example.test', 'To': ['receiver@example.test'], 'MaxEmailsPerRun': limit},
                'Logging': {'RollingFilePath': '', 'Level': 'Warning'}, 'App': {'LockFilePath': str(case / 'agent.lock')}}
            (case / 'appsettings.json').write_text(json.dumps(config), encoding='utf-8')
            Handler.reject = reject
            with mutex:
                messages.clear()
            start = time.perf_counter()
            process = subprocess.run(['dotnet', str(DLL)], cwd=case, capture_output=True, timeout=30)
            duration = round(time.perf_counter() - start, 3)
            (OUT / ('smtp-' + label + '.log')).write_bytes(process.stdout + process.stderr)
            with mutex:
                received = list(messages)
            assert process.returncode == 1 and len(list(watch.glob('*.txt'))) == 20, label
            if not enabled or reject:
                assert len(received) == 0, label
            elif limit:
                assert len(received) == limit, (label, len(received))
            else:
                assert len(received) > 20, (label, len(received))
            result = {'case': label, 'exit': process.returncode, 'seconds': duration,
                'received_before_process_exit': len(received), 'source_files_preserved': 20, 'passed': True, 'messages': received}
            results.append(result)
            print(json.dumps({key: value for key, value in result.items() if key != 'messages'}), flush=True)
    finally:
        server.shutdown()
        thread.join(timeout=5)
(OUT / 'smtp-results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
