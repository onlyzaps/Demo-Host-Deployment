FROM node:18-alpine

WORKDIR /usr/src/app

# Copy package.json and install dependencies
COPY package*.json ./
RUN npm install

# Copy application files
COPY . .

# Setting the environment variable so standard docker-compose usages
# map directly into this variable.
ENV STORAGE_DIR=/storage
ENV PORT=3000

# Expose port
EXPOSE 3000

# Start server
CMD [ "node", "server.js" ]
