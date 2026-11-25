import os, json, time, hmac, hashlib, base64, urllib.request, urllib.error, sys

env = {}
for line in open(os.path.join(os.path.dirname(os.path.abspath(__file__)), '../../.env')):
    line = line.strip()
    if line and not line.startswith('#') and '=' in line:
        k, v = line.split('=', 1); env[k] = v.strip().strip('"')

PUB = 'http://localhost:5200'; ADM = 'http://localhost:5100'
TOKEN_W = env['TEST_WIDGET_TOKEN_MODE_ID']; DIRECT_W = env['TEST_WIDGET_DIRECT_MODE_ID']
SITE = 'http://localhost:5300'; MIRROR = 'http://localhost:5301'
ok = True

def call(method, url, body=None, headers=None):
    req = urllib.request.Request(url, method=method, data=None if body is None else json.dumps(body).encode(),
                                 headers={'content-type': 'application/json', **(headers or {})})
    try:
        with urllib.request.urlopen(req) as r:
            data = r.read(); return r.status, (json.loads(data) if data else None)
    except urllib.error.HTTPError as e:
        data = e.read(); return e.code, (json.loads(data) if data else None)

def check(name, cond, extra=''):
    global ok
    ok &= bool(cond)
    print(('PASS ' if cond else 'FAIL ') + name + (f'  [{extra}]' if extra and not cond else ''))

def b64(x): return base64.urlsafe_b64encode(x).rstrip(b'=').decode()
def client_token(client_id, exp_in=300, kid=None, secret=None):
    h = b64(json.dumps({'alg': 'HS256', 'typ': 'JWT', 'kid': kid or env['TEST_SITE_KEY_ID']}).encode())
    now = int(time.time())
    p = b64(json.dumps({'clientId': client_id, 'locale': 'en', 'iat': now, 'exp': now + exp_in}).encode())
    sig = hmac.new((secret or env['TEST_SITE_SECRET']).encode(), f'{h}.{p}'.encode(), hashlib.sha256).digest()
    return f'{h}.{p}.{b64(sig)}'

o = lambda origin=SITE: {'Origin': origin}
v = lambda tok, origin=SITE: {'Origin': origin, 'X-Relayo-Visitor': tok}

# --- Origin и настройки
s, _ = call('GET', f'{PUB}/api/widget/{TOKEN_W}/config', headers=o('https://evil.example'))
check('config from foreign origin -> 403', s == 403, s)
s, cfg = call('GET', f'{PUB}/api/widget/{TOKEN_W}/config', headers=o())
check('config from test site -> 200, signed_token mode', s == 200 and cfg['identificationMode'] == 'signed_token', cfg)
s, _ = call('GET', f'{PUB}/api/widget/00000000-0000-0000-0000-00000000ffff/config', headers=o())
check('unknown widget -> 404', s == 404, s)

# --- Анонимный посетитель пишет
s, sess = call('POST', f'{PUB}/api/widget/{TOKEN_W}/session', headers=o())
A = sess['visitorToken']
check('anonymous session created', s == 200 and A and sess['verified'] is False and sess['messages'] == [], sess)
cid = f'e2e-{int(time.time())}'
s, m1 = call('POST', f'{PUB}/api/widget/{TOKEN_W}/messages', {'text': 'Hello, I need help', 'clientMessageId': cid + '-1'}, v(A))
check('anonymous visitor sends message', s == 200 and m1['author'] == 'client', m1)
s, m1dup = call('POST', f'{PUB}/api/widget/{TOKEN_W}/messages', {'text': 'Hello, I need help', 'clientMessageId': cid + '-1'}, v(A))
check('resend with same clientMessageId does not duplicate', s == 200 and m1dup['id'] == m1['id'], m1dup)

# --- Оператор
s, auth = call('POST', f'{ADM}/api/auth/login', {'login': 'admin', 'password': env['BOOTSTRAP_ADMIN_PASSWORD']})
H = {'authorization': 'Bearer ' + auth['accessToken']}
s, tickets = call('GET', f'{ADM}/api/tickets', headers=H)
ticket = next((t for t in tickets if t['lastMessagePreview'] == 'Hello, I need help'), None)
check('ticket appears in admin inbox as New', ticket and ticket['status'] == 'New', tickets[:1])
s, details = call('GET', f"{ADM}/api/tickets/{ticket['id']}", headers=H)
chat_id = details['chats'][0]['id']
call('PUT', f'{ADM}/api/me/alias', {'alias': ''}, H)
s, err = call('PUT', f'{ADM}/api/me/alias', {'alias': '   '}, H)
check('empty alias rejected', s == 400, s)
s, me = call('PUT', f'{ADM}/api/me/alias', {'alias': 'Anna'}, H)
check('alias set', s == 200 and me['alias'] == 'Anna', me)
s, reply = call('POST', f"{ADM}/api/tickets/{ticket['id']}/messages", {'chatId': chat_id, 'text': 'Hi! Sure, what happened?'}, H)
check('operator replies', s == 200, reply)
s, hist = call('GET', f'{PUB}/api/widget/{TOKEN_W}/messages', headers=v(A))
op = [m for m in hist if m['author'] == 'operator']
check('visitor sees reply with alias only', op and op[-1]['authorName'] == 'Anna' and 'Administrator' not in json.dumps(hist), hist)

