const express = require('express');
const multer = require('multer');
const fs = require('fs');
const path = require('path');
const cors = require('cors');

const app = express();
app.use(cors());
app.use(express.static('public'));

const storageDir = process.env.STORAGE_DIR || './demos';
app.use('/demos', express.static(storageDir));

const storage = multer.diskStorage({
  destination: function (req, file, cb) {
    const dateStr = new Date().toISOString().split('T')[0]; // UTC date folder (YYYY-MM-DD)
    const dir = path.join(storageDir, dateStr);
    if (!fs.existsSync(dir)){
        fs.mkdirSync(dir, { recursive: true });
    }
    cb(null, dir);
  },
  filename: function (req, file, cb) {
    cb(null, file.originalname);
  }
});

const upload = multer({ storage: storage });

app.post('/upload', upload.single('demo'), (req, res) => {
  if (!req.file) {
    return res.status(400).send('No file uploaded.');
  }
  console.log(`Received demo: ${req.file.originalname}`);
  res.send('File uploaded successfully.');
});

app.get('/api/demos', (req, res) => {
  if (!fs.existsSync(storageDir)){
    return res.json([]);
  }
  
  let allDemos = [];
  try {
    const folders = fs.readdirSync(storageDir);
    for (const folder of folders) {
      const folderPath = path.join(storageDir, folder);
      if (fs.statSync(folderPath).isDirectory()) {
        const files = fs.readdirSync(folderPath);
        for (const file of files) {
          if (file.endsWith('.dem')) {
            const stats = fs.statSync(path.join(folderPath, file));
            allDemos.push({
              folder: folder,
              name: file,
              path: `${folder}/${file}`,
              size: (stats.size / (1024 * 1024)).toFixed(2) + ' MB',
              date: stats.mtime
            });
          }
        }
      }
    }
  } catch (err) {
    return res.status(500).send('Error reading directory.');
  }
  
  res.json(allDemos.sort((a, b) => new Date(b.date) - new Date(a.date)));
});

const PORT = process.env.PORT || 8080;
app.listen(PORT, () => {
  console.log(`Demo server running on port ${PORT}`);
  console.log(`Storage directory is set to ${storageDir}`);
});
