# Build the React app, then serve the static output with nginx (which also
# proxies /api to the api service). Build context is this folder.

FROM node:20-alpine AS build
WORKDIR /app
COPY package.json ./
RUN npm install
COPY . .
RUN npm run build

FROM nginx:alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
# nginx:alpine runs envsubst on /etc/nginx/templates/*.template at startup,
# writing the result to /etc/nginx/conf.d/. API_UPSTREAM defaults to the compose
# service; override it when the API is hosted separately. The ENVSUBST_FILTER
# keeps nginx's own $host / $remote_addr variables from being substituted.
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
ENV API_UPSTREAM=http://api:8080
ENV NGINX_ENVSUBST_FILTER=API_UPSTREAM
EXPOSE 80
