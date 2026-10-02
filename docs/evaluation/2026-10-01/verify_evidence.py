"""Verify published results and report references without running transfers."""
from pathlib import Path
from html.parser import HTMLParser
from urllib.parse import unquote
import ast
import hashlib
import json
import re
import statistics
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
REPORT=REPO/'docs/html/evaluation-report.html'
text=REPORT.read_text(encoding='utf-8')

class Document(HTMLParser):
    def __init__(self):
        super().__init__()
        self.stack=[];self.ids=[];self.hrefs=[];self.rows=[]
    def handle_starttag(self,tag,attrs):
        attributes=dict(attrs)
        if 'id' in attributes:self.ids.append(attributes['id'])
        if tag=='a':self.hrefs.append(attributes.get('href',''))
        if 'data-csv-row' in attributes:self.rows.append(int(attributes['data-csv-row']))
        if tag not in {'area','base','br','col','embed','hr','img','input','link','meta','param','source','track','wbr'}:self.stack.append(tag)
    def handle_endtag(self,tag):
        assert self.stack and self.stack.pop()==tag,tag

document=Document();document.feed(text)
assert not document.stack
assert len(document.ids)==len(set(document.ids))
assert len(document.rows)==221 and set(document.rows)==set(range(1,223))-{216}
for href in document.hrefs:
    if href.startswith('#'):assert href[1:] in document.ids,href
    elif not re.match(r'^https?://',href):assert (REPORT.parent/unquote(href.split('#')[0])).exists(),href
appendix=text.split('<div id="manual-groups">',1)[1].split('</section>',1)[0]
assert hashlib.sha256(appendix.encode()).hexdigest()==(ROOT/'manual-appendix-before-v13.sha256').read_text().strip()
manual=json.loads((ROOT/'manual-results.json').read_text(encoding='utf-8'))
assert hashlib.sha256((REPO/manual['source']).read_bytes().replace(b'\r\n',b'\n')).hexdigest()==manual['source_sha256_lf']
assert all(item['result']=='検証OK' for item in manual['items'])
assert {item['csv_row'] for item in manual['items']}==set(document.rows)
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
counters=ET.parse(ROOT/'windows-release.trx').find('.//t:Counters',ns).attrib
assert counters['passed']=='443' and counters['failed']=='0' and counters['notExecuted']=='0'
results=json.loads((ROOT/'speed-native/windows-speed-results.json').read_text())
expected=json.loads((ROOT/'speed-native/expected-sha256.json').read_text())
assert len(expected)==1000 and len(results)==4 and {r['concurrency'] for r in results}=={1,4,8,16}
for result in results:
    assert len(result['seconds'])==3 and statistics.median(result['seconds'])==result['median_seconds']
    assert result['files']==1000 and result['bytes_per_file']==4194304
    assert result['exit_codes']==[0,0,0] and result['source_remaining_files']==[0,0,0]
    assert result['observed_retry_counts']==[0,0,0]
    assert f"{result['median_seconds']:.2f}秒" in text
    for repeat in range(3):
        label=f"bulk-c{result['concurrency']}-hashTrue-existsTrue-r{repeat}"
        hashes={line.split(maxsplit=1)[1].rsplit('/',1)[1]:line.split(maxsplit=1)[0] for line in (ROOT/'speed-native'/(label+'-sha256.txt')).read_text().splitlines()}
        assert hashes==expected,label
cleanup=json.loads((ROOT/'speed-native/cleanup.json').read_text())
assert not cleanup['exists_after_cleanup'] and cleanup['server_socket_closed'] and cleanup['transports_active']==0
for script in ROOT.glob('*.py'):ast.parse(script.read_text(encoding='utf-8'))
index=json.loads((ROOT/'evidence-index.json').read_text(encoding='utf-8'))
for entry in index['source_files']+index['additional_evidence']:
    assert hashlib.sha256((REPO/entry['path']).read_bytes().replace(b'\r\n',b'\n')).hexdigest()==entry['sha256_lf'],entry['path']
print(json.dumps({'structure_valid':True,'links_valid':len(document.hrefs),'manual_items':221,'automated_passed':443,'sftp_runs':12,'verified_sftp_files':12000,'source_and_evidence_hashes_match':True}))
