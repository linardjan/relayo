-- Сведения о нашей компании в базе Usedesk для плана переноса (docs/usedesk-migration.md, раздел 9).
-- Лёгкие запросы: настройки, справочники, правила, макросы. Тяжёлые (по сотням миллионов строк) — stats-heavy.sql.
-- Запуск — README.md рядом. Только SELECT; токены, пароли, URL с ключами и персональные данные не выводятся:
-- только счётчики, длины и короткие префиксы.
--
-- Перед запуском: SET @company = <id нашей компании>;

-- Нулевые даты MySQL (0000-00-00) сравниваются только без строгого режима, как на серверах Usedesk.
SET SESSION sql_mode = '';

-- ===== Каналы, статусы, виджеты =====

-- 1. Каналы почты и мессенджеров
SELECT id, name, type, sub_type, deleted FROM company_email_channels WHERE company_id = @company;

-- 6. Виджеты: какие функции включены
SELECT id, name, company_email_channel_id, language, show_type, topics_required,
       custom_fields IS NOT NULL AS has_custom_fields, ticket_fields IS NOT NULL AS has_ticket_fields,
       chat_callback_enabled, allow_rating, docs IS NOT NULL AS has_docs, CHAR_LENGTH(offline_answer) AS offline_answer_len
FROM usedesk_widget WHERE company_id = @company;

-- 7. WhatsApp (Pact)
SELECT id, channel_id, provider_type, is_business, group_chats, is_active
FROM pact_settings WHERE company_id = @company AND deleted_at IS NULL;

-- ===== Сотрудники и группы =====

-- Операторы: роли, удалённые, статусы, есть ли GUID AD
SELECT u.role, u.deleted > 0 AS deleted, u.status, g.duplicate_guid IS NOT NULL AS has_guid, COUNT(*)
FROM users u LEFT JOIN users_guid_migration g ON g.original_id = u.id
WHERE u.company_id = @company GROUP BY 1, 2, 3, 4;
-- Операторы, у которых несколько GUID (должно быть пусто)
SELECT g.original_id, COUNT(*) FROM users_guid_migration g JOIN users u ON u.id = g.original_id
WHERE u.company_id = @company GROUP BY g.original_id HAVING COUNT(*) > 1;

-- Группы: живые, удалённые, демо, своё рабочее время, ограничение персональных данных
SELECT deleted > 0 AS deleted, is_demo, custom_working_time, client_personal_data_access, COUNT(*)
FROM user_groups WHERE company_id = @company GROUP BY 1, 2, 3, 4;
-- Повторяющиеся имена живых групп (у нас имя уникально)
SELECT name, COUNT(*) FROM user_groups WHERE company_id = @company AND deleted = 0 GROUP BY name HAVING COUNT(*) > 1;

-- ===== Правила (triggers) =====

-- Сколько, включённые, ручные, по времени, словоформы, HTML
SELECT enabled, manual_starting, is_time_based, use_word_forms, ignore_html, COUNT(*)
FROM triggers WHERE company_id = @company AND deleted = 0 GROUP BY 1, 2, 3, 4, 5;
-- Повторяющиеся позиции
SELECT position, COUNT(*) FROM triggers WHERE company_id = @company AND deleted = 0
GROUP BY position HAVING COUNT(*) > 1;

-- События: пары target × condition, в скольких правилах, примеры value → new_value
SELECT e.target, e.`condition`, e.`boolean`, COUNT(*) AS n, COUNT(DISTINCT e.trigger_id) AS rules,
       MIN(e.value), MAX(e.value), MIN(e.new_value), MAX(e.new_value)
FROM trigger_targets e JOIN triggers t ON t.id = e.trigger_id
WHERE t.company_id = @company AND t.deleted = 0 GROUP BY 1, 2, 3 ORDER BY 1, 2;
-- Какие target бывают у событий правил по времени и без времени (где «тикет создан»)
SELECT t.is_time_based, e.target, e.`condition`, COUNT(DISTINCT t.id) AS rules
FROM trigger_targets e JOIN triggers t ON t.id = e.trigger_id
WHERE t.company_id = @company AND t.deleted = 0 GROUP BY 1, 2, 3 ORDER BY 1, 2, 3;
-- target = trigger: value — id правила нашей компании?
SELECT COUNT(*) AS n, SUM(r.id IS NOT NULL) AS is_our_rule
FROM trigger_targets e JOIN triggers t ON t.id = e.trigger_id
LEFT JOIN triggers r ON r.id = e.value AND r.company_id = @company
WHERE t.company_id = @company AND t.deleted = 0 AND e.target = 'trigger';

