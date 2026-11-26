import { Breadcrumb, Button, Card, Form, Space, Tag, Tooltip, type FormProps } from 'antd'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'

export interface Crumb {
  title: string
  to?: string
}

/**
 * Полоса над содержимым страницы: «хлебные крошки» слева, действия справа
 * (например, «Add channel»). Так пользователь всегда видит, где он, и может вернуться на уровень выше.
 */
export function PageHeader({ crumbs, extra }: { crumbs: Crumb[]; extra?: ReactNode }) {
  return (
    <div className="page-header">
      <Breadcrumb
        items={crumbs.map((c) => ({ title: c.to ? <Link to={c.to}>{c.title}</Link> : c.title }))}
      />
      {extra && <Space>{extra}</Space>}
    </div>
  )
}

/** Страница раздела: шапка с крошками и содержимое карточками друг под другом. */
export function Page({ crumbs, extra, children }: { crumbs: Crumb[]; extra?: ReactNode; children: ReactNode }) {
  return (
    <div className="page">
      <PageHeader crumbs={crumbs} extra={extra} />
      <Space orientation="vertical" size={16} style={{ width: '100%' }}>
        {children}
      </Space>
    </div>
  )
}

/** Карточка с серой шапкой, как у блоков настроек. */
export function SectionCard({ title, extra, children }: { title: ReactNode; extra?: ReactNode; children: ReactNode }) {
  return (
    <Card title={title} extra={extra} className="section-card">
      {children}
    </Card>
  )
}

/**
 * Форма настроек: подписи слева (выровнены по правому краю), поля справа.
 * Кнопки «Save» и «Cancel» стоят под полями, на одной линии с ними.
 */
export function SettingsForm<T extends object>({
  saving,
  onCancel,
  saveText,
  actions,
  children,
  ...props
}: FormProps<T> & { saving?: boolean; onCancel?: () => void; saveText?: string; actions?: ReactNode; children: ReactNode }) {
  const { t } = useTranslation()
  return (
    <Form<T>
      layout="horizontal"
      labelCol={{ flex: '240px' }}
      labelWrap
      // База 0, а не auto: иначе длинная подсказка под полем переносит поле под подпись.
      wrapperCol={{ flex: '1 1 0' }}
      colon={false}
      style={{ maxWidth: 1000 }}
      {...props}
    >
      {children}
      <Form.Item label=" ">
        <Space>
          <Button type="primary" htmlType="submit" loading={saving}>
            {saveText ?? t('settings.save')}
          </Button>
          {actions}
          {onCancel && <Button onClick={onCancel}>{t('settings.cancel')}</Button>}
        </Space>
      </Form.Item>
    </Form>
  )
}

/** Раскладка полей настроек: подпись слева (240px, по правому краю), поле справа. Общая для форм из нескольких карточек. */
export const settingsLayout = {
  layout: 'horizontal',
  labelCol: { flex: '240px' },
  labelWrap: true,
  wrapperCol: { flex: '1 1 0' },
  colon: false,
} as const

/** Кнопки под формой из нескольких карточек, на одной линии с полями: «Save», затем остальные действия. */
export function SaveBar({ saving, saveText, children }: { saving?: boolean; saveText?: string; children?: ReactNode }) {
  const { t } = useTranslation()
  return (
    <div className="save-bar">
      <Space>
        <Button type="primary" htmlType="submit" loading={saving}>
          {saveText ?? t('settings.save')}
        </Button>
        {children}
      </Space>
    </div>
  )
}

/** Пометка «Coming soon» для того, что повторяет раскладку Usedesk, но ещё не работает. */
export function SoonTag() {
  const { t } = useTranslation()
  return (
    <Tooltip title={t('common.comingSoonHelp')}>
      <Tag className="soon-tag">{t('common.comingSoon')}</Tag>
    </Tooltip>
  )
}

/** Поле, которого ещё нет: выключенный элемент и рядом пометка «Coming soon». */
export function SoonField({ label, children }: { label: ReactNode; children: ReactNode }) {
  return (
    <Form.Item label={label}>
      <span className="soon-field">
        {children}
        <SoonTag />
      </span>
    </Form.Item>
  )
}
