const express = require('express');
const multer = require('multer');
const fs = require('fs');
const path = require('path');
const cors = require('cors');

const app = express();
app.use(cors());
app.use(express.static('public'));
app.use(express.json()); // Allow reading JSON bodies for logs

const storageDir = process.env.STORAGE_DIR || './demos';
app.use('/demos', express.static(storageDir));

const storage = multer.diskStorage({
  destination: function (req, file, cb) {
    const serverName = req.body.serverName || '.Unknown_Server';
    let cleanServerName = serverName.replace(/\s+/g, '_');
    if (!cleanServerName.startsWith('.')) {
        cleanServerName = '.' + cleanServerName;
    }
    const dateStr = new Date().toISOString().split('T')[0]; // UTC date folder (YYYY-MM-DD)
    const dir = path.join(storageDir, cleanServerName, dateStr);
    if (!fs.existsSync(dir)){
        fs.mkdirSync(dir, { recursive: true });
    }
    cb(null, dir);
  },
  filename: function (req, file, cb) {
    cb(null, file.originalname.replace(/\s+/g, '_'));
  }
});

const upload = multer({ storage: storage });

const EXPECTED_API_KEY = process.env.API_SECRET_KEY || 'CHANGE_ME_PLEASE';

const authenticateRequest = (req, res, next) => {
  const apiKey = req.headers['x-api-key'];
  if (!apiKey || apiKey !== EXPECTED_API_KEY) {
    console.log(`Unauthorized upload attempt rejected.`);
    return res.status(401).send('Unauthorized: Invalid API Key');
  }
  next();
};

app.post('/upload', authenticateRequest, upload.single('demo'), (req, res) => {
  if (!req.file) {
    return res.status(400).send('No file uploaded.');
  }
  console.log(`Received demo: ${req.file.originalname} for server ${req.body.serverName || 'Unknown_Server'}`);
  res.send('File uploaded successfully.');
});

// Logs POST API
app.post('/upload-log', authenticateRequest, (req, res) => {
  const serverName = req.body.serverName || 'Unknown_Server';
  const logMsg = req.body.log || '';
  
  let cleanServerName = serverName.replace(/\s+/g, '_');
  
  const dateStr = new Date().toISOString().split('T')[0]; // UTC Date
  const logDir = path.join(storageDir, '.logs', cleanServerName);
  
  if (!fs.existsSync(logDir)) {
      fs.mkdirSync(logDir, { recursive: true });
  }
  
  const logFile = path.join(logDir, `${dateStr}.log`);
  fs.appendFileSync(logFile, logMsg);
  
  res.send('Log appended successfully.');
});

// Get all servers (excluding .logs)
app.get('/api/servers', (req, res) => {
  if (!fs.existsSync(storageDir)) return res.json([]);
  try {
    const servers = fs.readdirSync(storageDir).filter(f => f.startsWith('.') && f !== '.logs' && fs.statSync(path.join(storageDir, f)).isDirectory());
    res.json(servers);
  } catch (err) {
    res.status(500).send('Error reading storage directory.');
  }
});

// Get all dates for a given server
app.get('/api/servers/:server/dates', (req, res) => {
  const serverPath = path.join(storageDir, req.params.server);
  if (!fs.existsSync(serverPath)) return res.json([]);
  try {
    const dates = fs.readdirSync(serverPath).filter(f => fs.statSync(path.join(serverPath, f)).isDirectory());
    res.json(dates.sort((a,b) => b.localeCompare(a))); // Descending dates
  } catch (err) {
    res.status(500).send('Error reading server directory.');
  }
});

// Get all demos for a given server and date
app.get('/api/servers/:server/dates/:date/demos', (req, res) => {
  const datePath = path.join(storageDir, req.params.server, req.params.date);
  if (!fs.existsSync(datePath)) return res.json([]);

  try {
    let allDemos = [];
    const files = fs.readdirSync(datePath);
    for (const file of files) {
      if (file.endsWith('.dem')) {
        const stats = fs.statSync(path.join(datePath, file));
        allDemos.push({
          name: file,
          path: `${req.params.server}/${req.params.date}/${file}`,
          size: (stats.size / (1024 * 1024)).toFixed(2) + ' MB',
          date: stats.mtime
        });
      }
    }
    res.json(allDemos.sort((a, b) => new Date(b.date) - new Date(a.date)));
  } catch (err) {
    res.status(500).send('Error reading demos.');
  }
});

// -- LOGGING READ ENDPOINTS --

app.get('/api/logs/servers', (req, res) => {
  const logsBase = path.join(storageDir, '.logs');
  if (!fs.existsSync(logsBase)) return res.json([]);
  try {
    const servers = fs.readdirSync(logsBase).filter(f => fs.statSync(path.join(logsBase, f)).isDirectory());
    res.json(servers);
  } catch (err) {
    res.status(500).send('Error reading logs directory.');
  }
});

app.get('/api/logs/:server/dates', (req, res) => {
  const serverPath = path.join(storageDir, '.logs', req.params.server);
  if (!fs.existsSync(serverPath)) return res.json([]);
  try {
    const files = fs.readdirSync(serverPath).filter(f => f.endsWith('.log'));
    const dates = files.map(f => f.replace('.log', ''));
    res.json(dates.sort((a,b) => b.localeCompare(a))); // Descending dates
  } catch (err) {
    res.status(500).send('Error reading log server directory.');
  }
});

app.get('/api/logs/:server/dates/:date/content', (req, res) => {
  const logPath = path.join(storageDir, '.logs', req.params.server, `${req.params.date}.log`);
  if (!fs.existsSync(logPath)) return res.status(404).send('Log not found.');
  try {
    const content = fs.readFileSync(logPath, 'utf8');
    res.send(content);
  } catch (err) {
    res.status(500).send('Error reading log file.');
  }
});

const PORT = process.env.PORT || 8080;
app.listen(PORT, () => {
  console.log(`Demo server running on port ${PORT}`);
  console.log(`Storage directory is set to ${storageDir}`);
});