-- Условия: пары target × condition, target_subtype, в скольких правилах, примеры значений
SELECT c.target, c.`condition`, c.target_subtype, c.`boolean`, COUNT(*) AS n, COUNT(DISTINCT c.trigger_id) AS rules,
       MIN(LEFT(c.value, 50)), MAX(LEFT(c.value, 50)), SUM(c.word_form IS NOT NULL AND c.word_form <> '') AS word_forms
FROM trigger_conditions c JOIN triggers t ON t.id = c.trigger_id
WHERE t.company_id = @company AND t.deleted = 0 GROUP BY 1, 2, 3, 4 ORDER BY 1, 2;

-- Действия: какие, в скольких правилах; примеры значений (у HTTP-запросов — только схема и хост)
SELECT a.action, COUNT(*) AS n, COUNT(DISTINCT a.trigger_id) AS rules, SUM(a.post_body IS NOT NULL) AS with_body,
       MIN(LEFT(IF(a.action IN ('get_query', 'post_query'), SUBSTRING_INDEX(a.value, '/', 3), a.value), 60)),
       MAX(LEFT(IF(a.action IN ('get_query', 'post_query'), SUBSTRING_INDEX(a.value, '/', 3), a.value), 60))
FROM trigger_actions a JOIN triggers t ON t.id = a.trigger_id
WHERE t.company_id = @company AND t.deleted = 0 GROUP BY a.action ORDER BY 3 DESC;
-- Переменные в текстах ответов и уведомлений (синтаксис подстановок Usedesk)
SELECT a.action, LEFT(a.value, 300) FROM trigger_actions a JOIN triggers t ON t.id = a.trigger_id
WHERE t.company_id = @company AND t.deleted = 0 AND a.action IN ('create_comment', 'user_message')
  AND (a.value LIKE '%{%' OR a.value LIKE '%[%') LIMIT 10;
-- Кому уходит user_message (значение — сотрудник? текст?)
SELECT LEFT(a.value, 100) FROM trigger_actions a JOIN triggers t ON t.id = a.trigger_id
WHERE t.company_id = @company AND t.deleted = 0 AND a.action = 'user_message' LIMIT 5;

-- Правила, которые переносятся целиком: только уже поддержанные события, условия и действия
SELECT COUNT(*) AS rules_total,
       SUM(NOT EXISTS (SELECT 1 FROM trigger_targets e WHERE e.trigger_id = t.id
                         AND e.target NOT IN ('message_from_client', 'message_from_agent', 'ticket_changed',
                                              'last_client_commented_at', 'last_user_commented_at'))
           AND NOT EXISTS (SELECT 1 FROM trigger_conditions c WHERE c.trigger_id = t.id
                         AND c.target NOT IN ('status', 'assignee', 'channel', 'comment'))
           AND NOT EXISTS (SELECT 1 FROM trigger_actions a WHERE a.trigger_id = t.id
                         AND a.action NOT IN ('create_comment', 'update_status', 'update_assignee'))) AS rules_full,
       SUM(EXISTS (SELECT 1 FROM trigger_actions a WHERE a.trigger_id = t.id
                     AND (a.action IN ('add_tags', 'delete_tags') OR a.action LIKE 'ticket\_field\_\_%'))) AS rules_tags_fields
FROM triggers t WHERE t.company_id = @company AND t.deleted = 0 AND t.enabled = 1;

-- ===== Доп. поля =====

-- Сколько, по типам (1 text, 2 list, 3 checkbox, 4 connected-list), признаки
SELECT ticket_field_type_id, active, hidden, api, required, add_tag, parent_field_id IS NOT NULL AS child, COUNT(*)
FROM ticket_fields WHERE company_id = @company AND deleted = 0 GROUP BY 1, 2, 3, 4, 5, 6, 7;
-- Что лежит в ticket_fields.value
SELECT id, name, ticket_field_type_id, LEFT(value, 200) FROM ticket_fields
WHERE company_id = @company AND deleted = 0 AND value IS NOT NULL AND value <> '' LIMIT 10;
-- Варианты: сколько по полям, скрытые, удалённые
SELECT f.id, f.name, f.ticket_field_type_id, COUNT(*) AS options, SUM(o.hidden) AS hidden,
       SUM(o.deleted_at IS NOT NULL) AS deleted
