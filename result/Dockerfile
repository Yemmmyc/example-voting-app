FROM node:18-slim AS base

# Add curl for healthcheck and tini for process supervision
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl tini && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /usr/local/app

COPY package*.json ./

RUN npm ci && \
    npm cache clean --force && \
    mv /usr/local/app/node_modules /node_modules

# Set ownership of app directory and node_modules to built-in node user
RUN chown -R node:node /usr/local/app /node_modules

FROM base AS dev

# Install nodemon for development orchestration
RUN npm install -g nodemon

COPY --chown=node:node . .
ENV PORT=80
EXPOSE 80

USER node

ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["nodemon", "--inspect=0.0.0.0", "server.js"]

FROM base AS final

COPY --chown=node:node . .
ENV PORT=80
EXPOSE 80

USER node

ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["node", "server.js"]
