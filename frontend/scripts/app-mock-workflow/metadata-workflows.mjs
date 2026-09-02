import { newAppPage } from './app-harness.mjs'
import { clickActivity } from './navigation-helpers.mjs'
import {
  assertBridgeCallCount,
  assertButtonDisabled,
  bridgeCallCount,
  clickCardAction,
  expectHidden,
  expectVisible,
  waitForBridgeCall,
  waitForBridgeCallArg,
  waitForBridgeCallCountAfter,
} from './page-helpers.mjs'

export async function verifyMetadataPanels(page) {
  await page.getByTitle('角色').click()
  await expectVisible(page.getByRole('heading', { name: /角色/ }), 'characters heading')
  await expectVisible(page.getByText('林岚').first(), 'character fixture')

  await page.getByTitle('地点').click()
  await expectVisible(page.getByRole('heading', { name: /地点/ }), 'locations heading')
  await expectVisible(page.getByText('旧城门').first(), 'location fixture')

  await page.getByTitle('弧线').click()
  await expectVisible(page.getByRole('heading', { name: /弧线节点/ }), 'story arc heading')
  await expectVisible(page.getByText('雨夜调查线').first(), 'story arc fixture')

  await page.getByTitle('时间线').click()
  await expectVisible(page.getByRole('heading', { name: /章节计划/ }), 'timeline heading')
  await expectVisible(page.getByText('桌面水痕').first(), 'timeline fixture')

  await page.getByTitle('读者视角').click()
  await expectVisible(page.getByRole('heading', { name: /读者视角/ }), 'reader heading')
  await expectVisible(page.getByText(/读者知道林岚正在调查/).first(), 'reader fixture')

  await page.getByTitle('偏好').click()
  await expectVisible(page.getByRole('heading', { name: /创作偏好/ }), 'preference heading')
  await expectVisible(page.getByText(/保持受限视角/).first(), 'preference fixture')

  await page.getByTitle('技能').click()
  await expectVisible(page.getByText('技能 (2)'), 'skills side panel')
  await expectVisible(page.getByText('节奏控制').first(), 'skill fixture')
}

export async function verifyMetadataActionWorkflow(browser, url, consoleErrors, pageErrors) {
  await verifyMetadataEmptyStates(browser, url, consoleErrors, pageErrors)
  await verifyMetadataValidationWorkflow(browser, url, consoleErrors, pageErrors)
  await verifyMetadataBridgeFailureRecoveryWorkflow(browser, url, consoleErrors, pageErrors)

  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    confirmResult: true,
  })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'metadata action workspace')

  await verifyCharacterActions(page)
  await verifyLocationActions(page)
  await verifyStoryArcActions(page)
  await verifyTimelineActions(page)
  await verifyReaderActions(page)
  await verifyPreferenceActions(page)
  await verifyProfileActions(page)
  await verifySkillActions(page)

  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
  await page.close()
}

async function verifyMetadataEmptyStates(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    characters: [],
    locations: [],
    storyArcs: [],
    arcNodes: [],
    chapterPlans: [],
    timelineEntries: [],
    readerPerspectives: [],
    preferences: { global: [], novel: [] },
    writingActivity: [],
    writingStats: {
      total_words: 0,
      total_days_active: 0,
      current_streak: 0,
      longest_streak: 0,
      total_novels: 1,
      total_chapters: 2,
    },
    skills: [],
  })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'metadata empty workspace')

  await clickActivity(page, '角色')
  await expectVisible(page.locator('main').getByText('暂无角色'), 'empty characters state')
  await clickActivity(page, '地点')
  await expectVisible(page.locator('main').getByText('暂无地点'), 'empty locations state')
  await clickActivity(page, '弧线')
  await expectVisible(page.locator('main').getByText('暂无叙事弧线'), 'empty story arc state')
  await clickActivity(page, '时间线')
  await expectVisible(page.locator('main').getByText('暂无伏笔或用户指令'), 'empty timeline state')
  await clickActivity(page, '读者视角')
  await expectVisible(page.locator('main').getByText('暂无读者认知数据'), 'empty reader state')
  await clickActivity(page, '偏好')
  await expectVisible(page.locator('main').getByText('暂无全局偏好'), 'empty global preference state')
  await expectVisible(page.locator('main').getByText('暂无本书偏好'), 'empty novel preference state')
  await clickActivity(page, '技能')
  await expectVisible(page.locator('aside').getByText('暂无技能'), 'empty skill state')
  await page.locator('header').getByRole('button', { name: '个人中心' }).click()
  await expectVisible(page.getByText('还没有写作记录。开始写吧，每天的字数都会被记录下来。'), 'empty profile writing state')
  await page.close()
}

