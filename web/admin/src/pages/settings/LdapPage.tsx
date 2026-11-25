import { App as AntApp, Button, Form, Input, InputNumber, Select, Space, Spin, Switch, Typography } from 'antd'
import { CheckCircleFilled, CloseCircleFilled, MinusCircleFilled } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import type { Role } from '../../api/client'
import { settingsApi, type DirectoryProvider, type LdapSecurity, type LdapSettings, type LdapSettingsInput } from '../../api/settings'
import { formatDateTime } from '../../format'
import { Page, SectionCard, SettingsForm } from '../../layout/Page'
import { errorText } from '../../settings/errors'

const roles: Role[] = ['Operator', 'Supervisor', 'Admin']
const defaultPort: Record<LdapSecurity, number> = { Ldaps: 636, StartTls: 389 }
const providerAttributes: Record<DirectoryProvider, Pick<LdapSettingsInput, 'guidAttribute' | 'loginAttribute' | 'fullNameAttribute'>> = {
  ActiveDirectory: { guidAttribute: 'objectGUID', loginAttribute: 'cn', fullNameAttribute: 'displayName' },
  OpenLdap: { guidAttribute: 'entryUUID', loginAttribute: 'uid', fullNameAttribute: 'cn' },
}

/**
 * Подключение к AD (п. 3.1 ТЗ): сервер, служебная учётка, папка с пользователями.
 * Сохранение сразу проверяет связь; статус и результат последнего импорта видны внизу формы.
 */
