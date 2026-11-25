// Стили живут внутри Shadow DOM: стили сайта их не ломают, а наши не задевают сайт.
export const styles = (accent: string) => `
:host { all: initial; }
* { box-sizing: border-box; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif; }
button { border: 0; background: none; cursor: pointer; padding: 0; display: grid; place-items: center; color: inherit; font: inherit; }
button:focus-visible { outline: 2px solid #888; outline-offset: 3px; }
svg { display: block; }

/* Колонка круглых кнопок: чат, мессенджеры и кнопка сворачивания */
.launcher { position: fixed; bottom: 25px; z-index: 2147483000; display: flex; flex-direction: column; gap: 8px; }
.right .launcher { right: 11px; }
.left .launcher { left: 11px; }
.actions { display: flex; flex-direction: column; gap: 8px; }
.round { width: 48px; height: 48px; border-radius: 50%; color: #fff; box-shadow: 0 3px 10px rgba(0,0,0,.12); position: relative;
  transition: transform .15s; }
.round:hover { transform: scale(1.06); }
.round.chat { background: ${accent}; }
.round.telegram { background: #229ed9; }
.round.whatsapp { background: #24b74b; }
.round.toggle { background: #999; }
.badge { position: absolute; top: -3px; right: -3px; min-width: 20px; height: 20px; padding: 0 6px; border-radius: 10px;
  background: #ff4d4f; color: #fff; font-size: 12px; line-height: 20px; text-align: center; }

/* Окно чата слева от кнопок */
.widget { position: fixed; bottom: 17px; z-index: 2147483000; width: 400px; height: 516px; max-width: calc(100vw - 100px);
  max-height: calc(100dvh - 30px); border-radius: 12px; overflow: hidden; display: flex; flex-direction: column;
  background: #f0f1f8; box-shadow: 0 5px 20px rgba(0,0,0,.13); }
.right .widget { right: 86px; }
.left .widget { left: 86px; }
.header { height: 50px; flex-shrink: 0; background: ${accent}; color: #fff; display: flex; align-items: center;
  justify-content: space-between; padding: 0 19px; font-size: 16px; }
.header .title { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.header button { font-size: 26px; line-height: 1; width: 28px; height: 28px; }

.messages { flex: 1; overflow-y: auto; padding: 10px; display: flex; flex-direction: column; gap: 10px; }
.bubble { max-width: 85%; padding: 11px 14px 8px; border-radius: 12px; font-size: 13px; line-height: 1.4;
  white-space: pre-wrap; overflow-wrap: anywhere; box-shadow: 0 2px 2px rgba(0,0,0,.06); }
.bubble time { font-size: 12px; margin-left: 6px; vertical-align: -6px; white-space: nowrap; opacity: .75; float: right;
  margin-top: 4px; }
.out { align-self: flex-end; display: flex; flex-direction: column; align-items: flex-end; max-width: 85%; }
.out .bubble { max-width: 100%; background: ${accent}; color: #fff; }
.in { align-self: flex-start; display: flex; align-items: flex-end; gap: 10px; max-width: 90%; }
.in .bubble { max-width: none; background: #fff; color: #111; }
.in strong { display: block; font-size: 13px; margin-bottom: 4px; }
.avatar { width: 24px; height: 24px; flex-shrink: 0; border-radius: 50%; background: ${accent}; color: #fff;
  display: grid; place-items: center; margin-bottom: 5px; }
.system { align-self: center; color: #81899e; font-size: 12px; text-align: center; padding: 0 12px; }
.failed { color: #ff4d4f; font-size: 12px; cursor: pointer; margin-top: 3px; }
.notice { font-size: 12px; color: #81899e; text-align: center; padding: 4px; }
.error { font-size: 13px; color: #ff4d4f; text-align: center; padding: 8px; }

.composer { height: 60px; flex-shrink: 0; background: #fff; border-top: 1px solid #d8d8dd; display: flex; align-items: center;
  padding: 0 16px 0 19px; gap: 12px; position: relative; }
.composer input { min-width: 0; flex: 1; border: 0; outline: none; font-size: 15px; color: #222; background: transparent; }
.composer input::placeholder { color: #81899e; }
.composer > button { color: #81899e; }
.composer > button:hover { color: #222; }
.emoji { position: absolute; bottom: 66px; right: 16px; background: #fff; padding: 10px; border-radius: 8px;
  box-shadow: 0 2px 12px rgba(0,0,0,.2); display: grid; grid-template-columns: repeat(5, 32px); gap: 6px; }
.emoji button { font-size: 22px; width: 32px; height: 32px; }

@media (max-width: 480px) {
  .launcher { bottom: 18px; }
  .right .launcher { right: 9px; }
  .left .launcher { left: 9px; }
  .round { width: 46px; height: 46px; }
  .widget { bottom: 12px; max-width: calc(100vw - 78px); }
  .right .widget { right: 68px; }
  .left .widget { left: 68px; }
}
`
