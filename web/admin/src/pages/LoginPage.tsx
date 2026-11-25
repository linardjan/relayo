import { useState } from 'react'
import { Alert, Button, Card, Form, Input, Typography } from 'antd'
import { LockOutlined, UserOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { Navigate, useLocation, useNavigate } from 'react-router'
import { ApiError } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import logo from '../assets/login-logo.png'

interface LoginForm {
  login: string
  password: string
}

export function LoginPage() {
  const { t } = useTranslation()
  const { status, login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const from = (location.state as { from?: string } | null)?.from ?? '/'
  if (status === 'authenticated') return <Navigate to={from} replace />

  const onFinish = async (values: LoginForm) => {
    setError(null)
    setSubmitting(true)
    try {
      await login(values.login, values.password)
      navigate(from, { replace: true })
    } catch (e) {
      const status = e instanceof ApiError ? e.status : 0
      setError(status === 401 ? t('login.invalid') : status === 429 ? t('login.tooManyAttempts') : t('login.failed'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    // Логотип нарисован на чёрном фоне, поэтому и страница входа тёмная.
    <div style={{ minHeight: '100%', display: 'grid', placeItems: 'center', alignContent: 'center', gap: 8, background: '#000' }}>
      <img src={logo} alt={t('app.name')} style={{ width: 360, maxWidth: '90vw', height: 'auto' }} />
      <Card style={{ width: 360, maxWidth: '90vw' }}>
        <Typography.Title level={4} style={{ marginTop: 0 }}>
          {t('login.title')}
        </Typography.Title>
        {error && <Alert type="error" title={error} showIcon style={{ marginBottom: 16 }} />}
        <Form<LoginForm> layout="vertical" onFinish={onFinish} requiredMark={false} disabled={submitting}>
          <Form.Item name="login" label={t('login.login')} rules={[{ required: true, message: t('login.loginRequired') }]}>
            <Input prefix={<UserOutlined />} autoComplete="username" autoFocus />
          </Form.Item>
          <Form.Item name="password" label={t('login.password')} rules={[{ required: true, message: t('login.passwordRequired') }]}>
            <Input.Password prefix={<LockOutlined />} autoComplete="current-password" />
          </Form.Item>
          <Button type="primary" htmlType="submit" block loading={submitting}>
            {t('login.submit')}
          </Button>
        </Form>
      </Card>
    </div>
  )
}
