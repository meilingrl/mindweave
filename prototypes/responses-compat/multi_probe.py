#!/usr/bin/env python3
"""Bounded, credential-in-memory protocol probe; no response text in reports."""
import getpass
import hashlib
import json
import re
import sys
import time
import urllib.request
import urllib.error
from pathlib import Path
from datetime import datetime, timezone
from probe import NoRedirect, events

BASES = {'gpt': 'https://api.pinaic.com/v1', 'claude': 'https://api.pinaic.com/v1',
         'deepseek': 'https://api.deepseek.com'}

def choose(ids):
    def pick(word, preferred):
        candidates = sorted(x for x in ids if word in x.lower() and not any(t in x.lower() for t in ('image','audio','embedding')))
        return next((x for p in preferred for x in candidates if x.lower() == p), candidates[0] if candidates else None)
    return {'gpt':pick('gpt', ['gpt-6.1-sol']),
            'claude':pick('claude', ['claude-sonnet-4-6','claude-sonnet-4-5','claude-haiku-4-5']),
            'deepseek':pick('deepseek', ['deepseek-chat','deepseek-v3'])}

def outcome(chars, finish, ended, chunks=0):
    return {'status':'pass' if chars and ended and finish in ('stop','end_turn','stop_sequence') else 'not_verified',
            'text_chars':chars, 'delta_events':chunks,
            'finish_reason':finish if finish in ('stop','end_turn','stop_sequence','length','max_tokens','tool_use','tool_calls') else None,
            'terminal_seen':ended}

def parse_plain(protocol, obj):
    if protocol == 'chat':
        c=obj.get('choices', [{}])[0]
        text=c.get('message', {}).get('content') or ''
        finish=c.get('finish_reason')
    else:
        text=''.join(x.get('text','') for x in obj.get('content',[]) if x.get('type')=='text')
        finish=obj.get('stop_reason')
    return outcome(len(text) if isinstance(text,str) else 0, finish, True)

def parse_stream(protocol, items):
    chars=chunks=0
    finish=None
    ended=False
    for obj in items:
        if obj == '[DONE]':
            ended=protocol=='chat'
            break
        if not isinstance(obj,dict): raise ValueError('invalid_event')
        if obj.get('error') or obj.get('type')=='error':
            return {'status':'stream_error','text_chars':chars,'delta_events':chunks}
        delta=''
        if protocol=='chat':
            choices=obj.get('choices',[])
            if choices:
                c=choices[0]
                delta=c.get('delta',{}).get('content') or ''
                finish=c.get('finish_reason') or finish
        else:
            kind=obj.get('type')
            if kind=='content_block_delta' and obj.get('delta',{}).get('type')=='text_delta':
                delta=obj['delta'].get('text','')
            if kind=='message_delta': finish=obj.get('delta',{}).get('stop_reason') or finish
            if kind=='message_stop':
                ended=True
                break
        if isinstance(delta,str) and delta:
            chars+=len(delta)
            chunks+=1
    return outcome(chars,finish,ended,chunks)

def open_request(base, key, path, payload=None, protocol='chat'):
    headers={'Content-Type':'application/json'}
    if protocol=='messages':
        headers.update({'x-api-key':key,'anthropic-version':'2023-06-01'})
    else: headers['Authorization']='Bearer '+key
    req=urllib.request.Request(base+path, data=json.dumps(payload).encode() if payload is not None else None, headers=headers)
    return urllib.request.build_opener(urllib.request.ProxyHandler({}),NoRedirect()).open(req,timeout=30)

