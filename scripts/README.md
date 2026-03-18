# Azure Apps Discovery & Sync Scripts

Automated tools to discover web apps from Azure, verify connectivity, and update the website's app catalog.

## Overview

These scripts automatically:
- Query all Azure resource groups for web applications
- Discover App Services, Container Apps, and Static Web Apps
- Test real-time connectivity to each app
- Generate detailed reports with status information
- Sync the discovered apps with your website's `apps.json`

## Prerequisites

- **Azure CLI** installed and authenticated (`az login`)
- **Node.js** 18+ (already configured in package.json)
- Access to your Azure subscription

## Available Scripts

### 1. Discover Apps (`npm run discover-apps`)

Scans all Azure resource groups and generates a comprehensive report.

```bash
npm run discover-apps
```

**What it does:**
- Lists all resource groups in your subscription
- For each resource group, queries:
  - Web Apps (App Service)
  - Container Apps
  - Static Web Apps
- Extracts app URLs from Azure metadata
- Tests HTTP connectivity to each app
- Determines status: `active` (200-399), `broken` (400+), or `disabled` (timeout/error)
- Generates `azure-apps-report.json` with full details

**Output Report Includes:**
- Summary statistics (total apps, status breakdown, resource types)
- Comparison with existing `apps.json` (new/updated/removed apps)
- Full app details with connectivity test results
- Azure resource metadata (tags, resource group, type)

### 2. Azure Full Report (`npm run azure-report`)

Consolidated single-pass audit — replaces running discover, cleanup, and cost-audit separately.

```bash
npm run azure-report
```

**What it does (one command):**
1. Verifies `az login` and loads subscription info
2. Discovers every App Service, Container App, and Static Web App across all resource groups
3. Live-tests HTTP connectivity to every URL
4. Queries ALL Azure resources via Resource Graph
5. Checks every resource against a free-tier knowledge base
6. Detects unused/idle resources (orphaned IPs, empty plans, Azure Advisor flags)
7. Pulls 30-day consumption cost data
8. Diffs discovered apps against `wwwroot/data/apps.json`
9. Prints a full console summary with copy-paste `az` commands
10. Saves everything to **`azure-full-report.json`**

**Report sections:**
- Web services: URL, HTTP status, response time, free-tier status per service
- All Azure resources: count by type
- Free-tier analysis: on free / can go free / no free option
- Unused/idle resources with remediation commands
- 30-day cost breakdown + top cost drivers
- apps.json sync diff (new/removed/updated)
- Quick actions (copy-paste `az` commands to downgrade SKUs or delete idle resources)

---

### 3. 7-Day Spend Detail (`npm run spend-detail`)

Deep-dives into the past 7 days of Azure usage — identifies the top 3 cost/usage drivers and explains exactly what each resource consumed.

```bash
npm run spend-detail
```

**What it does:**
- Fetches all consumption usage records for the last 7 days
- Aggregates by resource, then by meter type (e.g. "Compute Hours", "Data Stored", "Tokens")
- Identifies the **top 3 resources** by spend (or by raw usage quantity if subscription uses credits)
- For each top resource:
  - Shows the resource group and Azure service type
  - Lists every meter that generated charges, with a plain-English description of what it measures
  - Prints a **day-by-day timeline** with ASCII progress bars
  - Fetches live ARM detail (SKU, location, state, URL)
- Prints global meter-type summary across all resources
- Saves everything to **`azure-spend-detail-report.json`**

**Note on credits subscriptions (Visual Studio / MSDN / sponsored):**
The Azure consumption API returns `$0.00` costs when charges are covered by credits.
In that case, the script automatically switches to ranking by **usage quantity** instead,
so you still see which resources are doing the most work. Check the Azure portal Cost
Management blade for actual credit utilization if needed.

---

### 4. Azure Cost & Usage Audit (`npm run audit-azure`)

Scans every Azure resource in your subscription for unused resources and free-tier opportunities.

```bash
npm run audit-azure
```

