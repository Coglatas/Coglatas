FROM node:24-alpine@sha256:ebfe2f90462722a7a4de65e91990e97fe0d401c70e0e762c5b53302f905ec1c1

WORKDIR /workspace/frontend

ENV CHOKIDAR_USEPOLLING=true \
    WATCHPACK_POLLING=true \
    NG_POLL_INTERVAL=2000 \
    NPM_CONFIG_REGISTRY=https://registry.npmjs.org/ \
    NPM_CONFIG_STRICT_ALLOW_SCRIPTS=true \
    NPM_CONFIG_ALLOW_GIT=none \
    NPM_CONFIG_ALLOW_REMOTE=none \
    NPM_CONFIG_PREFER_OFFLINE=false \
    NPM_CONFIG_AUDIT=false \
    NPM_CONFIG_FUND=false

EXPOSE 4200

CMD if [ ! -x node_modules/.bin/ng ]; then \
      npm ci \
        --prefer-online \
        --strict-allow-scripts \
        --allow-git=none \
        --allow-remote=none \
        --no-audit \
        --no-fund; \
    fi \
    && printf '%s\n' \
      '{' \
      '  "/api": {' \
      '    "target": "http://backend:8080",' \
      '    "secure": false,' \
      '    "changeOrigin": false' \
      '  },' \
      '  "/health": {' \
      '    "target": "http://backend:8080",' \
      '    "secure": false,' \
      '    "changeOrigin": false' \
      '  },' \
      '  "/healthz": {' \
      '    "target": "http://backend:8080",' \
      '    "secure": false,' \
      '    "changeOrigin": false' \
      '  }' \
      '}' > /tmp/proxy.docker.conf.json \
    && npm run ng -- serve --host 0.0.0.0 --port 4200 --poll "${NG_POLL_INTERVAL}" --proxy-config /tmp/proxy.docker.conf.json