def catalog(base, key):
    with open_request(base,key,'/models') as res:
        raw=res.read(2_000_001)
        if len(raw)>2_000_000: raise ValueError('catalog_size')
        obj=json.loads(raw)
        # Only model identifiers, no arbitrary metadata; reject control characters.
        ids=sorted({x['id'] for x in obj.get('data',[]) if isinstance(x,dict) and isinstance(x.get('id'),str)
                    and re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._:/+@-]{0,159}',x['id']) and key not in x['id']})
        return ids

def call(base, key, model, protocol, streaming):
    start=time.monotonic()
    result={'base_url':base,'model':model,'protocol':protocol,'stream':streaming}
    payload={'model':model,'messages':[{'role':'user','content':'请用中文简述蒸汽机原理，不超过50字。'}], 'stream':streaming}
    payload['max_completion_tokens' if protocol=='chat' and 'gpt' in model.lower() else 'max_tokens']=1024
    path='/messages' if protocol=='messages' else '/chat/completions'
    try:
        with open_request(base,key,path,payload,protocol) as res:
            result['http_status']=res.status
            if streaming:
                def items():
                    for data in events(res):
                        if time.monotonic()-start>120: raise TimeoutError()
                        yield data if data=='[DONE]' else json.loads(data)
                result.update(parse_stream(protocol,items()))
                result['content_type_sse']='text/event-stream' in res.headers.get('Content-Type','')
                if not result['content_type_sse']:result['status']='unexpected_content_type'
            else:
                raw=res.read(2_000_001)
                if len(raw)>2_000_000:raise ValueError('size_limit')
                result.update(parse_plain(protocol,json.loads(raw)))
    except urllib.error.HTTPError as exc:
        result.update(status='http_error',http_status=exc.code)
        exc.close()
    except Exception as exc:
        result.update(status='transport_or_protocol_error',error_class=type(exc).__name__)
    result['elapsed_seconds']=round(time.monotonic()-start,2)
    return result

def main():
    if not sys.stdin.isatty():sys.exit('请在本地交互终端运行。')
    print('GPT / Claude：Pinaic；DeepSeek：官方。分别输入 Key，各自发现模型。')
    print('每组输入前显示实际地址；地址不符时请跳过。')
    print('最多 3 次模型列表读取、8 次生成，每次输出上限 1024 tokens；可能计费，无自动重试。')
    report={'started_utc':datetime.now(timezone.utc).isoformat(),'base_urls':BASES,
            'script_hashes':{p:hashlib.sha256(Path(__file__).with_name(p).read_bytes()).hexdigest() for p in ('multi_probe.py','probe.py')},
            'discovery':{},'results':[]}
    directory=Path(__file__).resolve().parent/'results';directory.mkdir(exist_ok=True)
    path=directory/('multi-'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')+'.json')
    def save():path.write_text(json.dumps(report,ensure_ascii=False,indent=2))
    for family,protocols in [('gpt',('chat',)),('claude',('messages','chat')),('deepseek',('chat',))]:
        base=BASES[family]
        print(f'目标 {family}：{base}',flush=True)
        key=getpass.getpass(f'{family} 专用 API Key（隐藏输入，回车跳过）：').strip()
        if not key or any(x.isspace() for x in key):
            report['discovery'][family]={'status':'skipped_empty_or_invalid_key'};save();continue
        try:
            print(f'读取 {family} 密钥可访问的模型列表……',flush=True)
            ids=catalog(base,key)
            model=choose(ids)[family]
            report['discovery'][family]={'model_ids':ids,'selected':model};save()
        except Exception as exc:
            error={'class':type(exc).__name__}
            if isinstance(exc,urllib.error.HTTPError):
                error['http_status']=exc.code;exc.close()
            report['discovery'][family]={'error':error};save()
            print(f'{family} 模型发现失败，跳过该组。');key=None;continue
        if not model:
            report['results'].append({'family':family,'status':'skipped_no_catalog_candidate'});save()
            print(f'{family}：列表无候选，跳过');key=None;continue
        for protocol in protocols:
            for streaming in (False,True):
                print(f'验证 {family}: {model} / {protocol} / '+('流式' if streaming else '普通'),flush=True)
                result=call(base,key,model,protocol,streaming)
                result['family']=family
                report['results'].append(result);save()
                print('结果：'+result['status'],flush=True)
                if result['status']!='pass':break
        key=None
    print('脱敏结果：'+str(path))
    print('模型 ID 只表示目录标识，不证明真实上游身份。失败不能单独证明服务不支持该协议。')

if __name__=='__main__':
    try:main()
    except KeyboardInterrupt:print('\n已停止，不自动重试；已完成项保留在 results。')
