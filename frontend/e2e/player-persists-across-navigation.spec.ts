import { expect, test } from '@playwright/test'

test('the radio player never pauses while navigating between tabs', async ({ page }) => {
  await page.goto('/')

  // Conta eventos 'pause' reais do elemento de áudio, do lado do browser.
  await page.evaluate(() => {
    const audio = document.querySelector('audio')
    if (!audio) throw new Error('audio element not found')
    ;(window as unknown as { __pauseEvents: number }).__pauseEvents = 0
    audio.addEventListener('pause', () => {
      ;(window as unknown as { __pauseEvents: number }).__pauseEvents += 1
    })
  })

  await page.getByTestId('player-toggle').click()

  await page.waitForFunction(() => {
    const audio = document.querySelector('audio') as HTMLAudioElement | null
    return !!audio && !audio.paused && audio.currentTime > 0
  })

  const currentTimeBefore = await page.evaluate(
    () => (document.querySelector('audio') as HTMLAudioElement).currentTime,
  )

  await page.getByRole('link', { name: 'Grade' }).click()
  await expect(page.getByRole('heading', { name: 'Grade' })).toBeVisible()
  await expect.poll(() => page.evaluate(isAudioPaused)).toBe(false)

  await page.getByRole('link', { name: 'Pedir Música' }).click()
  await expect(page.getByRole('heading', { name: 'Pedir Música' })).toBeVisible()
  await expect.poll(() => page.evaluate(isAudioPaused)).toBe(false)

  await page.getByRole('link', { name: 'Mural' }).click()
  await expect(page.getByRole('heading', { name: 'Mural' })).toBeVisible()
  await expect.poll(() => page.evaluate(isAudioPaused)).toBe(false)

  const currentTimeAfter = await page.evaluate(
    () => (document.querySelector('audio') as HTMLAudioElement).currentTime,
  )
  const pauseEvents = await page.evaluate(
    () => (window as unknown as { __pauseEvents: number }).__pauseEvents,
  )

  expect(currentTimeAfter).toBeGreaterThan(currentTimeBefore)
  expect(pauseEvents).toBe(0)
})

function isAudioPaused() {
  const audio = document.querySelector('audio') as HTMLAudioElement | null
  return audio ? audio.paused : true
}
