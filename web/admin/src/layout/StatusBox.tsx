import { CheckCircleFilled, CloseCircleFilled, MinusCircleFilled } from '@ant-design/icons'
import { formatDateTime } from '../format'

/** Результат последней проверки подключения: зелёный — связь есть, красный — ошибка, серый — ещё не проверяли. */
export function StatusBox({ ok, at, text }: { ok: boolean | null; at: string | null; text: string }) {
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
