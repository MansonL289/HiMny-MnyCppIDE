namespace WinUI3Shell.Oj;

/// <summary>注入到后台浏览器每个页面的公共工具脚本。</summary>
internal static class OjScripts
{
    /// <summary>
    /// 提供 __mny.htmlToMd(element)：把题面 DOM 转成 Markdown（保留段落、列表、代码块、表格、行内样式）。
    /// OJ 的抓取脚本只需要挑出题面容器，转换交给这里，避免每个 OJ 写一份。
    /// </summary>
    public const string Prelude = """
(function () {
  // MathJax / KaTeX 会把一段公式渲染成多份（可见字形、MathML、TeX 注释），
  // 直接取 textContent 会得到 "AAA" 这种重复文本；这里优先取 TeX 源码并包回 $…$
  function mathContainer(n) {
    const tag = n.tagName.toLowerCase();
    return tag === 'mjx-container' ||
      (n.classList && (n.classList.contains('katex') || n.classList.contains('katex-display')));
  }

  function mathTex(el) {
    const ann = el.querySelector('annotation[encoding="application/x-tex"]');
    if (ann && ann.textContent.trim()) { return ann.textContent.trim(); }
    const alt = el.getAttribute('alttext');
    if (alt && alt.trim()) { return alt.trim(); }
    const body = el.querySelector('.katex-html') || el.querySelector('mjx-math') || el;
    return (body.textContent || '').trim();
  }

  function mathMarkup(el) {
    const tex = mathTex(el);
    if (!tex) { return ''; }
    // 纯空白的公式（有的站点用 \,\, 之类的占位符做缩进）直接丢掉
    if (/^[\\,;:!\s]+$/.test(tex)) { return ''; }
    const tag = el.tagName.toLowerCase();
    const display = tag === 'mjx-container'
      ? (el.getAttribute('display') === 'true' || el.getAttribute('display') === 'block')
      : (el.classList && el.classList.contains('katex-display'));
    return display ? '\n$$' + tex + '$$\n' : '$' + tex + '$';
  }

  function inlineText(el) {
    let out = '';
    // 直接对公式容器调用时也要走公式分支，否则会把 MathML/TeX 注释重复拼一遍
    if (el.nodeType === 1 && mathContainer(el)) { return mathMarkup(el); }
    for (let i = 0; i < el.childNodes.length; i++) {
      const n = el.childNodes[i];
      if (n.nodeType === 3) { out += n.nodeValue.replace(/\s+/g, ' '); continue; }
      if (n.nodeType !== 1) continue;
      const tag = n.tagName.toLowerCase();
      if (tag === 'br') { out += '\n'; continue; }
      if (tag === 'strong' || tag === 'b') { out += '**' + inlineText(n).trim() + '**'; continue; }
      if (tag === 'em' || tag === 'i') { out += '*' + inlineText(n).trim() + '*'; continue; }
      if (tag === 'code') { out += '`' + n.textContent + '`'; continue; }
      if (tag === 'img') {
        const src = n.getAttribute('src') || '';
        if (src) out += '![](' + src + ')';
        continue;
      }
      if (tag === 'a') {
        const text = inlineText(n).trim();
        const href = n.getAttribute('href') || '';
        out += text ? (href ? '[' + text + '](' + href + ')' : text) : '';
        continue;
      }
      if (mathContainer(n)) { out += mathMarkup(n); continue; }
      if (tag === 'script' || tag === 'style') continue;
      out += inlineText(n);
    }
    return out;
  }

  // 保留 <pre> 里的原始换行，同时把公式容器还原成 $…$
  function preText(el) {
    let out = '';
    for (let i = 0; i < el.childNodes.length; i++) {
      const n = el.childNodes[i];
      if (n.nodeType === 3) { out += n.nodeValue; continue; }
      if (n.nodeType !== 1) continue;
      const tag = n.tagName.toLowerCase();
      if (tag === 'br') { out += '\n'; continue; }
      if (mathContainer(n)) { out += mathMarkup(n).trim(); continue; }
      if (tag === 'script' || tag === 'style') continue;
      out += preText(n);
    }
    return out;
  }

  function hasBlock(el) {
    return !!el.querySelector('p,div,section,pre,ul,ol,table,h1,h2,h3,h4,h5,h6,hr');
  }

  function toMarkdown(root) {
    const lines = [];
    function push(text) { lines.push(text); }

    // 块级标签以外的节点（span/u/b/公式容器等）都当作行内内容，
    // 牛客的题面就是「文字 + 行内公式」直接排在 .section-content 里，
    // 逐节点当段落会把一句话拆成很多行，公式也会被拆成重复的碎片
    const BLOCK_TAGS = {
      p: 1, div: 1, section: 1, article: 1, main: 1, blockquote: 1, pre: 1,
      ul: 1, ol: 1, table: 1, hr: 1, li: 1, figure: 1, details: 1,
      h1: 1, h2: 1, h3: 1, h4: 1, h5: 1, h6: 1
    };

    function walk(node) {
      let buf = '';
      function flush() {
        const text = buf.replace(/[ \t]+/g, ' ').replace(/\n{2,}/g, '\n').trim();
        buf = '';
        if (text) { push(''); push(text); push(''); }
      }

      for (let i = 0; i < node.childNodes.length; i++) {
        const n = node.childNodes[i];
        // 混合内容容器里直接挂在块级子元素旁边的文字（如 CF 的 time limit 值），
        // 与相邻的行内节点一起攒成一段
        if (n.nodeType === 3) {
          buf += n.nodeValue.replace(/\s+/g, ' ');
          continue;
        }
        if (n.nodeType !== 1) continue;
        const tag = n.tagName.toLowerCase();
        if (tag === 'script' || tag === 'style' || tag === 'noscript' || tag === 'svg') continue;
        if (tag === 'br') { flush(); continue; }
        if (mathContainer(n)) { buf += mathMarkup(n); continue; }
        if (!BLOCK_TAGS[tag]) { buf += inlineText(n); continue; }

        flush();
        if (/^h[1-6]$/.test(tag)) {
          const level = Number(tag.charAt(1));
          const text = inlineText(n).trim();
          if (text) { push(''); push('#'.repeat(level) + ' ' + text); push(''); }
          continue;
        }
        if (tag === 'pre') {
          // 有些 OJ 把样例逐行放在 .test-example-line 里，
          // 直接取 textContent 会把所有行拼成一行，这里按行还原换行
          const lineEls = n.querySelectorAll('.test-example-line');
          let code = '';
          if (lineEls.length) {
            const arr = [];
            for (let k = 0; k < lineEls.length; k++) arr.push(lineEls[k].textContent);
            code = arr.join('\n');
          } else {
            code = preText(n);
          }
          push('');
          push('```');
          push(code.replace(/\s+$/, ''));
          push('```');
          push('');
          continue;
        }
        if (tag === 'ul' || tag === 'ol') {
          const items = n.children;
          for (let k = 0; k < items.length; k++) {
            if (items[k].tagName.toLowerCase() !== 'li') continue;
            push((tag === 'ol' ? (k + 1) + '. ' : '- ') + inlineText(items[k]).trim());
          }
          push('');
          continue;
        }
        if (tag === 'table') {
          const rows = n.querySelectorAll('tr');
          for (let r = 0; r < rows.length; r++) {
            const cells = rows[r].querySelectorAll('th,td');
            if (!cells.length) continue;
            const values = [];
            for (let c = 0; c < cells.length; c++) {
              values.push(inlineText(cells[c]).trim().replace(/\|/g, '\\|'));
            }
            push('| ' + values.join(' | ') + ' |');
            if (r === 0 && rows[r].querySelector('th')) {
              push('|' + values.map(function () { return ' --- |'; }).join(''));
            }
          }
          push('');
          continue;
        }
        if (tag === 'hr') { push(''); push('---'); push(''); continue; }
        if (tag === 'p') {
          const text = inlineText(n).trim();
          if (text) { push(''); push(text); push(''); }
          continue;
        }
        if (tag === 'section' || tag === 'div' || tag === 'article' || tag === 'main' || tag === 'blockquote') {
          if (hasBlock(n)) {
            walk(n);
          } else {
            const text = inlineText(n).trim();
            if (text) { push(''); push(text); push(''); }
          }
          continue;
        }
        if (tag === 'li') {
          const text = inlineText(n).trim();
          if (text) push('- ' + text);
          continue;
        }
        walk(n);
      }
    }

    walk(root);
    return lines.join('\n')
      .replace(/[ \t]+\n/g, '\n')
      .replace(/\n{3,}/g, '\n\n')
      .trim();
  }

  window.__mny = {
    htmlToMd: toMarkdown,
    inlineText: inlineText
  };

  // 有些站点（如牛客）把首屏数据放在 window.__INITIAL_STATE__，页面框架水合后会删掉这个变量。
  // 公共脚本在文档创建时就注入，趁 DOMContentLoaded（早于页面自己的水合逻辑）先存一份快照。
  function snapshotState() {
    try {
      if (!window.__mny.state && window.__INITIAL_STATE__) {
        window.__mny.state = window.__INITIAL_STATE__;
      }
    } catch (e) { /* 快照失败不影响主流程 */ }
  }
  document.addEventListener('DOMContentLoaded', snapshotState);
  window.addEventListener('load', snapshotState);
})();
""";

