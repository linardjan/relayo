import { Button, Card, Dropdown, Empty, Spin, Typography } from 'antd'
import { DownOutlined, MailOutlined, MessageOutlined, PlusOutlined, SendOutlined, WhatsAppOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate } from 'react-router'
import { settingsApi } from '../../api/settings'
import { Page } from '../../layout/Page'

/** Два облачка переписки: значок виджета на плитке. */
const ChatBubbles = () => (
  <svg viewBox="0 0 100 88" width="1em" height="0.88em" fill="currentColor" aria-hidden="true">
    <path d="M14 28H30V59H65V65Q65 78 52 78H27L14 88V78Q2 78 2 65V41Q2 28 14 28Z" />
    <path d="M48 3H87Q100 3 100 16V42Q100 54 87 54V68L71 54H35V16Q35 3 48 3Z" />
  </svg>
)

interface Tile {
  key: string
  to: string
  name: string
  kind: 'widget' | 'telegram' | 'whatsapp' | 'email'
  count: number
  enabled: boolean
  hint: string
  unpublished?: boolean
}

/**
 * Каналы плитками: число открытых тикетов, название и тип. Клик по плитке открывает настройки канала.
 */
export function ChannelsPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const widgets = useQuery({ queryKey: ['widgets'], queryFn: settingsApi.widgets })
  const bots = useQuery({ queryKey: ['telegramBots'], queryFn: settingsApi.telegramBots })
  const whatsApp = useQuery({ queryKey: ['whatsAppChannels'], queryFn: settingsApi.whatsAppChannels })
  const email = useQuery({ queryKey: ['emailChannels'], queryFn: settingsApi.emailChannels })
  const sites = useQuery({ queryKey: ['sites'], queryFn: settingsApi.sites })
  const counts = useQuery({ queryKey: ['channelCounts'], queryFn: settingsApi.channelCounts, refetchInterval: 60_000 })
  const noSites = sites.data?.length === 0

  const tiles: Tile[] = [
    ...(widgets.data ?? []).map(({ widget: w }) => ({
      key: `w-${w.id}`,
      to: `/channels/widgets/${w.id}`,
      name: w.name,
      kind: 'widget' as const,
      count: counts.data?.widgets[w.id] ?? 0,
      enabled: true,
      unpublished: w.hasUnpublishedChanges,
      hint: `${t('client.site')}: ${w.siteCode} · ${w.identificationMode === 'SignedToken' ? t('widgets.modeToken') : t('widgets.modeDirect')}`,
    })),
    ...(bots.data ?? []).map((b) => ({
      key: `t-${b.id}`,
      to: `/channels/telegram/${b.id}`,
      name: b.name,
      kind: 'telegram' as const,
      count: counts.data?.telegramBots[b.id] ?? 0,
      enabled: b.isEnabled,
      hint: `@${b.username} · ${t('client.site')}: ${b.siteCode}${b.isEnabled ? '' : ` · ${t('telegram.disabled')}`}`,
    })),
    ...(whatsApp.data ?? []).map((c) => ({
      key: `wa-${c.id}`,
      to: `/channels/whatsapp/${c.id}`,
      name: c.name,
      kind: 'whatsapp' as const,
      count: counts.data?.whatsAppChannels?.[c.id] ?? 0,
      enabled: c.isEnabled,
      hint: [
        c.phoneNumber ? `+${c.phoneNumber}` : null,
        `${t('client.site')}: ${c.siteCode}`,
        c.mode === 'Gateway' ? (c.isConnected ? t('whatsapp.connected') : t('whatsapp.notConnected')) : t('whatsapp.modeLinkOnly'),
        c.isEnabled ? null : t('telegram.disabled'),
      ]
        .filter(Boolean)
        .join(' · '),
    })),
    // Тикетов из почты пока нет: письма начнут приходить со следующим шагом.
    ...(email.data ?? []).map((c) => ({
      key: `e-${c.id}`,
      to: `/channels/email/${c.id}`,
      name: c.name,
      kind: 'email' as const,
      count: 0,
      enabled: c.isEnabled,
      hint: [c.imap.email, `${t('client.site')}: ${c.siteCode}`, c.lastCheckOk === false ? t('email.checkFailedShort') : null, c.isEnabled ? null : t('telegram.disabled')]
        .filter(Boolean)
        .join(' · '),
    })),
  ]
  const tileIcon = { widget: <ChatBubbles />, telegram: <SendOutlined />, whatsapp: <WhatsAppOutlined />, email: <MailOutlined /> }
  const tileType = { widget: t('channels.widget'), telegram: 'Telegram', whatsapp: 'WhatsApp', email: t('channels.email') }
  const newChannel: Record<string, string> = {
    widget: '/channels/widgets/new',
    telegram: '/channels/telegram/new',
    whatsapp: '/channels/whatsapp/new',
    email: '/channels/email/new',
  }

  return (
    <Page
      crumbs={[{ title: t('nav.channels') }]}
      extra={
        <Dropdown
          disabled={noSites}
          menu={{
            items: [
              { key: 'widget', icon: <MessageOutlined />, label: t('channels.widget') },
              { key: 'telegram', icon: <SendOutlined />, label: t('channels.telegram') },
              { key: 'whatsapp', icon: <WhatsAppOutlined />, label: 'WhatsApp' },
              { key: 'email', icon: <MailOutlined />, label: t('channels.email') },
            ],
            onClick: ({ key }) => navigate(newChannel[key]),
          }}
        >
          <Button type="primary" icon={<PlusOutlined />}>
            {t('channels.add')} <DownOutlined />
          </Button>
        </Dropdown>
      }
    >
      {noSites && (
        <Card>
          <Typography.Text type="secondary">
            {t('channels.needSite')} <Link to="/settings/sites/new">{t('sites.add')}</Link>
          </Typography.Text>
        </Card>
      )}
      {widgets.isLoading || bots.isLoading || whatsApp.isLoading || email.isLoading ? (
        <Spin style={{ margin: 48 }} />
      ) : tiles.length === 0 ? (
        !noSites && (
          <Card>
            <Empty description={t('channels.empty')} />
          </Card>
        )
      ) : (
        <div className="channel-tiles">
          {tiles.map((tile) => (
            <Link key={tile.key} to={tile.to} title={tile.hint} className={`channel-tile ${tile.kind}${tile.enabled ? '' : ' disabled'}`}>
              <span className="channel-tile-icon">{tileIcon[tile.kind]}</span>
              <span className="channel-tile-count" title={t('channels.openTickets')}>
                {tile.count}
              </span>
              <span className="channel-tile-name">
                {tile.name} ({tileType[tile.kind]})
              </span>
              {tile.unpublished && <span className="channel-tile-flag">{t('channels.unpublished')}</span>}
            </Link>
          ))}
        </div>
      )}
    </Page>
  )
}