**What it does:**
- Lists every resource across all resource groups
- Checks each resource against a free-tier knowledge base (App Service, Static Web Apps, Cosmos DB, Azure SQL, Cognitive Services, Log Analytics, SignalR, AI Search, and more)
- Flags resources that could be downgraded to F0/F1/Free SKU
- Identifies unused/idle resources: orphaned public IPs, empty App Service Plans, etc.
- Pulls Azure Advisor Cost & High-Impact recommendations
- Fetches 30-day consumption cost data and ranks top cost drivers
- Prints a formatted console report with ready-to-run `az` commands
- Saves full JSON results to `azure-cost-audit-report.json`

---

### 3. Update Apps (`npm run update-apps`)

Merges the discovery report with your existing `apps.json`.

```bash
npm run update-apps
```

**What it does:**
- Reads `azure-apps-report.json` (must run discovery first)
- Loads existing `apps.json`
- Merges data (prefers manual descriptions/categories over inferred)
- Shows detailed change summary
- Asks for confirmation before proceeding
- Creates backup (`apps.json.backup`)
- Writes updated `apps.json`

**Merge Logic:**
- **Status**: Always uses fresh connectivity status from Azure
- **URL**: Always uses current URL from Azure
- **Description/Category**: Keeps manually-edited values if they exist
- **Technologies**: Preserves manual edits, falls back to inferred values
- **Removed apps**: Preserved in JSON (no automatic deletions)

### 3. Full Sync (`npm run sync-azure`)

Runs both scripts in sequence (discover → update).

```bash
npm run sync-azure
```

## Workflow Example

```bash
# 1. Discover all apps in Azure
npm run discover-apps

# 2. Review the generated report
cat azure-apps-report.json

# 3. Update apps.json with discovered apps
npm run update-apps
# (You'll be prompted to confirm changes)

# 4. Verify the website
# Open PoPunkouterSoftware/wwwroot/OurWebApps.html in browser

# 5. Update e2e tests if app count changed
# Edit e2e/site.spec.ts and update expected app count
```

## Understanding Status Values

- **Active** ✅: HTTP response 200-399, site is accessible
- **Broken** ❌: HTTP response 400+, site exists but has errors
- **Disabled** ⚠️: Timeout or connection error, site may be stopped/scaled down

## App Metadata

The scripts infer metadata from:
1. **Azure resource tags** (if you've tagged your resources)
2. **App name patterns** (keywords like "game", "quiz", "ai")
3. **Azure resource type** (App Service, Container App, Static Web App)

### Adding Custom Metadata in Azure

Tag your Azure resources for better descriptions:

```bash
az webapp update --name poappidea-web \
  --resource-group PoAppIdea \
  --set tags.category="productivity" \
        tags.description="AI-powered app idea generator" \
        tags.technologies="Azure OpenAI,Blazor,App Insights"
```

## File Structure

```
scripts/
├── discover-azure-apps.js    # Main discovery script
├── update-apps-from-report.js # Update helper script
└── README.md                 # This file

# Generated files
azure-apps-report.json        # Discovery report (gitignored)
apps.json.backup              # Backup before updates
```

## Troubleshooting

### "No resource groups found"
- Run `az login` to authenticate
- Verify: `az account show`

### "Apps not appearing in report"
- Check resource types: script looks for webapps, containerapps, staticwebapps
- Verify apps have public URLs configured

### "All apps show as disabled/timeout"
- Some apps may be stopped or scaled to zero
- Check Azure portal to start/scale up apps
- Increase timeout in `scripts/discover-azure-apps.js` (CONFIG.timeout)

### "Wrong descriptions/categories"
- Add Azure resource tags (see "Adding Custom Metadata" above)
- Or manually edit `apps.json` after sync (values are preserved on next run)

## Advanced Configuration

Edit `scripts/discover-azure-apps.js`:

```javascript
const CONFIG = {
    timeout: 10000, // HTTP request timeout (ms)
    userAgent: 'PoPunkouterSoftware-Discovery/1.0',
    outputFile: 'azure-apps-report.json'
};
```

## Security Notes

- Scripts read Azure metadata via Azure CLI (no credentials stored)
- HTTP connectivity tests use HEAD requests (minimal impact)
- No data is sent to external services
- Generated reports may contain URLs - don't commit if private

## Next Steps

After syncing apps:
1. Review `apps.json` for accuracy
2. Update e2e tests (`e2e/site.spec.ts`) if app count changed
3. Test locally: open `OurWebApps.html` in browser
4. Commit and deploy changes
5. Run sync periodically to keep apps up-to-date
