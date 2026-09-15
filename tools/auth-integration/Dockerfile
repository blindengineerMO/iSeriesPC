FROM ubuntu:24.04
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    ca-certificates openssl python3 krb5-kdc krb5-user krb5-admin-server slapd ldap-utils \
    sssd-ldap sssd-krb5 sssd-tools libnss-sss libpam-sss libicu74 \
    && rm -rf /var/lib/apt/lists/*
COPY run.py /opt/iseriespc-auth-test.py
ENTRYPOINT ["python3", "/opt/iseriespc-auth-test.py"]
