/* Behaviour shared by the list views: one way to press a button that changes something.

   Two rules encoded here rather than repeated in every view:
   - a mutating call is confirmed first, in words that name what will change;
   - the button disables itself while in flight, so a double click can't send two stop requests. */

import { h, reason } from '../ui.js';

/**
 * @param {object} ctx        View context.
 * @param {object} options
 * @param {string} options.action  Action id to invoke.
 * @param {object} [options.args]  Arguments.
 * @param {string} [options.ask]   Confirmation text; omitted means no prompt.
 * @param {string} [options.done]  Success toast text.
 * @param {HTMLButtonElement} [options.button]
 */
export async function perform(ctx, { action, args, ask, done, button }) {
  if (ask && !window.confirm(ask)) return false;
  button?.setAttribute('disabled', '');
  try {
    await ctx.api.invoke(action, args);
    ctx.notify(done ?? `${action} 完成`, 'ok');
    await ctx.refresh();
    return true;
  } catch (error) {
    ctx.notify(`${action} 失败：${reason(error)}`, 'bad');
    button?.removeAttribute('disabled');
    return false;
  }
}

/**
 * A button that runs an action. It renders disabled (with a title explaining why) when the identity
 * lacks the capability — the server would answer 403 anyway, but a button you can press only to be
 * told "no" is a worse interface than one that says "no" up front.
 */
export function actionButton(ctx, { action, args, label, ask, done, danger = false, disabledReason }) {
  const blocked = !ctx.allowed(action);
  return h('button', {
    class: `act${danger ? ' danger' : ''}`,
    type: 'button',
    text: label,
    disabled: blocked,
    title: blocked ? (disabledReason ?? `缺少能力（动作 ${action}）`) : '',
    onclick: (event) => perform(ctx, { action, args, ask, done, button: event.currentTarget }),
  });
}
