"""Verify retained Windows cleanup evidence without starting servers."""
from pathlib import Path
import hashlib
import json
import re
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
RESULTS = HERE / 'results'
rows = json.loads((RESULTS / 'results.json').read_text(encoding='utf-8'))
conditions = json.loads((RESULTS / 'conditions.json').read_text(encoding='utf-8'))
assert len(rows) == 18 and len({r['case'] for r in rows}) == 18
by_case = {row['case']: row for row in rows}
payload = conditions['payload_bytes']
assert payload == 32768
assert hashlib.sha256((RESULTS / 'measured-Worker.cs').read_bytes().replace(b'\r\n', b'\n')).hexdigest() == conditions['current_source_sha256_lf']
for verify in (False, True):
    previous = by_case[f'previous-hash{verify}']
    assert previous['exit_code'] == 0 and previous['local_matches']
    assert previous['read_bytes']['/payload.bin'] == payload * (4 if verify else 3)
    for transfer_end in (False, True):
        for failure in ('none', 'data', 'end'):
            name = f'new-hash{verify}-end{transfer_end}-fail{failure}'
            row = by_case[name]
            assert row['exit_code'] == 0 and row['local_matches'], name
            assert row['state_json_count'] == 0 and row['retry_logs'] == 0, name
            assert row['read_bytes']['/payload.bin'] == payload * (2 if verify else 1), name
            assert row['read_bytes'].get('/payload.bin.END', 0) == (5 * (2 if verify else 1) if transfer_end else 0), name
            assert row['deletes']['/payload.bin.END'] == 1, name
            assert row['deletes'].get('/payload.bin', 0) == (0 if failure == 'end' else 1), name
            assert row['remote_data_exists'] == (failure != 'none'), name
            assert row['remote_end_exists'] == (failure == 'end'), name
            assert row['cleanup_warning'] == (failure != 'none'), name
            log = (RESULTS / f'{name}.txt').read_text(encoding='utf-8')
            assert len(re.findall(r'\bRetry \d+/\d+ for ', log)) == 0, name
            if failure != 'none':
                path = '/payload.bin.END' if failure == 'end' else '/payload.bin'
                assert f'Could not delete remote file {path} after successful download.' in log, name
            if failure == 'data':
                following = by_case[f'{name}-next']
                assert following['exit_code'] == 0 and not following['local_matches']
                assert not following['read_bytes'] and not following['deletes']
                assert following['remote_data_exists'] and not following['remote_end_exists']
                assert following['state_json_count'] == 0 and following['retry_logs'] == 0
cleanup = json.loads((RESULTS / 'cleanup.json').read_text(encoding='utf-8'))
assert cleanup == dict(data_directory_remaining=False, server_socket_closed=True, active_transports=0)
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
for filename, passed in (('windows-release.trx', 460), ('cleanup-targeted.trx', 46)):
    counts = ET.parse(HERE / filename).find('.//t:Counters', ns).attrib
    assert int(counts['passed']) == passed and counts['failed'] == '0' and counts['notExecuted'] == '0'
report = (REPO / 'docs/html/evaluation-report.html').read_text(encoding='utf-8')
assert 'id="download-cleanup"' in report and '460' in report
assert '削除のための追加ハッシュ照合とJSON保存を外しました' in report
print(json.dumps(dict(regression_passed=460, targeted_passed=46, sftp_conditions=18,
                      extra_cleanup_data_reads=0, cleanup_confirmed=True)))
