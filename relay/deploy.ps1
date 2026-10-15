# Puts the Watch Together relay (party-relay.py) on a server, or puts it back after a reinstall. Safe to run again.
#
#   .\relay\deploy.ps1 -Server root@213.165.47.51 -Domain party-pylon.hubcore.org   # what runs now (HTTPS through nginx)
#   .\relay\deploy.ps1 -Server root@1.2.3.4                                          # plain HTTP on :8790, no nginx, no domain
#   .\relay\deploy.ps1 -Server root@... -Check                                       # only look, change nothing
#   .\relay\deploy.ps1 -Server root@... -Remove                                      # take it all off again
#   add -Key ~\.ssh\some_key for a key; without one ssh asks for the password (once: everything goes over one connection)
#
# With -Domain (the live setup on vps-usa, 2026-10-16):
#   - Cloudflare DNS: an A record <domain> -> the server's IPv4, DNS only (grey cloud), made or moved with the API token
#     certbot already uses on the server (/root/.secrets/cloudflare.ini, dns_cloudflare_api_token)
#   - certbot certonly --dns-cloudflare for <domain>, renew_hook = systemctl reload nginx
#   - nginx: the server's stream block on 443 routes by SNI (map $ssl_preread_server_name $sni_upstream, the VPN's front
#     door). One line "<domain> 127.0.0.1:18445;" goes into that map, and a site on 127.0.0.1:18445 proxies to the relay.
#     nginx.conf is backed up first; if "nginx -t" fails everything is put back and nginx is not reloaded.
#     Without that map (a fresh server before the VPN is set up) the script stops instead of making one.
#   - the relay listens on 127.0.0.1 only; the service is capped (half a core, 256 MB) so the VPN keeps its share.
# In Pylon: Settings -> Privacy -> Watch Together server = https://<domain>  (or http://<ip>:<port> without a domain)
param(
    [Parameter(Mandatory)] [string]$Server,
    [string]$Domain,
    [int]$Port = 8790,
    [int]$TlsPort = 18445,
    [string]$Key,
    [switch]$Check,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$ssh = @('-o', 'ConnectTimeout=10', '-o', 'StrictHostKeyChecking=accept-new')
if ($Key) { $ssh += @('-i', $Key, '-o', 'BatchMode=yes') }

# runs a bash script on the server (one connection, so one password prompt); the settings go in front as shell variables,
# the script itself is never expanded here
function Remote([string]$script) {
    $vars = "PORT='$Port'; TLSPORT='$TlsPort'; DOMAIN='$Domain'`n"
    $out = ($vars + $script) -replace "`r`n", "`n" | ssh @ssh $Server 'bash -s' 2>&1
    $code = $LASTEXITCODE
    $out | ForEach-Object { Write-Host "  $_" }
    if ($code) { throw "the server stopped with exit code $code (see above)" }
}

$look = @'
set -e
command -v python3 >/dev/null || { echo 'python3 is missing: apt install -y python3'; exit 2; }
python3 -c 'import sys; assert sys.version_info >= (3, 8)' || { echo 'python3 is older than 3.8'; exit 2; }
echo "python: $(python3 --version)"
holder=$(ss -tlnp "( sport = :$PORT )" | tail -n +2)
if [ -n "$holder" ] && ! systemctl is-active -q pylon-relay; then echo "port $PORT is taken by something else (pick another with -Port):"; echo "$holder"; exit 3; fi
echo "relay port $PORT: $([ -n "$holder" ] && echo 'ours' || echo free)"
echo "service: $(systemctl is-active pylon-relay 2>/dev/null || true)"
if [ -n "$DOMAIN" ]; then
  grep -q 'map $ssl_preread_server_name $sni_upstream' /etc/nginx/nginx.conf || { echo 'nginx has no SNI map on 443 (the VPN front door): set that up first'; exit 4; }
  [ -f /root/.secrets/cloudflare.ini ] || { echo '/root/.secrets/cloudflare.ini (dns_cloudflare_api_token) is missing'; exit 4; }
  holder=$(ss -tlnp "( sport = :$TLSPORT )" | tail -n +2)
  if [ -n "$holder" ] && ! grep -q "$TLSPORT" /etc/nginx/sites-available/pylon-relay 2>/dev/null; then echo "port $TLSPORT is taken (pick another with -TlsPort):"; echo "$holder"; exit 3; fi
  echo "domain $DOMAIN -> $(getent ahostsv4 "$DOMAIN" | awk 'NR==1{print $1}'), server $(curl -s -4 --max-time 5 ifconfig.me)"
fi
'@
Write-Host "== $Server"
if ($Check) { Remote $look; return }

if ($Remove) {
    Remote @'
systemctl disable --now pylon-relay 2>/dev/null || true
rm -f /etc/systemd/system/pylon-relay.service; rm -rf /opt/pylon-relay; systemctl daemon-reload
if [ -L /etc/nginx/sites-enabled/pylon-relay ] || grep -q "127.0.0.1:$TLSPORT;" /etc/nginx/nginx.conf; then
  cp -a /etc/nginx/nginx.conf /etc/nginx/nginx.conf.bak-pylon-remove-$(date +%Y%m%d-%H%M%S)
  sed -i "/127\.0\.0\.1:$TLSPORT;/d" /etc/nginx/nginx.conf
  rm -f /etc/nginx/sites-enabled/pylon-relay /etc/nginx/sites-available/pylon-relay
  nginx -t && systemctl reload nginx
fi
echo 'removed (the certificate and the DNS record stay)'
'@
    return
}

$code = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'party-relay.py')) -replace "`r`n", "`n"
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($code))
$relay = "B64='$b64'`n" + @'
set -e
mkdir -p /opt/pylon-relay
echo "$B64" | base64 -d > /opt/pylon-relay/party-relay.py
chmod 644 /opt/pylon-relay/party-relay.py
LISTEN=$([ -n "$DOMAIN" ] && echo 127.0.0.1 || echo 0.0.0.0)
cat > /etc/systemd/system/pylon-relay.service <<UNIT
[Unit]
Description=Pylon Watch Together relay
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=/usr/bin/python3 /opt/pylon-relay/party-relay.py --host $LISTEN --port $PORT
Restart=always
RestartSec=3
DynamicUser=yes
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
CPUQuota=50%
MemoryMax=256M

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable pylon-relay >/dev/null 2>&1
systemctl restart pylon-relay
sleep 2
echo "service: $(systemctl is-active pylon-relay), $(curl -s --max-time 5 http://127.0.0.1:$PORT/r/check/api/state)"
'@

