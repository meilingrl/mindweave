#!/usr/bin/env python3
"""Disposable Responses HTTP/SSE probe; never persists credentials or response bodies."""
import argparse
import getpass
import hashlib
import io
import json
from pathlib import Path
import socket
import sys
import time
from datetime import datetime, timezone
import urllib.error
import urllib.request

URL = 'https://relay.example.com/v1/responses'
MODEL = 'gpt-6.1-sol'

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None

def events(stream):
    data = []
    size = 0
    for raw in stream:
        size += len(raw)
        if size > 2_000_000:
            raise ValueError('size_limit')
        line = raw.decode('utf-8').rstrip('\r\n')
        if not line:
            if data:
                yield '\n'.join(data)
                data = []
        elif line.startswith('data:'):
            data.append(line[5:].lstrip(' '))
    if data:
        yield '\n'.join(data)

def run_case(name, key, prompt, streaming=False, stop=False):
    started = time.monotonic()
    result = {'case': name, 'status': 'not_verified'}
    payload = {'model': MODEL, 'input': prompt, 'store': False,
               'stream': streaming, 'max_output_tokens': 1024}
    request = urllib.request.Request(URL, data=json.dumps(payload).encode(),
        headers={'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json',
                 'Accept': 'text/event-stream' if streaming else 'application/json'}, method='POST')
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        with opener.open(request, timeout=30) as response:
            result['http_status'] = response.status
            if not streaming:
                body = response.read(2_000_001)
                if len(body) > 2_000_000:
                    raise ValueError('size_limit')
                obj = json.loads(body)
                text = ''.join(c.get('text', '') for item in obj.get('output', [])
                               if item.get('type') == 'message' for c in item.get('content', [])
                               if c.get('type') == 'output_text')
                result.update(response_status=obj.get('status'), text_chars=len(text))
                result['status'] = 'pass' if obj.get('status') == 'completed' and text else 'not_verified'
            else:
                result['content_type_sse'] = 'text/event-stream' in response.headers.get('Content-Type', '')
                chunks = 0
                chars = 0
                types = set()
                terminal = None
                for data in events(response):
                    if time.monotonic() - started > 120:
                        raise TimeoutError()
                    if data == '[DONE]':
                        break
                    obj = json.loads(data)
                    kind = obj.get('type', '')
                    # Only save known protocol names, not arbitrary server strings.
                    if kind in {'response.created', 'response.in_progress', 'response.output_text.delta',
                                'response.completed', 'response.failed', 'response.incomplete', 'error'}:
                        types.add(kind)
                    if kind == 'response.output_text.delta':
                        delta = obj.get('delta', '')
                        if delta:
                            chunks += 1
                            chars += len(delta)
                            if stop:
                                response.close()
                                result['status'] = 'client_closed_after_text'
                                break
                    if kind in {'response.completed', 'response.failed', 'response.incomplete', 'error'}:
                        terminal = kind
                        if kind == 'response.completed':
                            result['status'] = 'pass' if chars and result['content_type_sse'] else 'not_verified'
                        break
                result.update(delta_events=chunks, text_chars=chars, terminal_event=terminal,
                              event_types=sorted(types))
                if not terminal and not stop:
                    result['status'] = 'missing_terminal_event'
    except urllib.error.HTTPError as exc:
        result.update(http_status=exc.code, status='http_error')
        if name == 'invalid_key':
            result['status'] = 'pass' if exc.code in (401, 403) else 'not_verified'
        exc.close()
    except (TimeoutError, socket.timeout):
        result['status'] = 'timeout'
    except Exception as exc:
        # Do not print exception messages: they may contain server text or secrets.
        result.update(status='client_or_protocol_error', error_class=type(exc).__name__)
    result['elapsed_seconds'] = round(time.monotonic() - started, 2)
    if name == 'invalid_key' and result.get('http_status') == 200:
        result['status'] = 'unexpected_auth_acceptance'
    return result

def self_test():
    sample = b': keepalive\r\ndata: {"type":\r\ndata: "response.created"}\r\n\r\ndata: [DONE]\n\n'
    parsed = list(events(io.BytesIO(sample)))
    assert json.loads(parsed[0])['type'] == 'response.created'
    assert parsed[1] == '[DONE]'
    assert list(events(io.BytesIO(b'data: final'))) == ['final']
    assert NoRedirect().redirect_request(None, None, 302, '', None, 'https://example.com') is None
    print('Local parser/redirect checks passed. No network requests made.')

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if not sys.stdin.isatty():
        sys.exit('请在本地交互终端运行，不要通过管道输入密钥。')
    print('目标：' + URL + '\n模型：' + MODEL)
    print('最多 3 次真实生成（每次输出上限 1024 tokens）和 1 次无效密钥检查；可能计费，不重试。')
    print('只保存状态、计数与耗时；不保存密钥、请求头、回复正文。不使用环境代理。')
    key = getpass.getpass('粘贴 API Key 后回车（输入不显示）：').strip()
    if not key or any(c.isspace() for c in key):
        sys.exit('密钥为空或包含空白；未发起请求。')
    report = {'started_utc': datetime.now(timezone.utc).isoformat(), 'endpoint': URL, 'model': MODEL,
              'probe_sha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), 'results': []}
    cases = [('invalid_key', 'mindweave-invalid-key', 'Reply OK.', False, False),
             ('ordinary', key, '请只回复：兼容验证成功。', False, False),
             ('stream', key, '用中文简要说明蒸汽机的工作原理，约80字。', True, False),
             ('client_stop', key, '请分十条简要介绍火车历史。', True, True)]
    directory = Path(__file__).resolve().parent / 'results'
    directory.mkdir(exist_ok=True)
    path = directory / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ') + '.json')
    for name, credential, prompt, stream, stop in cases:
        print('正在验证：' + name, flush=True)
        result = run_case(name, credential, prompt, stream, stop)
        report['results'].append(result)
        path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        print(name + ': ' + result['status'], flush=True)
        if name == 'invalid_key' and result['status'] == 'unexpected_auth_acceptance':
            print('无效密钥被接受，停止后续请求。')
            break
        if name == 'ordinary' and result['status'] != 'pass':
            print('基础请求未通过，停止后续真实请求。')
            break
    print('脱敏结果：' + str(path))
    print('停止项仅证明客户端断流，不证明服务端停止生成或计费。')

if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        print('\n已中止；不会自动重试。')
