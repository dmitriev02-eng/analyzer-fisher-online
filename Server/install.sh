#!/bin/bash
# SERVER-01 install: AFO data server behind nginx on 8443 (self-signed TLS, pinned in the client). Idempotent.
set -euo pipefail
IP=155.212.244.164
id afo >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin afo
install -d -o root -g root -m 755 /opt/afo /opt/afo/ref
install -d -o afo -g afo -m 750 /var/lib/afo
install -d -o root -g root -m 700 /var/backups/afo
install -m 644 /tmp/afo_upload/afo_server.py /opt/afo/afo_server.py
install -m 644 /tmp/afo_upload/fish_limits.json /tmp/afo_upload/waterbodies.json /opt/afo/ref/

cat > /etc/systemd/system/afo.service <<'EOF'
[Unit]
Description=Analyzer Fisher Online data server
After=network.target
[Service]
User=afo
Group=afo
Environment=AFO_DB=/var/lib/afo/afo.db AFO_REF=/opt/afo/ref AFO_PORT=8090
ExecStart=/usr/bin/python3 /opt/afo/afo_server.py
Restart=on-failure
RestartSec=3
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
ReadWritePaths=/var/lib/afo
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
RestrictNamespaces=yes
LockPersonality=yes
MemoryDenyWriteExecute=yes
CapabilityBoundingSet=
SystemCallArchitectures=native
MemoryMax=300M
[Install]
WantedBy=multi-user.target
EOF

if [ ! -f /etc/nginx/afo/afo.crt ]; then
  install -d -m 700 /etc/nginx/afo
  openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 3650 \
    -subj "/CN=$IP" -addext "subjectAltName=IP:$IP" -keyout /etc/nginx/afo/afo.key -out /etc/nginx/afo/afo.crt 2>/dev/null
  chmod 600 /etc/nginx/afo/afo.key
fi

cat > /etc/nginx/conf.d/afo.conf <<'EOF'
# AFO data server (SERVER-01). No IP addresses in logs.
limit_req_zone $binary_remote_addr zone=afo_submit:10m rate=30r/m;
limit_req_zone $binary_remote_addr zone=afo_reg:10m rate=1r/m;
limit_req_zone $binary_remote_addr zone=afo_read:10m rate=60r/m;
limit_conn_zone $binary_remote_addr zone=afo_conn:10m;
log_format afo_noip '$time_iso8601 "$request" $status $body_bytes_sent $request_time';
server {
    listen 8443 ssl;
    server_name _;
    ssl_certificate     /etc/nginx/afo/afo.crt;
    ssl_certificate_key /etc/nginx/afo/afo.key;
    ssl_protocols TLSv1.2 TLSv1.3;
    access_log /var/log/nginx/afo_access.log afo_noip;
    client_max_body_size 256k;
    client_body_timeout 10s;
    client_header_timeout 10s;
    keepalive_timeout 15s;
    limit_conn afo_conn 10;
    server_tokens off;
    location = /api/v1/register { limit_req zone=afo_reg burst=5 nodelay; proxy_pass http://127.0.0.1:8090; }
    location = /api/v1/submit   { limit_req zone=afo_submit burst=10 nodelay; proxy_pass http://127.0.0.1:8090; }
    location /api/v1/           { limit_req zone=afo_read burst=20 nodelay; limit_except GET POST { deny all; } proxy_pass http://127.0.0.1:8090; }
    location / { return 404; }
}
EOF

cat > /etc/cron.daily/afo-backup <<'EOF'
#!/bin/sh
python3 -c "import sqlite3;s=sqlite3.connect('/var/lib/afo/afo.db');d=sqlite3.connect('/var/backups/afo/afo-$(date +%F).db');s.backup(d)" 2>/dev/null
find /var/backups/afo -name 'afo-*.db' -mtime +14 -delete
find /var/log/nginx -name 'afo_access.log.*' -mtime +7 -delete
EOF
chmod 755 /etc/cron.daily/afo-backup

ufw allow 8443/tcp comment 'AFO data server' >/dev/null
systemctl daemon-reload
systemctl enable afo >/dev/null 2>&1
systemctl restart afo
nginx -t 2>&1 | tail -1
systemctl reload nginx
sleep 1
curl -sk https://127.0.0.1:8443/api/v1/health; echo
echo "PIN sha256/spki: $(openssl x509 -in /etc/nginx/afo/afo.crt -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | base64)"
systemctl is-active afo