    /// <summary>
    /// 登录浮层专用：隐藏站点顶部导航、侧边栏、页脚（含备案号）等与登录无关的元素，
    /// 只保留居中的登录表单区域。单页应用会在路由切换后重绘，因此持续监听 DOM 变化。
    /// </summary>
    public const string LoginCleanup = """
(function () {
  var HIDE = [
    'header', 'nav', 'footer', 'aside',
    '#lfe-header', '#lfe-footer', '.lfe-header', '.lfe-footer',
    '.l-header', '.l-footer', '.l-nav', '.l-sidebar', '.lg-header', '.lg-footer',
    '.navbar', '.navbar-right', '.navigation', '.site-header', '.site-footer',
    '.site-nav', '.main-nav', '.top-nav', '.topbar', '.side-bar',
    '.nowcoder-header', '.nc-header', '.nc-footer', '.nc-sidebar',
    '.copyright', '.footer-copyright', '.beian', '[class*="beian"]',
    '[class*="copyright"]', '[class*="license"]',
    '[class*="statement"]', '[class*="icp"]', '[id*="footer"]'
  ].join(',');
  var CSS = HIDE + '{display:none !important;}' +
    'html,body{overflow-x:hidden !important;background:transparent !important;}' +
    '.container,.main,.content,#main,.wrapper{padding-top:0 !important;margin-top:0 !important;}';

  function ensureStyle() {
    if (document.getElementById('mny-login-clean')) { return; }
    var style = document.createElement('style');
    style.id = 'mny-login-clean';
    style.textContent = CSS;
    (document.head || document.documentElement).appendChild(style);
  }

  // 备案号/版权声明通常放在自定义页脚容器里（未必是 footer 标签）。
  // 向上找到整段说明文字所在的块并隐藏，但绝不隐藏包含输入框或按钮的表单容器。
  function stripBeian() {
    var nodes = document.querySelectorAll('div,section,p,span,a,ul,li');
    for (var i = 0; i < nodes.length; i++) {
      var el = nodes[i];
      if (el.querySelector('input,textarea,button,form')) { continue; }
      var text = (el.textContent || '').trim();
      if (!text || text.length > 400) { continue; }
      if (!/(ICP\s*备|公安\s*备|备案号|增值电信业务|All rights reserved|©\s*\d{4})/i.test(text)) { continue; }

      var target = el;
      while (target.parentElement &&
             !target.parentElement.querySelector('input,textarea,button,form') &&
             (target.parentElement.textContent || '').length < 600) {
        target = target.parentElement;
      }

      target.style.setProperty('display', 'none', 'important');
    }
  }

  function run() { ensureStyle(); stripBeian(); }

  var pending = false;
  function schedule() {
    if (pending) { return; }
    pending = true;
    setTimeout(function () { pending = false; run(); }, 300);
  }

  run();
  document.addEventListener('DOMContentLoaded', run);
  window.addEventListener('load', run);
  try {
    new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
  } catch (e) { }
  setTimeout(run, 400);
  setTimeout(run, 1200);
  setTimeout(run, 2400);
})();
""";

