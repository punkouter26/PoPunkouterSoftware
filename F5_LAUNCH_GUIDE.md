# F5 Launch Guide for PoPunkouterSoftware

## How to Use F5 to Launch Your Website

You now have **3 options** to launch your website using F5 in VS Code:

### Option 1: 🌐 Start Local Server & Open Website (Recommended)
- **What it does**: Starts a Node.js HTTP server on `http://localhost:3000` and automatically opens your browser
- **Best for**: Development and testing with proper HTTP serving
- **Features**: 
  - Serves all static files correctly
  - Proper MIME types for CSS, JS, images
  - Automatic browser opening
  - Console logging for debugging

### Option 2: 📂 Open Website (File Protocol)
- **What it does**: Opens the HTML file directly using `file://` protocol
- **Best for**: Quick viewing without server setup
- **Note**: Some features may not work due to CORS restrictions

### Option 3: 🔧 Live Preview (Chrome Debug)
- **What it does**: Uses Chrome debugging with live reload capabilities
- **Best for**: Advanced debugging with DevTools
- **Requires**: Chrome browser installed

## How to Launch:

1. **Press F5** in VS Code
2. **Select your preferred option** from the dropdown:
   - "🌐 Start Local Server & Open Website" (recommended)
   - "📂 Open Website (File Protocol)"
   - "🔧 Live Preview (if extension installed)"

## Fixed Issues:

✅ **Image Display Problem**: Removed references to non-existent `.webp` files  
✅ **CSS Gallery Issues**: Fixed duplicate CSS rules that were hiding images  
✅ **Favicon Loading**: Proper favicon.ico file path configured  
✅ **F5 Launch**: Complete launch configuration for all scenarios  

## Files Created:

- `server.js` - Local HTTP server for serving static files
- `open-browser.js` - Direct file opening utility
- `.vscode/launch.json` - F5 launch configurations
- `.vscode/tasks.json` - Background server task

## Troubleshooting:

**Images not showing?**
- Make sure `images/1.png` and `images/2.png` exist
- Check the browser console for 404 errors

**Server won't start?**
- Ensure Node.js is installed (`node --version`)
- Check if port 3000 is available
- Try Option 2 (File Protocol) as fallback

**F5 not working?**
- Make sure you're in the workspace folder
- Check the bottom-left status bar for any errors
- Try running the task manually: Ctrl+Shift+P → "Tasks: Run Task" → "start-live-server"

## Current Status:

🟢 **Website is working** at both:
- Local development: `http://localhost:3000`
- Live site: `https://popunkoutersoftware.com`

🟢 **Images are displaying** correctly with rotation animation  
🟢 **Favicon is loading** without console errors  
🟢 **F5 launches** website successfully