$https = @'
set -e
IP=$(curl -s -4 --max-time 5 ifconfig.me)
TOKEN=$(awk -F' *= *' '/dns_cloudflare_api_token/{print $2}' /root/.secrets/cloudflare.ini)
API=https://api.cloudflare.com/client/v4
ZONENAME=$(echo "$DOMAIN" | awk -F. '{print $(NF-1)"."$NF}')
ZONE=$(curl -s -H "Authorization: Bearer $TOKEN" "$API/zones?name=$ZONENAME" | python3 -c 'import sys,json;print(json.load(sys.stdin)["result"][0]["id"])')
REC=$(curl -s -H "Authorization: Bearer $TOKEN" "$API/zones/$ZONE/dns_records?type=A&name=$DOMAIN" | python3 -c 'import sys,json;r=json.load(sys.stdin)["result"];print(r[0]["id"]+" "+r[0]["content"] if r else "")')
BODY="{\"type\":\"A\",\"name\":\"$DOMAIN\",\"content\":\"$IP\",\"ttl\":300,\"proxied\":false,\"comment\":\"Pylon Watch Together relay\"}"
if [ -z "$REC" ]; then
  curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" "$API/zones/$ZONE/dns_records" --data "$BODY" | python3 -c 'import sys,json;r=json.load(sys.stdin);print("dns: created" if r["success"] else r["errors"])'
elif [ "${REC#* }" != "$IP" ]; then
  curl -s -X PUT -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" "$API/zones/$ZONE/dns_records/${REC%% *}" --data "$BODY" | python3 -c 'import sys,json;r=json.load(sys.stdin);print("dns: moved to this server" if r["success"] else r["errors"])'
else echo "dns: already $IP"; fi

if [ ! -f "/etc/letsencrypt/live/$DOMAIN/fullchain.pem" ]; then
  certbot certonly --non-interactive --agree-tos --register-unsafely-without-email --dns-cloudflare --dns-cloudflare-credentials /root/.secrets/cloudflare.ini --dns-cloudflare-propagation-seconds 45 -d "$DOMAIN" | tail -2
fi
F=/etc/letsencrypt/renewal/$DOMAIN.conf
grep -q '^renew_hook' "$F" || sed -i '/^\[renewalparams\]/a renew_hook = systemctl reload nginx' "$F"
echo "certificate: $(openssl x509 -noout -enddate -in /etc/letsencrypt/live/$DOMAIN/fullchain.pem)"

cat > /etc/nginx/sites-available/pylon-relay <<SITE
# Pylon Watch Together relay ($DOMAIN). Reached through the SNI map in nginx.conf's stream block on 443;
# the relay itself (pylon-relay.service) listens on 127.0.0.1:$PORT only.
server {
    listen 127.0.0.1:$TLSPORT ssl;
    server_name $DOMAIN;
    ssl_certificate     /etc/letsencrypt/live/$DOMAIN/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/$DOMAIN/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;

    location / {
        proxy_pass http://127.0.0.1:$PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-Host \$host;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header Connection "";
        # the host's upload and the guests' stream are endless: pass them through as they come
        proxy_buffering off;
        proxy_request_buffering off;
        client_max_body_size 0;
        proxy_read_timeout 1d;
        proxy_send_timeout 1d;
    }
}
SITE
BAK=/etc/nginx/nginx.conf.bak-pylon-$(date +%Y%m%d-%H%M%S)
cp -a /etc/nginx/nginx.conf "$BAK"
if ! grep -q "^[[:space:]]*$DOMAIN[[:space:]]" /etc/nginx/nginx.conf; then
  sed -i "/map \$ssl_preread_server_name \$sni_upstream {/a\        $DOMAIN    127.0.0.1:$TLSPORT;" /etc/nginx/nginx.conf
fi
ln -sfn /etc/nginx/sites-available/pylon-relay /etc/nginx/sites-enabled/pylon-relay
if nginx -t >/dev/null 2>&1; then
  systemctl reload nginx
  echo "nginx: reloaded (backup $BAK)"
else
  nginx -t 2>&1 | tail -3
  cp -a "$BAK" /etc/nginx/nginx.conf; rm -f /etc/nginx/sites-enabled/pylon-relay
  echo 'nginx: the check failed, everything put back, not reloaded'; exit 5
fi
sleep 1
echo "https: $(curl -s --max-time 8 --resolve $DOMAIN:443:127.0.0.1 https://$DOMAIN/r/check/api/state)"
'@

# each part in its own subshell: "set -e" in one stops that part, and a failed part stops the rest
Remote ("( $look`n) || exit `$?`n( $relay`n) || exit `$?`n" + $(if ($Domain) { "( $https`n) || exit `$?`n" } else { '' }))
if ($Domain) {
    Write-Host "`nDone. In Pylon: Watch Together server = https://$Domain"
} else {
    $ip = ($Server -split '@')[-1]
    Write-Host "`nDone. In Pylon: Watch Together server = http://${ip}:$Port"
}