export function LdapPage() {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const queryClient = useQueryClient()
  const { data, isLoading } = useQuery({ queryKey: ['ldap'], queryFn: settingsApi.ldap })
  const [form] = Form.useForm<LdapSettingsInput>()
  const verifyCertificate = Form.useWatch('verifyCertificate', form)

  useEffect(() => {
    if (data) form.setFieldsValue({ ...data, bindPassword: '' })
  }, [data, form])

  const done = (settings: LdapSettings) => {
    queryClient.setQueryData(['ldap'], settings)
    form.setFieldValue('bindPassword', '')
  }
  const save = useMutation({
    mutationFn: (input: LdapSettingsInput) => settingsApi.saveLdap({ ...input, bindPassword: input.bindPassword || undefined }),
    onSuccess: (s) => {
      done(s)
      message[s.lastCheckOk ? 'success' : 'warning'](s.lastCheckOk ? t('ldap.savedConnected') : t('ldap.savedNotConnected'))
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })
  const check = useMutation({
    mutationFn: settingsApi.checkLdap,
    onSuccess: done,
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })
  const sync = useMutation({
    mutationFn: settingsApi.syncLdap,
    onSuccess: (s) => {
      done(s)
      void queryClient.invalidateQueries({ queryKey: ['users'] })
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const crumbs = [{ title: t('nav.settings') }, { title: 'LDAP' }]
  if (isLoading || !data) return <Page crumbs={crumbs}><Spin style={{ margin: 48 }} /></Page>
  const busy = save.isPending || check.isPending || sync.isPending

  return (
    <Page crumbs={crumbs}>
      <SectionCard title="LDAP">
        <SettingsForm<LdapSettingsInput>
          form={form}
          onFinish={(v) => save.mutate(v)}
          saving={save.isPending}
          onValuesChange={(changed: Partial<LdapSettingsInput>, all) => {
            // Порт и атрибуты подстраиваются под выбор, если их не меняли вручную.
            if (changed.security && Object.values(defaultPort).includes(all.port))
              form.setFieldValue('port', defaultPort[changed.security])
            if (changed.provider) form.setFieldsValue(providerAttributes[changed.provider])
          }}
          actions={
            <>
              <Button onClick={() => check.mutate()} loading={check.isPending} disabled={busy || !data.host}>
                {t('ldap.check')}
              </Button>
              <Button onClick={() => sync.mutate()} loading={sync.isPending} disabled={busy || !data.host}>
                {t('ldap.import')}
              </Button>
            </>
          }
        >
          <Form.Item name="isEnabled" label={t('ldap.enabled')} valuePropName="checked" extra={t('ldap.enabledHelp')}>
            <Switch />
          </Form.Item>
          <Form.Item name="host" label={t('ldap.host')} rules={[{ required: true }]}>
            <Input placeholder="dc1.company.local" />
          </Form.Item>
          <Form.Item name="bindDn" label={t('ldap.bindDn')} extra={t('ldap.bindDnHelp')} rules={[{ required: true }]}>
            <Input placeholder="CN=relayo-sync,OU=Service Accounts,DC=company,DC=local" />
          </Form.Item>
          <Form.Item
            name="bindPassword"
            label={t('ldap.password')}
            extra={data.hasBindPassword ? t('ldap.passwordKeep') : undefined}
            rules={data.hasBindPassword ? [] : [{ required: true }]}
          >
            <Input.Password autoComplete="new-password" placeholder={data.hasBindPassword ? '••••••••' : undefined} />
          </Form.Item>
          <Form.Item name="security" label={t('ldap.security')} extra={t('ldap.securityHelp')}>
            <Select
              options={[
                { value: 'Ldaps', label: 'LDAPS (SSL)' },
                { value: 'StartTls', label: 'StartTLS' },
              ]}
            />
          </Form.Item>
          <Form.Item
            name="verifyCertificate"
            label={t('ldap.verifyCertificate')}
            valuePropName="checked"
            extra={verifyCertificate === false ? <Typography.Text type="warning">{t('ldap.verifyOffWarning')}</Typography.Text> : undefined}
          >
            <Switch />
          </Form.Item>
          <Form.Item name="usersDn" label={t('ldap.usersDn')} extra={t('ldap.usersDnHelp')} rules={[{ required: true }]}>
            <Input placeholder="OU=Support,DC=company,DC=local" />
          </Form.Item>
          <Form.Item name="port" label={t('ldap.port')} rules={[{ required: true }]}>
            <InputNumber min={1} max={65535} style={{ width: 140 }} />
          </Form.Item>
          <Form.Item name="provider" label={t('ldap.provider')}>
            <Select
              options={[
                { value: 'ActiveDirectory', label: 'Active Directory' },
                { value: 'OpenLdap', label: 'OpenLDAP' },
              ]}
            />
          </Form.Item>
          <Form.Item name="guidAttribute" label={t('ldap.guidAttribute')} extra={t('ldap.guidAttributeHelp')} rules={[{ required: true }]}>
            <Input />
          </Form.Item>
          <Form.Item name="loginAttribute" label={t('ldap.loginAttribute')} extra={t('ldap.loginAttributeHelp')} rules={[{ required: true }]}>
            <Input />
          </Form.Item>
          <Form.Item name="fullNameAttribute" label={t('ldap.fullNameAttribute')} rules={[{ required: true }]}>
            <Input />
          </Form.Item>
          <Form.Item name="mailAttribute" label={t('ldap.mailAttribute')} extra={t('ldap.optionalAttribute')}>
            <Input placeholder="mail" />
          </Form.Item>
          <Form.Item name="phoneAttribute" label={t('ldap.phoneAttribute')}>
            <Input placeholder="telephoneNumber" />
          </Form.Item>
          <Form.Item name="positionAttribute" label={t('ldap.positionAttribute')}>
            <Input placeholder="title" />
          </Form.Item>
          <Form.Item name="defaultRole" label={t('ldap.defaultRole')} extra={t('ldap.defaultRoleHelp')}>
            <Select options={roles.map((r) => ({ value: r, label: t(`user.roles.${r}`) }))} />
          </Form.Item>
          <Form.Item label={t('ldap.syncInterval')} extra={t('ldap.syncIntervalHelp')}>
            <Space.Compact>
              <Form.Item name="syncIntervalMinutes" noStyle>
                <InputNumber min={0} max={10080} style={{ width: 120 }} />
              </Form.Item>
              <Button disabled tabIndex={-1} style={{ color: 'rgba(0,0,0,0.65)', cursor: 'default' }}>
                {t('ldap.minutes')}
              </Button>
            </Space.Compact>
          </Form.Item>
          <Form.Item label={t('ldap.status')}>
            <StatusBox ok={data.lastCheckOk} at={data.lastCheckAt} text={data.lastCheckMessage ?? t('ldap.notChecked')} />
          </Form.Item>
          <Form.Item label={t('ldap.lastImport')}>
            <StatusBox ok={data.lastSyncOk} at={data.lastSyncAt} text={data.lastSyncMessage ?? t('ldap.notImported')} />
          </Form.Item>
        </SettingsForm>
      </SectionCard>
    </Page>
  )
}

function StatusBox({ ok, at, text }: { ok: boolean | null; at: string | null; text: string }) {
  const kind = ok === null ? 'none' : ok ? 'ok' : 'error'
  return (
    <div className={`status-box ${kind}`}>
      {kind === 'ok' ? <CheckCircleFilled /> : kind === 'error' ? <CloseCircleFilled /> : <MinusCircleFilled />}
      <div>
        <div>{text}</div>
        {at && <div className="status-box-time">{formatDateTime(at)}</div>}
      </div>
    </div>
  )
}
