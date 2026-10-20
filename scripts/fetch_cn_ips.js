const https = require('https');
const fs = require('fs');

function fetch(url) {
  return new Promise((resolve, reject) => {
    const mod = url.startsWith('https') ? https : require('http');
    mod.get(url, { headers: { 'User-Agent': 'Mozilla/5.0' }, timeout: 30000 }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        return fetch(res.headers.location).then(resolve, reject);
      }
      let data = '';
      res.on('data', c => data += c);
      res.on('end', () => resolve(data));
    }).on('error', reject);
  });
}

function ipToInt(ip) {
  return ip.split('.').reduce((a, b) => (a << 8) + parseInt(b), 0) >>> 0;
}

function intToIp(n) {
  return [(n >>> 24) & 0xFF, (n >>> 16) & 0xFF, (n >>> 8) & 0xFF, n & 0xFF].join('.');
}

function* rangeToCidrs(start, end) {
  while (start <= end) {
    let maxBits = 32;
    while (maxBits > 0) {
      const mask = (0xFFFFFFFF << (32 - maxBits)) >>> 0;
      if ((start & mask) >>> 0 !== start) { maxBits--; continue; }
      const blockEnd = (start | (~mask >>> 0)) >>> 0;
      if (blockEnd > end || blockEnd < start) { maxBits--; continue; }
      break;
    }
    yield `${intToIp(start)}/${maxBits}`;
    const step = (1 << (32 - maxBits));
    const next = start + step;
    if (next <= start || next > 0xFFFFFFFF) break;
    start = next;
  }
}

async function main() {
  const url = 'https://raw.githubusercontent.com/17mon/china_ip_list/master/china_ip_list.txt';
  console.error(`Fetching CN IP list...`);
  const cnData = await fetch(url);
  const cnCidrs = cnData.trim().split('\n').filter(l => l.includes('/'));
  console.error(`CN CIDRs: ${cnCidrs.length}`);

  const cnRanges = cnCidrs.map(cidr => {
    const [ip, prefix] = cidr.split('/');
    const start = ipToInt(ip);
    const size = Math.pow(2, 32 - parseInt(prefix));
    return [start, start + size - 1];
  }).sort((a, b) => a[0] - b[0]);

  const merged = [cnRanges[0].slice()];
  for (let i = 1; i < cnRanges.length; i++) {
    const last = merged[merged.length - 1];
    if (cnRanges[i][0] <= last[1] + 1) {
      last[1] = Math.max(last[1], cnRanges[i][1]);
    } else {
      merged.push(cnRanges[i].slice());
    }
  }
  console.error(`Merged CN ranges: ${merged.length}`);

  const privateRanges = [
    [ipToInt('10.0.0.0'), ipToInt('10.255.255.255')],
    [ipToInt('172.16.0.0'), ipToInt('172.31.255.255')],
    [ipToInt('192.168.0.0'), ipToInt('192.168.255.255')],
    [ipToInt('127.0.0.0'), ipToInt('127.255.255.255')],
    [ipToInt('169.254.0.0'), ipToInt('169.254.255.255')],
    [ipToInt('100.64.0.0'), ipToInt('100.127.255.255')],
    [ipToInt('224.0.0.0'), ipToInt('239.255.255.255')],
    [ipToInt('240.0.0.0'), ipToInt('255.255.255.254')],
  ];

  const allExclude = [...merged, ...privateRanges].sort((a, b) => a[0] - b[0]);
  const excMerged = [allExclude[0].slice()];
  for (let i = 1; i < allExclude.length; i++) {
    const last = excMerged[excMerged.length - 1];
    if (allExclude[i][0] <= last[1] + 1) {
      last[1] = Math.max(last[1], allExclude[i][1]);
    } else {
      excMerged.push(allExclude[i].slice());
    }
  }
  console.error(`Exclude ranges: ${excMerged.length}`);

  const out = fs.createWriteStream('/tmp/foreign_ips.txt');
  let count = 0;
  let cursor = 0;

  for (const [start, end] of excMerged) {
    if (cursor < start) {
      for (const cidr of rangeToCidrs(cursor, start - 1)) {
        out.write(cidr + '\n');
        count++;
      }
    }
    cursor = Math.max(cursor, end + 1);
    if (cursor === 0) break;
  }
  if (cursor <= 0xFFFFFFFF && cursor > 0) {
    for (const cidr of rangeToCidrs(cursor, 0xFFFFFFFF)) {
      out.write(cidr + '\n');
      count++;
    }
  }

  out.end();
  console.error(`Foreign CIDRs: ${count}`);
}

main().catch(e => { console.error(e); process.exit(1); });
