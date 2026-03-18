/**
 * azure-spend-detail.js
 * Detailed breakdown of Azure spending over the past 7 days.
 *
 * What it does:
 *   1. Pulls all consumption usage records for the last 7 days
 *   2. If the subscription uses credits ($0 billed), falls back to quantity-based usage ranking
 *   3. Also tries az costmanagement query for enriched cost data
 *   4. Identifies the top 3 cost / usage drivers
 *   5. For each top resource: shows per-day timeline, meter breakdown (what was used),
 *      and maps back to the owning app / resource group
 *   6. Writes azure-spend-detail-report.json + prints a human-readable console summary
 *
 * Usage:
 *   npm run spend-detail
 *
 * Prerequisites:
 *   az login   (Azure CLI authenticated)
 */

import { exec } from 'child_process';
import { promisify } from 'util';
import { writeFile } from 'fs/promises';

const execAsync = promisify(exec);

// ─── Config ───────────────────────────────────────────────────────────────────

const CONFIG = {
    outputFile: 'azure-spend-detail-report.json',
    lookbackDays: 7,
    topN: 3,
};

// ─── Friendly meter category descriptions ────────────────────────────────────

const METER_DESCRIPTIONS = {
    'Compute Hours':              'CPU time consumed running app instances',
    'vCore-hours':                'vCore CPU time (container / serverless compute)',
    'Memory Duration':            'RAM allocated to running processes',
    'Data Transfer':              'Outbound network egress bandwidth',
    'Bandwidth':                  'Inbound + outbound data transfer',
    'Operations':                 'API call count (reads, writes, list operations)',
    'Data Stored':                'Data at rest in storage (GB-months)',
    'LRS Data Stored':            'Locally-redundant storage footprint',
    'Write Operations':           'Storage write requests',
    'Read Operations':            'Storage read requests',
    'Other Operations':           'Storage metadata / list operations',
    'Standard Transactions':      'Transaction count against Standard storage tier',
    'Tokens':                     'AI/OpenAI tokens consumed',
    'Standard Unit':              'Cognitive Services API calls processed',
    'Standard Calls':             'Cognitive Services HTTP API calls',
    'Transaction':                'Cognitive Services request count',
    'Key Operations':             'Key Vault cryptographic operations',
    'Secret Operations':          'Key Vault secret read / write operations',
    'Data Ingestion':             'Log Analytics or Application Insights data ingested',
    'Data Retention':             'Log Analytics extended retention',
    'vCPU Duration':              'Container Apps serverless vCPU-seconds consumed',
    'Memory (GiB) Duration':      'Container Apps serverless memory-seconds consumed',
    'Request Units':              'Cosmos DB request units consumed',
    'SQL Database':               'Azure SQL compute (DTU / vCore hours)',
    'Standard Database Days':     'SQL database daily instance charge',
    'Map Transactions':           'Azure Maps API transactions',
    'Static Transactions':        'Azure Maps static tile requests',
};

function describeMeter(meterName, meterCategory, meterSubCategory) {
    // Try exact name match first
    for (const [key, desc] of Object.entries(METER_DESCRIPTIONS)) {
        if (meterName?.toLowerCase().includes(key.toLowerCase())) return desc;
    }
    // Fall back to category description
    if (meterCategory?.toLowerCase().includes('cognitive')) return 'Azure AI / Cognitive Services usage';
    if (meterCategory?.toLowerCase().includes('storage'))   return 'Azure Storage operations and capacity';
    if (meterCategory?.toLowerCase().includes('app service')) return 'App Service compute (web app hosting)';
    if (meterCategory?.toLowerCase().includes('functions')) return 'Azure Functions execution';
    if (meterCategory?.toLowerCase().includes('container')) return 'Container compute (ACI / Container Apps)';
    if (meterCategory?.toLowerCase().includes('sql'))       return 'Azure SQL database compute / storage';
    if (meterCategory?.toLowerCase().includes('key vault')) return 'Key Vault operations';
    if (meterCategory?.toLowerCase().includes('monitor') || meterCategory?.toLowerCase().includes('insight'))
        return 'Azure Monitor / Application Insights telemetry';
    if (meterCategory?.toLowerCase().includes('bandwidth') || meterCategory?.toLowerCase().includes('network'))
        return 'Network data transfer / egress';
    return meterSubCategory || meterCategory || 'Azure service usage';
}

