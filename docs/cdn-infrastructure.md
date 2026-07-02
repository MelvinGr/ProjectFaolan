# AoC Content CDN — infrastructure fingerprint

Reconnaissance of the live Age of Conan content-distribution ("UPM") hosts, captured **2026-07-02**.
Complements `docs/` CDN notes and the agent memory `aoc-content-cdn-mirror` (host roles + the
`Faolan.Web.Content` store-and-forward mirror). All probing was header/DNS fingerprinting of public hosts.

## Hosts (from `<AoC install>/LocalConfig.xml`)

| Host | LocalConfig key | Role | Path form |
|---|---|---|---|
| `aoc-eu-gameupdate.live.ageofconan.com` | `<ClientHttpPatchAddr>` | on-demand RDB resources (loading-hang / sweep path) | `/upm/rdb/res/<h[0:2]>/<h[2:3]>/<h[3:]>` |
| `aoc-eu-update.live.ageofconan.com` | `<HttpPatchAddr>` | patcher client files + bulk RDB packs | `/upm/client/<md5>`, `/upm/rdb/pack/<md5>` |
| `aoc-eu-control.live.ageofconan.com` | `<ControlHttpPatchAddr>` | manifests / project config | `/<folder>/LocalConfig.xml`, etc. |

The path `<md5>` is the hash of the **uncompressed** resource (the 16-byte content hash in `RDB/le.idx`), not of
the served (compressed) blob.

## Topology

- **`gameupdate` + `update`** are fronted by **Akamai**:
  - DNS → CNAME `a1705.dscd.akamai.net` (Akamai Download Delivery / SureRoute — the `dscd` cluster).
  - IPv4 e.g. `104.109.143.94`; IPv6 e.g. `2a02:26f0:1180:71::210:6a08/6a0a`.
  - Edge server: **`AkamaiGHost/22.5.4-b620ca40a6c21d08122eeb20d0541579`** (`*.deploy.akamaitechnologies.com`).
  - Two-tier delivery: edge `X-Cache` + parent `X-Cache-Remote` (Akamai tiered distribution).
- **`control`** skips Akamai — served directly from **Funcom's own box**:
  - DNS → `cdn-web-nj4.funcom.com` = `37.18.192.154` / `37.18.192.155`.

## Origin

All three report the same origin server header:

```
Server: Apache/2.2.15 (CentOS)
```

Apache **2.2.15** shipped **March 2010** (CentOS 6 era), long EOL — a frozen legacy origin, consistent with a
pinned content revision (5.01.0 / rev 602248) and no active development.

## Behaviour

- Root `/` → **HTTP 200** with a deliberate placeholder page (no directory index):
  ```html
  <title>Nothing here, move along citizen</title>
  ```
  Content is only fetchable by exact hash path (directory listing is 403).
- Blobs are edge-cacheable and immutable (content-addressed), which is why the store-and-forward mirror works —
  Akamai treats each `<md5>` as a cacheable object.

### Akamai edge-debug headers (via `Pragma: akamai-x-cache-on, akamai-x-get-cache-key, …`)

Probe of `…/upm/rdb/res/00/0/nonexistent` (404):

```
HTTP/1.1 404 Not Found
Server: Apache/2.2.15 (CentOS)
X-Akamai-Request-ID: 128f1e98.4375b424
X-Cache: TCP_MISS from a2-23-99-202.deploy.akamaitechnologies.com (AkamaiGHost/22.5.4-...) (-)
X-Cache-Remote: TCP_MISS from a96-16-251-132.deploy.akamaitechnologies.com (AkamaiGHost/22.5.4-...) (-)
X-Cache-Key: S/L/1705/1557746/1d/aoc-eu-gameupdate.live.ageofconan.com/upm/rdb/res/00/0/nonexistent
X-True-Cache-Key: /L/aoc-eu-gameupdate.live.ageofconan.com/upm/rdb/res/00/0/nonexistent vcd=21965
X-Check-Cacheable: YES
```

Akamai config slot **`1705`**, CP code **`1557746`** front this property.

## Summary

**Akamai edge (AkamaiGHost 22.5.4) in front of a legacy Apache 2.2.15 / CentOS origin** for game + client
content (`gameupdate`, `update`); the control/manifest host (`control`) is served straight from Funcom's own
`cdn-web-nj4.funcom.com` (`37.18.192.0/24`).
