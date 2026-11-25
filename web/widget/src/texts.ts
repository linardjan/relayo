// Тексты виджета. Чтобы добавить язык: добавить словарь с теми же ключами.
const en = {
  open: 'Open chat',
  close: 'Close chat',
  placeholder: 'Type a message',
  send: 'Send',
  operatorFallback: 'Support',
  you: 'You',
  connecting: 'Connecting…',
  sendFailed: 'Not sent. Click to retry.',
  loadFailed: 'Could not load the chat. Please try again later.',
  notConfigured: 'This messenger is not connected yet.',
  openTelegram: 'Continue in Telegram',
  emoji: 'Emoji',
  collapse: 'Hide chat buttons',
  expand: 'Show chat buttons',
  attach: 'Attach a file',
  removeFile: 'Remove',
  dropFiles: 'Drop files to attach',
  fileTooLarge: 'Files are too large: up to {mb} MB per message.',
  fileRejected: 'This file could not be sent.',
  typeNotAllowed: 'This file type is not supported.',
  file: 'File',
}

export type Texts = typeof en

const dictionaries: Record<string, Texts> = { en }

export function textsFor(locale: string | undefined): Texts {
  const code = (locale || 'en').toLowerCase().split(/[-_]/)[0]
  return dictionaries[code] ?? en
}