async function verifyMetadataValidationWorkflow(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(browser, consoleErrors, pageErrors, { initialized: true })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'metadata validation workspace')

  await clickActivity(page, '角色')
  await page.getByRole('button', { name: '新建角色' }).click()
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await expectVisible(page.locator('main').getByText('请输入角色名称'), 'character validation message')
  await assertBridgeCallCount(page, 'CreateCharacter', 0)

  await clickActivity(page, '地点')
  await page.getByRole('button', { name: '新建地点' }).click()
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await expectVisible(page.locator('main').getByText('请输入地点名称'), 'location validation message')
  await assertBridgeCallCount(page, 'CreateLocation', 0)

  await clickActivity(page, '弧线')
  await page.getByRole('button', { name: '新弧线' }).click()
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await expectVisible(page.locator('main').getByText('请输入弧线名称'), 'story arc validation message')
  await assertBridgeCallCount(page, 'CreateStoryArc', 0)

  await clickActivity(page, '时间线')
  await page.locator('main').getByRole('button', { name: '新建' }).click()
  await assertButtonDisabled(page.locator('main').getByRole('button', { name: '创建' }).last(), 'timeline create before title')
  await assertBridgeCallCount(page, 'CreateTimelineEntry', 0)

  await clickActivity(page, '读者视角')
  await page.locator('main').getByRole('button', { name: '新建' }).click()
  await assertButtonDisabled(page.locator('main').getByRole('button', { name: '创建' }).last(), 'reader create before content')
  await assertBridgeCallCount(page, 'CreateReaderPerspective', 0)

  await clickActivity(page, '偏好')
  await page.locator('section').filter({ hasText: '全局偏好' }).getByRole('button', { name: '添加' }).click()
  await assertButtonDisabled(page.locator('main').getByRole('button', { name: '创建' }).last(), 'preference create before content')
  await assertBridgeCallCount(page, 'CreatePreference', 0)

  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
  await page.close()
}

