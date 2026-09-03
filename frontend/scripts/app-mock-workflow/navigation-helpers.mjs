import assert from 'node:assert/strict'
import { expectVisible } from './page-helpers.mjs'

export function activityButton(page, label) {
  return page.locator('nav').first().getByRole('button', { name: label })
}

export function novelCard(page, title) {
  return page.getByRole('article', { name: `作品卡片 ${title}`, exact: true })
}

export function tabLabel(page, title) {
  return page.locator('main').locator('> div').first().getByText(title, { exact: true })
}

export async function getActivityStates(page) {
  return await page.locator('nav').first().getByRole('button').evaluateAll((buttons) =>
    buttons.map((button) => {
      const title = button.getAttribute('title') ?? button.getAttribute('aria-label') ?? ''
      const label = title.replace(/（即将推出）$/, '')
      return {
        label,
        isActiveBackground: button.classList.contains('bg-muted') || button.classList.contains('bg-primary/15'),
        hasActiveIndicator: Array.from(button.querySelectorAll('span')).some((span) =>
          span.classList.contains('bg-primary')),
      }
    }),
  )
}

export async function assertActiveActivity(page, label) {
  await page.waitForFunction(
    (expectedLabel) => {
      const states = Array.from(document.querySelectorAll('nav:first-of-type button')).map((button) => {
        const title = button.getAttribute('title') ?? button.getAttribute('aria-label') ?? ''
        return {
          label: title.replace(/（即将推出）$/, ''),
          active: button.classList.contains('bg-muted') ||
            Array.from(button.querySelectorAll('span')).some((span) => span.classList.contains('bg-primary')),
        }
      })
      const active = states.filter((state) => state.active)
      return active.length === 1 && active[0].label === expectedLabel
    },
    label,
    { timeout: 12_000 },
  ).catch((error) => {
    throw new Error(`Expected active activity: ${label}`, { cause: error })
  })

  const states = await getActivityStates(page)
  const activeStates = states.filter((state) => state.isActiveBackground || state.hasActiveIndicator)
  assert.equal(activeStates.length, 1, `Expected exactly one active activity, got ${activeStates.map((state) => state.label).join(', ') || 'none'}.`)
  assert.equal(activeStates[0].label, label, `Expected active activity ${label}, got ${activeStates[0].label}.`)
  assert.equal(activeStates[0].isActiveBackground, true, `Expected ${label} to have active background.`)
  assert.equal(activeStates[0].hasActiveIndicator, true, `Expected ${label} to have active indicator.`)
}

export async function assertNoActiveActivity(page, description) {
  const activeStates = (await getActivityStates(page))
    .filter((state) => state.isActiveBackground || state.hasActiveIndicator)
  assert.deepEqual(activeStates.map((state) => state.label), [], `Expected no active activity for ${description}.`)
}

export async function assertHeaderButtonActive(page, label) {
  const isActive = await page.locator('header').getByRole('button', { name: label }).evaluate((button) =>
    button.classList.contains('text-foreground'))
  assert.equal(isActive, true, `Expected header button ${label} to be active.`)
}

export async function clickActivity(page, label) {
  await activityButton(page, label).click()
  await assertActiveActivity(page, label)
}

export async function ensureChapterBlockExpanded(page) {
  const firstChapter = chapterButton(page, '雨夜线索')
  if (await firstChapter.isVisible()) return

  const chapterBlock = page.getByRole('button', { name: /第 1 - \d+ 章/ })
  if (await chapterBlock.isVisible()) {
    await chapterBlock.click()
  }
  await expectVisible(firstChapter, 'expanded first chapter')
}

export async function ensureChapterBlockForTitleExpanded(page, title) {
  const target = chapterButton(page, title)
  if (await target.isVisible()) return

  const blocks = page.locator('aside').getByRole('button', { name: /第 \d+( - \d+)? 章/ })
  const count = await blocks.count()
  for (let index = 0; index < count; index += 1) {
    await blocks.nth(index).click()
    if (await target.isVisible()) return
  }

  await expectVisible(target, `expanded chapter ${title}`)
}

export function chapterButton(page, title) {
  return page.locator('aside').getByRole('button', { name: new RegExp(`第\\d+章\\s+${escapeRegExp(title)}`) })
}

export function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}
