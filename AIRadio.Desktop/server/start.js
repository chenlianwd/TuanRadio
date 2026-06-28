// Auto-start NeteaseCloudMusicApi server for AIRadio
const http = require('http');

// Set port and bind to localhost only before requiring the API module
process.env.PORT = '37250';
process.env.HOST = '127.0.0.1';

try {
  require('./node_modules/NeteaseCloudMusicApi/app.js');
  console.log('AIRadio Music API server started on port 37250');
} catch (e) {
  console.error('Failed to start music server:', e.message);
  process.exit(1);
}