# --- Идентификация подписанным токеном
s, err = call('POST', f'{PUB}/api/widget/{TOKEN_W}/identify', {'token': client_token('1001', exp_in=-120)}, v(A))
check('expired token -> 401', s == 401 and err.get('code') == 'invalid_client_token', err)
s, err = call('POST', f'{PUB}/api/widget/{TOKEN_W}/identify', {'token': client_token('1001', secret='x' * 44)}, v(A))
check('token with foreign signature -> 401', s == 401, err)
s, err = call('POST', f'{PUB}/api/widget/{TOKEN_W}/identify', {'token': client_token('1001', exp_in=86400)}, v(A))
check('too long-lived token -> 401', s == 401, err)
s, err = call('POST', f'{PUB}/api/widget/{TOKEN_W}/identify-direct', {'clientId': '1001'}, v(A))
check('direct clientId on token-mode widget -> 403', s == 403 and err.get('code') == 'wrong_identification_mode', err)
client_id = f'1001-{int(time.time())}'
s, idn = call('POST', f'{PUB}/api/widget/{TOKEN_W}/identify', {'token': client_token(client_id)}, v(A))
check('valid token -> verified, history kept', s == 200 and idn['verified'] and len(idn['messages']) == 2, idn)
s, t2 = call('GET', f"{ADM}/api/tickets/{ticket['id']}", headers=H)
check('ticket now belongs to identified client', t2['client']['externalClientId'] == client_id and t2['client']['isVerified'], t2['client'])

# --- Тот же клиент с другого зеркала (новый браузер / localStorage)
s, sessB = call('POST', f'{PUB}/api/widget/{TOKEN_W}/session', headers=o(MIRROR))
B = sessB['visitorToken']
check('mirror: new anonymous visitor sees nothing', sessB['messages'] == [], sessB)
s, idB = call('POST', f'{PUB}/api/widget/{TOKEN_W}/identify', {'token': client_token(client_id)}, v(B, MIRROR))
check('mirror: after identify sees the same conversation', s == 200 and len(idB['messages']) == 2, idB)
s, mB = call('POST', f'{PUB}/api/widget/{TOKEN_W}/messages', {'text': 'Writing from the mirror'}, v(B, MIRROR))
s, t3 = call('GET', f"{ADM}/api/tickets/{ticket['id']}", headers=H)
check('mirror message goes to the same open ticket and chat', len(t3['chats']) == 1 and len(t3['chats'][0]['messages']) == 3, t3['chats'])

# --- Небезопасный режим: кто-то подставил тот же clientId
s, sessC = call('POST', f'{PUB}/api/widget/{DIRECT_W}/session', headers=o())
C = sessC['visitorToken']
s, err = call('POST', f'{PUB}/api/widget/{DIRECT_W}/identify', {'token': client_token(client_id)}, v(C))
check('signed token on direct-mode widget -> 403', s == 403, err)
s, idC = call('POST', f'{PUB}/api/widget/{DIRECT_W}/identify-direct', {'clientId': client_id, 'product': 'test'}, v(C))
check('direct clientId -> not verified, sees none of the verified chat', s == 200 and not idC['verified'] and idC['messages'] == [], idC)
s, err = call('POST', f'{PUB}/api/widget/{DIRECT_W}/identify-direct', {'clientId': '1', 'product': 'nope'}, v(C))
check('unknown product -> 400', s == 400 and err.get('code') == 'unknown_product', err)
s, mC = call('POST', f'{PUB}/api/widget/{DIRECT_W}/messages', {'text': 'I am totally the same person'}, v(C))
s, t4 = call('GET', f"{ADM}/api/tickets/{ticket['id']}", headers=H)
chats = {c['id']: c for c in t4['chats']}
check('unverified visitor gets a separate chat in the same ticket', len(chats) == 2 and sum(1 for c in chats.values() if not c['isVerified']) == 1, [(c['isVerified'], len(c['messages'])) for c in chats.values()])
s, histC = call('GET', f'{PUB}/api/widget/{DIRECT_W}/messages', headers=v(C))
check('unverified visitor sees only own messages', [m['text'] for m in histC] == ['I am totally the same person'], histC)
s, histA = call('GET', f'{PUB}/api/widget/{TOKEN_W}/messages', headers=v(A))
check('real client does not see the impostor chat', 'totally' not in json.dumps(histA), [m['text'] for m in histA])

# --- Закрытие и новый тикет
s, closed = call('POST', f"{ADM}/api/tickets/{ticket['id']}/close", headers=H)
check('ticket closed', closed['status'] == 'Closed', closed)
s, mD = call('POST', f'{PUB}/api/widget/{TOKEN_W}/messages', {'text': 'One more question'}, v(A))
s, tickets2 = call('GET', f'{ADM}/api/tickets', headers=H)
new_ticket = next((t for t in tickets2 if t['lastMessagePreview'] == 'One more question'), None)
check('message after close opens a new ticket', new_ticket and new_ticket['id'] != ticket['id'], new_ticket)
s, histA2 = call('GET', f'{PUB}/api/widget/{TOKEN_W}/messages', headers=v(A))
check('widget history still shows previous (closed) chat', len(histA2) >= 4, len(histA2))

print('\nALL PASSED' if ok else '\nSOME CHECKS FAILED'); sys.exit(0 if ok else 1)
