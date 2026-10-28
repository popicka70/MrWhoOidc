FROM node:24-alpine AS build
WORKDIR /app

ARG VITE_OIDC_AUTHORITY=https://localhost:8443/t/default
ARG VITE_OIDC_CLIENT_ID=react-demo
ARG VITE_REDIRECT_URI=http://localhost:5173/callback
ARG VITE_POST_LOGOUT_REDIRECT_URI=http://localhost:5173/

ENV VITE_OIDC_AUTHORITY=${VITE_OIDC_AUTHORITY}
ENV VITE_OIDC_CLIENT_ID=${VITE_OIDC_CLIENT_ID}
ENV VITE_REDIRECT_URI=${VITE_REDIRECT_URI}
ENV VITE_POST_LOGOUT_REDIRECT_URI=${VITE_POST_LOGOUT_REDIRECT_URI}

COPY package*.json ./
RUN npm ci

COPY . .
RUN npm run build

# The unprivileged image runs as a normal user (UID 101) and listens on 8080: the nginx master process does not
# need the root privileges, and a compromised worker must not own the container.
FROM nginxinc/nginx-unprivileged:1.29-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 8080

CMD ["nginx", "-g", "daemon off;"]