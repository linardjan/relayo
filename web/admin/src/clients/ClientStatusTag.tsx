import { Tag, Tooltip } from 'antd'
import { CheckCircleOutlined, WarningOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'

/** Подтверждён ли clientId (п. 3.3.2 ТЗ). */
export function ClientStatusTag({ isVerified, externalClientId }: { isVerified: boolean; externalClientId: string | null }) {
  const { t } = useTranslation()
  if (isVerified)
    return (
      <Tag icon={<CheckCircleOutlined />} color="success">
        {t('client.verified')}
      </Tag>
    )
  if (externalClientId)
    return (
      <Tooltip title={t('client.unverifiedHelp')}>
        <Tag icon={<WarningOutlined />} color="warning">
          {t('client.unverified')}
        </Tag>
      </Tooltip>
    )
  return <Tag>{t('client.anonymous')}</Tag>
}
