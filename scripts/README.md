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

### 2. Update Apps (`npm run update-apps`)

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
