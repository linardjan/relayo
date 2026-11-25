import os, json, time, urllib.request, urllib.error, sys
env = {}
for line in open(os.path.join(os.path.dirname(os.path.abspath(__file__)), '../../.env')):
    line=line.strip()
    if line and not line.startswith('#') and '=' in line:
        k,v=line.split('=',1); env[k]=v.strip().strip('"')
ADM='http://localhost:5100'; ok=True
def call(m,u,b=None,h=None):
    r=urllib.request.Request(ADM+u,method=m,data=None if b is None else json.dumps(b).encode(),headers={'content-type':'application/json',**(h or {})})
    try:
        with urllib.request.urlopen(r) as x: d=x.read(); return x.status,(json.loads(d) if d else None)
    except urllib.error.HTTPError as e: d=e.read(); return e.code,(json.loads(d) if d else None)
def check(n,c,e=''):
    global ok; ok&=bool(c); print(('PASS ' if c else 'FAIL ')+n+(f'  [{e}]' if e and not c else ''))
def login(l,p):
    s,a=call('POST','/api/auth/login',{'login':l,'password':p}); return {'authorization':'Bearer '+a['accessToken']} if s==200 else None
A=login('admin',env['BOOTSTRAP_ADMIN_PASSWORD']); sfx=str(int(time.time()))[-6:]

# Пользователи
s,u=call('POST','/api/settings/users',{'login':'op'+sfx,'fullName':'Olga Operator','role':'Operator','alias':'Olga','password':'short'},A)
check('weak password rejected',s==400 and u['code']=='weak_password',u)
s,u=call('POST','/api/settings/users',{'login':'op'+sfx,'fullName':'Olga Operator','role':'Operator','alias':'Olga','password':'Operator_pass1'},A)
check('local operator created',s==200 and u['role']=='Operator' and u['alias']=='Olga',u)
s,dup=call('POST','/api/settings/users',{'login':'OP'+sfx,'fullName':'X','role':'Operator','password':'Operator_pass1'},A)
check('duplicate login (case-insensitive) -> 409',s==409,dup)
O=login('op'+sfx,'Operator_pass1')
check('operator can sign in',O is not None)
s,_=call('GET','/api/settings/sites',h=O); check('operator cannot open settings -> 403',s==403,s)
s,_=call('GET','/api/clients',h=O); check('operator can open clients',s==200,s)
s,users=call('GET','/api/settings/users',h=A); me=next(x for x in users if x['login']=='admin')
s,e=call('PUT',f"/api/settings/users/{me['id']}",{'fullName':me['fullName'],'alias':me['alias'],'role':'Operator','isBlocked':False},A)
check('admin cannot remove own admin role -> 409',s==409 and e['code']=='last_admin',e)
s,b=call('PUT',f"/api/settings/users/{u['id']}",{'fullName':'Olga O.','alias':'Olga','role':'Operator','isBlocked':True},A)
check('operator blocked',s==200 and b['isBlocked'],b)
s,r=call('POST','/api/auth/refresh',h=O)
check('blocked operator cannot sign in',login('op'+sfx,'Operator_pass1') is None)
s,_=call('POST',f"/api/settings/users/{u['id']}/password",{'password':'New_password_1'},A); check('admin resets password',s==204,s)

# Сайты и ключи
s,e=call('POST','/api/settings/sites',{'code':'Bad Code','name':'x','allowedOrigins':['https://a.com']},A)
check('invalid site code -> 400',s==400,e)
s,e=call('POST','/api/settings/sites',{'code':'s'+sfx,'name':'Shop','allowedOrigins':['https://shop.example/path']},A)
check('domain with path -> 400',s==400,e)
s,site=call('POST','/api/settings/sites',{'code':'s'+sfx,'name':'Shop','allowedOrigins':['https://Shop.example','https://mirror.example:8443']},A)
check('site created, domains normalized',s==200 and site['allowedOrigins']==['https://shop.example','https://mirror.example:8443'],site)
s,e=call('POST','/api/settings/sites',{'code':'s'+sfx,'name':'Again','allowedOrigins':['https://a.com']},A)
check('duplicate site code -> 409',s==409,e)
s,k=call('POST',f"/api/settings/sites/{site['id']}/keys",h=A)
check('key created, secret returned once',s==200 and len(k['secret'])>=40 and k['key']['keyId'].startswith('s'+sfx),k)
s,sites=call('GET','/api/settings/sites',h=A)
check('secret is not returned in the list','secret' not in json.dumps(sites) and k['secret'] not in json.dumps(sites))
s,rk=call('POST',f"/api/settings/sites/{site['id']}/keys/{k['key']['id']}/revoke",h=A)
check('key revoked',s==200 and rk['revokedAt'],rk)

# Виджеты
wi={'siteId':site['id'],'name':'Shop widget','identificationMode':'SignedToken','title':'Help','greeting':'Hello!','accentColor':'#ff6600','position':'Left','defaultLocale':'en'}
s,e=call('POST','/api/settings/widgets',{**wi,'accentColor':'red'},A); check('invalid color -> 400',s==400,e)
s,w=call('POST','/api/settings/widgets',wi,A)
check('widget created with embed code',s==200 and w['widget']['position']=='Left' and w['embedCode']=='<script async src="http://localhost:9000/relayo-widgets/widget_%s.js"></script>' % w['widget']['publicKey'] and w['widget']['publishedVersion'] is None,w)
s,w2=call('PUT',f"/api/settings/widgets/{w['widget']['id']}",{**{k:v for k,v in wi.items() if k!='siteId'},'identificationMode':'Direct','title':'Support'},A)
check('widget updated',s==200 and w2['widget']['identificationMode']=='Direct' and w2['widget']['title']=='Support',w2)

# Клиенты
s,page=call('GET','/api/clients?pageSize=5',h=A)
check('clients list (identified only by default)',s==200 and all(c['externalClientId'] for c in page['items']) and page['total']>=1,page)
s,anon=call('GET','/api/clients?anonymous=true&pageSize=100',h=A)
check('anonymous switch includes visitors',anon['total']>=page['total'])
s,found=call('GET','/api/clients?search=1001',h=A)
check('search by clientId',s==200 and all('1001' in c['externalClientId'] for c in found['items']) and found['items'],found)
s,pct=call('GET','/api/clients?search=%25',h=A)
check('search escapes LIKE wildcards',s==200 and pct['total']==0,pct['total'])
cid=found['items'][0]['id']
s,d=call('GET',f'/api/clients/{cid}',h=A)
check('client details with tickets and chats',s==200 and d['tickets'] and all('chats' in t for t in d['tickets']) and d['identities'],{k:(len(v) if isinstance(v,list) else v) for k,v in d.items()})
check('visitor token hash is not exposed','externalId' not in json.dumps(d['identities']))
s,_=call('GET','/api/clients/00000000-0000-0000-0000-000000000000',h=A); check('unknown client -> 404',s==404,s)
print('\nALL PASSED' if ok else '\nSOME CHECKS FAILED'); sys.exit(0 if ok else 1)
