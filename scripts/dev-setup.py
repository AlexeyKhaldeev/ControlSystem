#!/usr/bin/env python3
"""Generate isolated LAN prototype secrets and a localhost-only development CA."""
import hashlib
import os
from pathlib import Path
import secrets
import shlex
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / ".local"
os.umask(0o077)
LOCAL.mkdir(mode=0o700, exist_ok=True)
if (LOCAL / "dev.env").exists():
    raise SystemExit("Configuration already exists. Reuse: source .local/dev.env")

def openssl(*args):
    subprocess.run(["openssl", *args], cwd=LOCAL, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)

(LOCAL / "ca.cnf").write_text("""[req]
distinguished_name=dn
x509_extensions=ca_ext
prompt=no
[dn]
CN=ControlSystem Local Development CA
[ca_ext]
basicConstraints=critical,CA:TRUE
keyUsage=critical,keyCertSign,cRLSign
subjectKeyIdentifier=hash
""")
(LOCAL / "server.cnf").write_text("""[req]
distinguished_name=dn
prompt=no
[dn]
CN=localhost
[server_ext]
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:localhost,IP:127.0.0.1
""")
openssl("req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "30", "-sha256", "-config", "ca.cnf", "-keyout", "ca.key.pem", "-out", "ca.crt.pem")
openssl("req", "-new", "-newkey", "rsa:2048", "-nodes", "-sha256", "-config", "server.cnf", "-keyout", "localhost.key.pem", "-out", "localhost.csr")
openssl("x509", "-req", "-in", "localhost.csr", "-CA", "ca.crt.pem", "-CAkey", "ca.key.pem", "-CAcreateserial", "-days", "30", "-sha256", "-extfile", "server.cnf", "-extensions", "server_ext", "-out", "localhost.crt.pem")
operator_token = secrets.token_urlsafe(32)
agent_token = secrets.token_urlsafe(32)
device_id = str(uuid.uuid4())
settings = {
    "ASPNETCORE_ENVIRONMENT": "Development",
    "DOTNET_ENVIRONMENT": "Development",
    "ASPNETCORE_URLS": "https://localhost:7443",
    "Kestrel__Certificates__Default__Path": str(LOCAL / "localhost.crt.pem"),
    "Kestrel__Certificates__Default__KeyPath": str(LOCAL / "localhost.key.pem"),
    "Database__Provider": "InMemory",
    "Security__OperatorTokenHash": hashlib.sha256(operator_token.encode()).hexdigest(),
    "Security__Agents__0__DeviceId": device_id,
    "Security__Agents__0__TokenHash": hashlib.sha256(agent_token.encode()).hexdigest(),
    "Agent__ServerUrl": "https://localhost:7443/",
    "Agent__DeviceId": device_id,
    "Agent__Token": agent_token,
    "Agent__TrustedCaPath": str(LOCAL / "ca.crt.pem"),
}
(LOCAL / "operator-token").write_text(operator_token + "\n")
(LOCAL / "device-id").write_text(device_id + "\n")
(LOCAL / "dev.env").write_text("# Local secrets. Never commit or share this file.\n" + "".join(f"export {k}={shlex.quote(v)}\n" for k, v in settings.items()))
print("Development configuration generated. Run: source .local/dev.env")
print("Operator key: cat .local/operator-token")
print("Certificates expire in 30 days. Development CA is not installed into system trust.")