// ─── Resource ID → friendly name ─────────────────────────────────────────────

function friendlyName(instanceId, instanceName) {
    if (instanceName) return instanceName;
    if (!instanceId) return 'Unknown';
    // Extract the resource name from the ARM resource id
    const parts = instanceId.split('/');
    return parts[parts.length - 1] || instanceId;
}

function resourceGroupFromId(id) {
    if (!id) return null;
    const m = id.match(/resourceGroups\/([^/]+)/i);
    return m ? m[1] : null;
}

function resourceTypeFromId(id) {
    if (!id) return null;
    const m = id.match(/providers\/([^/]+\/[^/]+)/i);
    return m ? m[1] : null;
}

// ─── Azure CLI wrapper ────────────────────────────────────────────────────────

async function az(args, fallback = null) {
    try {
        const { stdout, stderr } = await execAsync(`az ${args}`, { maxBuffer: 50 * 1024 * 1024 });
        if (stderr && !stderr.toLowerCase().includes('warning')) {
            process.stderr.write(`  [az warn] ${stderr.trim().split('\n')[0]}\n`);
        }
        return JSON.parse(stdout || (fallback === null ? 'null' : JSON.stringify(fallback)));
    } catch (e) {
        if (process.env.DEBUG) process.stderr.write(`  [az err] ${e.message?.split('\n')[0]}\n`);
        return fallback;
    }
}

// ─── Date helpers ─────────────────────────────────────────────────────────────

function fmt(d) { return d.toISOString().split('T')[0]; }

function dateRange(days) {
    const end   = new Date();
    const start = new Date(); start.setDate(end.getDate() - days);
    return { start: fmt(start), end: fmt(end) };
}

// ─── Cost parsers ─────────────────────────────────────────────────────────────

function parseCost(rec) {
    const raw = rec.pretaxCost        ??
                rec.costInBillingCurrency ??
                rec.cost              ??
                rec.paygCostInBillingCurrency ??
                rec.effectivePrice    ??
                null;
    const val = parseFloat(raw);
    return isNaN(val) ? 0 : val;
}

function parseQuantity(rec) {
    const raw = rec.quantity ?? rec.usageQuantity ?? rec.consumedQuantity ?? null;
    const val = parseFloat(raw);
    return isNaN(val) ? 0 : val;
}

function parseCurrency(rec) {
    return rec.billingCurrencyCode || rec.currency || rec.billingCurrency || 'USD';
}

function parseDate(rec) {
    return (rec.usageStart || rec.date || rec.billingPeriodStartDate || '').split('T')[0];
}

// ─── Fetch consumption records ────────────────────────────────────────────────

async function fetchConsumption(start, end) {
    process.stdout.write('  Fetching az consumption records ...');
    const records = await az(
        `consumption usage list --start-date ${start} --end-date ${end} --output json`,
        []
    );
    process.stdout.write(` ${records.length} records\n`);
    return records;
}

// ─── Try Cost Management query (richer detail for some subscription types) ───

