const { spawn } = require('child_process');
const path = require('path');

// Get the absolute path to the HTML file
const htmlFile = path.join(__dirname, 'PoPunkouterSoftware', 'wwwroot', 'index.html');
const fileUrl = `file:///${htmlFile.replace(/\\/g, '/')}`;

console.log(`Opening: ${fileUrl}`);

// Open in default browser (Windows)
if (process.platform === 'win32') {
    spawn('cmd', ['/c', 'start', fileUrl], { stdio: 'inherit' });
} else if (process.platform === 'darwin') {
    spawn('open', [fileUrl], { stdio: 'inherit' });
} else {
    spawn('xdg-open', [fileUrl], { stdio: 'inherit' });
}
