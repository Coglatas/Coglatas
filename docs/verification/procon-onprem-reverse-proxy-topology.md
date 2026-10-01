# Procon on-prem reverse-proxy topology

This is the supported generic on-prem contract for Issue #467. It is not a
claim that a particular operator host, certificate, tunnel, or DNS record has
already been verified.

## Canonical route

```text
Internet
  -> operator-provided external TLS proxy / tunnel
  -> host loopback or explicitly documented private origin port
  -> AIPsite Compose app
  -> PostgreSQL on the Compose-only network
```

The external proxy owns certificates, TLS renewal, public DNS, and firewall
policy. The Compose project does not own a certificate and is not a public HTTP
listener. `deploy/onprem/compose.yml` maps the application only to
`127.0.0.1:${AIP_PORTAL_PORT:-8080}` by default; PostgreSQL has no host port.

## Required operator configuration

Before exposing the service publicly, set:

```bash
AIP_PORTAL_BIND_ADDRESS=127.0.0.1
AIP_PORTAL_PORT=8080
REVERSE_PROXY_TRUST_FORWARDED_HEADERS=true
REVERSE_PROXY_TRUSTED_PROXIES=<comma-delimited immediate proxy peer IPs>
# or
REVERSE_PROXY_TRUSTED_NETWORKS=<comma-delimited dedicated proxy CIDRs>
```

The trusted address is the immediate peer seen by the app container, not an
end-user address, a public DNS record, or an upstream CDN range. For a
host-side proxy forwarding through Docker, inspect the actual gateway/source
address after deployment. Do not use `0.0.0.0/0`, `::/0`, public-client ranges,
or a hostname. The app rejects unspecified proxy addresses, all-address CIDRs,
non-IP proxy entries, and non-CIDR network entries at startup.

## Required proxy behavior

- Terminate TLS and redirect public HTTP to HTTPS at the operator proxy.
- Preserve the public host and send one consistent
  `X-Forwarded-For`, `X-Forwarded-Proto: https`, and `X-Forwarded-Host` hop.
- Route the normal health check through
  `https://<public-host>/health/ready`.
- Do not expose the loopback origin through a public NAT, firewall rule, or
  second direct listener.

The application accepts forwarded headers only from an explicit immediate
proxy boundary, processes one symmetric hop, and otherwise uses the direct
connection values. Direct public clients therefore cannot spoof the HTTPS
scheme, host, or client IP through forwarded headers.

## Verification procedure

1. Render the Compose configuration and inspect the origin binding:

   ```bash
   DB_PASSWORD='<strong secret>' docker compose -f deploy/onprem/compose.yml config
   ss -ltn | grep ':8080'
   ```

   The application mapping must show loopback (or the separately documented
   private interface), never an all-interface production origin.

2. Start a clean Compose stack with the deployment build secret and wait for
   the one-shot `migrate` service to succeed. Verify the internal readiness
   health check reports healthy.

3. From outside the host, verify the public TLS route:

   ```bash
   curl --fail --show-error --location https://<public-host>/health/ready
   ```

4. Authenticate through the proxy and verify a secure auth/CSRF cookie is
   issued. A direct public HTTP attempt to the app origin must be impossible;
   a host-local HTTP request is intentionally not a public deployment route.

The repository test suite verifies the configuration parser and a real Kestrel
request with `Security:CookieSecurePolicy=Always` through an explicitly trusted
forwarded HTTPS boundary. A target-host run remains an operator evidence item
because it requires a real certificate, DNS/tunnel, and host firewall.

## Sakura compatibility

`deploy/sakura/docker-compose.yml` remains a conforming implementation: Caddy
is the TLS proxy, the application is internal-only to its Compose network, and
the proxy network is declared as the forwarded-header trust boundary. This is
an implementation of the vendor-neutral contract, not an application
dependency on Caddy or Cloudflare APIs.
