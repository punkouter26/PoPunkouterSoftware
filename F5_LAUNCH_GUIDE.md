# F5 Launch Instructions for PoPunkouterSoftware

## 🚀 How to Launch Your Website with F5

Your VS Code workspace is now configured to launch your HTML website using **F5**!

### Available Launch Options

When you press **F5**, you'll see these options:

#### 1. 🌐 **Launch Website (Local Server)** ⭐ *Recommended*
- Starts a local HTTP server on `http://localhost:3000`
- Automatically opens your website in the default browser
- **Best for development** - works with all browser features (CORS, modules, etc.)
- **Hot refresh**: Make changes and refresh the browser to see updates

#### 2. 📂 **Open Website (Direct File)**
- Opens the HTML file directly using `file://` protocol
- Faster startup but limited functionality
- Some features may not work (CORS restrictions, relative paths)

### 🎯 Quick Start

1. **Press F5** in VS Code
2. Select "🌐 Launch Website (Local Server)" (recommended)
3. VS Code will:
   - Start the local server
   - Open your browser automatically
   - Display your website at `http://localhost:3000`

### 🔄 Development Workflow

1. **Start**: Press F5 → Select local server option
2. **Develop**: Make changes to your HTML/CSS/JS files
3. **Test**: Refresh your browser to see changes
4. **Stop**: Use Ctrl+C in the VS Code terminal to stop the server

### 🛠️ Manual Server Commands

You can also start the server manually:

```powershell
# Start the server
node server.js

# Or use the VS Code task
Ctrl+Shift+P → "Tasks: Run Task" → "start-live-server"
```

### 📁 File Structure

Your website files are served from:
```
PoPunkouterSoftware/wwwroot/
├── index.html          # Main page
├── css/style.css       # Styles
├── js/script.js        # JavaScript
├── images/             # Images and favicon
└── *.html              # Other pages
```

### 🔧 Troubleshooting

**Port 3000 already in use?**
- Stop other applications using port 3000
- Or modify `server.js` to use a different port

**Browser doesn't open automatically?**
- Manually navigate to `http://localhost:3000`
- Check VS Code's output panel for any error messages

**File changes not reflecting?**
- Hard refresh: Ctrl+F5 or Ctrl+Shift+R
- Clear browser cache if needed

---

Happy coding! 🎉