    /// <summary>
    /// 洛谷题目列表 HTML 页：读取每一行的题号与答题状态（status 列）。
    /// 行结构：.row-wrap > .row，内含 .status / .pid / .title 等列（与官网题目列表一致）。
    /// 返回 { items: [{ id, state }] }，state 取 accepted / wrong / none。
    /// </summary>
    public const string LuoguAnswerStatus = """
(function () {
  function norm(v) { return (v || '').toString().toLowerCase(); }

  function stateOf(cell) {
    if (!cell) { return 'none'; }
    var anchor = cell.querySelector('a');
    var nodes = cell.querySelectorAll('*');
    var text = (cell.textContent || '').replace(/\s+/g, ' ').trim();
    var bucket = norm(cell.className) + ' ' + norm(anchor ? anchor.className : '');
    for (var i = 0; i < nodes.length; i++) {
      bucket += ' ' + norm(nodes[i].className) +
        ' ' + norm(nodes[i].getAttribute('data-status')) +
        ' ' + norm(nodes[i].getAttribute('title')) +
        ' ' + norm(nodes[i].getAttribute('aria-label'));
    }
    var all = bucket + ' ' + norm(text) + ' ' + norm(cell.innerHTML);

    // 没有任何子节点也没有文字，说明这一题没有作答记录
    if (nodes.length === 0 && !text) { return 'none'; }

    // 先判断答错：避免 unaccepted 之类的类名命中 accepted
    if (/(unaccepted|notaccepted|wrong|incorrect|failed|failure|error|rejected|unac|\bwa\b|答错|未通过|未答对)/.test(all)) {
      return 'wrong';
    }
    if (/(accepted|correct|passed|success|solved|\bac\b|\bok\b|已通过|已答对|通过)/.test(all)) {
      return 'accepted';
    }

    // 图标可能直接内联绿色/红色（svg fill 或 style 颜色）
    if (/#(2fa84f|52c41a|00b42a|19be6b|67c23a)/i.test(cell.innerHTML || '')) { return 'accepted'; }
    if (/#(f5222d|ff4d4f|e74c3c|d13438|f56c6c)/i.test(cell.innerHTML || '')) { return 'wrong'; }

    // 类名和文字都没有明确特征时，用状态图标的实际颜色兜底（绿=通过，红=答错）
    var color = '';
    try { color = getComputedStyle(anchor || cell).color || ''; } catch (e) { }
    var m = /rgba?\((\d+)[^\d]+(\d+)[^\d]+(\d+)/.exec(color);
    if (m) {
      var r = +m[1], g = +m[2], b = +m[3];
      if (g > 110 && g > r + 30 && g > b + 20) { return 'accepted'; }
      if (r > 140 && r > g + 40) { return 'wrong'; }
    }
    return 'none';
  }

  var rows = document.querySelectorAll('.row-wrap div.row');
  if (!rows.length) { rows = document.querySelectorAll('div.row'); }

  var items = [];
  for (var i = 0; i < rows.length; i++) {
    var row = rows[i];
    var pidEl = row.querySelector('.pid');
    var pid = pidEl ? (pidEl.getAttribute('title') || pidEl.textContent || '').trim() : '';
    if (!pid) {
      var link = row.querySelector('.title a') || row.querySelector('a[href*="/problem/"]');
      if (link) {
        var hm = /\/problem\/([A-Za-z]+[0-9][A-Za-z0-9_]*)/.exec(link.getAttribute('href') || '');
        if (hm) { pid = hm[1]; }
      }
    }
    if (!pid) { continue; }
    items.push({ id: pid.toUpperCase(), state: stateOf(row.querySelector('.status')) });
  }

  return { items: items };
})()
""";
}