async function fetchCostMgmt(subId, start, end) {
    process.stdout.write('  Fetching az costmanagement data ...');
    const body = JSON.stringify({
        type: 'ActualCost',
        timeframe: 'Custom',
        timePeriod: { from: `${start}T00:00:00Z`, to: `${end}T23:59:59Z` },
        dataset: {
            granularity: 'Daily',
            aggregation: {
                totalCost: { name: 'PreTaxCost', function: 'Sum' },
                totalUsage: { name: 'UsageQuantity', function: 'Sum' },
            },
            grouping: [
                { type: 'Dimension', name: 'ResourceId' },
                { type: 'Dimension', name: 'ResourceType' },
                { type: 'Dimension', name: 'ResourceGroupName' },
                { type: 'Dimension', name: 'MeterCategory' },
                { type: 'Dimension', name: 'MeterSubcategory' },
            ],
        },
    });

    // Write body to a temp file to avoid shell escaping issues
    const tmp = process.env.TEMP || process.env.TMP || '.';
    const tmpFile = `${tmp}\\az-costmgmt-body.json`.replace(/\\/g, '\\\\');
    try {
        const { writeFile } = await import('fs/promises');
        await writeFile(tmpFile.replace(/\\\\/g, '\\'), JSON.stringify(JSON.parse(body), null, 2));
    } catch { /* ignore */ }

    const result = await az(
        `costmanagement query --scope /subscriptions/${subId} --type ActualCost ` +
        `--timeframe Custom --time-period from=${start}T00:00:00Z to=${end}T23:59:59Z ` +
        `--dataset-granularity Daily ` +
        `--dataset-aggregation totalCost="{name=PreTaxCost,function=Sum}" ` +
        `--dataset-grouping name=ResourceId type=Dimension name=MeterCategory type=Dimension ` +
        `--output json`,
        null
    );
    const count = result?.rows?.length ?? result?.properties?.rows?.length ?? 0;
    process.stdout.write(` ${count} rows\n`);
    return result;
}

// ─── Aggregate consumption records ───────────────────────────────────────────

function aggregateRecords(records) {
    const byResource = new Map();   // key: instanceId/instanceName
    let totalCost = 0;
    let totalQty  = 0;
    let currency  = 'USD';
    const allCostsZero = records.every(r => parseCost(r) === 0);

    for (const rec of records) {
        const cost = parseCost(rec);
        const qty  = parseQuantity(rec);
        const id   = (rec.instanceId   || rec.instanceName || 'Unknown').toLowerCase();
        const name = rec.instanceName  || friendlyName(rec.instanceId, null);
        const rg   = rec.resourceGroup || resourceGroupFromId(rec.instanceId) || 'Unknown';
        const type = rec.consumedService || resourceTypeFromId(rec.instanceId) || 'Unknown';
        const date = parseDate(rec);
        currency   = parseCurrency(rec);
        totalCost += cost;
        totalQty  += qty;

        if (!byResource.has(id)) {
            byResource.set(id, {
                id:              rec.instanceId    || id,
                name,
                resourceGroup:   rg,
                resourceType:    type,
                totalCost:       0,
                totalQty:        0,
                currency,
                meters:          new Map(),   // meterName → { cost, qty, unit, category, subCategory, desc, days: Map<date,{cost,qty}> }
            });
        }

        const entry = byResource.get(id);
        entry.totalCost += cost;
        entry.totalQty  += qty;

        const meterKey = rec.meterName || rec.meterDetails?.meterName || 'Unknown';
        if (!entry.meters.has(meterKey)) {
            entry.meters.set(meterKey, {
                name:        meterKey,
                category:    rec.meterCategory    || rec.meterDetails?.meterCategory    || '',
                subCategory: rec.meterSubCategory || rec.meterDetails?.meterSubCategory || '',
                unit:        rec.unitOfMeasure    || rec.meterDetails?.unit             || '',
                cost:        0,
                qty:         0,
                description: '',
                days:        new Map(),
            });
        }
        const meter = entry.meters.get(meterKey);
        meter.cost += cost;
        meter.qty  += qty;
        meter.description = describeMeter(meterKey, meter.category, meter.subCategory);
        if (date) {
            const prev = meter.days.get(date) || { cost: 0, qty: 0 };
            meter.days.set(date, { cost: prev.cost + cost, qty: prev.qty + qty });
        }
    }

    // Convert Maps → sorted arrays for serialisation
    const resources = [...byResource.values()].map(r => ({
        ...r,
        meters: [...r.meters.values()]
            .map(m => ({
                ...m,
                days: [...m.days.entries()]
                    .sort(([a], [b]) => a.localeCompare(b))
                    .map(([date, vals]) => ({ date, ...vals })),
            }))
            .sort((a, b) => allCostsZero ? b.qty - a.qty : b.cost - a.cost),
    }));

    // Sort by cost (or quantity if all zero)
    resources.sort((a, b) => allCostsZero ? b.totalQty - a.totalQty : b.totalCost - a.totalCost);

    return { resources, totalCost, totalQty, currency, allCostsZero, recordCount: records.length };
}

