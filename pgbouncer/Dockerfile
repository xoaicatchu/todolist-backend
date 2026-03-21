FROM alpine:3.20

RUN addgroup -S pgbouncer && adduser -S -G pgbouncer pgbouncer && \
    apk add --no-cache pgbouncer && \
    mkdir -p /var/log/pgbouncer /var/run/pgbouncer && \
    chown -R pgbouncer:pgbouncer /etc/pgbouncer /var/log/pgbouncer /var/run/pgbouncer

COPY pgbouncer.ini /etc/pgbouncer/pgbouncer.ini
COPY userlist.txt /etc/pgbouncer/userlist.txt
RUN chown pgbouncer:pgbouncer /etc/pgbouncer/pgbouncer.ini /etc/pgbouncer/userlist.txt

EXPOSE 6432
USER pgbouncer
CMD ["pgbouncer", "/etc/pgbouncer/pgbouncer.ini"]
