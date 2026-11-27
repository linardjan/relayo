-- Сведения о нашей компании в базе Usedesk — тяжёлые запросы по большим таблицам: тикеты, сообщения, клиенты,
-- значения доп. полей (до 585 млн строк), теги у тикетов (до 67 млн), история тикетов (до 212 млн).
-- Запускать на реплике или в тихие часы: каждый запрос может идти десятки минут. Запуск — README.md рядом.
-- Только SELECT; персональные данные не выводятся — только счётчики, длины и короткие префиксы.
--
-- Перед запуском: SET @company = <id нашей компании>;

-- Нулевые даты MySQL (0000-00-00) сравниваются только без строгого режима, как на серверах Usedesk.
SET SESSION sql_mode = '';

-- ===== Тикеты и сообщения =====

-- 1. Каналы и объём
SELECT channel, COUNT(*), MIN(published_at), MAX(published_at) FROM tickets WHERE company_id = @company GROUP BY channel;
-- 2. Статусы; что такое Delivery (9)
SELECT status_id, COUNT(*), MAX(last_updated_at) FROM tickets WHERE company_id = @company GROUP BY status_id;
SELECT channel, COUNT(*) FROM tickets WHERE company_id = @company AND status_id = 9 GROUP BY channel;
-- Номер тикета для счётчика Relayo (на всём экземпляре)
SELECT MAX(id) FROM tickets;

-- 3. Объединение: как записано (служебный текст)
SELECT c.ticket_id, LEFT(c.message, 200) FROM ticket_comments c JOIN tickets t ON t.id = c.ticket_id
WHERE t.company_id = @company AND c.type = 'merged' LIMIT 5;
-- 4. Типы сообщений, HTML, длина; нулевые и будущие даты
SELECT c.type, c.`from`, c.is_html, c.by_mandrill, COUNT(*), MAX(CHAR_LENGTH(c.message))
FROM ticket_comments c JOIN tickets t ON t.id = c.ticket_id WHERE t.company_id = @company GROUP BY 1, 2, 3, 4;
SELECT SUM(c.published_at = '0000-00-00 00:00:00') AS zero_dates, SUM(c.published_at > NOW()) AS future, COUNT(*)
FROM ticket_comments c JOIN tickets t ON t.id = c.ticket_id WHERE t.company_id = @company;

-- 5. Чаты и идентичности: по платформам — длины, без самих значений
SELECT platform, COUNT(*), SUM(ticket_id IS NULL) AS no_ticket, MAX(CHAR_LENGTH(token)) AS token_len,
       SUM(additional_id IS NOT NULL AND additional_id <> '') AS with_additional_id, MAX(CHAR_LENGTH(additional_id)),
       SUM(html IS NOT NULL AND html <> '') AS with_html, MAX(LENGTH(html))
FROM chats WHERE company_id = @company GROUP BY platform;
SELECT channel, COUNT(*), MIN(CHAR_LENGTH(social_id)), MAX(CHAR_LENGTH(social_id)), MIN(LEFT(social_id, 3))
FROM tickets WHERE company_id = @company AND social_id IS NOT NULL GROUP BY channel;

-- 8. Вложения: обычные и встроенные в письма; сообщения только с файлом
SELECT is_inline, COUNT(*) FROM ticket_comment_attachments WHERE company_id = @company GROUP BY is_inline;
SELECT COUNT(DISTINCT a.ticket_comment_id) FROM ticket_comment_attachments a
JOIN ticket_comments c ON c.id = a.ticket_comment_id
WHERE a.company_id = @company AND a.is_inline = 0 AND TRIM(c.message) = '';

-- Тикеты, которые ссылаются на удалённые группы
SELECT COUNT(*) FROM tickets t JOIN user_groups g ON g.id = t.`group` WHERE t.company_id = @company AND g.deleted > 0;

-- ===== Клиенты =====

-- Сколько, удалённые, заметки, компании, VIP и спамеры
SELECT COUNT(*), SUM(deleted_at IS NOT NULL) AS deleted, SUM(note IS NOT NULL AND note <> '') AS with_note,
       SUM(client_company_id IS NOT NULL) AS with_company, SUM(vip), SUM(spammer)
FROM clients WHERE company_id = @company;
-- clientId сайтов: бренды (должны совпасть с кодами сайтов Relayo), длина clientId
SELECT m.client_brand, COUNT(*), MAX(CHAR_LENGTH(m.client_id)), SUM(CHAR_LENGTH(m.client_id) > 128)
FROM client_identity_map m JOIN clients c ON c.id = m.canonical_client_id
WHERE c.company_id = @company GROUP BY m.client_brand;
-- Клиенты с несколькими брендами и с несколькими clientId в одном бренде
SELECT SUM(brands > 1) AS several_brands, SUM(ids > brands) AS several_ids FROM (
  SELECT m.canonical_client_id, COUNT(DISTINCT m.client_brand) brands, COUNT(*) ids
  FROM client_identity_map m JOIN clients c ON c.id = m.canonical_client_id
  WHERE c.company_id = @company GROUP BY m.canonical_client_id) x;
