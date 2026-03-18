import fs from 'fs';
import path from 'path';

const resultsDir = path.resolve('TESTRESULTS');
const outputJson = path.join(resultsDir, 'test-trend.json');
const outputMd = path.join(resultsDir, 'test-trend.md');

function getE2EPoint(filePath) {
  const raw = fs.readFileSync(filePath, 'utf8');
  const doc = JSON.parse(raw);
  const e2e = (doc.tiers || []).find((tier) => String(tier.tier).startsWith('E2E'));
  if (!e2e) {
    return null;
  }
  return {
    timestamp: doc.timestamp,
    file: path.basename(filePath),
    passed: Number(e2e.passed || 0),
    failed: Number(e2e.failed || 0),
  };
}

function bar(count, token) {
  return token.repeat(Math.max(0, count));
}

if (!fs.existsSync(resultsDir)) {
  fs.mkdirSync(resultsDir, { recursive: true });
}

const files = fs
  .readdirSync(resultsDir)
  .filter((name) => /^test-results-\d{8}-\d{6}\.json$/.test(name))
  .sort();

const points = files
  .map((name) => getE2EPoint(path.join(resultsDir, name)))
  .filter(Boolean);

const trend = {
  generatedAt: new Date().toISOString(),
  points,
};

fs.writeFileSync(outputJson, JSON.stringify(trend, null, 2));

const lines = [];
lines.push('# Test Pass/Fail Trend');
lines.push('');
lines.push('| Timestamp | Passed | Failed | Visual |');
lines.push('|---|---:|---:|---|');

for (const point of points) {
  lines.push(
    `| ${point.timestamp} | ${point.passed} | ${point.failed} | ${bar(point.passed, 'P')}${bar(point.failed, 'F')} |`
  );
}

if (points.length === 0) {
  lines.push('| n/a | 0 | 0 | |');
}

fs.writeFileSync(outputMd, `${lines.join('\n')}\n`);

console.log(`Trend points: ${points.length}`);
console.log(`Wrote ${outputJson}`);
console.log(`Wrote ${outputMd}`);
