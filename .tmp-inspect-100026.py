import json
from pathlib import Path
j=json.load(open(r'D:\VideoDownloader\.tmp-ytdlp-bili.json',encoding='utf-8-sig'))
fmts=j['formats']
f=next(x for x in fmts if '100026.m4s' in (x.get('url') or '') or str(x.get('format_id'))=='100026')
print('format_id', f.get('format_id'))
print('protocol', f.get('protocol'))
print('vcodec', f.get('vcodec'))
print('acodec', f.get('acodec'))
print('ext', f.get('ext'))
print('filesize', f.get('filesize') or f.get('filesize_approx'))
print('url_host', f.get('url','').split('/')[2])
print('obj', f.get('url','').split('?')[0].rsplit('/',1)[-1])
print('total formats', len(fmts))