-- Клиенты с тикетами, но без clientId (мессенджеры, анонимные посетители)
SELECT COUNT(DISTINCT t.client_id) FROM tickets t
LEFT JOIN client_identity_map m ON m.canonical_client_id = t.client_id
WHERE t.company_id = @company AND m.id IS NULL;
-- Контакты: состояния адресов, типы и формат телефонов (только префикс и длина)
SELECT e.status, COUNT(*), COUNT(DISTINCT e.client_id) FROM client_emails e
JOIN clients c ON c.id = e.client_id WHERE c.company_id = @company GROUP BY e.status;
SELECT p.type, COUNT(*), COUNT(DISTINCT p.client_id), MAX(CHAR_LENGTH(p.phone)) FROM client_phones p
JOIN clients c ON c.id = p.client_id WHERE c.company_id = @company GROUP BY p.type;
SELECT LEFT(p.phone, 2) AS prefix, CHAR_LENGTH(p.phone) AS len, p.phone REGEXP '^[0-9]+$' AS digits_only, COUNT(*)
FROM client_phones p JOIN clients c ON c.id = p.client_id WHERE c.company_id = @company GROUP BY 1, 2, 3
ORDER BY 4 DESC LIMIT 20;

-- ===== Значения доп. полей =====

-- По полям: сколько и сколько непустых
SELECT f.id, f.name, f.ticket_field_type_id, COUNT(*), SUM(v.value IS NOT NULL AND v.value NOT IN ('', '0')) AS filled
FROM ticket_field_values v JOIN tickets t ON t.id = v.ticket_id JOIN ticket_fields f ON f.id = v.ticket_field_id
WHERE t.company_id = @company GROUP BY 1, 2, 3 ORDER BY 4 DESC;
-- Повторы пары (тикет, поле)
SELECT COUNT(*) FROM (SELECT v.ticket_id, v.ticket_field_id FROM ticket_field_values v
  JOIN tickets t ON t.id = v.ticket_id WHERE t.company_id = @company GROUP BY 1, 2 HAVING COUNT(*) > 1) x;
-- Значения флажков
SELECT v.value, COUNT(*) FROM ticket_field_values v JOIN ticket_fields f ON f.id = v.ticket_field_id
WHERE f.company_id = @company AND f.ticket_field_type_id = 3 GROUP BY v.value;
-- Удалённые варианты, на которые ссылаются тикеты
SELECT COUNT(DISTINCT o.id), COUNT(*) FROM ticket_field_options o
JOIN ticket_fields f ON f.id = o.ticket_field_id
JOIN ticket_field_values v ON v.ticket_field_id = f.id AND v.value = CAST(o.id AS CHAR)
WHERE f.company_id = @company AND o.deleted_at IS NOT NULL;

-- ===== Теги у тикетов =====

-- Что такое tag_company_id (совпадает ли с company_id тикета)
SELECT x.tag_company_id = t.company_id AS same_company, x.tag_company_id IS NULL AS is_null, COUNT(*)
FROM ticket_tags_2_tickets x JOIN tickets t ON t.id = x.ticket_id WHERE t.company_id = @company GROUP BY 1, 2;
-- Сколько наших тегов разовых, редких, частых и ставились ли за последний год (какие переносить)
SELECT CASE WHEN n = 1 THEN '1' WHEN n < 10 THEN '2-9' WHEN n < 100 THEN '10-99' ELSE '100+' END AS tickets_per_tag,
       COUNT(*) AS tags, SUM(last_at > NOW() - INTERVAL 1 YEAR) AS used_last_year
FROM (SELECT x.ticket_tag_id, COUNT(*) n, MAX(t.published_at) last_at FROM ticket_tags_2_tickets x
      JOIN tickets t ON t.id = x.ticket_id WHERE t.company_id = @company GROUP BY x.ticket_tag_id) g GROUP BY 1;
-- Сто самых частых наших тегов
SELECT g.id, g.name, COUNT(*), MAX(t.published_at)
FROM ticket_tags_2_tickets x JOIN tickets t ON t.id = x.ticket_id JOIN ticket_tags g ON g.id = x.ticket_tag_id
WHERE t.company_id = @company GROUP BY 1, 2 ORDER BY 3 DESC LIMIT 100;
-- Повторы пары (тикет, тег)
SELECT COUNT(*) FROM (SELECT x.ticket_id, x.ticket_tag_id FROM ticket_tags_2_tickets x
  JOIN tickets t ON t.id = x.ticket_id WHERE t.company_id = @company GROUP BY 1, 2 HAVING COUNT(*) > 1) d;

-- ===== История тикетов (trigger_changes) =====

-- Объём, доля правил и сотрудников
SELECT trigger_id IS NOT NULL AS by_rule, user_id IS NOT NULL AS by_user, COUNT(*), MIN(changed_at), MAX(changed_at)
FROM trigger_changes WHERE company_id = @company GROUP BY 1, 2;
-- Какие правила живые: срабатывания за последний месяц
SELECT c.trigger_id, t.name, t.enabled, COUNT(*) FROM trigger_changes c JOIN triggers t ON t.id = c.trigger_id
WHERE c.company_id = @company AND c.changed_at > NOW() - INTERVAL 30 DAY GROUP BY 1, 2, 3 ORDER BY 4 DESC;
-- Как часто применяют макросы за последний месяц
SELECT COUNT(*) FROM trigger_changes WHERE company_id = @company AND changed_at > NOW() - INTERVAL 30 DAY
  AND data LIKE '%"use_macros"%';
-- Что означает число у ticket_created (канал? счётчик?) — 3 примера
SELECT c.ticket_id, t.channel, t.email_channel_id, LEFT(c.data, 150) FROM trigger_changes c
JOIN tickets t ON t.id = c.ticket_id
WHERE c.company_id = @company AND c.changed_at > NOW() - INTERVAL 7 DAY AND c.data LIKE '%"ticket_created"%' LIMIT 3;
