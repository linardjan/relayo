import { Card, Empty, Typography } from 'antd'
import { useTranslation } from 'react-i18next'
import { Page } from '../layout/Page'
import type { MenuNode } from '../layout/menu'

/** Заглушка для раздела меню, который ещё не сделан. */
export function ComingSoonPage({ node, parent }: { node: MenuNode; parent?: MenuNode }) {
  const { t } = useTranslation()
  const crumbs = [...(parent ? [{ title: t(parent.label) }] : []), { title: t(node.label) }]
  return (
    <Page crumbs={crumbs}>
      <Card>
        <Empty
          style={{ margin: '48px 0' }}
          description={
            <>
              <Typography.Title level={5}>{t('common.comingSoon')}</Typography.Title>
              <Typography.Text type="secondary">{t('common.comingSoonHelp')}</Typography.Text>
            </>
          }
        />
      </Card>
    </Page>
  )
}
