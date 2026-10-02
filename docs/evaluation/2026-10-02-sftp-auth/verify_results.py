"""Check trial completeness, product regression results, cleanup and report figures."""
from pathlib import Path
import hashlib
import importlib.util
import json
import re
import xml.etree.ElementTree as ET
import sys
sys.dont_write_bytecode = True

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
spec = importlib.util.spec_from_file_location('run_probe', HERE/'run_probe.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
trials = json.loads((HERE/'results/trials.json').read_text())
keys = json.loads((HERE/'results/key-auth/trials.json').read_text())
assert len(trials) == 190 and len(keys) == 40
assert all(len(trial['Connections']) == 16 for trial in trials+keys)
assert len({(t['HeldUnauthenticated'], t['Limit'], t['Round']) for t in trials}) == 190
assert module.summarize(trials) == json.loads((HERE/'results/summary.json').read_text())
assert module.summarize(keys) == json.loads((HERE/'results/key-auth/summary.json').read_text())
for trial in trials+keys:
    for item in trial['Connections']:
        assert not item['Success'] or item['HashMatches']
        assert item['Success'] or item['FailureStage'] == 'connect'
assert all(c['Success'] for t in trials if t['HeldUnauthenticated'] == 0 and t['Limit'] <= 8 for c in t['Connections'])
assert all(c['Success'] for t in keys for c in t['Connections'])
assert all(c['Success'] for t in trials if t['HeldUnauthenticated'] == 4 and t['Limit'] == 4 for c in t['Connections'])
assert any(not c['Success'] for t in trials if t['HeldUnauthenticated'] == 4 and t['Limit'] == 8 for c in t['Connections'])
server_log = (HERE/'results/server-log.txt').read_text(encoding='utf-8')
assert 'Maxstartups' in server_log
assert not any('drop connection' in line and 'penalty:' in line for line in server_log.splitlines())
cleanup = json.loads((HERE/'results/cleanup.json').read_text())
assert not any(value for name, value in cleanup.items() if name.endswith('_remaining'))
smoke = json.loads((HERE/'results/batch-smoke.json').read_text())
assert len(smoke) == 3
assert {(s['limit'], s['authentication']) for s in smoke} == {(4, 'password'), (8, 'password'), (8, 'key')}
assert all(s['exit_code'] == 0 and s['source_remaining'] == 0 and s['established_sessions'] == 16 and s['retry_log_count'] == 0 for s in smoke)
for row in smoke:
    log = (HERE/'results'/f"batch-cap{row['limit']}-{row['authentication']}.txt").read_text(encoding='utf-8')
    assert len(re.findall(r'\bRetry \d+/\d+ for ', log)) == row['retry_log_count']
    assert log.count('Hash verification successful') == row['files']
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
counters = ET.parse(HERE/'windows-release.trx').find('.//t:Counters', ns).attrib
assert counters['passed'] == '451' and counters['failed'] == '0' and counters['notExecuted'] == '0'
for item in json.loads((HERE/'results/source-hashes.json').read_text()):
    assert hashlib.sha256((HERE/'results'/item['snapshot']).read_bytes().replace(b'\r\n', b'\n')).hexdigest() == item['sha256_lf'], item['path']
    if item['path'].startswith('FtpTransferAgent/'):
        assert hashlib.sha256((REPO/item['path']).read_bytes().replace(b'\r\n', b'\n')).hexdigest() == item['sha256_lf'], item['path']
text = (REPO/'docs/html/evaluation-report.html').read_text(encoding='utf-8')
for row in module.summarize(trials):
    if row['held_unauthenticated'] == 0 and row['limit'] <= 8:
        assert f"{row['all_authenticated_median_ms']/1000:.3f}秒" in text
    assert f"{row['failures']} / {row['connection_attempts']}" in text
print(json.dumps(dict(trials=len(trials)+len(keys), connection_attempts=(len(trials)+len(keys))*16,
                      regression_passed=451, batch_smoke_passed=len(smoke), cleanup_confirmed=True,
                      all_report_figures_match=True)))
