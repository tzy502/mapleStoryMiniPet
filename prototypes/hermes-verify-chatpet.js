const fs = require('fs');
const path = '/Users/a502/IdeaProjects/mapleStoryMiniPet/prototypes/ChatPetPanel.html';
const html = fs.readFileSync(path, 'utf8');

let ok = true, errors = [];

function check(desc, pass) {
  if (!pass) { ok = false; errors.push('✗ ' + desc); }
}
// 1. forbidden keywords absent
for (const w of ['设置', '素材', '搭配']) {
  check(`no "${w}"`, !html.includes(w));
}
// 2. title
check('title "💬 桌宠对话"', html.includes('<title>💬 桌宠对话'));
check('title-text "💬 桌宠对话"', html.includes('"💬 桌宠对话"'));
// 3. title-right: only 1 button (minimize), no ⚙
check('title-right has minimize', html.includes('title="最小化"'));
const iconBtns = (html.match(/class="icon-btn"/g) || []).length;
check(`title-right has exactly 1 icon-btn (${iconBtns})`, iconBtns === 1);
// 4. quick actions
const qb = (html.match(/class="quick-btn"/g) || []).length;
check(`exactly 5 quick-btn (${qb})`, qb === 5);
// 5. required quick labels
for (const l of ['查怪物', '切怪', '天气', '讲个笑话', '运势']) {
  check(`quick-btn contains "${l}"`, html.includes(l));
}
// 6. demo responses: no settings/搭配/素材, has 笑话/运势
check('no demo "搭配"', !html.includes("'搭配':"));
check('no demo "设置"', !html.includes("'设置':"));
check('no demo "素材"', !html.includes("'素材':"));
check('demo "笑话"', html.includes("'笑话':"));
check('demo "运势"', html.includes("'运势':"));
// 7. welcome message clean
check('welcome no "开设置"', !html.includes('开设置'));
// 8. structural integrity
for (const m of ['<!DOCTYPE html>', 'id="chatArea"', 'function sendMsg', 'function sendQuick', '</html>']) {
  check(`structure: ${m}`, html.includes(m));
}

if (ok) {
  console.log('✓ AD-HOC VERIFY PASS — ChatPetPanel.html purified correctly');
  console.log('  Title: 💬 桌宠对话 | Quick: 5 | Title-right: 1 (minimize only) | 设置/素材/搭配: 0');
} else {
  console.log('✗ FAILED — ' + errors.length + ' issue(s):');
  errors.forEach(e => console.log('  ' + e));
  process.exit(1);
}