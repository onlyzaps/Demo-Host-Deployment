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
    const serverName = req.body.serverName || 'Unknown_Server';
    const cleanServerName = serverName.replace(/[^a-zA-Z0-9_\-]/g, '_');
    const matchFolder = (req.body.matchFolder || '').replace(/[^a-zA-Z0-9_\-]/g, '');
    const matchDate = (req.body.matchDate || new Date().toISOString().split('T')[0]).replace(/[^0-9\-]/g, '');

    let dir;
    if (matchFolder) {
      dir = path.join(storageDir, cleanServerName, matchDate, matchFolder);
    } else {
      dir = path.join(storageDir, cleanServerName, matchDate);
    }
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
  console.log(`Received demo: ${req.file.originalname} for server ${req.body.serverName || 'Unknown_Server'}${req.body.matchFolder ? ` (match: ${req.body.matchFolder})` : ''}`);
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
  let existing = '';
  if (fs.existsSync(logFile)) {
    existing = fs.readFileSync(logFile, 'utf8');
  }
  fs.writeFileSync(logFile, logMsg + existing);
  
  res.send('Log appended successfully.');
});

// Get all servers (excluding .logs)
app.get('/api/servers', (req, res) => {
  if (!fs.existsSync(storageDir)) return res.json([]);
  try {
    const servers = fs.readdirSync(storageDir).filter(f => f.startsWith('DBS_') && fs.statSync(path.join(storageDir, f)).isDirectory());
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

// Get all matches (with grouped rounds) for a given server and date
app.get('/api/servers/:server/dates/:date/matches', (req, res) => {
  const serverDir = req.params.server.replace(/[^a-zA-Z0-9_\-\.]/g, '');
  const dateDir = req.params.date.replace(/[^0-9\-]/g, '');
  const datePath = path.join(storageDir, serverDir, dateDir);
  if (!fs.existsSync(datePath)) return res.json([]);

  try {
    const entries = fs.readdirSync(datePath);
    let matches = [];

    for (const entry of entries) {
      const entryPath = path.join(datePath, entry);
      const stat = fs.statSync(entryPath);

      if (stat.isDirectory()) {
        // Match folder containing round demos
        const rounds = fs.readdirSync(entryPath)
          .filter(f => f.endsWith('.dem'))
          .map(f => {
            const fStats = fs.statSync(path.join(entryPath, f));
            return {
              name: f,
              path: `${serverDir}/${dateDir}/${entry}/${f}`,
              size: (fStats.size / (1024 * 1024)).toFixed(2) + ' MB',
              sizeBytes: fStats.size,
              date: fStats.mtime
            };
          })
          .sort((a, b) => new Date(a.date) - new Date(b.date));

        const totalBytes = rounds.reduce((sum, r) => sum + r.sizeBytes, 0);
        matches.push({
          matchFolder: entry,
          rounds: rounds,
          roundCount: rounds.length,
          totalSize: (totalBytes / (1024 * 1024)).toFixed(2) + ' MB',
          date: rounds.length > 0 ? rounds[0].date : stat.mtime
        });
      } else if (entry.endsWith('.dem')) {
        // Legacy flat file — treat as single-round match
        matches.push({
          matchFolder: null,
          rounds: [{
            name: entry,
            path: `${serverDir}/${dateDir}/${entry}`,
            size: (stat.size / (1024 * 1024)).toFixed(2) + ' MB',
            sizeBytes: stat.size,
            date: stat.mtime
          }],
          roundCount: 1,
          totalSize: (stat.size / (1024 * 1024)).toFixed(2) + ' MB',
          date: stat.mtime
        });
      }
    }

    matches.sort((a, b) => new Date(b.date) - new Date(a.date));
    res.json(matches);
  } catch (err) {
    res.status(500).send('Error reading matches.');
  }
});

// -- SOURCE FILE INVENTORY --

// Receive inventory of .dem files from a CS2 server
app.post('/upload-source-files', authenticateRequest, (req, res) => {
  const serverName = req.body.serverName || 'Unknown_Server';
  const files = req.body.files || [];
  const cleanServerName = serverName.replace(/[^a-zA-Z0-9_\-]/g, '_');

  const sourceDir = path.join(storageDir, '.source-files');
  if (!fs.existsSync(sourceDir)) fs.mkdirSync(sourceDir, { recursive: true });

  const data = {
    serverName: cleanServerName,
    updatedAt: new Date().toISOString(),
    files: files
  };
  fs.writeFileSync(path.join(sourceDir, `${cleanServerName}.json`), JSON.stringify(data, null, 2));
  res.send('Source file inventory updated.');
});

// Get all servers that have reported source files
app.get('/api/source-files/servers', (req, res) => {
  const sourceDir = path.join(storageDir, '.source-files');
  if (!fs.existsSync(sourceDir)) return res.json([]);
  try {
    const files = fs.readdirSync(sourceDir).filter(f => f.endsWith('.json'));
    const servers = files.map(f => f.replace('.json', '')).filter(s => s.startsWith('DBS_'));
    res.json(servers);
  } catch (err) {
    res.status(500).send('Error reading source files directory.');
  }
});

// Get source file inventory for a specific server
app.get('/api/source-files/:server', (req, res) => {
  const cleanServer = req.params.server.replace(/[^a-zA-Z0-9_\-]/g, '_');
  const filePath = path.join(storageDir, '.source-files', `${cleanServer}.json`);
  if (!fs.existsSync(filePath)) return res.json({ serverName: cleanServer, updatedAt: null, files: [] });
  try {
    const data = JSON.parse(fs.readFileSync(filePath, 'utf8'));
    res.json(data);
  } catch (err) {
    res.status(500).send('Error reading source file inventory.');
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
