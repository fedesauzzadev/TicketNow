FROM node:20-alpine AS build
WORKDIR /app
COPY src/spa/package.json src/spa/package-lock.json* ./
RUN npm install
COPY src/spa/ ./
RUN npm run build

FROM nginx:1.27-alpine AS final
COPY src/spa/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
