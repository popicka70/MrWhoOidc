# The unprivileged image runs as a normal user (UID 101) and listens on 8080: the nginx master process does not
# need the root privileges, and a compromised worker must not own the container.
FROM nginxinc/nginx-unprivileged:1.29-alpine

COPY nginx.default.conf /etc/nginx/conf.d/default.conf
COPY . /usr/share/nginx/html

EXPOSE 8080