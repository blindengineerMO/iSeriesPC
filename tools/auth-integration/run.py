#!/usr/bin/env python3
"""Isolated integration: real MIT KDC, OpenLDAP TLS, SSSD, PAM and app service guards."""
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import sqlite3
import struct
import subprocess
import time

os.umask(0o077)
root = Path('/fixture')
root.mkdir(exist_ok=True)
os.environ.update(KRB5_CONFIG=str(root/'krb5.conf'), KRB5_KDC_PROFILE=str(root/'kdc.conf'),
                  KRB5CCNAME='FILE:/fixture/test.ccache', LDAPTLS_CACERT='/fixture/ca/ldap.crt', LDAPTLS_REQCERT='demand')
password = secrets.token_hex(16)
admin_password = secrets.token_hex(16)
ldap_password = secrets.token_hex(16)
known_secrets = [password, admin_password, ldap_password]
processes = []
logs = []


def run(arguments, text=None, timeout=30, env=None):
    result = subprocess.run(arguments, input=text, text=True, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, timeout=timeout, env=env)
    if result.returncode:
        # No supplied credential is printed, even when an external utility echoes it.
        diagnostic = (result.stderr + result.stdout)[-2000:]
        for secret in known_secrets: diagnostic = diagnostic.replace(secret, '[redacted]')
        raise AssertionError(f'{Path(arguments[0]).name} failed: {diagnostic}')
    return result.stdout


def start(arguments, name, env=None):
    log = open(root/f'{name}.log', 'w')
    logs.append(log)
    process = subprocess.Popen(arguments, stdout=log, stderr=log, env=env)
    processes.append(process)
    return process


def stop(process):
    if process.poll() is None:
        process.terminate()
        try: process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


def ldap_change(ldif):
    return run(['ldapmodify', '-x', '-H', 'ldaps://ldap.ipc.test', '-D', 'cn=admin,dc=ipc,dc=test',
                '-y', str(root/'ldap.pass')], ldif)


def exact(stream, size):
    data = bytearray()
    while len(data) < size:
        block = stream.recv(size-len(data))
        if not block: raise AssertionError('Server closed the command connection')
        data.extend(block)
    return bytes(data)


def receive(stream):
    length, = struct.unpack('!I', exact(stream, 4))
    assert 0 < length <= 256*1024
    return json.loads(exact(stream, length))


def send(stream, payload):
    data = json.dumps(payload).encode()
    stream.sendall(struct.pack('!I', len(data))+data)


def authenticate(user, supplied_password, succeeds=True):
    stream = socket.socket(socket.AF_UNIX)
    stream.settimeout(25)
    stream.connect('/fixture/catalog/run/as400.sock')
    receive(stream)
    send(stream, {'Version': 1, 'CommandSession': {'Operation': 'Authenticate', 'User': user, 'Password': supplied_password}})
    reply = receive(stream)
    assert reply['Success'] == succeeds, f'Unexpected authentication outcome for {user}'
    if not succeeds:
        stream.close()
        return None
    return stream


def command(stream, text, succeeds=True):
    send(stream, {'Operation': 'Execute', 'Command': text})
    reply = receive(stream)
    assert reply['Success'] == succeeds, f'Unexpected command outcome for {text}: {reply}'
    return reply


