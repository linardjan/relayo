#!/bin/sh
# Наполняет тестовый AD (Samba) данными для проверки Relayo.
# Запуск: docker compose exec samba sh /seed/seed.sh
# Скрипт можно запускать повторно: существующие объекты пропускаются.
set -e

DN="DC=$(echo "$REALM" | tr 'A-Z' 'a-z' | sed 's/\./,DC=/g')"
MAIL_DOMAIN=$(echo "$REALM" | tr 'A-Z' 'a-z')
PASSWORD='Oper_Passw0rd'
SAM=/usr/local/samba/private/sam.ldb

ou() {
  samba-tool ou create "$1" >/dev/null 2>&1 && echo "OU $1 created" || echo "OU $1 exists"
}

# user <login> <имя> <фамилия> <папка>
user() {
  if samba-tool user create "$1" "$PASSWORD" --userou="$4" --use-username-as-cn \
       --given-name="$2" --surname="$3" --mail-address="$1@$MAIL_DOMAIN" >/dev/null 2>&1; then
    # samba-tool не умеет задавать displayName при создании
    ldbmodify -H "$SAM" >/dev/null <<LDIF
dn: CN=$1,$4,$DN
changetype: modify
replace: displayName
displayName: $2 $3
LDIF
    echo "user $1 created in $4"
  else
    echo "user $1 exists"
  fi
}

ou "OU=Support"
ou "OU=Service Accounts"
ou "OU=Sales"

# Операторы, которых импортирует Relayo
user ivanov  Ivan   Ivanov   "OU=Support"
user petrova Maria  Petrova  "OU=Support"
user sidorov Petr   Sidorov  "OU=Support"

# Пользователь из другой папки: Relayo не должен его импортировать
user smirnov Oleg   Smirnov  "OU=Sales"

# Служебная учётка для синхронизации
user relayo-sync Relayo Sync "OU=Service Accounts"

echo
echo "Done. Base DN: $DN, password for all users: $PASSWORD"