// ─── Fetch extra detail for a specific resource ───────────────────────────────

async function fetchResourceDetail(resourceId) {
    if (!resourceId || resourceId === 'Unknown') return null;
    // Try to get the resource details from ARM
    return az(`resource show --ids "${resourceId}" --output json`);
}

// ─── Print helpers ────────────────────────────────────────────────────────────

const hr = (ch = '─', len = 72) => ch.repeat(len);

function printBar(val, max, len = 20) {
    const filled = max > 0 ? Math.round((val / max) * len) : 0;
    return '█'.repeat(filled) + '░'.repeat(len - filled);
}

function fmtCost(n, currency = 'USD') {
    return `$${n.toFixed(4)} ${currency}`;
}

function fmtQty(n, unit = '') {
    return `${n.toFixed(2)}${unit ? ' ' + unit : ''}`;
}

function printTopResource(res, rank, maxCost, maxQty, allCostsZero, currency) {
    const score    = allCostsZero ? res.totalQty  : res.totalCost;
    const maxScore = allCostsZero ? maxQty        : maxCost;
    const scoreStr = allCostsZero
        ? `${fmtQty(res.totalQty)}  (quantity-ranked — subscription uses credits)`
        : fmtCost(res.totalCost, currency);

    console.log(`\n  #${rank}  ${res.name}`);
    console.log(`       Resource group : ${res.resourceGroup}`);
    console.log(`       Service type   : ${res.resourceType}`);
    console.log(`       7-day total    : ${scoreStr}`);
    console.log(`       Usage bar      : [${printBar(score, maxScore)}]`);

    if (res.meters.length > 0) {
        console.log(`\n       Meter breakdown:`);
        for (const m of res.meters) {
            const costStr = allCostsZero
                ? `qty ${fmtQty(m.qty, m.unit)}`
                : `${fmtCost(m.cost, currency)}  (qty: ${fmtQty(m.qty, m.unit)})`;
            console.log(`         • ${m.name}`);
            console.log(`           What it measures : ${m.description}`);
            console.log(`           Charged          : ${costStr}`);
            if (m.days.length > 0) {
                console.log(`           Daily timeline  :`);
                for (const d of m.days) {
                    const dayScore = allCostsZero ? d.qty : d.cost;
                    const dayMax   = allCostsZero ? m.qty  : m.cost;
                    const bar = printBar(dayScore, dayMax, 12);
                    const dayStr = allCostsZero
                        ? `qty ${fmtQty(d.qty, m.unit)}`
                        : fmtCost(d.cost, currency);
                    console.log(`             ${d.date}  [${bar}]  ${dayStr}`);
                }
            }
        }
    }
}

// ─── Main ─────────────────────────────────────────────────────────────────────

