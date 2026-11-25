import { Form, Input, Modal, App as AntApp } from 'antd'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ticketsApi } from '../api/tickets'
import { useAuth } from '../auth/AuthContext'

/** Псевдоним: его видят клиенты вместо ФИО (п. 3.1 ТЗ). */
export function AliasModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation()
  const { user, updateUser } = useAuth()
  const { message } = AntApp.useApp()
  const [saving, setSaving] = useState(false)
  const [form] = Form.useForm<{ alias: string }>()

  const save = async () => {
    const { alias } = await form.validateFields()
    setSaving(true)
    try {
      updateUser(await ticketsApi.setAlias(alias))
      message.success(t('alias.saved'))
      onClose()
    } catch {
      message.error(t('alias.failed'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal
      open={open}
      title={t('alias.title')}
      okText={t('alias.save')}
      onOk={save}
      confirmLoading={saving}
      onCancel={onClose}
      destroyOnHidden
    >
      <p>{t('alias.help')}</p>
      <Form form={form} layout="vertical" initialValues={{ alias: user?.alias ?? '' }} preserve={false}>
        <Form.Item
          name="alias"
          label={t('alias.label')}
          rules={[{ required: true, whitespace: true, max: 100, message: t('alias.required') }]}
        >
          <Input autoFocus maxLength={100} />
        </Form.Item>
      </Form>
    </Modal>
  )
}
