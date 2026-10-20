import urllib.request
import ipaddress
import sys

def ip_to_int(ip):
    return int(ipaddress.IPv4Address(ip))

def int_to_ip(n):
    return ipaddress.IPv4Address(n)

def range_to_cidrs(start, end):
    cidrs = []
    while start <= end:
        max_size = 32
        while max_size > 0:
            mask = (0xFFFFFFFF << (32 - max_size)) & 0xFFFFFFFF
            block_start = start & mask
            if block_start != start:
                max_size -= 1
                continue
            block_end = start | (~mask & 0xFFFFFFFF)
            if block_end > end:
                max_size -= 1
                continue
            break
        cidrs.append(f"{int_to_ip(start)}/{max_size}")
        start += (1 << (32 - max_size))
    return cidrs

def fetch_cn_ips():
    url = "https://ftp.apnic.net/stats/apnic/delegated-apnic-extended-latest"
    print("Fetching APNIC data...", file=sys.stderr)
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=30) as resp:
        data = resp.read().decode("utf-8")

    cn_ranges = []
    for line in data.splitlines():
        parts = line.split("|")
        if len(parts) < 5:
            continue
        if parts[1] == "CN" and parts[2] == "ipv4":
            start = ip_to_int(parts[3])
            count = int(parts[4])
            cn_ranges.append((start, start + count - 1))

    cn_ranges.sort()
    merged = [cn_ranges[0]]
    for s, e in cn_ranges[1:]:
        if s <= merged[-1][1] + 1:
            merged[-1] = (merged[-1][0], max(merged[-1][1], e))
        else:
            merged.append((s, e))

    print(f"Merged to {len(merged)} CN ranges", file=sys.stderr)
    return merged

private_ranges = [
    (ip_to_int("10.0.0.0"), ip_to_int("10.255.255.255")),
    (ip_to_int("172.16.0.0"), ip_to_int("172.31.255.255")),
    (ip_to_int("192.168.0.0"), ip_to_int("192.168.255.255")),
    (ip_to_int("127.0.0.0"), ip_to_int("127.255.255.255")),
    (ip_to_int("169.254.0.0"), ip_to_int("169.254.255.255")),
    (ip_to_int("100.64.0.0"), ip_to_int("100.127.255.255")),
    (ip_to_int("224.0.0.0"), ip_to_int("239.255.255.255")),
    (ip_to_int("240.0.0.0"), ip_to_int("255.255.255.254")),
]

cn_merged = fetch_cn_ips()

exclude = sorted(cn_merged + private_ranges, key=lambda x: x[0])
exclude_merged = [exclude[0]]
for s, e in exclude[1:]:
    if s <= exclude_merged[-1][1] + 1:
        exclude_merged[-1] = (exclude_merged[-1][0], max(exclude_merged[-1][1], e))
    else:
        exclude_merged.append((s, e))

IPV4_MAX = 0xFFFFFFFF
foreign_cidrs = []
cursor = 0

for s, e in exclude_merged:
    if cursor < s:
        foreign_cidrs.extend(range_to_cidrs(cursor, s - 1))
    cursor = max(cursor, e + 1)

if cursor <= IPV4_MAX:
    foreign_cidrs.extend(range_to_cidrs(cursor, IPV4_MAX))

print(f"Generated {len(foreign_cidrs)} foreign CIDR ranges", file=sys.stderr)

for cidr in foreign_cidrs:
    print(cidr)
