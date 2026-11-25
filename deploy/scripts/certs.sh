#!/bin/sh
# Сертификаты стенда (этап 8, B5). Запуск из папки deploy: docker compose run --rm certs [команда]
#
#   init         (по умолчанию) выпустить недостающее: корневой, сертификат стенда, сертификаты входа
#   stand        перевыпустить сертификат стенда действующим корневым (истекает срок)
#   ca           новый корневой и новый сертификат стенда (сменились имя или адрес): корневой ставится на компьютеры заново
#   self-signed  запасной путь: самоподписанный сертификат стенда без корневого
#   auth         новые сертификаты входа (сменился CERT_PASSWORD): все входы и токены перестают действовать
#   show         сроки и отпечатки
#
# Файлы: certs/ca — корневой и его ключ (ключ контейнерам не отдаётся), certs/stand — для nginx, certs/auth — для хоста API.
set -eu
umask 077

: "${STAND_HOST:?не задан STAND_HOST}"
: "${CERT_PASSWORD:?не задан CERT_PASSWORD}"
STAND_IP="${STAND_IP:-}"

ROOT=/certs
CA=$ROOT/ca
STAND=$ROOT/stand
AUTH=$ROOT/auth
CA_DAYS=3650
STAND_DAYS=820      # Safari не принимает сертификаты со сроком больше 825 дней
AUTH_DAYS=1825
API_UID=1654        # пользователь app в образе хоста

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

fail() { echo "Ошибка: $1" >&2; exit 1; }

is_ipv4() { printf '%s' "$1" | grep -Eq '^([0-9]{1,3}\.){3}[0-9]{1,3}$'; }

# Имя в SAN и адрес в SAN — записи разного вида; адрес может быть задан и вместо имени.
DNS_NAME=
IP_ADDRESS=$STAND_IP
if is_ipv4 "$STAND_HOST"; then
    if [ -n "$STAND_IP" ] && [ "$STAND_IP" != "$STAND_HOST" ]; then
        fail "STAND_HOST — адрес $STAND_HOST, а STAND_IP — другой адрес $STAND_IP"
    fi
    IP_ADDRESS=$STAND_HOST
else
    printf '%s' "$STAND_HOST" | grep -Eq '^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$' \
        || fail "STAND_HOST «$STAND_HOST»: нужно имя строчными латинскими буквами или адрес IPv4, без https:// и порта"
    DNS_NAME=$STAND_HOST
fi
if [ -n "$IP_ADDRESS" ] && ! is_ipv4 "$IP_ADDRESS"; then
    fail "STAND_IP «$IP_ADDRESS»: нужен адрес IPv4"
fi

san() {
    list=
    [ -z "$DNS_NAME" ] || list="DNS:$DNS_NAME"
    [ -z "$IP_ADDRESS" ] || list="${list:+$list,}IP:$IP_ADDRESS"
    printf '%s' "$list"
}

# Корневой ставится на компьютеры кафедры, поэтому он годится только для этого стенда: утечка его ключа
# не даёт подделать чужие сайты. Вид имени, которого у стенда нет, ограничивается заведомо негодным значением.
constraints() {
    printf 'critical,permitted;DNS:%s,permitted;IP:%s/255.255.255.255' "${DNS_NAME:-stand.invalid}" "${IP_ADDRESS:-127.0.0.1}"
}

make_ca() {
    mkdir -p "$CA"
    openssl genrsa -out "$CA/ca.key" 4096 2>/dev/null
    openssl req -x509 -new -utf8 -sha256 -key "$CA/ca.key" -days $CA_DAYS -out "$CA/rpdfos-ca.crt" \
        -subj "/O=RPD-FOS/CN=RPD-FOS stand root ($STAND_HOST)" \
        -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
        -addext "keyUsage=critical,keyCertSign,cRLSign" \
        -addext "extendedKeyUsage=serverAuth" \
        -addext "nameConstraints=$(constraints)"
    chmod 755 "$CA"
    chmod 644 "$CA/rpdfos-ca.crt"
    echo "Корневой сертификат: certs/ca/rpdfos-ca.crt — поставьте его на компьютеры кафедры."
}

