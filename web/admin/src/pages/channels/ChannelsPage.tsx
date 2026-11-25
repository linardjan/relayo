import { Button, Card, Dropdown, Empty, Spin, Typography } from 'antd'
import { DownOutlined, MessageOutlined, PlusOutlined, SendOutlined } from '@ant-design/icons'
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
  kind: 'widget' | 'telegram'
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
  ]

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
            ],
            onClick: ({ key }) => navigate(key === 'widget' ? '/channels/widgets/new' : '/channels/telegram/new'),
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
      {widgets.isLoading || bots.isLoading ? (
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
              <span className="channel-tile-icon">{tile.kind === 'widget' ? <ChatBubbles /> : <SendOutlined />}</span>
              <span className="channel-tile-count" title={t('channels.openTickets')}>
                {tile.count}
              </span>
              <span className="channel-tile-name">
                {tile.name} ({tile.kind === 'widget' ? t('channels.widget') : 'Telegram'})
              </span>
              {tile.unpublished && <span className="channel-tile-flag">{t('channels.unpublished')}</span>}
            </Link>
          ))}
        </div>
      )}
    </Page>
  )
}
