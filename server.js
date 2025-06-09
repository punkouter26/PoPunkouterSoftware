const http = require('http');
const fs = require('fs');
const path = require('path');
const url = require('url');

const port = process.env.PORT || 3000;
const rootDir = path.join(__dirname, 'PoPunkouterSoftware', 'wwwroot');

// MIME types mapping
const mimeTypes = {
    '.html': 'text/html',
    '.css': 'text/css',
    '.js': 'application/javascript',
    '.png': 'image/png',
    '.jpg': 'image/jpeg',
    '.jpeg': 'image/jpeg',
    '.gif': 'image/gif',
    '.ico': 'image/x-icon',
    '.webp': 'image/webp',
    '.svg': 'image/svg+xml',
    '.json': 'application/json',
    '.txt': 'text/plain'
};

console.log(`🌐 Starting PoPunkouterSoftware local server...`);
console.log(`📁 Serving files from: ${rootDir}`);

const server = http.createServer((req, res) => {
    let pathname = url.parse(req.url).pathname;
    
    // Default to index.html for root requests
    if (pathname === '/') {
        pathname = '/index.html';
    }
    
    const filePath = path.join(rootDir, pathname);
    
    // Security check - ensure file is within root directory
    if (!filePath.startsWith(rootDir)) {
        console.log(`❌ Security: Blocked access to ${pathname}`);
        res.writeHead(403, { 'Content-Type': 'text/plain' });
        res.end('403 Forbidden');
        return;
    }
    
    fs.readFile(filePath, (err, data) => {
        if (err) {
            if (err.code === 'ENOENT') {
                console.log(`📄 404: ${pathname}`);
                res.writeHead(404, { 'Content-Type': 'text/html' });
                res.end(`
                    <!DOCTYPE html>
                    <html>
                    <head><title>404 - Not Found</title></head>
                    <body>
                        <h1>404 - File Not Found</h1>
                        <p>The requested file <code>${pathname}</code> was not found.</p>
                        <p><a href="/">← Back to Home</a></p>
                    </body>
                    </html>
                `);
            } else {
                console.log(`❌ Server error for ${pathname}:`, err.message);
                res.writeHead(500, { 'Content-Type': 'text/plain' });
                res.end('500 Internal Server Error');
            }
            return;
        }
        
        const ext = path.extname(filePath);
        const mimeType = mimeTypes[ext] || 'application/octet-stream';
        
        console.log(`✅ ${req.method} ${pathname} (${mimeType})`);
        
        res.writeHead(200, { 
            'Content-Type': mimeType,
            'Cache-Control': 'no-cache' // Disable caching for development
        });
        res.end(data);
    });
});

server.listen(port, () => {
    console.log(`🚀 Server running at http://localhost:${port}`);
    console.log(`📱 Press Ctrl+C to stop the server`);
    console.log(`🔄 Make changes to your files and refresh the browser to see updates`);
});
