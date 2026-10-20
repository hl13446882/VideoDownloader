import sys

def ip_to_int(ip):
    parts = ip.split('.')
    return (int(parts[0]) << 24) + (int(parts[1]) << 16) + (int(parts[2]) << 8) + int(parts[3])

def int_to_ip(n):
    return f"{(n >> 24) & 0xFF}.{(n >> 16) & 0xFF}.{(n >> 8) & 0xFF}.{n & 0xFF}"

def range_to_cidrs(start, end):
    cidrs = []
    while start <= end:
        if start == 0:
            max_host_bits = 32
        else:
            max_host_bits = (start & -start).bit_length() - 1
        gap = end - start + 1
        host_bits = max_host_bits
        while (1 << host_bits) > gap:
            host_bits -= 1
        prefix = 32 - host_bits
        block_size = 1 << host_bits
        cidrs.append(f"{int_to_ip(start)}/{prefix}")
        start += block_size
        if start > 0xFFFFFFFF:
            break
    return cidrs

cn_ranges = []
with open('/tmp/cn_ips.txt') as f:
    for line in f:
        line = line.strip()
        if '/' not in line:
            continue
        ip, prefix = line.split('/')
        start = ip_to_int(ip)
        size = 2 ** (32 - int(prefix))
        cn_ranges.append((start, start + size - 1))

cn_ranges.sort()
merged = [list(cn_ranges[0])]
for s, e in cn_ranges[1:]:
    if s <= merged[-1][1] + 1:
        merged[-1][1] = max(merged[-1][1], e)
    else:
        merged.append([s, e])

print(f"Merged CN ranges: {len(merged)}", file=sys.stderr)

private_ranges = [
    (ip_to_int('10.0.0.0'), ip_to_int('10.255.255.255')),
    (ip_to_int('172.16.0.0'), ip_to_int('172.31.255.255')),
    (ip_to_int('192.168.0.0'), ip_to_int('192.168.255.255')),
    (ip_to_int('127.0.0.0'), ip_to_int('127.255.255.255')),
    (ip_to_int('169.254.0.0'), ip_to_int('169.254.255.255')),
    (ip_to_int('100.64.0.0'), ip_to_int('100.127.255.255')),
    (ip_to_int('224.0.0.0'), ip_to_int('239.255.255.255')),
    (ip_to_int('240.0.0.0'), ip_to_int('255.255.255.254')),
]

all_exclude = sorted(merged + [list(r) for r in private_ranges], key=lambda x: x[0])
exc_merged = [list(all_exclude[0])]
for s, e in all_exclude[1:]:
    if s <= exc_merged[-1][1] + 1:
        exc_merged[-1][1] = max(exc_merged[-1][1], e)
    else:
        exc_merged.append([s, e])

print(f"Exclude ranges: {len(exc_merged)}", file=sys.stderr)

count = 0
cursor = 0
with open('/tmp/foreign_ips.txt', 'w') as out:
    for s, e in exc_merged:
        if cursor < s:
            for cidr in range_to_cidrs(cursor, s - 1):
                out.write(cidr + '\n')
                count += 1
        cursor = max(cursor, e + 1)
        if cursor > 0xFFFFFFFF:
            break
    if cursor <= 0xFFFFFFFF:
        for cidr in range_to_cidrs(cursor, 0xFFFFFFFF):
            out.write(cidr + '\n')
            count += 1

print(f"Foreign CIDRs: {count}", file=sys.stderr)