async function main() {
    console.log('\n💸  Azure 7-Day Spend Detail');
    console.log(`    ${new Date().toLocaleString()}`);
    console.log(hr('='));

    // 1. Auth
    console.log('\n[1/4] Subscription info...');
    const sub = await az('account show --output json');
    if (!sub) {
        console.error('\n❌  Not authenticated. Run `az login` first.\n');
        process.exit(1);
    }
    console.log(`  ${sub.name}  (${sub.id})`);

    const { start, end } = dateRange(CONFIG.lookbackDays);
    console.log(`  Date range : ${start}  →  ${end}  (${CONFIG.lookbackDays} days)\n`);

    // 2. Consumption records
    console.log('[2/4] Fetching usage data...');
    const records = await fetchConsumption(start, end);

    // Also try Cost Management for enriched cost rows (best-effort)
    // const cmResult = await fetchCostMgmt(sub.id, start, end);

    if (records.length === 0) {
        console.log('\n  ⚠  No consumption records returned for this period.');
        console.log('     This is normal for Visual Studio / sponsored subscriptions.');
        console.log('     Check Azure Cost Management in the portal for actual spend.\n');
        await writeFile(CONFIG.outputFile, JSON.stringify({
            subscription: sub.name,
            dateRange: { start, end },
            note: 'No consumption records available via CLI for this subscription type.',
            generatedAt: new Date().toISOString(),
        }, null, 2));
        return;
    }

    // 3. Aggregate
    console.log('\n[3/4] Aggregating...');
    const agg = aggregateRecords(records);
    console.log(`  Total records  : ${agg.recordCount}`);
    console.log(`  Total cost     : ${agg.totalCost === 0 ? '$0.00 (credits subscription)' : fmtCost(agg.totalCost, agg.currency)}`);
    console.log(`  Resources seen : ${agg.resources.length}`);
    if (agg.allCostsZero) {
        console.log(`  ℹ  All billed costs are $0 — subscription is covered by credits.`);
        console.log(`     Rankings below use raw usage QUANTITY instead of dollar cost.`);
    }

    // 4. Fetch ARM detail for top N resources
    console.log(`\n[4/4] Enriching top ${CONFIG.topN} resources with ARM detail...`);
    const top = agg.resources.slice(0, CONFIG.topN);
    const details = await Promise.all(top.map(r => fetchResourceDetail(r.id)));

    // Attach ARM detail to each top resource
    for (let i = 0; i < top.length; i++) {
        const detail = details[i];
        if (detail) {
            top[i].armDetail = {
                kind:     detail.kind    || null,
                sku:      detail.sku     || null,
                location: detail.location || null,
                state:    detail.properties?.state
                       || detail.properties?.provisioningState
                       || null,
                url:      detail.properties?.defaultHostName
                        ? `https://${detail.properties.defaultHostName}` : null,
            };
        }
    }

    // ── Print report ──────────────────────────────────────────────────────────

    console.log('\n');
    console.log(hr('='));
    console.log('  7-DAY SPEND SUMMARY');
    console.log(hr('='));
    console.log(`  Subscription : ${sub.name}  (${sub.id})`);
    console.log(`  Period       : ${start}  →  ${end}`);
    console.log(`  Total cost   : ${agg.totalCost === 0
        ? '$0.00  (subscription credits cover all charges)'
        : fmtCost(agg.totalCost, agg.currency)}`);
    console.log(`  Records      : ${agg.recordCount}  across ${agg.resources.length} resources`);
    if (agg.allCostsZero) {
        console.log(`\n  ⚠  Billed dollar costs are all $0.`);
        console.log(`     This subscription type (Visual Studio / MSDN / sponsored) has`);
        console.log(`     credits that absorb real costs. The billing API returns $0`);
        console.log(`     for covered charges.\n`);
        console.log(`  Resources are ranked by RAW USAGE QUANTITY below.`);
        console.log(`  Check https://portal.azure.com/#view/Microsoft_Azure_CostManagement`);
        console.log(`  → Cost analysis → Last 7 days for actual spend/credit utilization.`);
    }

    console.log('\n');
    console.log(hr('─'));
    console.log(`  TOP ${CONFIG.topN} RESOURCES BY ${agg.allCostsZero ? 'USAGE QUANTITY' : 'SPEND'}`);
    console.log(hr('─'));

    const maxCost = top[0]?.totalCost || 1;
    const maxQty  = top[0]?.totalQty  || 1;

    for (let i = 0; i < top.length; i++) {
        printTopResource(top[i], i + 1, maxCost, maxQty, agg.allCostsZero, agg.currency);
        if (top[i].armDetail) {
            const d = top[i].armDetail;
            if (d.sku)      console.log(`\n       Current SKU    : ${JSON.stringify(d.sku)}`);
            if (d.location) console.log(`       Location       : ${d.location}`);
            if (d.state)    console.log(`       State          : ${d.state}`);
            if (d.url)      console.log(`       Live URL       : ${d.url}`);
        }
        if (i < top.length - 1) {
            console.log('\n  ' + hr('·', 68));
        }
    }

    // All remaining resources (brief)
    if (agg.resources.length > CONFIG.topN) {
        console.log('\n');
        console.log(hr('─'));
        console.log('  ALL OTHER RESOURCES IN THE PERIOD');
        console.log(hr('─'));
        for (const res of agg.resources.slice(CONFIG.topN)) {
            const scoreStr = agg.allCostsZero
                ? `qty ${fmtQty(res.totalQty)}`
                : fmtCost(res.totalCost, agg.currency);
            console.log(`  ${res.resourceGroup.padEnd(28)}  ${res.name.padEnd(40)}  ${scoreStr}`);
        }
    }

    console.log('\n');
    console.log(hr('='));
    console.log('  WHAT DROVE USAGE — KEY TAKEAWAYS');
    console.log(hr('='));

    // Aggregate meters across ALL resources to find subscription-wide patterns
    const globalMeters = new Map();
    for (const res of agg.resources) {
        for (const m of res.meters) {
            const key = `${m.category} / ${m.name}`;
            const prev = globalMeters.get(key) || { cost: 0, qty: 0, unit: m.unit, description: m.description, resources: new Set() };
            prev.cost += m.cost;
            prev.qty  += m.qty;
            prev.resources.add(res.name);
            globalMeters.set(key, prev);
        }
    }
    const sortedMeters = [...globalMeters.entries()]
        .sort(([, a], [, b]) => agg.allCostsZero ? b.qty - a.qty : b.cost - a.cost)
        .slice(0, 8);

    console.log(`\n  Most active meter types across all resources:\n`);
    for (const [key, m] of sortedMeters) {
        const scoreStr = agg.allCostsZero
            ? `qty ${fmtQty(m.qty, m.unit)}`
            : fmtCost(m.cost, agg.currency);
        const resourceList = [...m.resources].slice(0, 3).join(', ') + (m.resources.size > 3 ? ` +${m.resources.size - 3} more` : '');
        console.log(`  • ${key}`);
        console.log(`    → ${m.description}`);
        console.log(`    → ${scoreStr}   used by: ${resourceList}`);
    }

    console.log('\n');
    console.log(hr('='));
    console.log(`  Full report saved → ${CONFIG.outputFile}`);
    console.log(`  Generated at       ${new Date().toISOString()}`);
    console.log(hr('='));
    console.log('');

    // ── Write JSON report ─────────────────────────────────────────────────────

    const report = {
        subscription: { name: sub.name, id: sub.id, tenantId: sub.tenantId, state: sub.state },
        dateRange:    { start, end, days: CONFIG.lookbackDays },
        summary: {
            totalCost:       +agg.totalCost.toFixed(4),
            totalFormatted:  `$${agg.totalCost.toFixed(2)}`,
            currency:        agg.currency,
            resourceCount:   agg.resources.length,
            recordCount:     agg.recordCount,
            allCostsZero:    agg.allCostsZero,
            rankedByQuantity: agg.allCostsZero,
        },
        top3: top.map(r => ({
            rank:          top.indexOf(r) + 1,
            name:          r.name,
            resourceGroup: r.resourceGroup,
            resourceType:  r.resourceType,
            totalCost:     +r.totalCost.toFixed(4),
            totalQty:      +r.totalQty.toFixed(4),
            armDetail:     r.armDetail || null,
            meters: r.meters.map(m => ({
                name:        m.name,
                category:    m.category,
                subCategory: m.subCategory,
                unit:        m.unit,
                description: m.description,
                cost:        +m.cost.toFixed(6),
                qty:         +m.qty.toFixed(4),
                dailyBreakdown: m.days,
            })),
        })),
        allResources: agg.resources.map(r => ({
            name:          r.name,
            resourceGroup: r.resourceGroup,
            resourceType:  r.resourceType,
            totalCost:     +r.totalCost.toFixed(4),
            totalQty:      +r.totalQty.toFixed(4),
            meterCount:    r.meters.length,
        })),
        globalMeterSummary: sortedMeters.map(([key, m]) => ({
            meter:       key,
            description: m.description,
            cost:        +m.cost.toFixed(6),
            qty:         +m.qty.toFixed(4),
            unit:        m.unit,
            usedBy:      [...m.resources],
        })),
        generatedAt: new Date().toISOString(),
    };

    await writeFile(CONFIG.outputFile, JSON.stringify(report, null, 2));
}

main().catch(e => {
    console.error('\n❌  Fatal error:', e.message);
    process.exit(1);
});