try:
    with open('/etc/hosts', 'a') as hosts: hosts.write('\n127.0.0.1 ldap.ipc.test kdc.ipc.test sssd.ipc.test\n')
    (root/'ca').mkdir()
    run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '2',
         '-subj', '/CN=ldap.ipc.test', '-addext', 'subjectAltName=DNS:ldap.ipc.test',
         '-keyout', str(root/'ldap.key'), '-out', str(root/'ca/ldap.crt')])
    run(['openssl', 'rehash', str(root/'ca')])
    (root/'ldap').mkdir()
    (root/'ldap.pass').write_text(ldap_password)
    ldap_hash = run(['slappasswd', '-T', str(root/'ldap.pass')]).strip()
    (root/'slapd.conf').write_text(f'''include /etc/ldap/schema/core.schema
include /etc/ldap/schema/cosine.schema
include /etc/ldap/schema/inetorgperson.schema
include /etc/ldap/schema/nis.schema
pidfile /fixture/slapd.pid
argsfile /fixture/slapd.args
modulepath /usr/lib/ldap
moduleload back_mdb
TLSCertificateFile /fixture/ca/ldap.crt
TLSCertificateKeyFile /fixture/ldap.key
TLSCACertificateFile /fixture/ca/ldap.crt
database mdb
maxsize 10485760
suffix "dc=ipc,dc=test"
rootdn "cn=admin,dc=ipc,dc=test"
rootpw {ldap_hash}
directory /fixture/ldap
access to attrs=userPassword by self write by anonymous auth by * none
access to * by * read
''')
    slapd = start(['/usr/sbin/slapd', '-f', str(root/'slapd.conf'), '-h', 'ldaps://127.0.0.1:636', '-d', '0'], 'slapd')
    for _ in range(100):
        try:
            with socket.create_connection(('127.0.0.1', 636), timeout=.2): break
        except OSError: time.sleep(.1)
    ldap_change('''dn: dc=ipc,dc=test
changetype: add
objectClass: top
objectClass: dcObject
objectClass: organization
o: IPC Test
dc: ipc

dn: cn=ipcusers,dc=ipc,dc=test
changetype: add
objectClass: posixGroup
cn: ipcusers
gidNumber: 10001

dn: uid=alice,dc=ipc,dc=test
changetype: add
objectClass: inetOrgPerson
objectClass: posixAccount
cn: Alice
sn: Test
uid: alice
uidNumber: 10001
gidNumber: 10001
homeDirectory: /home/alice
loginShell: /bin/bash
description: alice@IPC.TEST
employeeType: active
''')
    entry = run(['ldapsearch', '-LLL', '-x', '-H', 'ldaps://ldap.ipc.test', '-b', 'uid=alice,dc=ipc,dc=test', '-s', 'base', 'entryUUID'])
    entry_id = next(line.split(': ', 1)[1] for line in entry.splitlines() if line.startswith('entryUUID: '))
    (root/'krb5.conf').write_text('''[libdefaults]
 default_realm = IPC.TEST
 dns_lookup_kdc = false
 dns_lookup_realm = false
 rdns = false
 dns_canonicalize_hostname = false
 udp_preference_limit = 1
[realms]
 IPC.TEST = {
  kdc = 127.0.0.1
 }
''')
    Path('/etc/krb5.conf').write_bytes((root/'krb5.conf').read_bytes())
    Path('/etc/krb5.conf').chmod(0o644)
    os.environ['KRB5_CONFIG'] = '/etc/krb5.conf'
    (root/'kdc.conf').write_text('''[kdcdefaults]
 kdc_ports = 88
 kdc_tcp_ports = 88
[realms]
 IPC.TEST = {
  database_name = /fixture/principal
  key_stash_file = /fixture/stash
  acl_file = /fixture/kadm5.acl
 }
''')
    master = secrets.token_hex(24)
    known_secrets.append(master)
    run(['kdb5_util', 'create', '-s', '-r', 'IPC.TEST'], master+'\n'+master+'\n')
    run(['kadmin.local', '-r', 'IPC.TEST'], f'addprinc alice\n{password}\n{password}\naddprinc -randkey host/sssd.ipc.test\nktadd -k /fixture/sssd.keytab host/sssd.ipc.test\nquit\n')
    kdc = start(['krb5kdc', '-n', '-r', 'IPC.TEST', '-P', '/fixture/kdc.pid'], 'kdc')
    time.sleep(.3)
    run(['kinit', 'alice@IPC.TEST'], password+'\n')
    assert 'alice@IPC.TEST' in run(['klist'])
    run(['kdestroy'])
    (root/'sssd.conf').write_text('''[sssd]
config_file_version = 2
services = nss, pam
domains = IPC.TEST
[domain/IPC.TEST]
id_provider = ldap
auth_provider = krb5
access_provider = ldap
sudo_provider = none
ldap_uri = ldaps://ldap.ipc.test
ldap_search_base = dc=ipc,dc=test
ldap_tls_cacert = /fixture/ca/ldap.crt
ldap_tls_reqcert = demand
ldap_user_principal = description
ldap_access_filter = (employeeType=active)
ldap_access_order = filter
ldap_referrals = false
krb5_realm = IPC.TEST
krb5_server = 127.0.0.1
krb5_validate = true
krb5_keytab = /fixture/sssd.keytab
krb5_ccachedir = /tmp
krb5_ccname_template = FILE:/tmp/krb5cc_%U_XXXXXX
cache_credentials = false
use_fully_qualified_names = false
enumerate = false
entry_cache_timeout = 10
''')
    (root/'sssd.conf').chmod(0o600)
    Path('/etc/pam.d/iseriespc-sssd').write_text('auth required pam_sss.so\naccount required pam_sss.so\n')
    nss = Path('/etc/nsswitch.conf')
    nss.write_text('\n'.join(line+' sss' if line.startswith(('passwd:', 'group:')) else line for line in nss.read_text().splitlines())+'\n')
    os.environ.pop('KRB5CCNAME', None)
    sssd = start(['/usr/sbin/sssd', '-i', '-c', str(root/'sssd.conf'), '--logger=stderr'], 'sssd')
    for attempt in range(50):
        result = subprocess.run(['getent', 'passwd', 'alice'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        if result.returncode == 0: break
        time.sleep(.2)
    assert result.returncode == 0 and ':10001:10001:' in result.stdout, 'SSSD did not resolve the LDAP account'
    catalog = root/'catalog'
    catalog.mkdir(mode=0o700)
    config = {'Authentication': {'SssdPamService': 'iseriespc-sssd', 'Directories': [{
        'Name': 'test', 'Uri': 'ldaps://ldap.ipc.test', 'TrustedCertificatesDirectory': '/fixture/ca',
        'PrincipalAttribute': 'description'}, {'Name': 'bad-hostname', 'Uri': 'ldaps://127.0.0.1',
        'TrustedCertificatesDirectory': '/fixture/ca', 'PrincipalAttribute': 'description'}]}}
    (catalog/'system.json').write_text(json.dumps(config))
    server = '/app/Ipc.Server/as400server.dll'
    run(['/dotnet/dotnet', server, '--data-dir', str(catalog), '--reset-admin'])
    database = catalog/'system.db'
    with sqlite3.connect(database) as connection:
        connection.create_function('ipc_actor', 0, lambda: 'TESTFIXTURE')
        connection.create_function('ipc_job', 0, lambda: None)
        connection.row_factory = sqlite3.Row
        template = dict(connection.execute("select * from sys_profiles where name='QUSER'").fetchone())
        for profile in ('NETUSER', 'BADCERT'):
            template['name'] = profile
            connection.execute('insert into sys_profiles ('+','.join(template)+') values ('+','.join('?' for _ in template)+')', tuple(template.values()))
        salt = secrets.token_bytes(16)
        hashed = '210000$'+base64.b64encode(salt).decode()+'$'+base64.b64encode(hashlib.pbkdf2_hmac('sha256', admin_password.encode(), salt, 210000)).decode()
        connection.execute("update sys_profiles set password_hash=?,status='Enabled',password_expires=NULL where name='QSECOFR'", (hashed,))
    app_environment = dict(os.environ)
    app_environment.pop('LDAPTLS_CACERT', None)
    host = start(['/dotnet/dotnet', server, '--data-dir', str(catalog)], 'app', env=app_environment)
    for _ in range(100):
        if (catalog/'run/as400.sock').exists(): break
        if host.poll() is not None: raise AssertionError('App server exited during initialization')
        time.sleep(.1)
    administrator = authenticate('QSECOFR', admin_password)
    reply = command(administrator, f"ADDEIMMAP PROFILE(NETUSER) DIRECTORY(test) DN('uid=alice,dc=ipc,dc=test') ENTRYID('{entry_id}') PRINCIPAL('alice@IPC.TEST') ACCOUNT(alice)")
    assert 'Active' in reply['Result']['Message'], f'LDAP mapping did not activate: {reply}'
    bad_certificate = command(administrator, f"ADDEIMMAP PROFILE(BADCERT) DIRECTORY(bad-hostname) DN('uid=alice,dc=ipc,dc=test') ENTRYID('{entry_id}') PRINCIPAL('other@IPC.TEST') ACCOUNT(other)")
    assert 'Unavailable' in bad_certificate['Result']['Message'], 'LDAP certificate hostname mismatch was not rejected'
    authenticate('NETUSER', 'WrongPassword1', succeeds=False)
    mapped = authenticate('NETUSER', password)
    command(mapped, 'DSPJOB')
    ldap_change('''dn: uid=alice,dc=ipc,dc=test
changetype: modify
replace: employeeType
employeeType: disabled
''')
    print('Kerberos login and LDAP TLS checks passed; waiting for scheduled directory revocation.', flush=True)
    deadline = time.monotonic() + 45
    while True:
        with sqlite3.connect(database) as observer:
            state = observer.execute("select directory_state from sys_eim_mappings where profile='NETUSER'").fetchone()[0]
        if state == 'Disabled': break
        assert time.monotonic() < deadline, 'Scheduled directory refresh did not propagate revocation'
        time.sleep(1)
    reply = command(administrator, 'RFREIMMAP PROFILE(NETUSER)')
    assert 'Disabled' in reply['Result']['Message']
    command(mapped, 'DSPJOB', succeeds=False)
    authenticate('NETUSER', password, succeeds=False)
    ldap_change('''dn: uid=alice,dc=ipc,dc=test
changetype: modify
replace: employeeType
employeeType: active
-
replace: description
description: other@IPC.TEST
''')
    reply = command(administrator, 'RFREIMMAP PROFILE(NETUSER)')
    assert 'IdentityMismatch' in reply['Result']['Message']
    ldap_change('''dn: uid=alice,dc=ipc,dc=test
changetype: modify
replace: description
description: alice@IPC.TEST
''')
    command(administrator, 'RFREIMMAP PROFILE(NETUSER)')
    stop(kdc)
    authenticate('NETUSER', password, succeeds=False)
    stop(slapd)
    reply = command(administrator, 'RFREIMMAP PROFILE(NETUSER)')
    assert 'Unavailable' in reply['Result']['Message']
    command(mapped, 'DSPJOB', succeeds=False)
    authenticate('NETUSER', password, succeeds=False)
    command(administrator, 'RMVEIMMAP PROFILE(NETUSER)')
    mapped.close()
    command(administrator, 'SIGNOFF')
    administrator.close()
    stop(host)
    with sqlite3.connect(database) as connection:
        assert connection.execute("select status from sys_profiles where name='NETUSER'").fetchone()[0] == 'Disabled'
        events = connection.execute('select payload from sys_events').fetchall()
        assert all(all(secret not in payload for secret in known_secrets) for (payload,) in events)
    print('Kerberos/LDAP/SSSD smoke passed: real KDC credentials, TLS directory binding, PAM/SSSD login, principal conflict, live disable, provider outages, mapping removal and secret-free audit.')
except Exception:
    for name in ('app', 'sssd', 'kdc', 'slapd'):
        path = root/f'{name}.log'
        if path.exists():
            text = path.read_text(errors='replace')[-3000:]
            for secret in known_secrets: text = text.replace(secret, '[redacted]')
            print(f'{name} diagnostic: {text}', flush=True)
    raise
finally:
    for process in reversed(processes): stop(process)
    for log in logs: log.close()
