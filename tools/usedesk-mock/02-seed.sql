-- Фейковые данные тестовой БД Usedesk для проверки импорта (tools/checks/usedesk-import.cjs).
-- Наша компания — 7, компания 8 — чужая (её данные импорт не должен видеть).
-- Токены и пароли — явно фейковые.
SET SESSION sql_mode = '';
SET NAMES utf8mb4;

INSERT INTO users (id, email, password, name, position, phone, role, company_id, lang, status, deleted, use_signature) VALUES
  (101, 'anna.ivanova@example.test', 'x', 'Анна', 'Старший оператор', '+7 900 000-00-01', 'admin', 7, 'ru', 1, 0, ''),
  (102, 'boris.petrov@example.test', 'x', 'Борис', NULL, NULL, 'employee', 7, 'ru', 1, 1, ''),
  (103, 'vera.smirnova@example.test', 'x', 'Вера', 'Оператор', NULL, 'employee', 7, 'en', 1, 0, ''),
  (104, 'support@usedesk.example', 'x', 'Usedesk Support', NULL, NULL, 'support', 7, 'ru', 1, 0, ''),
  (105, 'bot@example.test', 'x', 'Бот', NULL, NULL, 'bot', 7, 'ru', 1, 0, ''),
  (201, 'other@example.test', 'x', 'Чужой', NULL, NULL, 'admin', 8, 'ru', 1, 0, '');

INSERT INTO users_guid_migration (original_id, duplicate_guid) VALUES
  (101, '3f2504e0-4f89-11d3-9a0c-0305e82c3301');

INSERT INTO company_email_channels
  (id, name, incoming_connection, outgoing_connection, imap_host, imap_port, imap_username, imap_password, imap_encrypted,
   from_name, from_email, smtp_host, smtp_port, smtp_username, smtp_password, smtp_encrypted, company_id, type, quotes, deleted, main_email)
VALUES
  (10, 'Почта поддержки', 'external', 'external', 'imap.example.test', 993, 'support@example.test', 'fake-imap-password', 'ssl',
   'Поддержка', 'support@example.test', 'smtp.example.test', 587, 'support@example.test', 'fake-smtp-password', 'tls', 7, 'email', 1, 0, 'support@example.test'),
  (11, 'Чат на сайте', 'internal', 'internal', NULL, NULL, NULL, NULL, '', '', NULL, NULL, NULL, NULL, NULL, '', 7, 'chat', 0, 0, NULL),
  (12, 'Telegram-бот', 'internal', 'internal', NULL, NULL, NULL, NULL, '', '', NULL, NULL, NULL, NULL, NULL, '', 7, 'telegram', 0, 0, NULL),
  (13, 'WhatsApp поддержки', 'internal', 'internal', NULL, NULL, NULL, NULL, '', '', NULL, NULL, NULL, NULL, NULL, '', 7, 'whatsapp', 0, 0, NULL),
  (14, 'Старая почта', 'external', 'external', 'imap.old.example.test', 143, 'old', 'fake-old-password', 'tls',
   'Old', 'old@example.test', 'smtp.old.example.test', 25, 'old', 'fake-old-password', '', 7, 'email', 0, 1, NULL),
  (15, 'Внутренняя почта', 'internal', 'internal', NULL, NULL, NULL, NULL, '', 'Inner', 'inner@example.test', NULL, NULL, NULL, NULL, '', 7, 'email', 1, 0, NULL),
  (20, 'Чужой канал', 'external', 'external', 'imap.other.example.test', 993, 'x@other.example.test', 'fake', 'ssl',
   'X', 'x@other.example.test', 'smtp.other.example.test', 465, 'x@other.example.test', 'fake', 'ssl', 8, 'email', 1, 0, NULL);

INSERT INTO telegram_settings (id, token, channel_id, company_id) VALUES
  (1, '123456:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA', '12', 7),
  (2, '654321:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB', '20', 8);

INSERT INTO pact_settings (id, channel_id, company_id, is_active, provider_type, account_name, group_chats, deleted_at) VALUES
  (1, 13, 7, 1, 'whatsapp', 'Support WA', 0, NULL),
  (2, 13, 7, 0, 'whatsapp', 'Removed WA', 0, '2025-01-01 00:00:00');

INSERT INTO usedesk_widget
  (id, company_id, company_email_channel_id, title, color, attachment, z_index, language, badge_text, badge_text_color, badge_color,
   badge_font_size, aligment, button_color, main_icon, powered_by, custom_fields, operator_name, name, show_tooltip_type, hide_when_clicked_outside)
VALUES
  (1, 7, 11, 'Напишите нам', '#32a8d9', 1, 1050, 'RU', '<b>Нужна помощь?</b>', '#ffffff', '#ff7048',
   14, 'left', '#ff7048', '', '', '[]', 'Поддержка', 'Виджет магазина', 1, 0),
  (2, 8, 20, 'Other', '#000000', 1, 1050, 'EN', NULL, NULL, NULL, 12, 'right', NULL, '', '', '[]', NULL, 'Чужой виджет', 0, 1);

INSERT INTO messenger_settings (id, company_id, facebook_url, vk_url, viber_url, tg_url, channel_id, active_json) VALUES
  (1, 7, '', 'https://vk.com/example', 'viber://chat?number=79990000000', 'https://t.me/relayo_fake_bot', 11,
   '[{"type":"CHAT_CALLBACK_FORM"},{"type":"TG","externalLink":"https://t.me/relayo_fake_bot"},{"type":"WHATSAPP","externalLink":"79990000000"},{"type":"VK"},{"type":"VIBER"}]');
