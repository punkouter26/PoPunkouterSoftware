/**
 * Update Apps Helper Script
 * Merges Azure discovery report with existing apps.json
 */

import { readFile, writeFile, copyFile } from 'fs/promises';
import { existsSync } from 'fs';

// Configuration
const CONFIG = {
    reportFile: 'azure-apps-report.json',
    appsJsonPath: './PoPunkouterSoftware/wwwroot/data/apps.json',
    backupSuffix: '.backup'
};

/**
 * Merge discovered app with existing app data
 */
function mergeAppData(discoveredApp, existingApp) {
    // Prefer manually-entered data over inferred data
    return {
        id: discoveredApp.id,
        name: discoveredApp.name,
        description: existingApp?.description || discoveredApp.description,
        category: existingApp?.category || discoveredApp.category,
        status: discoveredApp.status, // Always use fresh connectivity status
        technologies: existingApp?.technologies || discoveredApp.technologies,
        url: discoveredApp.url
    };
}

/**
 * Ask user for confirmation
 */
async function confirm(question) {
    const readline = await import('readline');
    const rl = readline.createInterface({
        input: process.stdin,
        output: process.stdout
    });

    return new Promise((resolve) => {
        rl.question(`${question} (y/n): `, (answer) => {
            rl.close();
            resolve(answer.toLowerCase() === 'y' || answer.toLowerCase() === 'yes');
        });
    });
}

/**
 * Main execution
 */
async function main() {
    console.log('📝 Starting Apps Update Process\n');
    console.log('='.repeat(60));

    // Check if report exists
    if (!existsSync(CONFIG.reportFile)) {
        console.error(`❌ Report file not found: ${CONFIG.reportFile}`);
        console.error('   Please run `npm run discover-apps` first.');
        process.exit(1);
    }

    // Load report
    console.log(`\n📖 Reading report: ${CONFIG.reportFile}`);
    const report = JSON.parse(await readFile(CONFIG.reportFile, 'utf-8'));
    
    console.log(`   Generated at: ${new Date(report.generatedAt).toLocaleString()}`);
    console.log(`   Total apps discovered: ${report.apps.length}`);
    console.log(`   Active: ${report.summary.byStatus.active}`);
    console.log(`   Broken: ${report.summary.byStatus.broken}`);
    console.log(`   Disabled: ${report.summary.byStatus.disabled}`);

    // Load existing apps.json
    let existingApps = [];
    if (existsSync(CONFIG.appsJsonPath)) {
        console.log(`\n📖 Reading existing: ${CONFIG.appsJsonPath}`);
        const existingData = JSON.parse(await readFile(CONFIG.appsJsonPath, 'utf-8'));
        existingApps = existingData.apps || [];
        console.log(`   Current apps: ${existingApps.length}`);
    } else {
        console.log(`\nℹ️  No existing apps.json found - will create new file`);
    }

    // Create lookup map for existing apps
    const existingMap = new Map(existingApps.map(app => [app.id, app]));

    // Merge data
    console.log(`\n🔄 Merging data...`);
    const mergedApps = report.apps.map(discoveredApp => 
        mergeAppData(discoveredApp, existingMap.get(discoveredApp.id))
    );

    // Show changes
    console.log('\n' + '='.repeat(60));
    console.log('\n📊 Changes Summary:\n');
    
    const changesDetails = {
        new: [],
        updated: [],
        statusChanged: [],
        urlChanged: []
    };

    mergedApps.forEach(app => {
        const existing = existingMap.get(app.id);
        if (!existing) {
            changesDetails.new.push(app);
        } else {
            changesDetails.updated.push(app);
            if (existing.status !== app.status) {
                changesDetails.statusChanged.push({
                    name: app.name,
                    oldStatus: existing.status,
                    newStatus: app.status
                });
            }
            if (existing.url !== app.url) {
                changesDetails.urlChanged.push({
                    name: app.name,
                    oldUrl: existing.url,
                    newUrl: app.url
                });
            }
        }
    });

    // Check for removed apps
    const discoveredIds = new Set(mergedApps.map(a => a.id));
    const removedApps = existingApps.filter(a => !discoveredIds.has(a.id));

    console.log(`   New apps: ${changesDetails.new.length}`);
    if (changesDetails.new.length > 0) {
        changesDetails.new.forEach(app => {
            console.log(`      ✨ ${app.name} - ${app.status}`);
        });
    }

    console.log(`\n   Status changes: ${changesDetails.statusChanged.length}`);
    if (changesDetails.statusChanged.length > 0) {
        changesDetails.statusChanged.forEach(change => {
            const icon = change.newStatus === 'active' ? '✅' : change.newStatus === 'broken' ? '❌' : '⚠️';
            console.log(`      ${icon} ${change.name}: ${change.oldStatus} → ${change.newStatus}`);
        });
    }

    console.log(`\n   URL changes: ${changesDetails.urlChanged.length}`);
    if (changesDetails.urlChanged.length > 0) {
        changesDetails.urlChanged.forEach(change => {
            console.log(`      🔗 ${change.name}:`);
            console.log(`         Old: ${change.oldUrl}`);
            console.log(`         New: ${change.newUrl}`);
        });
    }

    console.log(`\n   Apps not in Azure: ${removedApps.length}`);
    if (removedApps.length > 0) {
        removedApps.forEach(app => {
            console.log(`      ⚠️  ${app.name} - will be kept in apps.json`);
        });
        console.log(`\n      ℹ️  These apps will be preserved (you can manually remove them later)`);
    }

    // Combine discovered + removed (preserved)
    const finalApps = [...mergedApps, ...removedApps];

    console.log('\n' + '='.repeat(60));
    console.log(`\n📈 Final result: ${finalApps.length} total apps`);
    console.log(`   (${mergedApps.length} from Azure + ${removedApps.length} preserved)`);

    // Ask for confirmation
    console.log('\n' + '='.repeat(60));
    const shouldProceed = await confirm('\n❓ Proceed with update?');

    if (!shouldProceed) {
        console.log('\n❌ Update cancelled by user');
        process.exit(0);
    }

    // Create backup
    if (existsSync(CONFIG.appsJsonPath)) {
        const backupPath = CONFIG.appsJsonPath + CONFIG.backupSuffix;
        console.log(`\n💾 Creating backup: ${backupPath}`);
        await copyFile(CONFIG.appsJsonPath, backupPath);
    }

    // Write updated apps.json
    const newAppsJson = {
        apps: finalApps.sort((a, b) => a.name.localeCompare(b.name))
    };

    console.log(`\n✍️  Writing updated file: ${CONFIG.appsJsonPath}`);
    await writeFile(
        CONFIG.appsJsonPath,
        JSON.stringify(newAppsJson, null, 2) + '\n'
    );

    console.log('\n' + '='.repeat(60));
    console.log('\n✅ Update complete!');
    console.log(`\n📋 Summary:`);
    console.log(`   Total apps: ${finalApps.length}`);
    console.log(`   New: ${changesDetails.new.length}`);
    console.log(`   Updated: ${changesDetails.updated.length}`);
    console.log(`   Preserved: ${removedApps.length}`);
    
    console.log('\n💡 Next steps:');
    console.log('   1. Review the updated apps.json file');
    console.log('   2. Test the web apps page locally');
    console.log('   3. Update e2e tests if needed (app count changed)');
    console.log('   4. Commit and deploy\n');
}

// Run
main().catch(error => {
    console.error('❌ Fatal error:', error);
    process.exit(1);
});
