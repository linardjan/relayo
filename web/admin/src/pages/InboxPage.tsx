import { Card, Empty } from 'antd'
import { useTranslation } from 'react-i18next'

export function InboxPage() {
  const { t } = useTranslation()
  return (
    <Card title={t('nav.inbox')}>
      <Empty description={`${t('inbox.empty')}. ${t('inbox.comingSoon')}`} />
    </Card>
  )
}