make_stand() {
    [ -f "$CA/rpdfos-ca.crt" ] || fail "нет корневого сертификата certs/ca/rpdfos-ca.crt — сначала команда init"
    [ -f "$CA/ca.key" ] || fail "нет ключа корневого certs/ca/ca.key — верните его с носителя на время выпуска"
    mkdir -p "$STAND"
    openssl genrsa -out "$TMP/stand.key" 2048 2>/dev/null
    # В CN — не имя стенда: без имени DNS в SAN проверка приняла бы его (например, адрес) за имя; браузеры смотрят только SAN.
    openssl req -new -sha256 -key "$TMP/stand.key" -subj "/O=RPD-FOS/CN=RPD-FOS stand" -out "$TMP/stand.csr"
    cat > "$TMP/stand.ext" <<EXT
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectKeyIdentifier=hash
authorityKeyIdentifier=keyid
subjectAltName=$(san)
EXT
    openssl x509 -req -sha256 -in "$TMP/stand.csr" -CA "$CA/rpdfos-ca.crt" -CAkey "$CA/ca.key" \
        -set_serial "0x$(openssl rand -hex 16)" -days $STAND_DAYS -extfile "$TMP/stand.ext" -out "$TMP/stand.crt" 2>/dev/null
    # Проверка до замены рабочих файлов: имя или адрес, не разрешённые корневым, браузер отвергнет.
    openssl verify -purpose sslserver -CAfile "$CA/rpdfos-ca.crt" "$TMP/stand.crt" >"$TMP/verify" 2>&1 || {
        cat "$TMP/verify" >&2
        fail "имя или адрес стенда не разрешены корневым сертификатом — выпустите новый корневой командой ca"
    }
    install_stand
    echo "Сертификат стенда: $(san)."
}

make_self_signed() {
    mkdir -p "$STAND"
    openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days $STAND_DAYS \
        -keyout "$TMP/stand.key" -out "$TMP/stand.crt" -subj "/O=RPD-FOS/CN=RPD-FOS stand" \
        -addext "basicConstraints=critical,CA:FALSE" \
        -addext "keyUsage=critical,digitalSignature,keyEncipherment" \
        -addext "extendedKeyUsage=serverAuth" \
        -addext "subjectAltName=$(san)" 2>/dev/null
    install_stand
    echo "Самоподписанный сертификат стенда: $(san). Браузер будет предупреждать на каждом компьютере."
}

install_stand() {
    cp "$TMP/stand.key" "$STAND/stand.key"
    cp "$TMP/stand.crt" "$STAND/stand.crt"
    chmod 600 "$STAND/stand.key"
    chmod 644 "$STAND/stand.crt"
}

# Сертификаты подписи и шифрования токенов OpenIddict: самоподписанные, в браузер не попадают.
make_auth_one() {
    openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days $AUTH_DAYS \
        -keyout "$TMP/$1.key" -out "$TMP/$1.crt" -subj "/O=RPD-FOS/CN=RPD-FOS token $1" \
        -addext "basicConstraints=critical,CA:FALSE" -addext "keyUsage=critical,$2" 2>/dev/null
    openssl pkcs12 -export -inkey "$TMP/$1.key" -in "$TMP/$1.crt" -out "$AUTH/$1.pfx" -passout env:CERT_PASSWORD
    chown $API_UID:$API_UID "$AUTH/$1.pfx" 2>/dev/null || true
    chmod 400 "$AUTH/$1.pfx"
}

make_auth() {
    mkdir -p "$AUTH"
    rm -f "$AUTH/signing.pfx" "$AUTH/encryption.pfx"
    make_auth_one signing digitalSignature
    make_auth_one encryption keyEncipherment
    # Хост работает не от root: папка должна открываться его пользователю.
    chmod 755 "$AUTH"
    echo "Сертификаты входа: certs/auth/signing.pfx, certs/auth/encryption.pfx."
}

show() {
    for file in "$CA/rpdfos-ca.crt" "$STAND/stand.crt"; do
        [ -f "$file" ] || continue
        echo "--- ${file#"$ROOT"/}"
        openssl x509 -in "$file" -noout -subject -enddate -fingerprint -sha256
        openssl x509 -in "$file" -noout -ext subjectAltName 2>/dev/null | tail -n 1
    done
    for name in signing encryption; do
        [ -f "$AUTH/$name.pfx" ] || continue
        echo "--- auth/$name.pfx"
        openssl pkcs12 -in "$AUTH/$name.pfx" -passin env:CERT_PASSWORD -nokeys 2>/dev/null | openssl x509 -noout -subject -enddate \
            || echo "не читается: пароль файла не совпадает с CERT_PASSWORD"
    done
}

case "${1:-init}" in
    init)
        [ -f "$CA/rpdfos-ca.crt" ] || [ -f "$STAND/stand.crt" ] || make_ca
        [ -f "$STAND/stand.crt" ] || make_stand
        [ -f "$AUTH/signing.pfx" ] && [ -f "$AUTH/encryption.pfx" ] || make_auth
        show ;;
    stand) make_stand; show ;;
    ca) make_ca; make_stand; show ;;
    self-signed) make_self_signed; show ;;
    auth) make_auth; show ;;
    show) show ;;
    *) fail "неизвестная команда «$1»: init, stand, ca, self-signed, auth, show" ;;
esac
