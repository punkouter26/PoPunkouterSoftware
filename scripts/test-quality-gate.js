import fs from 'fs';
import path from 'path';

const resultsDir = path.resolve('TESTRESULTS');
const maxAllowedFailures = Number(process.env.MAX_ALLOWED_E2E_FAILURES || 0);
const allowRegressionIncrease = Number(process.env.ALLOW_FAILURE_INCREASE || 0);

function readResult(filePath) {
  const raw = fs.readFileSync(filePath, 'utf8');
  const doc = JSON.parse(raw);
  const e2e = (doc.tiers || []).find((tier) => String(tier.tier).startsWith('E2E'));
  return {
    file: path.basename(filePath),
    timestamp: doc.timestamp,
    passed: Number(e2e?.passed || 0),
    failed: Number(e2e?.failed || 0),
  };
}

if (!fs.existsSync(resultsDir)) {
  console.error('TESTRESULTS directory not found');
  process.exit(1);
}

const files = fs
  .readdirSync(resultsDir)
  .filter((name) => /^test-results-\d{8}-\d{6}\.json$/.test(name))
  .sort();

if (files.length === 0) {
  console.error('No test-results-YYYYMMDD-HHMMSS.json files found in TESTRESULTS');
  process.exit(1);
}

const latest = readResult(path.join(resultsDir, files[files.length - 1]));
const previous = files.length > 1 ? readResult(path.join(resultsDir, files[files.length - 2])) : null;

const checks = [];
checks.push({
  name: 'Latest E2E failures under threshold',
  pass: latest.failed <= maxAllowedFailures,
  details: `latest.failed=${latest.failed}, maxAllowedFailures=${maxAllowedFailures}`,
});

if (previous) {
  const delta = latest.failed - previous.failed;
  checks.push({
    name: 'Failure count regression check',
    pass: delta <= allowRegressionIncrease,
    details: `delta=${delta}, allowRegressionIncrease=${allowRegressionIncrease}`,
  });
}

let allPassed = true;
for (const check of checks) {
  const status = check.pass ? 'PASS' : 'FAIL';
  console.log(`${status}: ${check.name} (${check.details})`);
  if (!check.pass) {
    allPassed = false;
  }
}

console.log(`Latest result: ${latest.file} | passed=${latest.passed} failed=${latest.failed}`);
if (previous) {
  console.log(`Previous result: ${previous.file} | passed=${previous.passed} failed=${previous.failed}`);
}

if (!allPassed) {
  process.exit(1);
}
