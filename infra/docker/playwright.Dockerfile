# package-lock.json resolves @playwright/test to 1.63.0, so use the matching Linux image tag here.
FROM mcr.microsoft.com/playwright:v1.63.0-noble

WORKDIR /work

ENV CI=true

CMD ["bash"]
