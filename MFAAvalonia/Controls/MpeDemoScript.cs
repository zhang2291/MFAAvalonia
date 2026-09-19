namespace MFAAvalonia.Controls;

internal static class MpeDemoScript
{
    internal const string Start = """
(() => {
  if (window.__mfaMpeDemo?.start) {
    window.__mfaMpeDemo.start();
    return 'restarted';
  }

  const steps = [
    {
      title: '认识 Pipeline 编辑器',
      texts: ['MPE LocalBridge', 'MPE'],
      html: `<p>这里是 MBCCtools 的可视化 Pipeline 编辑器。</p>
             <p><b>本演示只做界面讲解，不会创建、修改或保存任何 Pipeline 文件。</b></p>
             <p>你可以随时点击“退出演示”，或直接按 <kbd>Esc</kbd> 中断。</p>`
    },
    {
      title: '① 打开或新建 Pipeline',
      texts: ['在本地打开', '本地文件'],
      html: `<p>已有任务：点击 <b>在本地打开</b>，从 MBCCtools 的 pipeline 文件列表中选择 JSON。</p>
             <p>新任务：点击顶部的 <b>＋ / 新建文件</b>，先得到一个空白 Pipeline，再保存到目标 pipeline 目录。</p>
             <p>LocalBridge 当前根目录就是 MBCCtools，所以这里看到的是项目中的真实文件。</p>`
    },
    {
      title: '② 创建节点',
      selector: '.react-flow__pane, .react-flow',
      html: `<p>在画布空白处<b>右键</b>，会出现节点模板面板。</p>
             <p>常用模板包括空白节点、OCR 节点、模板匹配节点、点击节点等。</p>
             <p>如果先选中一个已有节点再创建新节点，MPE 会把新节点放到它后面，并自动建立连接。</p>`
    },
    {
      title: '③ 配置节点：识别 → 动作',
      texts: ['节点字段', '字段配置'],
      html: `<p>选中节点后，在“节点字段”中编辑：</p>
             <p><code>key</code> = 节点名；<code>recognition</code> = 如何判断当前画面；<code>action</code> = 识别成功后做什么。</p>
             <p>例如：<b>OCR 文字识别 → Click 点击</b>，就是“看到指定文字后点击它”。</p>
             <p><code>others</code> 中可继续添加 timeout、retry、next 等运行参数。</p>`
    },
    {
      title: '④ 连接节点，组织流程',
      selector: '.react-flow__pane, .react-flow',
      html: `<p>节点右侧是出口，左侧是入口。拖动端点即可连接流程。</p>
             <p>正常出口会编译成 <code>next</code>，错误出口会编译成 <code>on_error</code>。</p>
             <p>一个节点可以连接多个后继节点，连接顺序就是 JSON 数组中的顺序。</p>`
    },
    {
      title: '⑤ 截图、ROI 和 OCR',
      texts: ['连接设备', '实时画面'],
      html: `<p>需要从真实画面取参数时，先点顶部 <b>连接设备</b>。</p>
             <p>Android 模拟器一般选 <b>ADB</b>；Windows 游戏/程序窗口选 <b>Win32</b>。</p>
             <p>随后在 <code>roi</code>、<code>expected</code>、<code>template</code> 等字段右侧使用快捷工具：截图 → 框选区域 → OCR/截模板 → 自动填回字段。</p>
             <p>没有设备时，也可以上传本地截图作为底图。</p>`
    },
    {
      title: '⑥ 先单点调试，再跑完整流程',
      texts: ['调试', '错误列表'],
      html: `<p>建议按这个顺序验证：</p>
             <p><b>测试识别 → 测试动作 → 单节点运行 → 完整流程</b></p>
             <p>右键 Pipeline 节点可以找到“测试识别”“测试动作”“单节点运行”“从此节点开始调试”等功能。</p>
             <p>这样出问题时，很容易判断到底是识别参数不对，还是动作/流程连接不对。</p>`
    },
    {
      title: '⑦ 保存回 MBCCtools',
      texts: ['Pipeline JSON', '导出（粘贴板）'],
      html: `<p>确认流程后打开 <b>Pipeline JSON</b> 面板。</p>
             <p>通过 LocalBridge 打开的真实文件，可以使用“保存到本地”写回原 JSON。</p>
             <p><b>保存会覆盖原文件</b>，正式改动前建议先确认当前打开的是正确的 base / global / bilibili 资源文件。</p>`
    },
    {
      title: '演示完成',
      texts: ['MPE LocalBridge', 'MPE'],
      html: `<p>最核心的思路只有一条：</p>
             <p style="font-size:18px"><b>识别什么 → 做什么 → 下一步去哪</b></p>
             <p>对应 MaaFramework 就是：<code>recognition → action → next / on_error</code>。</p>
             <p>现在可以退出演示，直接在真实 Pipeline 上操作。</p>`
    }
  ];

  const host = document.createElement('div');
  host.id = 'mfa-mpe-demo';
  host.innerHTML = `
    <style>
      #mfa-mpe-demo { position: fixed; inset: 0; z-index: 2147483646; pointer-events: none; font-family: system-ui, sans-serif; }
      #mfa-mpe-demo .mfa-demo-card { pointer-events: auto; position: fixed; right: 24px; bottom: 24px; width: min(440px, calc(100vw - 48px)); background: rgba(255,255,255,.98); color:#1f1f1f; border:1px solid #d9d9d9; border-radius:14px; box-shadow:0 12px 40px rgba(0,0,0,.24); padding:18px; }
      #mfa-mpe-demo .mfa-demo-head { display:flex; justify-content:space-between; gap:12px; align-items:center; margin-bottom:10px; }
      #mfa-mpe-demo .mfa-demo-title { font-size:18px; font-weight:700; }
      #mfa-mpe-demo .mfa-demo-progress { font-size:12px; color:#666; white-space:nowrap; }
      #mfa-mpe-demo .mfa-demo-body { font-size:14px; line-height:1.65; }
      #mfa-mpe-demo .mfa-demo-body p { margin:7px 0; }
      #mfa-mpe-demo .mfa-demo-body code { background:#f5f5f5; padding:1px 5px; border-radius:4px; }
      #mfa-mpe-demo .mfa-demo-body kbd { border:1px solid #bbb; border-bottom-width:2px; border-radius:4px; padding:1px 5px; background:#fafafa; }
      #mfa-mpe-demo .mfa-demo-hint { margin-top:8px; color:#8c8c8c; font-size:12px; }
      #mfa-mpe-demo .mfa-demo-actions { display:flex; justify-content:space-between; gap:8px; margin-top:14px; }
      #mfa-mpe-demo button { border:1px solid #d9d9d9; background:#fff; border-radius:7px; padding:7px 12px; cursor:pointer; font-size:14px; }
      #mfa-mpe-demo button:hover { border-color:#1677ff; color:#1677ff; }
      #mfa-mpe-demo .mfa-demo-primary { background:#1677ff; border-color:#1677ff; color:#fff; }
      #mfa-mpe-demo .mfa-demo-primary:hover { background:#4096ff; color:#fff; }
      #mfa-mpe-demo .mfa-demo-exit { color:#cf1322; }
    </style>
    <div class="mfa-demo-card">
      <div class="mfa-demo-head"><div class="mfa-demo-title"></div><div class="mfa-demo-progress"></div></div>
      <div class="mfa-demo-body"></div>
      <div class="mfa-demo-hint"></div>
      <div class="mfa-demo-actions">
        <button class="mfa-demo-exit">退出演示</button>
        <div><button class="mfa-demo-prev">上一步</button> <button class="mfa-demo-next mfa-demo-primary">下一步</button></div>
      </div>
    </div>`;
  document.body.appendChild(host);

  const titleEl = host.querySelector('.mfa-demo-title');
  const progressEl = host.querySelector('.mfa-demo-progress');
  const bodyEl = host.querySelector('.mfa-demo-body');
  const hintEl = host.querySelector('.mfa-demo-hint');
  const prevBtn = host.querySelector('.mfa-demo-prev');
  const nextBtn = host.querySelector('.mfa-demo-next');
  const exitBtn = host.querySelector('.mfa-demo-exit');
  let index = 0;
  let highlighted = null;
  let oldOutline = '';
  let oldOutlineOffset = '';

  const isVisible = (el) => {
    if (!el) return false;
    const style = getComputedStyle(el);
    const rect = el.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
  };

  const findByText = (texts) => {
    if (!texts) return null;
    const candidates = document.querySelectorAll('button, [role="button"], div, span, p, h1, h2, h3');
    for (const text of texts) {
      for (const el of candidates) {
        if (host.contains(el) || !isVisible(el)) continue;
        const value = (el.textContent || '').trim();
        if (value === text || value.includes(text)) return el;
      }
    }
    return null;
  };

  const clearHighlight = () => {
    if (!highlighted) return;
    highlighted.style.outline = oldOutline;
    highlighted.style.outlineOffset = oldOutlineOffset;
    highlighted = null;
  };

  const findTarget = (step) => {
    if (step.selector) {
      for (const el of document.querySelectorAll(step.selector)) {
        if (isVisible(el)) return el;
      }
    }
    return findByText(step.texts);
  };

  const render = () => {
    clearHighlight();
    const step = steps[index];
    titleEl.textContent = step.title;
    progressEl.textContent = `${index + 1} / ${steps.length}`;
    bodyEl.innerHTML = step.html;
    prevBtn.disabled = index === 0;
    nextBtn.textContent = index === steps.length - 1 ? '完成' : '下一步';

    // 私用版采用纯文字演示，不再尝试给 MPE 内部控件加边框。
    // MPE 自身 DOM 会随版本/布局变化，错误高亮反而容易误导。
    hintEl.textContent = '文字演示模式：只说明当前步骤，不会高亮、点击或修改 MPE 界面。';
  };

  const stop = () => {
    clearHighlight();
    document.removeEventListener('keydown', onKeyDown, true);
    host.remove();
    delete window.__mfaMpeDemo;
  };

  const next = () => {
    if (index >= steps.length - 1) { stop(); return; }
    index++;
    render();
  };

  const previous = () => {
    if (index <= 0) return;
    index--;
    render();
  };

  const start = () => {
    index = 0;
    render();
  };

  const onKeyDown = (event) => {
    if (event.key === 'Escape') { event.preventDefault(); stop(); }
    else if (event.key === 'ArrowRight') { event.preventDefault(); next(); }
    else if (event.key === 'ArrowLeft') { event.preventDefault(); previous(); }
  };

  exitBtn.addEventListener('click', stop);
  nextBtn.addEventListener('click', next);
  prevBtn.addEventListener('click', previous);
  document.addEventListener('keydown', onKeyDown, true);
  window.__mfaMpeDemo = { start, stop, next, previous };
  start();
  return 'started';
})()
""";

    internal const string Stop = """
(() => {
  if (!window.__mfaMpeDemo?.stop) return 'not-running';
  window.__mfaMpeDemo.stop();
  return 'stopped';
})()
""";
}