FROM ticket_field_options o JOIN ticket_fields f ON f.id = o.ticket_field_id
WHERE f.company_id = @company AND f.deleted = 0 GROUP BY 1, 2, 3;
-- Связанные списки: value полей и подписи вариантов
SELECT f.id, f.name, f.parent_field_id, LEFT(f.value, 300) FROM ticket_fields f
WHERE f.company_id = @company AND f.deleted = 0 AND f.ticket_field_type_id = 4;
SELECT o.ticket_field_id, LEFT(o.value, 100) FROM ticket_field_options o JOIN ticket_fields f ON f.id = o.ticket_field_id
WHERE f.company_id = @company AND f.ticket_field_type_id = 4 LIMIT 10;
-- Условия показа полей: пары target × condition, примеры value
SELECT c.target, c.`condition`, c.`boolean`, COUNT(*) AS n, COUNT(DISTINCT c.ticket_field_id) AS fields,
       MIN(LEFT(c.value, 50)), MAX(LEFT(c.value, 50))
FROM ticket_field_conditions c JOIN ticket_fields f ON f.id = c.ticket_field_id
WHERE f.company_id = @company AND f.deleted = 0 GROUP BY 1, 2, 3 ORDER BY 1, 2;
-- Задают ли условия связь связанных списков: условия дочерних полей ссылаются на родителя?
SELECT f.id, f.name, f.parent_field_id, c.target, c.`condition`, LEFT(c.value, 50)
FROM ticket_fields f JOIN ticket_field_conditions c ON c.ticket_field_id = f.id
WHERE f.company_id = @company AND f.deleted = 0 AND f.parent_field_id IS NOT NULL LIMIT 20;

-- ===== Макросы =====

-- Сколько, включённые, по доступу, личные; что в sort
SELECT enabled, privacy, for_user_id IS NOT NULL AS personal, sort, COUNT(*)
FROM macroses WHERE company_id = @company AND deleted = 0 GROUP BY 1, 2, 3, 4;
-- HTML и переменные в текстах, длина
SELECT SUM(message LIKE '%<%>%') AS html, SUM(message LIKE '%{%' OR message LIKE '%[%') AS vars,
       MAX(CHAR_LENGTH(message)) AS max_len
FROM macroses WHERE company_id = @company AND deleted = 0;
SELECT LEFT(message, 300) FROM macroses
WHERE company_id = @company AND deleted = 0 AND (message LIKE '%{%' OR message LIKE '%[%') LIMIT 5;
-- Действия макросов
SELECT a.action, COUNT(*) AS n, COUNT(DISTINCT a.macros_id) AS macros, MIN(LEFT(a.value, 50)), MAX(LEFT(a.value, 50))
FROM macros_actions a JOIN macroses m ON m.id = a.macros_id
WHERE m.company_id = @company AND m.deleted = 0 GROUP BY a.action ORDER BY 3 DESC;
-- Папки: глубина
SELECT depth, COUNT(*) FROM macros_categories WHERE company_id = @company GROUP BY depth;
-- Макросы с несколькими папками и без папки
SELECT SUM(n > 1) AS several_folders, SUM(n = 0) AS no_folder FROM (
  SELECT m.id, COUNT(c.id) n FROM macroses m LEFT JOIN macroses_to_macros_categories c ON c.macros_id = m.id
  WHERE m.company_id = @company AND m.deleted = 0 GROUP BY m.id) x;
-- Доступ по группам: сколько макросов, связей, групп; связи с удалёнными группами
SELECT COUNT(DISTINCT m.id) AS macros, COUNT(*) AS links, COUNT(DISTINCT x.user_group_id) AS user_groups,
       SUM(g.id IS NULL OR g.deleted > 0) AS deleted_groups
FROM macroses m JOIN macroses_2_user_groups x ON x.macros_id = m.id LEFT JOIN user_groups g ON g.id = x.user_group_id
WHERE m.company_id = @company AND m.deleted = 0 AND m.privacy = 'groups';
SELECT COUNT(*) AS groups_privacy_without_groups FROM macroses m
WHERE m.company_id = @company AND m.deleted = 0 AND m.privacy = 'groups'
  AND NOT EXISTS (SELECT 1 FROM macroses_2_user_groups x WHERE x.macros_id = m.id);

-- ===== Таблицы, которые ещё не разобраны =====

SHOW TABLES LIKE '%work%';
SHOW TABLES LIKE '%schedule%';
SHOW TABLES LIKE '%holiday%';
SHOW TABLES LIKE '%bot%';
SHOW TABLES LIKE '%rating%';
SHOW TABLES LIKE '%csi%';
SHOW TABLES LIKE '%compan%';
