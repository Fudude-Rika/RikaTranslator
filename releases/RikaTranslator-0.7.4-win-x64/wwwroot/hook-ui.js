'use strict';

const hookSnapshots = {};
const hookUrl = id => `/api/games/${encodeURIComponent(id)}/hook`;
function hookModal(title,content) { modal(title,'直接提取游戏文字，调用自己的翻译服务。',content); }
function hookCard(g) {
  if (!['x86','x64'].includes(g.architecture)) return '';
  return `<section class="card hook-card" id="game-hook" data-game-id="${esc(g.id)}"><div class="card-header"><div><h2>${icon('plug')}Galgame 文本 Hook</h2><p>选择对话通道，使用当前 API 实时翻译。</p></div><button class="btn ghost small" data-action="hook-refresh" data-id="${esc(g.id)}">${icon('refresh')}刷新</button></div><p class="tiny subtle">适用于可取词的 Windows 32/64 位游戏。浮窗直接显示译文；内嵌仅对支持回填的文本通道开放。</p><div class="hook-controls"><button class="btn primary" data-action="hook-start" data-id="${esc(g.id)}">${icon('play')}启动并连接</button><button class="btn" data-action="hook-processes" data-id="${esc(g.id)}">选择已运行进程</button><button class="btn" data-action="hook-settings" data-id="${esc(g.id)}">${icon('settings')}字体与显示</button></div><div id="hook-session-body">${hookSessionBody(g,hookSnapshots[g.id])}</div></section>`;
}
function hookSessionBody(g,s) {
  if (!s) return '<p class="tiny subtle section-gap">正在读取 Hook 状态…</p>';
  const stage={stopped:'未连接',connecting:'正在连接游戏',connected:'Hook 已连接',receiving:'正在翻译',translated:'已获得译文',disconnected:'游戏已断开',error:'连接出现问题'}[s.stage]||s.stage;
  const rows=[...(s.threads||[])].sort((a,b)=>Number(b.embeddable)-Number(a.embeddable)||b.count-a.count).map(t=>`<button class="hook-thread ${s.selectedId===t.id?'selected':''}" data-action="hook-select" data-id="${esc(g.id)}" data-thread="${esc(t.id)}"><div><strong>${esc(t.name||'未命名通道')}</strong> ${badge(t.embeddable?'可内嵌':'浮窗',t.embeddable?'blue':'')} ${s.selectedId===t.id?badge('已选择','green'):''}<small>${number(t.count)} 条</small></div><p>${esc(t.preview||'尚未捕获文字，请在游戏中显示对话')}</p><small class="mono">${esc(t.hookCode)} · ${esc(t.context)} / ${esc(t.context2)}</small></button>`).join('');
  return `<div class="section-gap">${badge(stage,s.active?'blue':'')}${s.pid?badge('PID '+s.pid):''}${s.embedding?badge('内嵌已开启','orange'):badge('翻译浮窗')}</div>${!s.componentsPresent?notice('Hook 组件不完整，请使用完整便携目录或本地更新包。','error'):''}${s.lastError?notice(esc(s.lastError),'error'):''}<p class="tiny subtle section-gap">${s.active?(s.selectedId?'正在翻译选定通道。':'请让游戏显示一段对话，再点击下面能正确显示原文的通道。选择前不会请求翻译。'):'连接不会向游戏目录安装文件。测试时请使用游戏副本。'}</p><div class="hook-threads">${rows||'<div class="inline-empty">暂无文本通道</div>'}</div><div class="adapter-actions"><button class="btn small" data-action="hook-overlay" data-id="${esc(g.id)}" ${!s.overlayAvailable?'disabled':''}>${icon('monitor')}${s.overlayShown?'隐藏浮窗':'显示浮窗'}</button><button class="btn small" data-action="hook-code" data-id="${esc(g.id)}" ${!s.active?'disabled':''}>手动 Hook 代码</button><button class="btn small" data-action="hook-retry" data-id="${esc(g.id)}" ${!s.selectedId?'disabled':''}>重新翻译</button><button class="btn danger small" data-action="hook-stop" data-id="${esc(g.id)}" ${!s.active?'disabled':''}>停止连接</button></div><div class="runtime-counts section-gap">${[['收到取词',s.received],['获得译文',s.translated],['内嵌接收',s.embeddedReplies],['内嵌超时',s.embeddingTimeouts]].map(([name,value])=>`<div><strong>${number(value||0)}</strong><small>${name}</small></div>`).join('')}</div><p class="tiny subtle section-gap">${esc(s.displayStatus||'')}</p>${s.source?`<div class="hook-preview"><small>最近选定原文</small><p>${esc(s.source)}</p><small>译文</small><p>${esc(s.translation||s.displayStatus||'等待译文…')}</p></div>`:''}<p class="tiny subtle section-gap">浮窗可拖动、调整大小；快捷键见浮窗下方；Ctrl+Alt+T 被占用时自动尝试 Ctrl+Alt+F8。停止连接后游戏继续运行。</p>`;
}
async function refreshHook(id,reportErrors=false) {
  const holder=document.getElementById('hook-session-body'),card=document.getElementById('game-hook');
  if (!holder||card?.dataset.gameId!==id) return;
  try {
    const snapshot=await api(hookUrl(id));hookSnapshots[id]=snapshot;
    if (!holder.isConnected||card.dataset.gameId!==id) return;
    const scroll=holder.querySelector('.hook-threads')?.scrollTop||0;
    holder.innerHTML=hookSessionBody(gameById(id),snapshot);
    const game=gameById(id),status=document.getElementById('game-adapter-status');
    if(status&&game)status.innerHTML=badge(adapterLabel(game),adapterTone(game));
    const list=holder.querySelector('.hook-threads');if(list)list.scrollTop=scroll;
  } catch(e) { if(reportErrors)errorToast(e); }
}
function initHookFontPicker(form,fonts) {
  const picker=form.querySelector('.hook-font-picker'),input=form.elements.fontFamily;
  const toggle=picker.querySelector('button'),choices=picker.querySelector('.hook-font-choices');
  const list=picker.querySelector('[role=listbox]'),count=picker.querySelector('.hook-font-count'),preview=picker.querySelector('.hook-font-preview');
  let visible=[],active=-1;
  const catalog=Array.isArray(fonts)?fonts:[];
  function previewFont(){preview.style.fontFamily=JSON.stringify(input.value.trim())+', sans-serif';}
  function close(){choices.hidden=true;input.setAttribute('aria-expanded','false');toggle.setAttribute('aria-expanded','false');input.removeAttribute('aria-activedescendant');active=-1;}
  function highlight(index){
    active=index;
    for(const [i,option] of [...list.children].entries())option.setAttribute('aria-selected',String(i===index));
    if(list.children[index]){input.setAttribute('aria-activedescendant',list.children[index].id);list.children[index].scrollIntoView({block:'nearest'});}
  }
  function choose(index){
    if(!visible[index])return;
    input.value=visible[index].family;input.dispatchEvent(new Event('input',{bubbles:true}));
    close();input.focus();previewFont();
  }
  function render(all=false){
    const query=all?'':input.value.trim().toLocaleLowerCase();
    const matches=catalog.filter(font=>(font.aliases||[font.family,font.displayName]).some(name=>name.toLocaleLowerCase().includes(query)));
    visible=matches.slice(0,80);active=-1;input.removeAttribute('aria-activedescendant');
    list.innerHTML=visible.map((font,index)=>`<button type="button" role="option" aria-selected="false" id="hook-font-option-${index}" tabindex="-1"><strong>${esc(font.displayName||font.family)}</strong>${font.displayName!==font.family?`<small>${esc(font.family)}</small>`:''}</button>`).join('');
    [...list.children].forEach((option,index)=>{
      option.style.fontFamily=JSON.stringify(visible[index].family)+', sans-serif';
      option.onmousedown=e=>e.preventDefault();option.onclick=()=>choose(index);
    });
    count.textContent=matches.length>80?`显示前 80 个，共 ${matches.length} 个；输入名称缩小范围。`:matches.length?`找到 ${matches.length} 个本机字体。`:'未找到匹配字体，可保留手动填写的名称。';
    choices.hidden=false;input.setAttribute('aria-expanded','true');toggle.setAttribute('aria-expanded','true');
  }
  input.addEventListener('input',()=>{previewFont();if(catalog.length)render();});
  toggle.onclick=()=>{if(!choices.hidden){close();input.focus();}else{render(true);input.focus();}};
  picker.addEventListener('keydown',e=>{
    if(e.key==='Escape'&&!choices.hidden){e.preventDefault();e.stopPropagation();close();input.focus();}
    else if(e.target===input&&(e.key==='ArrowDown'||e.key==='ArrowUp')){
      e.preventDefault();if(choices.hidden)render(true);
      if(visible.length)highlight(active<0?(e.key==='ArrowDown'?0:visible.length-1):Math.max(0,Math.min(visible.length-1,active+(e.key==='ArrowDown'?1:-1))));
    }else if(e.target===input&&e.key==='Enter'&&!choices.hidden&&active>=0){e.preventDefault();choose(active);}
  });
  form.addEventListener('pointerdown',e=>{if(!picker.contains(e.target))close();});
  if(!catalog.length)picker.querySelector('.hook-font-help').textContent='未取得本机字体列表，请手动填写字体名称；保存设置仍可使用。';
  previewFont();
}
function initHookColorPicker(form) {
  const color=form.elements.fontColor,hex=form.elements.fontColorHex;
  const preview=form.querySelector('.hook-font-preview');
  const valid=value=>/^#[A-Fa-f0-9]{6}$/.test(value);
  function render(){preview.style.color=color.value;preview.style.fontSize=(Number(form.elements.fontSize.value)||20)+'px';}
  color.addEventListener('input',()=>{hex.value=color.value.toUpperCase();hex.dispatchEvent(new Event('input',{bubbles:true}));render();});
  hex.addEventListener('input',()=>{if(valid(hex.value)){color.value=hex.value;render();}});
  hex.addEventListener('change',()=>{if(valid(hex.value))hex.value=hex.value.toUpperCase();});
  form.elements.fontSize.addEventListener('input',render);
  render();
}
async function showHookSettings(id) {
  const [s,fonts]=await Promise.all([api(hookUrl(id)),api('/api/fonts').catch(()=>null)]);hookSnapshots[id]=s;
  const settings=s.settings||{},embedAllowed=s.active&&s.embeddable;
  const bgiFontCache=gameById(id)?.engine==='BGI / Ethornell';
  hookModal('Galgame Hook 字体与显示',`<form id="hook-settings-form"><label class="field">文字编码<select name="codepage">${[[932,'日文 Shift-JIS'],[936,'简体中文 GBK'],[950,'繁体中文 Big5'],[65001,'UTF-8'],[1200,'UTF-16']].map(([value,name])=>`<option value="${value}" ${Number(settings.codepage||932)===value?'selected':''}>${name}</option>`).join('')}</select><small>原文乱码时调整；UTF-16 通道通常由 Hook 自动识别。</small></label><div class="field hook-font-picker"><label for="hook-font-family">中文字体</label>
      <div class="hook-font-input"><input id="hook-font-family" name="fontFamily" value="${esc(settings.fontFamily||'Microsoft YaHei UI')}" required maxlength="90" role="combobox" aria-expanded="false" aria-autocomplete="list" aria-controls="hook-font-options" autocomplete="off"><button type="button" class="btn" aria-label="展开本机字体列表" aria-expanded="false">选择字体</button></div>
      <div class="hook-font-choices" hidden><div id="hook-font-options" role="listbox" aria-label="本机字体"></div><small class="hook-font-count"></small></div>
      <small class="hook-font-help">可搜索本机字体的中英文名称，也可手动填写；字体是否包含所需汉字请查看预览。</small>
      <div class="hook-font-preview" aria-label="字体预览">梨花翻译 · 中文字体预览 Aa 123</div>
      <label class="hook-color-field" for="hook-font-color">浮窗字体颜色</label>
      <div class="hook-color-input"><input id="hook-font-color" name="fontColor" type="color" aria-label="选择浮窗字体颜色" value="${esc(settings.fontColor||'#FFFFFF')}"><input name="fontColorHex" type="text" aria-label="字体颜色色值" maxlength="7" pattern="#[A-Fa-f0-9]{6}" value="${esc(settings.fontColor||'#FFFFFF')}" required spellcheck="false" autocomplete="off"></div>
      <small>可选颜色或填写 #RRGGBB；原文和译文使用同一颜色。仅修改浮窗文字，保存后立即生效。</small>
    </div><div class="field-row"><label class="field">浮窗字号<input name="fontSize" type="number" min="12" max="48" value="${Number(settings.fontSize)||20}" required><small>只调整翻译浮窗。</small></label><label class="field">内嵌字号（%）<input name="embedFontSizePercent" type="number" min="50" max="200" step="1" value="${Number(settings.embedFontSizePercent)||100}" required><small>50–200%，100% 保持游戏原字号。</small></label></div><p class="tiny subtle hook-font-note">内嵌字号与浮窗字号分开保存。${bgiFontCache?'此引擎可能缓存已绘制的字形；修改字体或内嵌字号后，请保存进度并完全重启游戏，避免新旧大小混用。':'在可内嵌通道启用内嵌后应用到游戏；重新显示文字后检查，个别游戏需重启。'}自定义绘字可能不受影响。</p><div class="switch-row"><span>在浮窗显示原文</span><label class="switch"><input name="showOriginal" aria-label="在浮窗显示原文" type="checkbox" ${settings.showOriginal!==false?'checked':''}><span></span></label></div><div class="switch-row"><div><strong>在游戏内嵌译文</strong><p class="tiny subtle">${embedAllowed?'当前通道支持回填，字体与布局仍需检查。':'连接后选择标为“可内嵌”的通道才可启用。'}</p></div><label class="switch"><input name="embed" aria-label="在游戏内嵌译文" type="checkbox" ${settings.embed&&embedAllowed?'checked':''} ${!embedAllowed?'disabled':''}><span></span></label></div><label class="field">内嵌等待上限（毫秒）<input name="waitMs" type="number" min="200" max="5000" value="${Number(settings.waitMs)||2000}" required><small>超时保留原文；API 较慢时可先用浮窗，后续缓存命中再内嵌。</small></label><div class="switch-row"><div><strong>合并完全重复的整句</strong><p class="tiny subtle">取词重复时可开启；剧情本身重复句子时可关闭。</p></div><label class="switch"><input name="deduplicateSentences" aria-label="合并完全重复的整句" type="checkbox" ${settings.deduplicateSentences!==false?'checked':''}><span></span></label></div><div class="switch-row"><div><strong>通用系统取词</strong><p class="tiny subtle">未找到对话通道时开启，重新连接生效。未知引擎会自动启用，可能出现菜单和重复文字。</p></div><label class="switch"><input name="systemHooks" aria-label="通用系统取词" type="checkbox" ${settings.systemHooks?'checked':''}><span></span></label></div><div class="switch-row"><span>下次从工具启动时自动连接 Hook</span><label class="switch"><input name="enabled" aria-label="下次从工具启动时自动连接 Hook" type="checkbox" ${settings.enabled?'checked':''}><span></span></label></div><div class="form-actions"><button class="btn" type="button" data-action="close-modal">取消</button><button class="btn primary" type="submit">保存设置</button></div></form>`);
  const form=document.getElementById('hook-settings-form');
  initHookFontPicker(form,fonts);
  initHookColorPicker(form);
  form.onsubmit=e=>{e.preventDefault();busy(form.querySelector('[type=submit]'),async()=>{
    const data=new FormData(form);
    const gameFontChanged=String(data.get('fontFamily')).trim()!==settings.fontFamily||Number(data.get('embedFontSizePercent'))!==settings.embedFontSizePercent;
    await api(hookUrl(id)+'/settings',{method:'PUT',body:{...settings,systemHooks:form.elements.systemHooks.checked,deduplicateSentences:form.elements.deduplicateSentences.checked,enabled:form.elements.enabled.checked,embed:form.elements.embed.checked,showOriginal:form.elements.showOriginal.checked,codepage:Number(data.get('codepage')),fontFamily:String(data.get('fontFamily')).trim(),fontSize:Number(data.get('fontSize')),fontColor:String(data.get('fontColorHex')).toUpperCase(),embedFontSizePercent:Number(data.get('embedFontSizePercent')),waitMs:Number(data.get('waitMs'))}});
    closeModal(true);await refreshBootstrap();await refreshHook(id);toast(bgiFontCache&&gameFontChanged?'设置已保存；请保存进度并完全重启游戏，使字体和内嵌字号统一生效':'Hook 字体与显示设置已保存');
  });};
}
async function hookAction(action,id,button) {
  if(action==='hook-retry'){await api(hookUrl(id)+'/retry',{method:'POST'});await refreshHook(id);toast('已重新翻译最近原文；内嵌从下一句起生效');return;}
  if(action==='hook-remove-code'){await api(hookUrl(id)+'/remove-code',{method:'POST',body:{code:button.dataset.code}});closeModal(true);toast('已取消保存，下次连接不再插入；当前钩子需重启游戏清除');return;}
  if(action==='hook-refresh'){await refreshHook(id,true);return;}
  if(action==='hook-start'){await api(hookUrl(id)+'/start',{method:'POST',body:{pid:0,launch:true}});await refreshBootstrap();await refreshHook(id);toast('已开始连接，请在游戏中显示对话并选择文本通道');return;}
  if(action==='hook-stop'){await api(hookUrl(id)+'/stop',{method:'POST'});await refreshHook(id);await refreshRuntime(id);toast('Hook 已停止');return;}
  if(action==='hook-select'){await api(hookUrl(id)+'/select',{method:'POST',body:{threadId:button.dataset.thread}});await refreshHook(id);toast('已选择通道，开始翻译；内嵌默认关闭');return;}
  if(action==='hook-overlay'){const s=await api(hookUrl(id));await api(hookUrl(id)+'/overlay',{method:'POST',body:{show:!s.overlayShown}});await refreshHook(id);return;}
  if(action==='hook-settings'){await showHookSettings(id);return;}
  if(action==='hook-processes'){
    const processes=await api(hookUrl(id)+'/processes');
    hookModal('连接已运行的游戏',`<p class="tiny subtle">只列出此游戏目录中的进程。选择实际显示对话的程序。</p><div class="stack section-gap">${processes.map(p=>`<button class="btn hook-process" data-action="hook-connect-process" data-id="${esc(id)}" data-pid="${Number(p.pid)}"><strong>${esc(p.name)} · PID ${Number(p.pid)}</strong><small>${esc(p.path)}</small></button>`).join('')||notice('未找到游戏进程。请先启动游戏，再刷新列表。')}</div><div class="form-actions"><button class="btn" data-action="hook-processes" data-id="${esc(id)}">刷新列表</button><button class="btn" data-action="close-modal">关闭</button></div>`);return;
  }
  if(action==='hook-connect-process'){await api(hookUrl(id)+'/start',{method:'POST',body:{pid:Number(button.dataset.pid),launch:false}});closeModal(true);await refreshBootstrap();await refreshHook(id);toast('正在连接所选游戏进程');return;}
  if(action==='hook-code'){
    const saved=(await api(hookUrl(id))).settings.manualCodes||[];
    hookModal('手动 Hook 代码',`${saved.map(code=>`<div class="adapter-actions"><code>${esc(code)}</code><button class="btn small" data-action="hook-remove-code" data-id="${esc(id)}" data-code="${esc(code)}">移除保存</button></div>`).join('')}<form id="hook-code-form"><label class="field">Hook 代码<input name="code" required maxlength="1000" placeholder="填写与游戏版本匹配的 H / R / E 代码"><small>自动取词失败时使用。只保存并插入代码，正确的对话通道仍需选择。</small></label><div class="form-actions"><button class="btn" type="button" data-action="close-modal">取消</button><button class="btn primary" type="submit">保存并插入</button></div></form>`);
    const form=document.getElementById('hook-code-form');form.onsubmit=e=>{e.preventDefault();busy(form.querySelector('[type=submit]'),async()=>{await api(hookUrl(id)+'/insert',{method:'POST',body:{code:form.elements.code.value.trim()}});closeModal(true);toast('Hook 代码已发送，等待取词通道出现');});};
  }
}