async function verifyMetadataBridgeFailureRecoveryWorkflow(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      CreateCharacter: { mode: 'storage', message: '模拟角色保存失败' },
    },
  })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'metadata bridge failure workspace')

  await clickActivity(page, '角色')
  await page.getByRole('button', { name: '新建角色' }).click()
  await page.getByPlaceholder('角色名称').fill('故障恢复角色')
  await page.getByPlaceholder('角色外貌、背景等自然语言描述').fill('第一次保存会失败，切换回来后重试成功。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'CreateCharacter')
  await expectVisible(page.locator('main').getByText('模拟角色保存失败'), 'character create failure message')

  const failedCreateCount = await bridgeCallCount(page, 'CreateCharacter')
  await clickActivity(page, '地点')
  await expectVisible(page.getByRole('heading', { name: /地点/ }), 'metadata recovery navigation target')
  await clickActivity(page, '角色')
  await expectVisible(page.getByText('林岚').first(), 'characters recovered after failed create')
  await page.getByRole('button', { name: '新建角色' }).click()
  await page.getByPlaceholder('角色名称').fill('故障恢复角色')
  await page.getByPlaceholder('角色外貌、背景等自然语言描述').fill('第二次保存成功，证明桥失败后可恢复。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCallCountAfter(page, 'CreateCharacter', failedCreateCount)
  await expectVisible(page.getByText('故障恢复角色').first(), 'character recovered after failed create retry')

  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
  await page.close()
}

async function verifyCharacterActions(page) {
  await clickActivity(page, '角色')
  await page.getByRole('button', { name: '新建角色' }).click()
  await page.getByPlaceholder('角色名称').fill('沈望')
  await page.getByPlaceholder('角色外貌、背景等自然语言描述').fill('在雨夜负责确认门外脚印。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'CreateCharacter')
  await expectVisible(page.getByText('沈望').first(), 'created character')

  await clickCardAction(page, '沈望', '编辑')
  await page.getByPlaceholder('角色名称').fill('沈望-修订')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'UpdateCharacter')
  await expectVisible(page.getByText('沈望-修订').first(), 'updated character')

  await clickCardAction(page, '沈望-修订', '删除')
  await waitForBridgeCall(page, 'DeleteCharacter')
  await expectHidden(page.locator('main').getByText('沈望-修订'), 'deleted character')
}

async function verifyLocationActions(page) {
  await clickActivity(page, '地点')
  await page.getByRole('button', { name: '新建地点' }).click()
  await page.getByPlaceholder('地点名称').fill('旧钟楼')
  await page.getByPlaceholder('如：森林、城市、洞穴').fill('建筑')
  await page.getByPlaceholder('环境氛围、特色等自然语言描述').fill('能俯瞰旧城门雨线的钟楼。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'CreateLocation')
  await expectVisible(page.getByText('旧钟楼').first(), 'created location')

  await clickCardAction(page, '旧钟楼', '编辑')
  await page.getByPlaceholder('地点名称').fill('旧钟楼-修订')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'UpdateLocation')
  await expectVisible(page.getByText('旧钟楼-修订').first(), 'updated location')

  await clickCardAction(page, '旧钟楼-修订', '删除')
  await waitForBridgeCall(page, 'DeleteLocation')
  await expectHidden(page.locator('main').getByText('旧钟楼-修订'), 'deleted location')
}

async function verifyStoryArcActions(page) {
  await clickActivity(page, '弧线')
  await page.getByRole('button', { name: '新弧线' }).click()
  await page.getByPlaceholder('弧线名称').fill('真相回收线')
  await page.getByPlaceholder('弧线整体描述').fill('覆盖弧线创建流程。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'CreateStoryArc')
  await expectVisible(page.getByText('真相回收线').first(), 'created story arc')

  await page.getByRole('button', { name: '新建节点' }).click()
  await page.getByPlaceholder('节点标题').fill('门外脚印')
  await page.getByPlaceholder('节点详情').fill('脚印把旧钟楼和旧城门连起来。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'CreateArcNode')
  await expectVisible(page.getByText('门外脚印').first(), 'created arc node')

  await clickCardAction(page, '门外脚印', '标记完成')
  await waitForBridgeCall(page, 'UpdateArcNode')
  await expectVisible(page.getByText('已完成').first(), 'arc node quick status')

  await clickCardAction(page, '门外脚印', '删除')
  await waitForBridgeCall(page, 'DeleteArcNode')
  await expectHidden(page.locator('main').getByText('门外脚印'), 'deleted arc node')
}

async function verifyTimelineActions(page) {
  await clickActivity(page, '时间线')
  await page.locator('section').filter({ hasText: '章节计划' }).getByTitle('编辑').click({ force: true })
  await page.getByPlaceholder('细纲计划内容...').fill('下一章让钟楼线索与旧城门交叉。')
  await page.locator('section').filter({ hasText: '章节计划' }).getByRole('button', { name: '保存' }).click()
  await waitForBridgeCall(page, 'UpdateChapterPlan')
  await expectVisible(page.getByText('下一章让钟楼线索与旧城门交叉。'), 'updated chapter plan')

  await page.locator('main').getByRole('button', { name: '新建' }).click()
  await page.getByPlaceholder('简短标题').fill('钥匙回收')
  await page.getByPlaceholder('详细描述').fill('钥匙在第二章前被重新提及。')
  await page.locator('main').getByRole('button', { name: '创建' }).last().click()
  await waitForBridgeCall(page, 'CreateTimelineEntry')
  await expectVisible(page.getByText('钥匙回收').first(), 'created timeline entry')

  await clickCardAction(page, '钥匙回收', '标记已回收')
  await waitForBridgeCall(page, 'UpdateTimelineEntry')
  await expectVisible(page.getByText('已回收').first(), 'timeline quick status')
}

async function verifyReaderActions(page) {
  await clickActivity(page, '读者视角')
  await page.locator('main').getByRole('button', { name: '新建' }).click()
  await page.getByPlaceholder('读者知道/想知道/误以为的事情').fill('读者误以为钟楼里的人已经离开。')
  await page.getByPlaceholder('真实情况是什么').fill('钟楼里的人仍在观察旧城门。')
  await page.locator('main').getByRole('button', { name: '创建' }).last().click()
  await waitForBridgeCall(page, 'CreateReaderPerspective')
  await expectVisible(page.getByText(/读者误以为钟楼里的人/).first(), 'created reader entry')

  await expectVisible(page.getByText('作者视角真相'), 'reader inspect detail')
  await clickCardAction(page, '读者误以为钟楼里的人', '标记已回收')
  await waitForBridgeCall(page, 'UpdateReaderPerspective')
  await expectVisible(page.getByText(/第1章回收/).first(), 'reader quick reveal')

  await clickCardAction(page, '读者误以为钟楼里的人', '删除')
  await waitForBridgeCall(page, 'DeleteReaderPerspective')
  await expectHidden(page.locator('main').getByText(/读者误以为钟楼里的人/), 'deleted reader entry')
}

async function verifyPreferenceActions(page) {
  await clickActivity(page, '偏好')
  await page.locator('section').filter({ hasText: '全局偏好' }).getByRole('button', { name: '添加' }).click()
  await page.getByPlaceholder('风格、对话、世界观...').fill('对白')
  await page.getByPlaceholder('偏好内容').fill('对话保留半句停顿。')
  await page.locator('main').getByRole('button', { name: '创建' }).last().click()
  await waitForBridgeCall(page, 'CreatePreference')
  await expectVisible(page.getByText('对话保留半句停顿。'), 'created preference')

  await clickCardAction(page, '对话保留半句停顿。', '编辑')
  await page.getByPlaceholder('偏好内容').fill('对话保留半句停顿，避免提前解释。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'UpdatePreference')
  await expectVisible(page.getByText('对话保留半句停顿，避免提前解释。'), 'updated preference')

  await clickCardAction(page, '对话保留半句停顿，避免提前解释。', '删除')
  await waitForBridgeCall(page, 'DeletePreference')
  await expectHidden(page.locator('main').getByText('对话保留半句停顿，避免提前解释。'), 'deleted preference')
}

async function verifyProfileActions(page) {
  await page.locator('header').getByRole('button', { name: '个人中心' }).click()
  await expectVisible(page.getByText('累计字数'), 'profile stats before edit')
  await page.getByText('Mock User').click()
  await page.locator('main').getByRole('textbox').fill('Metadata Tester')
  await page.keyboard.press('Enter')
  await waitForBridgeCall(page, 'SaveUserName')
  await expectVisible(page.getByText('Metadata Tester'), 'updated profile name')
}

async function verifySkillActions(page) {
  await clickActivity(page, '技能')
  await page.getByPlaceholder('搜索...').fill('节奏')
  await expectVisible(page.locator('aside').getByText('节奏控制'), 'filtered skill')
  await page.locator('aside').getByRole('button', { name: /节奏控制/ }).click()
  await waitForBridgeCallArg(page, 'GetContent', 1, 'skills/节奏控制.md')
  await expectVisible(page.getByText('保持停顿和动作之间的张力。'), 'inspected skill content')

  await clickCardAction(page.locator('aside'), '节奏控制', '删除技能')
  await waitForBridgeCall(page, 'DeleteSkill')
  await expectVisible(page.getByText('技能 (1)'), 'skill count after delete')
  await expectHidden(page.locator('aside').getByText('节奏控制'), 'deleted skill hidden in side panel')
}
